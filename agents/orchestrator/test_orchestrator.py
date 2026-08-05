import json
import os
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(__file__))

from orchestrator_agent import (  # noqa: E402
    Orchestrator, build_orchestrator, commit_schedule, get_schedule,
)

SCHEDULE = {
    "versionId": "6f1c3b2a-0000-4000-8000-000000000001",
    "versionNumber": 3,
    "isCommitted": True,
    "days": 3,
    "companyMoves": 8,
    "unionViolations": 0,
    "costUsd": 41600,
    "scenes": 12,
    "locations": 5,
    "schedule": [{"dayNumber": 1, "date": "2026-08-10", "unit": "day",
                  "call": "07:00", "wrap": "19:00", "locations": ["221B BAKER STREET"],
                  "scenes": [1, 2]}],
}


class _FakeStripboard(BaseHTTPRequestHandler):
    """Stands in for the .NET service. `mode` decides how it answers."""

    mode = "ok"
    last_commit = None

    def _reply(self, status, body):
        payload = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def do_GET(self):
        if _FakeStripboard.mode == "empty":
            self._reply(404, {"error": "No schedule exists yet. Import a screenplay breakdown first."})
        else:
            self._reply(200, SCHEDULE)

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        _FakeStripboard.last_commit = json.loads(self.rfile.read(length) or b"{}")
        identity = _FakeStripboard.last_commit.get("identity", "")

        if identity == "producer":
            self._reply(200, {"committed": True, "versionNumber": 3, "days": 3, "costUsd": 41600})
        else:
            self._reply(403, {"committed": False, "error": (
                f"'{identity}' cannot commit a schedule. Only the Producer role may commit "
                "— agents propose, humans decide.")})

    def log_message(self, *_args):
        pass


class _ServedByFakeStripboard(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), _FakeStripboard)
        cls.port = cls.server.server_address[1]
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        _FakeStripboard.mode = "ok"
        os.environ["STRIPBOARD_URL"] = f"http://127.0.0.1:{self.port}"

    def tearDown(self):
        os.environ.pop("STRIPBOARD_URL", None)


class TestGetScheduleTool(_ServedByFakeStripboard):
    def test_the_committed_board_is_returned_as_the_service_reported_it(self):
        result = get_schedule()

        self.assertEqual(result["days"], 3)
        self.assertEqual(result["companyMoves"], 8)
        self.assertEqual(result["costUsd"], 41600)

    def test_no_schedule_yet_says_so_rather_than_returning_an_empty_board(self):
        # An empty board would read as "a shoot with zero days", which is a different
        # and much worse answer than "nothing has been scheduled".
        _FakeStripboard.mode = "empty"

        result = get_schedule()

        self.assertIn("No schedule exists yet", result["error"])
        self.assertNotIn("days", result)

    def test_an_unreachable_service_is_an_error_the_agent_must_report(self):
        os.environ["STRIPBOARD_URL"] = "http://127.0.0.1:1"

        result = get_schedule()

        self.assertIn("unreachable", result["error"])


class TestCommitAuthority(_ServedByFakeStripboard):
    """
    The point of the governance sub-agent: it is allowed to try, and it is refused.
    Authority lives in the service, not in the prompt.
    """

    def test_the_producer_commits(self):
        result = commit_schedule(SCHEDULE["versionId"], "producer")

        self.assertTrue(result["committed"])
        self.assertEqual(result["versionNumber"], 3)

    def test_an_agent_identity_is_refused_and_told_why(self):
        result = commit_schedule(SCHEDULE["versionId"], "sa-stripboard-replanner")

        self.assertFalse(result["committed"])
        self.assertIn("Only the Producer role may commit", result["error"])

    def test_the_refusal_is_returned_not_swallowed(self):
        # A False return with no reason would let the model narrate a plausible failure.
        result = commit_schedule(SCHEDULE["versionId"], "sentinel")

        self.assertIn("sentinel", result["error"])


class TestAgentTree(unittest.TestCase):
    def test_the_root_has_no_tools_so_it_cannot_answer_on_its_own(self):
        root = build_orchestrator()

        self.assertEqual(root.tools, [])
        self.assertEqual(len(root.sub_agents), 3)

    def test_each_specialist_owns_exactly_the_tool_its_job_needs(self):
        root = build_orchestrator()
        owned = {agent.name: [tool.__name__ for tool in agent.tools] for agent in root.sub_agents}

        self.assertEqual(owned, {
            "scheduler": ["get_schedule"],
            # Two, because a disruption and a poor schedule need different answers: one
            # absorbs blocked scene-dates, the other prices a tighter constraint.
            "replanner": ["propose_replan", "consolidate_schedule"],
            "governance": ["commit_schedule"],
        })

    def test_every_specialist_has_a_description_because_routing_depends_on_it(self):
        # ADK picks the transfer target from these descriptions. An empty one makes the
        # sub-agent unreachable, which fails silently as "the root answered instead".
        for agent in build_orchestrator().sub_agents:
            self.assertTrue(agent.description.strip(), agent.name)

    def test_vertex_is_selected_when_a_project_is_configured(self):
        previous = {key: os.environ.get(key)
                    for key in ("GOOGLE_CLOUD_PROJECT", "GOOGLE_GENAI_USE_VERTEXAI")}
        os.environ["GOOGLE_CLOUD_PROJECT"] = "stripboard-hack"
        os.environ.pop("GOOGLE_GENAI_USE_VERTEXAI", None)
        try:
            Orchestrator()
            self.assertEqual(os.environ["GOOGLE_GENAI_USE_VERTEXAI"], "TRUE")
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value


if __name__ == "__main__":
    unittest.main()
