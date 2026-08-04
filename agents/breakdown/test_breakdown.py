import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(__file__))

import breakdown_agent
from breakdown_agent import BreakdownAgent, screenplay_key
from screenplay_sources import (
    UnsupportedScreenplayError, fdx_to_text, load_screenplay,
)
from fountain_parser import FountainParser, estimate_eighths, split_heading
from gemini_client import GeminiClient, GeminiConfigError
from schema import validate_breakdown_dict, validate_breakdown_verbose

DEMO_DIR = os.path.join(os.path.dirname(__file__), "..", "..", "demo")
SHERLOCK = os.path.join(DEMO_DIR, "screenplay.fountain")
HARBOUR = os.path.join(DEMO_DIR, "screenplay-harbour.fountain")
METROPOLE_FDX = os.path.join(DEMO_DIR, "screenplay-metropole.fdx")
METROPOLE_PDF = os.path.join(DEMO_DIR, "screenplay-metropole.pdf")

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


class TestSceneHeadings(unittest.TestCase):
    """
    The old parser took the last hyphen-separated segment as the time of day, whatever it
    was, so "INT. BAKER STREET - SITTING ROOM" produced day_night="SITTING ROOM".
    """

    def test_a_trailing_set_name_is_not_mistaken_for_a_time_of_day(self):
        where, when = split_heading("BAKER STREET - SITTING ROOM")

        self.assertEqual(where, "BAKER STREET - SITTING ROOM")
        self.assertEqual(when, "DAY")

    def test_a_real_time_of_day_is_taken_and_normalised(self):
        self.assertEqual(split_heading("TRAWLER DECK - DAWN"), ("TRAWLER DECK", "DAWN"))
        self.assertEqual(split_heading("HOTEL - EVENING"), ("HOTEL", "NIGHT"))
        self.assertEqual(split_heading("ALLEY - MAGIC HOUR"), ("ALLEY", "DUSK"))

    def test_a_set_and_a_time_of_day_are_both_kept(self):
        where, when = split_heading("221B BAKER STREET - SITTING ROOM - DAY")

        self.assertEqual(where, "221B BAKER STREET - SITTING ROOM")
        self.assertEqual(when, "DAY")

    def test_continuous_inherits_the_previous_scene_time(self):
        where, when = split_heading("CORRIDOR - CONTINUOUS", previous_day_night="NIGHT")

        self.assertEqual(where, "CORRIDOR")
        self.assertEqual(when, "NIGHT")

    def test_int_ext_variants_are_normalised(self):
        scenes = FountainParser().parse(
            "INT./EXT. CAR - DAY\nDriving.\n\nI/E. BOAT - NIGHT\nSailing.\n")

        self.assertEqual([s["int_ext"] for s in scenes], ["INT/EXT", "INT/EXT"])


class TestFinalDraft(unittest.TestCase):
    def test_fdx_is_parsed_into_scenes(self):
        loaded = load_screenplay(METROPOLE_FDX)

        self.assertEqual(loaded.source_format, "final-draft")
        self.assertEqual(len(loaded.scenes), 5)
        self.assertEqual(loaded.scenes[0]["int_ext"], "INT")
        self.assertEqual(loaded.scenes[0]["day_night"], "DAY")
        self.assertIn("HOTEL METROPOLE", loaded.scenes[0]["set_location"])

    def test_styled_runs_within_a_line_are_joined(self):
        # Final Draft splits a line into several <Text> runs whenever styling changes.
        xml = ('<FinalDraft><Content>'
               '<Paragraph Type="Scene Heading"><Text>INT. </Text><Text>KITCHEN</Text>'
               '<Text> - DAY</Text></Paragraph>'
               '<Paragraph Type="Action"><Text>She cooks.</Text></Paragraph>'
               '</Content></FinalDraft>')

        self.assertIn("INT. KITCHEN - DAY", fdx_to_text(xml))

    def test_a_malicious_fdx_is_refused_rather_than_expanded(self):
        # Billion laughs. The stdlib XML parser would expand this; defusedxml refuses.
        bomb = ('<?xml version="1.0"?><!DOCTYPE lolz [<!ENTITY lol "lol">'
                '<!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">]>'
                '<FinalDraft><Content><Paragraph Type="Action"><Text>&lol2;</Text>'
                '</Paragraph></Content></FinalDraft>')

        with self.assertRaises(UnsupportedScreenplayError):
            fdx_to_text(bomb)

    def test_an_unsupported_format_says_what_is_supported(self):
        with self.assertRaises(UnsupportedScreenplayError) as ctx:
            load_screenplay("script.docx")

        self.assertIn(".fountain", str(ctx.exception))

    def test_reading_a_pdf_without_gemini_explains_why(self):
        with self.assertRaises(UnsupportedScreenplayError) as ctx:
            load_screenplay(METROPOLE_PDF, gemini_client=None)

        self.assertIn("no text layer", str(ctx.exception))


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
        good = {"scenes": [{"number": 1, "set_location": "TRAWLER DECK", "location": "TRAWLER DECK", "int_ext": "EXT",
                            "day_night": "DAWN", "eighths": 4, "synopsis": "Nell hauls a net.",
                            "cast": ["Nell Okonkwo"], "elements": [{"name": "Net", "category": "Prop"}]}]}
        ok, errors = validate_breakdown_verbose(good)

        self.assertTrue(ok, errors)


