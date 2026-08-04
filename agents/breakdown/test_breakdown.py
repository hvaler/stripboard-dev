import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(__file__))

import breakdown_agent
from breakdown_agent import BreakdownAgent
from fountain_parser import FountainParser, estimate_eighths
from gemini_client import GeminiClient, GeminiConfigError
from schema import validate_breakdown_dict, validate_breakdown_verbose

DEMO_DIR = os.path.join(os.path.dirname(__file__), "..", "..", "demo")
SHERLOCK = os.path.join(DEMO_DIR, "screenplay.fountain")
HARBOUR = os.path.join(DEMO_DIR, "screenplay-harbour.fountain")

GEMINI_AVAILABLE = GeminiClient.is_configured()
SKIP_REASON = (
    "No Google Cloud AI credentials. Set GOOGLE_CLOUD_PROJECT (with ADC) or GEMINI_API_KEY."
)


class _OfflineClient(GeminiClient):
    """Never reaches the network; forces the fallback path."""

    def generate_structured(self, *_args, **_kwargs):
        raise GeminiConfigError("offline test")


class TestFountainParser(unittest.TestCase):
    def test_extracts_scenes(self):
        with open(SHERLOCK, "r", encoding="utf-8") as f:
            scenes = FountainParser().parse(f.read())

        self.assertGreaterEqual(len(scenes), 4)
        self.assertEqual(scenes[0]["number"], 1)
        self.assertIn("BAKER STREET", scenes[0]["set_location"])

    def test_estimate_eighths_is_deterministic_and_positive(self):
        text = "A" * 600
        self.assertEqual(estimate_eighths(text), 10)
        self.assertEqual(estimate_eighths(text), estimate_eighths(text))
        # Never zero: a scene that exists occupies page space.
        self.assertGreater(estimate_eighths(""), 0)


class TestValidation(unittest.TestCase):
    def test_verbose_validator_explains_rejections(self):
        bad = {"scenes": [{"number": "one", "set_location": "", "int_ext": "INSIDE",
                           "day_night": "MIDNIGHT", "eighths": 0, "synopsis": "x",
                           "cast": None, "elements": None}]}
        ok, errors = validate_breakdown_verbose(bad)

        self.assertFalse(ok)
        joined = " ".join(errors)
        for expected in ["number", "set_location", "int_ext", "day_night", "eighths", "cast"]:
            self.assertIn(expected, joined)

    def test_verbose_validator_accepts_a_good_payload(self):
        good = {"scenes": [{"number": 1, "set_location": "TRAWLER DECK", "int_ext": "EXT",
                            "day_night": "DAWN", "eighths": 4, "synopsis": "Nell hauls a net.",
                            "cast": ["Nell Okonkwo"], "elements": [{"name": "Net", "category": "Prop"}]}]}
        ok, errors = validate_breakdown_verbose(good)

        self.assertTrue(ok, errors)


class TestFallbackPath(unittest.TestCase):
    """The fallback must stay usable, and must never look like a real extraction."""

    def setUp(self):
        self.agent = BreakdownAgent(client=_OfflineClient())

    def test_fallback_is_schema_valid_and_marked_as_fallback(self):
        result = self.agent.process_fountain_file(SHERLOCK, use_cache=False, allow_fallback=True)

        self.assertTrue(validate_breakdown_dict(result))
        self.assertEqual(result["source"], "fallback")
        self.assertGreaterEqual(len(result["scenes"]), 4)

    def test_fallback_leaves_cast_and_elements_empty(self):
        # Without a model there is no honest way to know who is in a scene, so the
        # fallback must not guess. This is what the pre-EV-18 keyword matching got wrong.
        result = self.agent.process_fountain_file(SHERLOCK, use_cache=False, allow_fallback=True)

        for scene in result["scenes"]:
            self.assertEqual(scene["cast"], [])
            self.assertEqual(scene["elements"], [])

    def test_allow_fallback_false_raises_instead_of_degrading(self):
        with self.assertRaises(GeminiConfigError):
            self.agent.process_fountain_file(SHERLOCK, use_cache=False, allow_fallback=False)


