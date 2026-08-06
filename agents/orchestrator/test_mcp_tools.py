"""
Tests for the ADK tools discovered from our own MCP servers (EV-23).

The fake server below is deliberately *stateless* — it completes `initialize` and returns no
`Mcp-Session-Id`, exactly as `Stripboard.Mcp.*` does with `options.Stateless = true`. The
client this replaced treated a missing session id as "not connected" and refused to speak to
our own servers at all, so that is the first thing asserted here.

The integration test at the bottom talks to a real `mcp-schedule`. It fails rather than skips
when the endpoint is configured and broken, matching every other integration test in this
repo: a test that skips on a misconfigured service reports green for a system that does not
work.
"""

import asyncio
import json
import os
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "common")))

from mcp_client import McpError, sanitise_schema  # noqa: E402
from mcp_tools import (  # noqa: E402
    GOVERNANCE_TOOLS, SCHEDULER_TOOLS, McpBackedTool, StripboardMcpClient, StripboardMcpToolset,
)

TOOLS = [
    {
        "name": "get_schedule",
        "description": "Read a shooting schedule.",
        # A `Guid?` in C# arrives as a union type. Gemini has no union type.
        "inputSchema": {
            "type": "object",
            "properties": {
                "versionId": {
                    "description": "Schedule version id.",
                    "type": ["string", "null"],
                    "format": "uuid",
                    "default": None,
                },
            },
        },
    },
    {
        "name": "validate_rules",
        "description": "Check a schedule against the union rules.",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "commit_schedule",
        "description": "Commit a draft version. Only a human Producer may.",
        "inputSchema": {
            "type": "object",
            "properties": {"versionId": {"type": "string"}},
            "required": ["versionId"],
        },
    },
]

REFUSAL = ("'Producer' claims the Producer role but nothing verified it. A commit requires "
           "an authenticated caller.")


class _FakeMcpServer(BaseHTTPRequestHandler):
    """A stateless MCP server: no Mcp-Session-Id, ever."""

    def log_message(self, *_args):  # keep the test output readable
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"] or 0)) or b"{}")
        method = body.get("method")

        if method == "notifications/initialized":
            self.send_response(202)
            self.end_headers()
            return

        if method == "initialize":
            result = {
                "protocolVersion": "2025-06-18",
                "serverInfo": {"name": "Stripboard.Mcp.Schedule", "version": "1.0.0.0"},
                "capabilities": {"tools": {}},
            }
        elif method == "tools/list":
            result = {"tools": TOOLS}
        elif method == "tools/call":
            name = body["params"]["name"]
            if name == "commit_schedule":
                result = {"isError": True, "content": [{"type": "text", "text": REFUSAL}]}
            else:
                result = {"content": [{"type": "text", "text": json.dumps(
                    {"versionId": "v1", "days": 3, "called": name})}]}
        else:
            result = {}

        payload = json.dumps({"jsonrpc": "2.0", "id": body.get("id"), "result": result}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)


class McpToolsetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), _FakeMcpServer)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.endpoint = f"http://127.0.0.1:{cls.server.server_address[1]}/mcp"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def toolset(self) -> StripboardMcpToolset:
        return StripboardMcpToolset(endpoint=self.endpoint).connect()

    def test_a_stateless_server_is_still_connected(self):
        # The bug this replaced: `is_connected` was `session_id is not None`, so a stateless
        # server — which is every server we wrote — looked permanently disconnected and every
        # call raised "Not connected" before reaching the wire.
        with self.toolset() as ts:
            self.assertTrue(ts.client.is_connected)
            self.assertIsNone(ts.client.session_id)
            self.assertEqual(ts.client.server_info["name"], "Stripboard.Mcp.Schedule")

    def test_tools_come_from_the_server_not_from_this_file(self):
        with self.toolset() as ts:
            self.assertEqual(sorted(ts.tool_names),
                             ["commit_schedule", "get_schedule", "validate_rules"])

    def test_a_union_type_becomes_a_nullable_type_gemini_accepts(self):
        # `"type": ["string", "null"]` is legal JSON Schema and invalid to Gemini, which
        # rejects the whole declaration rather than the one field. Left unhandled, the
        # scheduler would have had no tools at all.
        with self.toolset() as ts:
            declaration = {t.name: t for t in ts.tools()}["get_schedule"]._get_declaration()

        version = declaration.parameters.properties["versionId"]
        self.assertEqual(getattr(version.type, "value", version.type), "STRING")
        self.assertTrue(version.nullable)

    def test_the_schema_adapter_drops_vocabulary_gemini_rejects(self):
        cleaned = sanitise_schema({"type": "object", "$schema": "x", "additionalProperties": False,
                                   "properties": {"n": {"type": "integer", "default": 3}}})
        self.assertNotIn("$schema", cleaned)
        self.assertNotIn("additionalProperties", cleaned)
        self.assertNotIn("default", cleaned["properties"]["n"])

    def test_an_allowlist_that_names_a_missing_tool_raises(self):
        # A rename in C# must not silently shrink an agent's toolset. The first symptom would
        # be a confident answer that skipped a step it could no longer take.
        with self.toolset() as ts:
            with self.assertRaises(McpError):
                ts.tools(["get_schedule", "no_such_tool"])

    def test_the_specialists_get_exactly_the_tools_their_role_allows(self):
        with self.toolset() as ts:
            self.assertEqual([t.name for t in ts.tools(SCHEDULER_TOOLS)],
                             ["get_schedule", "validate_rules"])
            self.assertEqual([t.name for t in ts.tools(GOVERNANCE_TOOLS)], ["commit_schedule"])
            # The scheduler cannot even attempt a commit; governance can, and is refused.
            self.assertNotIn("commit_schedule", [t.name for t in ts.tools(SCHEDULER_TOOLS)])

    def test_a_tool_call_returns_the_servers_result(self):
        with self.toolset() as ts:
            tool = {t.name: t for t in ts.tools()}["get_schedule"]
            result = asyncio.run(tool.run_async(args={}, tool_context=None))
        self.assertEqual(result["days"], 3)
        self.assertEqual(result["called"], "get_schedule")

    def test_a_refused_commit_reaches_the_model_as_a_reason_not_an_exception(self):
        # The refusal is the most important thing this system says. Raising past the agent
        # would turn "you are not allowed" into "something went wrong", and the agent would
        # retry rather than report it.
        with self.toolset() as ts:
            tool = {t.name: t for t in ts.tools()}["commit_schedule"]
            result = asyncio.run(tool.run_async(args={"versionId": "v1"}, tool_context=None))

        self.assertIn("error", result)
        self.assertIn("nothing verified it", result["error"])
        self.assertNotIn("committed", result)

    def test_a_server_offering_no_tools_is_an_error_not_an_empty_agent(self):
        # An agent with no tools still answers, from nothing. Better to fail at startup.
        class _Empty(StripboardMcpClient):
            def list_tools(self):
                return []

        with self.assertRaises(McpError):
            StripboardMcpToolset(client=_Empty(endpoint=self.endpoint)).connect()


class LiveScheduleServerTests(unittest.TestCase):
    """
    Against a real `mcp-schedule`:

        dotnet run --project src/Stripboard.Mcp.Schedule
        export STRIPBOARD_MCP_SCHEDULE_ENDPOINT=http://localhost:5067/mcp
    """

    @classmethod
    def setUpClass(cls):
        if not os.getenv("STRIPBOARD_MCP_SCHEDULE_ENDPOINT"):
            raise unittest.SkipTest("STRIPBOARD_MCP_SCHEDULE_ENDPOINT is not set")
        # Configured but broken must fail, not skip.
        cls.toolset = StripboardMcpToolset().connect()

    @classmethod
    def tearDownClass(cls):
        if getattr(cls, "toolset", None):
            cls.toolset.close()

    def test_the_real_server_offers_the_tools_the_specialists_need(self):
        for name in SCHEDULER_TOOLS + GOVERNANCE_TOOLS:
            self.assertIn(name, self.toolset.tool_names)

    def test_every_declaration_the_real_server_produces_is_valid_for_gemini(self):
        for tool in self.toolset.tools():
            self.assertIsNotNone(tool._get_declaration())

    def test_claiming_to_be_the_producer_does_not_make_you_one(self):
        # Reads the committed schedule rather than creating a draft, because creating one is
        # itself authorised and the answer differs by environment. Locally the platform
        # proves nothing, so the payload identity is taken at face value and `create_schedule`
        # succeeds; on Cloud Run the caller is whoever Google says they are, and an account
        # holding no scheduling role is told it "is not permitted to run the solver".
        #
        # What must hold in BOTH is this: sending identity="Producer" never commits anything.
        tools = {t.name: t for t in self.toolset.tools()}

        board = asyncio.run(tools["get_schedule"].run_async(args={}, tool_context=None))
        self.assertIn("versionId", board, f"no committed schedule to work from: {board}")

        refused = asyncio.run(tools["commit_schedule"].run_async(
            args={"versionId": board["versionId"], "identity": "Producer"}, tool_context=None))

        self.assertIn("error", refused)
        self.assertNotIn("committed", refused)
        # The wording differs between environments — "nothing verified it" locally,
        # "'you@example.com' cannot commit" on Cloud Run — but both refuse on the same
        # grounds, and asserting the grounds rather than the sentence is the point.
        self.assertIn("Producer role", refused["error"])


if __name__ == "__main__":
    unittest.main()