class TestFallbackPath(unittest.TestCase):
    """The fallback must stay usable, and must never look like a real extraction."""

    def setUp(self):
        self.agent = BreakdownAgent(client=_OfflineClient())

    def test_fallback_is_schema_valid_and_marked_as_fallback(self):
        result = self.agent.process_screenplay(SHERLOCK, use_cache=False, allow_fallback=True)

        self.assertTrue(validate_breakdown_dict(result))
        self.assertEqual(result["source"], "fallback")
        self.assertGreaterEqual(len(result["scenes"]), 4)

    def test_fallback_leaves_cast_and_elements_empty(self):
        # Without a model there is no honest way to know who is in a scene, so the
        # fallback must not guess. This is what the pre-EV-18 keyword matching got wrong.
        result = self.agent.process_screenplay(SHERLOCK, use_cache=False, allow_fallback=True)

        for scene in result["scenes"]:
            self.assertEqual(scene["cast"], [])
            self.assertEqual(scene["elements"], [])

    def test_allow_fallback_false_raises_instead_of_degrading(self):
        with self.assertRaises(GeminiConfigError):
            self.agent.process_screenplay(SHERLOCK, use_cache=False, allow_fallback=False)


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
        sherlock_key = screenplay_key(load_screenplay(SHERLOCK).scenes)
        breakdown_agent._write_cache(
            sherlock_key,
            "screenplay.fountain",
            {"scenes": [{"number": 1, "set_location": "221B BAKER STREET", "location": "221B BAKER STREET",
                         "int_ext": "INT", "day_night": "DAY", "eighths": 3, "synopsis": "Holmes.",
                         "cast": ["Sherlock Holmes"], "elements": []}]},
        )

        # ...then ask for a different screenplay offline.
        result = agent.process_screenplay(HARBOUR, use_cache=True, allow_fallback=True)

        self.assertEqual(result["source"], "fallback")
        locations = {s["set_location"] for s in result["scenes"]}
        self.assertNotIn("221B BAKER STREET", locations)
        self.assertIn("TRAWLER DECK", locations)

    def test_cache_hit_replays_the_same_screenplay(self):
        agent = BreakdownAgent(client=_OfflineClient())
        key = screenplay_key(load_screenplay(HARBOUR).scenes)
        breakdown_agent._write_cache(
            key,
            "screenplay-harbour.fountain",
            {"scenes": [{"number": 1, "set_location": "TRAWLER DECK", "location": "TRAWLER DECK",
                         "int_ext": "EXT", "day_night": "DAWN", "eighths": 4, "synopsis": "Nell hauls a net.",
                         "cast": ["Nell Okonkwo"], "elements": []}]},
        )

        result = agent.process_screenplay(HARBOUR, use_cache=True, allow_fallback=True)

        self.assertEqual(result["source"], "cache")
        self.assertEqual(result["scenes"][0]["cast"], ["Nell Okonkwo"])


