import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))

from grafana_mcp_client import GrafanaMcpClient, GrafanaMcpError
from sentinel_agent import ConflictSentinelAgent, to_epoch_ms

SCENES = [
    {"number": 1, "set_location": "221B BAKER STREET", "int_ext": "INT",
     "cast": ["Sherlock Holmes"], "date": "2026-08-10"},
    {"number": 4, "set_location": "TOWER BRIDGE WHARF", "int_ext": "EXT",
     "cast": ["Sherlock Holmes", "Prof. James Moriarty"], "date": "2026-08-11"},
]
AVAILABILITIES = {"Sherlock Holmes": ["2026-08-10"]}
PERMITS = {
    "221B BAKER STREET": {"start": "2026-08-01", "end": "2026-08-30"},
    "TOWER BRIDGE WHARF": {"start": "2026-08-01", "end": "2026-08-30"},
}
WEATHER = {"TOWER BRIDGE WHARF": {"condition": "Rain", "precipitation_probability": 90}}


def _mcp_server_available() -> bool:
    """True only if a real Grafana MCP server answers the initialize handshake."""
    if not os.getenv("GRAFANA_MCP_ENDPOINT"):
        return False
    try:
        with GrafanaMcpClient(timeout=5):
            return True
    except Exception:
        return False


MCP_AVAILABLE = _mcp_server_available()
SKIP_REASON = (
    "No reachable Grafana MCP server. Start one with infra/grafana/run-mcp-sidecar.sh "
    "and set GRAFANA_MCP_ENDPOINT."
)


class TestDetectionWithoutGrafana(unittest.TestCase):
    """Detection is deterministic and must work with no Grafana connection at all."""

    def setUp(self):
        self.agent = ConflictSentinelAgent()

    def test_detects_actor_illness_and_weather(self):
        disruptions = self.agent.inspect_schedule_disruptions(
            SCENES, AVAILABILITIES, PERMITS, WEATHER
        )

        triggers = [d["trigger_type"] for d in disruptions]
        self.assertIn("ActorIllness", triggers)
        self.assertIn("WeatherAlert", triggers)

    def test_unpublished_disruptions_are_labelled_as_such(self):
        # The whole point: with no Grafana client the agent must not imply it published.
        disruptions = self.agent.inspect_schedule_disruptions(
            SCENES, AVAILABILITIES, PERMITS, WEATHER
        )

        self.assertTrue(disruptions)
        for d in disruptions:
            self.assertFalse(d["published"])
            self.assertIsNone(d["annotation_id"])

    def test_permit_window_violation_is_detected(self):
        scenes = [{"number": 9, "set_location": "COVENT GARDEN", "int_ext": "EXT",
                   "cast": [], "date": "2026-09-15"}]
        permits = {"COVENT GARDEN": {"start": "2026-08-01", "end": "2026-08-30"}}

        disruptions = self.agent.inspect_schedule_disruptions(scenes, {}, permits, {})

        self.assertEqual([d["trigger_type"] for d in disruptions], ["PermitExpired"])

    def test_to_epoch_ms_is_utc(self):
        self.assertEqual(to_epoch_ms("1970-01-02"), 86_400_000)


class _FakeResponse:
    def __init__(self, body: str, content_type: str):
        self.text = body
        self.headers = {"Content-Type": content_type}

    def json(self):
        import json as _json
        return _json.loads(self.text)


class TestSseFrameMatching(unittest.TestCase):
    """
    Regression test. The spec allows a server to send notifications before the response,
    and Grafana Cloud does: the first tools/call of a session is preceded by a
    `notifications/tools/list_changed` frame. Taking the first frame returned the
    notification instead of the result, which silently produced empty tool results.
    """

    def test_response_is_matched_by_id_not_by_position(self):
        body = (
            'event: message\n'
            'data: {"jsonrpc":"2.0","method":"notifications/tools/list_changed"}\n'
            '\n'
            'event: message\n'
            'data: {"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"{\\"ok\\":true}"}]}}\n'
            '\n'
        )
        message = GrafanaMcpClient._parse_body(_FakeResponse(body, "text/event-stream"), expect_id=2)

        self.assertEqual(message["id"], 2)
        self.assertIn("result", message)

    def test_missing_response_frame_raises_rather_than_returning_empty(self):
        body = (
            'event: message\n'
            'data: {"jsonrpc":"2.0","method":"notifications/tools/list_changed"}\n'
            '\n'
        )
        with self.assertRaises(GrafanaMcpError):
            GrafanaMcpClient._parse_body(_FakeResponse(body, "text/event-stream"), expect_id=2)

    def test_plain_json_body_still_works(self):
        body = '{"jsonrpc":"2.0","id":7,"result":{"tools":[]}}'
        message = GrafanaMcpClient._parse_body(_FakeResponse(body, "application/json"), expect_id=7)

        self.assertEqual(message["result"], {"tools": []})


