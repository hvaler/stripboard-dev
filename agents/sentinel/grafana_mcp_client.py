import os
import json
import logging
from typing import Dict, Any, List, Optional

logger = logging.getLogger("GrafanaMcpClient")

class GrafanaMcpClient:
    """
    Placeholder for the Grafana Cloud MCP Server client (§6 / ADR-008).

    NOT IMPLEMENTED: this class performs NO network I/O. query_tool() returns canned
    dictionaries and post_annotation() only logs. The Grafana partner track requires an
    active MCP client at runtime, which makes this the project's most important gap.
    Tracked as EV-19, together with the 401 on the real service-account token (DT-009).
    """
    def __init__(self, endpoint_url: Optional[str] = None, api_token: Optional[str] = None):
        self.endpoint_url = endpoint_url or os.getenv("GRAFANA_MCP_ENDPOINT", "https://pinkcorridor3522.grafana.net/api/mcp")
        self.api_token = api_token or os.getenv("GRAFANA_SENTINEL_TOKEN", "stub_glsa_token")

    def query_tool(self, tool_name: str, params: Dict[str, Any]) -> Dict[str, Any]:
        """
        STUB. Returns canned responses; does not contact the Grafana Cloud MCP server.
        """
        logger.info(f"[STUB] Grafana MCP tool NOT called: {tool_name} with params: {params}")
        
        # Hardcoded responses. There is no live code path here yet — see EV-19.
        if tool_name == "query_metrics":
            return {"status": "ok", "series": [{"name": "system_load", "value": 0.42}]}
        elif tool_name == "get_alerts":
            return {"status": "ok", "alerts": []}
        else:
            return {"status": "ok", "message": f"Executed tool {tool_name}"}

    def post_annotation(self, text: str, tags: List[str], dashboard_id: Optional[str] = None) -> bool:
        """
        STUB. Builds the payload, logs it and returns True. Nothing is ever sent to the
        Grafana Annotations API — see EV-19 / EV-20.
        """
        payload = {
            "text": text,
            "tags": tags + ["stripboard", "conflict-sentinel"],
            "time": int(os.getenv("TEST_TIMESTAMP", "1722720000000"))
        }
        if dashboard_id:
            payload["dashboardId"] = dashboard_id

        logger.info(f"[STUB] Grafana annotation NOT sent: {json.dumps(payload)}")
        return True