@unittest.skipUnless(GEMINI_AVAILABLE, SKIP_REASON)
class TestGeminiExtraction(unittest.TestCase):
    """
    Integration tests: these make real Gemini calls through Google Cloud. They are the
    evidence that the Stage One 'Google Cloud AI at runtime' requirement is met, so they
    must fail — not skip — whenever credentials are present but extraction is broken.
    """

    def test_two_sets_at_one_location_are_not_two_locations(self):
        # The EV-28 judgement: the hotel lobby and room 402 are one place to park the
        # trucks, while two streets across a city are two places even though their
        # headings share a prefix. Getting this wrong makes the schedule overstate cost.
        result = BreakdownAgent().process_screenplay(METROPOLE_FDX, use_cache=False, allow_fallback=False)

        by_number = {s["number"]: s for s in result["scenes"]}
        hotel_scenes = [s for s in result["scenes"] if "METROPOLE" in s["location"].upper()]

        self.assertGreaterEqual(len(hotel_scenes), 3, "lobby, room 402 and the return to the lobby")
        self.assertEqual(
            len({s["location"] for s in hotel_scenes}), 1,
            "every scene inside the hotel must share one location")

        locations = {s["location"] for s in result["scenes"]}
        self.assertLess(
            len(locations), len(result["scenes"]),
            "a five-scene script that revisits a location must not report five locations")
        self.assertNotEqual(
            by_number[3]["location"], by_number[4]["location"],
            "the riverside walk and the market square are different places")

    def test_a_pdf_screenplay_is_read_through_gemini_multimodal(self):
        result = BreakdownAgent().process_screenplay(METROPOLE_PDF, use_cache=False, allow_fallback=False)

        self.assertEqual(result["source_format"], "pdf-gemini")
        self.assertGreater(result.get("transcription_tokens", 0), 0)
        self.assertEqual(len(result["scenes"]), 5)
        self.assertTrue(validate_breakdown_dict(result))

        all_cast = {name for scene in result["scenes"] for name in scene["cast"]}
        self.assertTrue(any("Ines" in name for name in all_cast), f"got {all_cast}")

    def test_the_same_script_as_fdx_and_as_pdf_agree_on_scene_count(self):
        # Different route in, same film out. A disagreement here means the PDF
        # transcription dropped or invented a scene.
        from_fdx = BreakdownAgent().process_screenplay(METROPOLE_FDX, use_cache=False, allow_fallback=False)
        from_pdf = BreakdownAgent().process_screenplay(METROPOLE_PDF, use_cache=False, allow_fallback=False)

        self.assertEqual(len(from_fdx["scenes"]), len(from_pdf["scenes"]))
        self.assertEqual(
            [s["int_ext"] for s in from_fdx["scenes"]],
            [s["int_ext"] for s in from_pdf["scenes"]])

    def test_extraction_is_real_and_labelled_gemini(self):
        result = BreakdownAgent().process_screenplay(SHERLOCK, use_cache=False, allow_fallback=False)

        self.assertEqual(result["source"], "gemini")
        self.assertTrue(result["model"].startswith("gemini-"))
        self.assertGreater(result["total_tokens"], 0)
        self.assertTrue(validate_breakdown_dict(result))

    def test_extraction_generalises_to_an_unrelated_screenplay(self):
        # HARBOUR shares no characters, locations or props with the demo script, so a
        # correct result here cannot come from anything hardcoded.
        result = BreakdownAgent().process_screenplay(HARBOUR, use_cache=False, allow_fallback=False)

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
        result = BreakdownAgent().process_screenplay(HARBOUR, use_cache=False, allow_fallback=False)

        with open(HARBOUR, "r", encoding="utf-8") as f:
            raw = FountainParser().parse(f.read())
        expected_eighths = {s["number"]: estimate_eighths(s["raw_content"]) for s in raw}

        self.assertEqual([s["number"] for s in result["scenes"]], sorted(expected_eighths))
        for scene in result["scenes"]:
            # eighths is measured from the script, never taken from the model.
            self.assertEqual(scene["eighths"], expected_eighths[scene["number"]])


if __name__ == "__main__":
    unittest.main()