class TestCacheIsKeyedByScreenplay(unittest.TestCase):
    """
    Regression test. The cache used to be a single unkeyed file, so asking for one
    screenplay offline would silently hand back a different film's breakdown.
    """

    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.original = breakdown_agent.CACHE_FILE
        breakdown_agent.CACHE_FILE = os.path.join(self.tmp, "cache.json")

    def tearDown(self):
        breakdown_agent.CACHE_FILE = self.original
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_cache_miss_does_not_return_another_screenplay(self):
        agent = BreakdownAgent(client=_OfflineClient())

        # Seed the cache with the Sherlock breakdown...
        with open(SHERLOCK, "r", encoding="utf-8") as f:
            sherlock_key = breakdown_agent._screenplay_key(f.read())
        breakdown_agent._write_cache(
            sherlock_key,
            "screenplay.fountain",
            {"scenes": [{"number": 1, "set_location": "221B BAKER STREET", "int_ext": "INT",
                         "day_night": "DAY", "eighths": 3, "synopsis": "Holmes.",
                         "cast": ["Sherlock Holmes"], "elements": []}]},
        )

        # ...then ask for a different screenplay offline.
        result = agent.process_fountain_file(HARBOUR, use_cache=True, allow_fallback=True)

        self.assertEqual(result["source"], "fallback")
        locations = {s["set_location"] for s in result["scenes"]}
        self.assertNotIn("221B BAKER STREET", locations)
        self.assertIn("TRAWLER DECK", locations)

    def test_cache_hit_replays_the_same_screenplay(self):
        agent = BreakdownAgent(client=_OfflineClient())
        with open(HARBOUR, "r", encoding="utf-8") as f:
            key = breakdown_agent._screenplay_key(f.read())
        breakdown_agent._write_cache(
            key,
            "screenplay-harbour.fountain",
            {"scenes": [{"number": 1, "set_location": "TRAWLER DECK", "int_ext": "EXT",
                         "day_night": "DAWN", "eighths": 4, "synopsis": "Nell hauls a net.",
                         "cast": ["Nell Okonkwo"], "elements": []}]},
        )

        result = agent.process_fountain_file(HARBOUR, use_cache=True, allow_fallback=True)

        self.assertEqual(result["source"], "cache")
        self.assertEqual(result["scenes"][0]["cast"], ["Nell Okonkwo"])


@unittest.skipUnless(GEMINI_AVAILABLE, SKIP_REASON)
class TestGeminiExtraction(unittest.TestCase):
    """
    Integration tests: these make real Gemini calls through Google Cloud. They are the
    evidence that the Stage One 'Google Cloud AI at runtime' requirement is met, so they
    must fail — not skip — whenever credentials are present but extraction is broken.
    """

    def test_extraction_is_real_and_labelled_gemini(self):
        result = BreakdownAgent().process_fountain_file(SHERLOCK, use_cache=False, allow_fallback=False)

        self.assertEqual(result["source"], "gemini")
        self.assertTrue(result["model"].startswith("gemini-"))
        self.assertGreater(result["total_tokens"], 0)
        self.assertTrue(validate_breakdown_dict(result))

    def test_extraction_generalises_to_an_unrelated_screenplay(self):
        # HARBOUR shares no characters, locations or props with the demo script, so a
        # correct result here cannot come from anything hardcoded.
        result = BreakdownAgent().process_fountain_file(HARBOUR, use_cache=False, allow_fallback=False)

        self.assertEqual(result["source"], "gemini")
        self.assertEqual(len(result["scenes"]), 4)

        all_cast = {name for scene in result["scenes"] for name in scene["cast"]}
        self.assertTrue(
            any("Nell" in name for name in all_cast),
            f"expected the protagonist to be identified, got {all_cast}",
        )
        self.assertTrue(
            any(scene["elements"] for scene in result["scenes"]),
            "expected at least one production element to be extracted",
        )

    def test_scene_numbering_and_page_length_stay_deterministic(self):
        result = BreakdownAgent().process_fountain_file(HARBOUR, use_cache=False, allow_fallback=False)

        with open(HARBOUR, "r", encoding="utf-8") as f:
            raw = FountainParser().parse(f.read())
        expected_eighths = {s["number"]: estimate_eighths(s["raw_content"]) for s in raw}

        self.assertEqual([s["number"] for s in result["scenes"]], sorted(expected_eighths))
        for scene in result["scenes"]:
            # eighths is measured from the script, never taken from the model.
            self.assertEqual(scene["eighths"], expected_eighths[scene["number"]])


if __name__ == "__main__":
    unittest.main()
