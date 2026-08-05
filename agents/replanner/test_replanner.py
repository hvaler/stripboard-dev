import json
import os
import pathlib
import sys
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, os.path.dirname(__file__))

import replanner_agent
from replanner_agent import (
    ReplannerAgent, StripboardUnavailableError, _extract_options, consolidate_schedule,
    propose_replan,
)

CONSOLIDATION_RESPONSE = {
    "options": [
        {"title": "Leave it — the worst day visits 4 locations", "strategy": "as-scheduled",
         "isFeasible": True, "days": 2, "companyMoves": 8, "unionViolations": 0,
         "costUsd": 31800, "delta": {"extraShootDays": 0, "costDeltaUsd": 0}},
        {"title": "Consolidate — at most 2 location(s) a day", "strategy": "consolidate-locations",
         "isFeasible": True, "days": 4, "companyMoves": 4, "unionViolations": 0,
         "costUsd": 38200, "delta": {"extraShootDays": 2, "costDeltaUsd": 6400}},
    ],
}

SOLVER_RESPONSE = {
    "disruption": {"id": "d1", "trigger": "CastUnavailability", "description": "Holmes is ill"},
    "options": [
        {"title": "Option A", "strategy": "cover-day-swap", "isFeasible": True,
         "days": 3, "companyMoves": 8, "unionViolations": 0, "costUsd": 40100,
         "delta": {"extraShootDays": 0, "costDeltaUsd": -1500}},
    ],
}


class _FakeStripboard(BaseHTTPRequestHandler):
    """Stands in for the scheduling service. `mode` decides how it answers."""

    mode = "ok"
    last_payload = None
    last_path = None

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        _FakeStripboard.last_payload = json.loads(self.rfile.read(length) or b"{}")
        _FakeStripboard.last_path = self.path

        if _FakeStripboard.mode == "reject":
            body, status = json.dumps(
                {"error": "No cast member named 'Nobody'.", "known": ["Sherlock Holmes"]}), 400
        elif _FakeStripboard.mode == "nothing-to-consolidate":
            body, status = json.dumps(
                {"error": "No day visits more than 5 location(s) already — the worst is 4."}), 400
        elif self.path.endswith("/consolidate"):
            body, status = json.dumps(CONSOLIDATION_RESPONSE), 200
        else:
            body, status = json.dumps(SOLVER_RESPONSE), 200

        payload = body.encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

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


class TestProposeReplanTool(_ServedByFakeStripboard):
    """The tool is the only path to a number, so these tests are about what it does with one."""

    def test_the_disruption_is_forwarded_as_the_service_expects(self):
        propose_replan("CastUnavailability", "2026-08-10", 2, person_name="Sherlock Holmes")

        self.assertEqual(_FakeStripboard.last_payload, {
            "triggerType": "CastUnavailability", "startDate": "2026-08-10", "durationDays": 2,
            "personName": "Sherlock Holmes", "locationName": None, "description": None,
        })

    def test_solver_figures_are_returned_untouched(self):
        result = propose_replan("CastUnavailability", "2026-08-10", 1, person_name="Sherlock Holmes")

        self.assertEqual(result["options"][0]["costUsd"], 40100)
        self.assertEqual(result["options"][0]["delta"]["costDeltaUsd"], -1500)

    def test_a_rejected_request_returns_the_reason_for_the_model_to_act_on(self):
        # "No cast member named X, here are the known ones" is something the agent can
        # recover from. Raising would just end the conversation.
        _FakeStripboard.mode = "reject"

        result = propose_replan("CastUnavailability", "2026-08-10", 1, person_name="Nobody")

        self.assertIn("No cast member named", result["error"])
        self.assertIn("Sherlock Holmes", result["known"])

    def test_an_unreachable_service_raises_rather_than_inventing_a_plan(self):
        os.environ["STRIPBOARD_URL"] = "http://127.0.0.1:1"

        with self.assertRaises(StripboardUnavailableError):
            propose_replan("CastUnavailability", "2026-08-10", 1, person_name="Sherlock Holmes")


class TestConsolidateTool(_ServedByFakeStripboard):
    """
    A schedule-quality alert blocks no scene, so there is no disruption to absorb. This tool
    prices a constraint instead: what does obeying the cap cost?
    """

    def test_the_cap_is_sent_to_the_consolidation_endpoint(self):
        consolidate_schedule(2)

        self.assertTrue(_FakeStripboard.last_path.endswith("/api/schedule/consolidate"))
        self.assertEqual(_FakeStripboard.last_payload, {"maxLocationsPerDay": 2})

    def test_both_sides_of_the_trade_come_back(self):
        result = consolidate_schedule(2)

        as_is, consolidated = result["options"]
        self.assertEqual(as_is["delta"]["extraShootDays"], 0)
        self.assertEqual(consolidated["delta"]["extraShootDays"], 2)
        self.assertEqual(consolidated["companyMoves"], 4)

    def test_nothing_to_consolidate_returns_the_reason(self):
        # "Already fine" is a real answer. Returning a replan that changed nothing, dressed
        # up as an improvement, is not.
        _FakeStripboard.mode = "nothing-to-consolidate"

        result = consolidate_schedule(5)

        self.assertIn("the worst is 4", result["error"])
        self.assertNotIn("options", result)

    def test_an_unreachable_service_raises_rather_than_inventing_a_trade(self):
        os.environ["STRIPBOARD_URL"] = "http://127.0.0.1:1"

        with self.assertRaises(StripboardUnavailableError):
            consolidate_schedule(2)


class TestNoInventedFigures(unittest.TestCase):
    """
    Regression guard. This module used to return two hardcoded proposals with the literal
    figures $1,500 and $8,500, and called swapping two list elements "planning". The old
    test asserted those figures, so it passed while the agent produced fiction.
    """

    def test_the_module_body_contains_no_hardcoded_schedule_figures(self):
        source = pathlib.Path(replanner_agent.__file__).read_text(encoding="utf-8")
        body = source.split('"""', 2)[-1]  # the module docstring names the old figures on purpose

        for literal in ("1500.00", "8500.00", "1_500", "8_500", "estimated_cost_delta_usd"):
            self.assertNotIn(literal, body, f"{literal} looks like a figure the solver should produce")

    def test_every_tool_the_agent_has_goes_through_the_solver(self):
        agent = ReplannerAgent()

        self.assertEqual([tool.__name__ for tool in agent.agent.tools],
                         ["propose_replan", "consolidate_schedule"])


class TestOptionExtraction(unittest.TestCase):
    def test_options_are_found_however_adk_wraps_the_response(self):
        for payload in (
            SOLVER_RESPONSE,
            {"result": SOLVER_RESPONSE},
            {"response": SOLVER_RESPONSE},
            json.dumps(SOLVER_RESPONSE),
        ):
            self.assertEqual(len(_extract_options(payload)), 1, payload)

    def test_an_unrecognised_shape_yields_nothing_rather_than_guessing(self):
        self.assertEqual(_extract_options({"unexpected": 1}), [])
        self.assertEqual(_extract_options("not json"), [])


if __name__ == "__main__":
    unittest.main()
