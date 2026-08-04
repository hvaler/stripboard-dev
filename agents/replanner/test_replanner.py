import unittest
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))

from replanner_agent import ReplannerAgent

class TestReplannerAgent(unittest.TestCase):
    def setUp(self):
        self.agent = ReplannerAgent()
        self.mock_disruption = {
            "trigger_type": "ActorIllness",
            "description": "Sherlock Holmes is unavailable on 2026-08-10 due to flu.",
            "expected_duration_days": 2
        }
        self.mock_schedule = [
            {"number": 1, "set_location": "221B BAKER STREET", "int_ext": "INT"},
            {"number": 2, "set_location": "TOWER BRIDGE WHARF", "int_ext": "EXT"}
        ]

    def test_generate_replan_proposals_creates_two_distinct_options(self):
        proposals = self.agent.generate_replan_proposals(self.mock_disruption, self.mock_schedule)

        self.assertEqual(len(proposals), 2)

        # Check Option A (No extra shoot days)
        opt_a = proposals[0]
        self.assertEqual(opt_a["cost_deltas"]["extra_shoot_days"], 0)
        self.assertIn("Interior Cover Day", opt_a["title"])
        self.assertIn("ActorIllness", opt_a["justification"])

        # Check Option B (Extra shoot day)
        opt_b = proposals[1]
        self.assertEqual(opt_b["cost_deltas"]["extra_shoot_days"], 1)
        self.assertIn("Standby Rest Day", opt_b["title"])
        self.assertIn("$8,500", opt_b["justification"])

    def test_register_draft_proposal_returns_draft_status(self):
        proposals = self.agent.generate_replan_proposals(self.mock_disruption, self.mock_schedule)
        result = self.agent.register_draft_proposal(proposals[0])

        self.assertEqual(result["status"], "registered")
        self.assertEqual(result["version_status"], "draft")

if __name__ == "__main__":
    unittest.main()
