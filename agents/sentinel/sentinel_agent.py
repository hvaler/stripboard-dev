import os
import uuid
import datetime
from typing import List, Dict, Any, Optional
from grafana_mcp_client import GrafanaMcpClient

class ConflictSentinelAgent:
    """
    Conflict Sentinel Watcher Agent (Python ADK / §6 / ADR-008).
    Monitors schedule integrity across MCP servers, queries Grafana Cloud MCP Server,
    and raises Disruption events when conflicts occur.
    """
    def __init__(self, grafana_client: Optional[GrafanaMcpClient] = None):
        self.grafana = grafana_client or GrafanaMcpClient()

    def inspect_schedule_disruptions(
        self,
        schedule_scenes: List[Dict[str, Any]],
        person_availabilities: Dict[str, List[str]],
        location_permits: Dict[str, Dict[str, str]],
        weather_forecasts: Dict[str, Dict[str, Any]]
    ) -> List[Dict[str, Any]]:
        """
        Inspects schedule scenes against people availability, location permits, and weather forecasts.
        """
        disruptions = []

        # Query Grafana MCP Server status
        grafana_status = self.grafana.query_tool("get_alerts", {})

        for scene in schedule_scenes:
            scene_num = scene.get("number")
            location = scene.get("set_location", "")
            int_ext = scene.get("int_ext", "INT")
            cast = scene.get("cast", [])
            date_str = scene.get("date", "2026-08-10")

            # 1. Check Cast Availability
            for actor in cast:
                unavail = person_availabilities.get(actor, [])
                if date_str in unavail:
                    disruption = {
                        "id": str(uuid.uuid4()),
                        "trigger_type": "ActorIllness",
                        "description": f"Actor {actor} is unavailable on {date_str} for Scene #{scene_num} at {location}.",
                        "expected_duration_days": 2,
                        "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat()
                    }
                    disruptions.append(disruption)

                    # Post annotation to Grafana Cloud
                    self.grafana.post_annotation(
                        text=f"CRITICAL DISRUPTION: {actor} unavailable on {date_str} (Scene #{scene_num})",
                        tags=["disruption", "actor-unavailability", "critical"]
                    )

            # 2. Check Weather Risk for EXT Scenes
            if int_ext in ("EXT", "INT/EXT"):
                weather = weather_forecasts.get(location, {})
                condition = weather.get("condition", "Sunny")
                precip = weather.get("precipitation_probability", 0)

                if condition in ("Rain", "Heavy Rain") or precip > 70:
                    disruption = {
                        "id": str(uuid.uuid4()),
                        "trigger_type": "WeatherAlert",
                        "description": f"Weather alert ({condition}, {precip}% rain) for EXT Scene #{scene_num} at {location} on {date_str}.",
                        "expected_duration_days": 1,
                        "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat()
                    }
                    disruptions.append(disruption)

                    self.grafana.post_annotation(
                        text=f"WEATHER DISRUPTION: Rain predicted for EXT Scene #{scene_num} at {location}",
                        tags=["disruption", "weather-alert", "high"]
                    )

            # 3. Check Location Permit Expiration
            permit = location_permits.get(location, {})
            start_permit = permit.get("start", "2026-08-01")
            end_permit = permit.get("end", "2026-08-30")

            if date_str < start_permit or date_str > end_permit:
                disruption = {
                    "id": str(uuid.uuid4()),
                    "trigger_type": "PermitExpired",
                    "description": f"Permit expired or invalid for {location} on {date_str} (Scene #{scene_num}).",
                    "expected_duration_days": 3,
                    "timestamp": datetime.datetime.now(datetime.timezone.utc).isoformat()
                }
                disruptions.append(disruption)

                self.grafana.post_annotation(
                    text=f"PERMIT DISRUPTION: {location} permit expired on {date_str}",
                    tags=["disruption", "permit-expired", "critical"]
                )

        return disruptions
