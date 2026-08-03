import unittest
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))

from grafana_mcp_client import GrafanaMcpClient
from sentinel_agent import ConflictSentinelAgent

class TestConflictSentinelAgent(unittest.TestCase):
    def setUp(self):
        self.grafana_client = GrafanaMcpClient()
        self.agent = ConflictSentinelAgent(self.grafana_client)

    def test_grafana_mcp_client_queries_tools_and_posts_annotations(self):
        result = self.grafana_client.query_tool("get_alerts", {})
        self.assertEqual(result["status"], "ok")

        success = self.grafana_client.post_annotation("Test Annotation", ["test"])
        self.assertTrue(success)

    def test_sentinel_detects_actor_illness_and_weather_disruptions(self):
        scenes = [
            {
                "number": 1,
                "set_location": "221B BAKER STREET",
                "int_ext": "INT",
                "cast": ["Sherlock Holmes"],
                "date": "2026-08-10"
            },
            {
                "number": 4,
                "set_location": "TOWER BRIDGE WHARF",
                "int_ext": "EXT",
                "cast": ["Sherlock Holmes", "Prof. James Moriarty"],
                "date": "2026-08-11"
            }
        ]

        availabilities = {
            "Sherlock Holmes": ["2026-08-10"]  # Unavail on Aug 10
        }

        permits = {
            "221B BAKER STREET": {"start": "2026-08-01", "end": "2026-08-30"},
            "TOWER BRIDGE WHARF": {"start": "2026-08-01", "end": "2026-08-30"}
        }

        weather = {
            "TOWER BRIDGE WHARF": {"condition": "Rain", "precipitation_probability": 90}
        }

        disruptions = self.agent.inspect_schedule_disruptions(scenes, availabilities, permits, weather)

        self.assertGreaterEqual(len(disruptions), 2)
        triggers = [d["trigger_type"] for d in disruptions]
        self.assertIn("ActorIllness", triggers)
        self.assertIn("WeatherAlert", triggers)

if __name__ == "__main__":
    unittest.main()
