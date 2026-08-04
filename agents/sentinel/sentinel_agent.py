import datetime
import logging
import uuid
from typing import Any, Dict, List, Optional

from grafana_mcp_client import GrafanaMcpClient, GrafanaMcpError

logger = logging.getLogger("ConflictSentinel")

ANNOTATION_TAGS = ["stripboard", "conflict-sentinel"]


def to_epoch_ms(date_str: str) -> int:
    """Shoot dates are calendar days; Grafana annotations are epoch milliseconds."""
    dt = datetime.datetime.strptime(date_str, "%Y-%m-%d").replace(tzinfo=datetime.timezone.utc)
    return int(dt.timestamp() * 1000)


class ConflictSentinelAgent:
    """
    Watches a shooting schedule for disruptions and publishes them to Grafana
    (§6 / ADR-008 / ADR-010).

    Two responsibilities, deliberately separated:

    - **Detection** is deterministic Python over schedule facts (cast availability,
      weather on exteriors, permit windows). It does not need Grafana and does not need
      a model — these are hard rules, and EV-29 adds the reasoning layer on top.
    - **Publication** goes through the Grafana MCP server as real `create_annotation`
      tool calls.

    Without a connected MCP client the agent still detects, but every disruption is
    returned with `annotation_id=None` and `published=False`. It never pretends to have
    written to Grafana.
    """

    def __init__(self, grafana_client: Optional[GrafanaMcpClient] = None):
        self.grafana = grafana_client

    @property
    def can_publish(self) -> bool:
        return self.grafana is not None and self.grafana.is_connected

    def check_grafana_state(self) -> Dict[str, Any]:
        """
        Read live Grafana state over MCP. Used to confirm the partner integration is
        actually live before a shoot day, and as the seed for EV-29's reasoning layer.

        Deliberately uses core Grafana tools only. OnCall-backed tools such as
        `list_alert_groups` 404 on a stack without the OnCall plugin, which would make
        this check fail for reasons unrelated to the shoot.
        """
        if not self.can_publish:
            raise GrafanaMcpError("No connected Grafana MCP client.")

        return {
            "server": self.grafana.server_info,
            "datasources": self.grafana.call_tool("list_datasources", {}),
            "alert_rules": self.grafana.call_tool("alerting_manage_rules", {"operation": "list"}),
        }

    def inspect_schedule_disruptions(
        self,
        schedule_scenes: List[Dict[str, Any]],
        person_availabilities: Dict[str, List[str]],
        location_permits: Dict[str, Dict[str, str]],
        weather_forecasts: Dict[str, Dict[str, Any]],
    ) -> List[Dict[str, Any]]:
        """Detect disruptions and publish each one to Grafana as an annotation."""
        if not self.can_publish:
            logger.warning(
                "No connected Grafana MCP client: disruptions will be detected but NOT "
                "published. Set GRAFANA_MCP_ENDPOINT and pass a connected client."
            )

        disruptions: List[Dict[str, Any]] = []

        for scene in schedule_scenes:
            scene_num = scene.get("number")
            location = scene.get("set_location", "")
            int_ext = scene.get("int_ext", "INT")
            cast = scene.get("cast", [])
            date_str = scene.get("date", "2026-08-10")

            for actor in cast:
                if date_str in person_availabilities.get(actor, []):
                    disruptions.append(self._raise(
                        trigger_type="ActorIllness",
                        description=f"Actor {actor} is unavailable on {date_str} for Scene #{scene_num} at {location}.",
                        date_str=date_str,
                        severity="critical",
                        extra_tags=["actor-unavailability"],
                        expected_duration_days=2,
                    ))

            if int_ext in ("EXT", "INT/EXT"):
                weather = weather_forecasts.get(location, {})
                condition = weather.get("condition", "Sunny")
                precip = weather.get("precipitation_probability", 0)
                if condition in ("Rain", "Heavy Rain") or precip > 70:
                    disruptions.append(self._raise(
                        trigger_type="WeatherAlert",
                        description=f"Weather alert ({condition}, {precip}% rain) for EXT Scene #{scene_num} at {location} on {date_str}.",
                        date_str=date_str,
                        severity="high",
                        extra_tags=["weather-alert"],
                        expected_duration_days=1,
                    ))

            permit = location_permits.get(location, {})
            start_permit = permit.get("start", "2026-08-01")
            end_permit = permit.get("end", "2026-08-30")
            if date_str < start_permit or date_str > end_permit:
                disruptions.append(self._raise(
                    trigger_type="PermitExpired",
                    description=f"Permit expired or invalid for {location} on {date_str} (Scene #{scene_num}).",
                    date_str=date_str,
                    severity="critical",
                    extra_tags=["permit-expired"],
                    expected_duration_days=3,
                ))

        published = sum(1 for d in disruptions if d["published"])
        logger.info(
            "Detected %d disruption(s); %d published to Grafana.", len(disruptions), published
        )
        return disruptions

    # --- internals ----------------------------------------------------------------

    def _raise(
        self,
        trigger_type: str,
        description: str,
        date_str: str,
        severity: str,
        extra_tags: List[str],
        expected_duration_days: int,
    ) -> Dict[str, Any]:
        disruption = {
            "id": str(uuid.uuid4()),
            "trigger_type": trigger_type,
            "description": description,
            "severity": severity,
            "expected_duration_days": expected_duration_days,
            "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "annotation_id": None,
            "published": False,
        }

        annotation_id = self._annotate(
            text=f"{trigger_type.upper()}: {description}",
            tags=extra_tags + [severity],
            date_str=date_str,
        )
        if annotation_id is not None:
            disruption["annotation_id"] = annotation_id
            disruption["published"] = True

        return disruption

    def _annotate(self, text: str, tags: List[str], date_str: str) -> Optional[int]:
        """Publish one annotation via the Grafana MCP server. Returns its id, or None."""
        if not self.can_publish:
            return None

        try:
            result = self.grafana.call_tool("create_annotation", {
                "time": to_epoch_ms(date_str),
                "text": text,
                "tags": ANNOTATION_TAGS + tags,
            })
        except GrafanaMcpError as exc:
            # A failed publish is reported, never swallowed into a success.
            logger.error("Failed to publish annotation to Grafana: %s", exc)
            return None

        payload = result.get("Payload", result) if isinstance(result, dict) else {}
        annotation_id = payload.get("id") if isinstance(payload, dict) else None
        logger.info("Published Grafana annotation id=%s: %s", annotation_id, text[:80])
        return annotation_id
