import unittest
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))

from fountain_parser import FountainParser
from schema import BREAKDOWN_JSON_SCHEMA, validate_breakdown_dict
from breakdown_agent import BreakdownAgent

class TestBreakdownAgent(unittest.TestCase):
    def setUp(self):
        self.sample_fountain = os.path.join(os.path.dirname(__file__), "..", "..", "demo", "screenplay.fountain")

    def test_fountain_parser_extracts_scenes(self):
        parser = FountainParser()
        with open(self.sample_fountain, "r", encoding="utf-8") as f:
            content = f.read()

        scenes = parser.parse(content)
        self.assertGreaterEqual(len(scenes), 4)
        self.assertEqual(scenes[0]["number"], 1)
        self.assertIn("BAKER STREET", scenes[0]["set_location"])

    def test_breakdown_agent_produces_schema_valid_output(self):
        agent = BreakdownAgent()
        result = agent.process_fountain_file(self.sample_fountain, use_cache=False)

        # Validate against JSON Schema validator
        is_valid = validate_breakdown_dict(result)
        self.assertTrue(is_valid, "Extracted screenplay breakdown should pass schema validation")
        self.assertIn("scenes", result)
        self.assertGreaterEqual(len(result["scenes"]), 4)

        # Verify extracted cast
        second_scene = result["scenes"][1] # INT. 221B BAKER STREET
        self.assertIn("Sherlock Holmes", second_scene["cast"])

if __name__ == "__main__":
    unittest.main()