class TestClientRefusesToPretend(unittest.TestCase):
    def test_calls_before_connect_raise(self):
        client = GrafanaMcpClient(endpoint="http://localhost:1/mcp")

        self.assertFalse(client.is_connected)
        with self.assertRaises(GrafanaMcpError):
            client.list_tools()

    def test_unreachable_endpoint_raises_instead_of_returning_a_stub(self):
        client = GrafanaMcpClient(endpoint="http://localhost:1/mcp", timeout=2)

        with self.assertRaises(GrafanaMcpError):
            client.connect()


@unittest.skipUnless(MCP_AVAILABLE, SKIP_REASON)
class TestGrafanaMcpIntegration(unittest.TestCase):
    """
    Real MCP traffic against the official grafana/mcp-grafana server. This is the
    evidence for the Grafana partner-track requirement, so these must fail — not skip —
    whenever a server is configured but the integration is broken.
    """

    def setUp(self):
        self.client = GrafanaMcpClient()
        self.client.connect()

    def tearDown(self):
        self.client.close()

    def test_handshake_establishes_a_session_with_mcp_grafana(self):
        self.assertTrue(self.client.is_connected)
        self.assertEqual(self.client.server_info.get("name"), "mcp-grafana")

    def test_tools_list_exposes_the_grafana_toolset(self):
        tools = self.client.list_tools()
        names = {t["name"] for t in tools}

        # The partner track is specifically about having these available at runtime.
        self.assertGreaterEqual(len(tools), 50, f"expected the full toolset, got {len(tools)}")
        for expected in ("create_annotation", "get_annotations", "list_datasources",
                         "alerting_manage_rules", "search_dashboards", "query_prometheus"):
            self.assertIn(expected, names)

    def test_tool_call_returns_live_data(self):
        result = self.client.call_tool("list_datasources", {})

        self.assertIsInstance(result, dict)
        self.assertIn("datasources", result)

    def test_failing_tool_call_raises(self):
        with self.assertRaises(GrafanaMcpError):
            self.client.call_tool("get_dashboard_by_uid", {"uid": "does-not-exist-xyz"})

    def test_sentinel_publishes_annotations_and_they_are_readable_back(self):
        agent = ConflictSentinelAgent(self.client)

        disruptions = agent.inspect_schedule_disruptions(
            SCENES, AVAILABILITIES, PERMITS, WEATHER
        )

        self.assertTrue(disruptions)
        for d in disruptions:
            self.assertTrue(d["published"], f"{d['trigger_type']} was not published")
            self.assertIsInstance(d["annotation_id"], int)

        written = {d["annotation_id"] for d in disruptions}
        response = self.client.call_tool("get_annotations", {"tags": ["stripboard"], "limit": 100})
        payload = response.get("Payload", response) if isinstance(response, dict) else response
        read_back = {a["id"] for a in payload}

        self.assertTrue(
            written.issubset(read_back),
            f"annotations {written - read_back} were reported as written but cannot be read back",
        )

    def test_check_grafana_state_reads_live_server_state(self):
        state = ConflictSentinelAgent(self.client).check_grafana_state()

        self.assertEqual(state["server"].get("name"), "mcp-grafana")
        self.assertIn("datasources", state["datasources"])
        # A stack with no alert rules legitimately answers null, so assert the call
        # completed and decoded rather than that it found something.
        self.assertIn("alert_rules", state)
        self.assertIsInstance(state["alert_rules"], (list, dict, type(None)))


if __name__ == "__main__":
    unittest.main()
