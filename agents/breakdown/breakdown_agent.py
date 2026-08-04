import os
import json
from typing import Dict, Any, List
from fountain_parser import FountainParser
from schema import BREAKDOWN_JSON_SCHEMA, validate_breakdown_dict

CACHE_FILE = os.path.join(os.path.dirname(__file__), "demo_cache.json")

class BreakdownAgent:
    """
    Screenplay scene & element extraction, with JSON Schema validation and local
    caching for demo runs.

    NOT IMPLEMENTED YET: LLM-backed extraction. This class runs a deterministic
    keyword parser tuned to the demo screenplay; no model is called and no Google
    Cloud AI SDK is used. Gemini structured-output extraction is tracked as EV-18.
    Until then this is not an ADK agent and must not be described as one.
    """
    def __init__(self):
        self.parser = FountainParser()

    def process_fountain_file(self, filepath: str, use_cache: bool = True) -> Dict[str, Any]:
        """
        Parses a Fountain file and extracts structured scene breakdown.
        """
        if use_cache and os.path.exists(CACHE_FILE):
            with open(CACHE_FILE, "r", encoding="utf-8") as f:
                cached_data = json.load(f)
                if validate_breakdown_dict(cached_data):
                    return cached_data

        with open(filepath, "r", encoding="utf-8") as f:
            content = f.read()

        raw_scenes = self.parser.parse(content)
        breakdown_result = self._extract_deterministic(raw_scenes)

        # Validate against JSON Schema
        if not validate_breakdown_dict(breakdown_result):
            raise ValueError("Extracted screenplay breakdown failed JSON Schema validation.")

        # Save to cache
        with open(CACHE_FILE, "w", encoding="utf-8") as f:
            json.dump(breakdown_result, f, indent=2)

        return breakdown_result

    def _extract_deterministic(self, raw_scenes: List[Dict[str, Any]]) -> Dict[str, Any]:
        """
        Deterministic keyword extraction of cast and elements.

        Placeholder implementation: the character and prop lists below are hardcoded to
        the demo screenplay and will not generalise to an arbitrary script. Replaced by
        Gemini structured output (with a schema-validation retry loop) in EV-18.
        """
        extracted_scenes = []
        for raw in raw_scenes:
            cast = []
            elements = []

            text = raw.get("raw_content", "")
            if "HOLMES" in text.upper():
                cast.append("Sherlock Holmes")
            if "WATSON" in text.upper():
                cast.append("Dr. John Watson")
            if "IRENE" in text.upper():
                cast.append("Irene Adler")
            if "MORIARTY" in text.upper():
                cast.append("Prof. James Moriarty")

            if "cipher" in text.lower() or "letter" in text.lower():
                elements.append({"name": "Ciphered Document", "category": "Prop"})
            if "revolver" in text.lower():
                elements.append({"name": "Webley Revolver", "category": "Prop"})
            if "fog" in text.lower():
                elements.append({"name": "Fog Machine Smoke", "category": "Fx"})
            if "cab" in text.lower() or "carriage" in text.lower():
                elements.append({"name": "Hansom Cab Carriage", "category": "Vehicle"})

            extracted_scenes.append({
                "number": raw["number"],
                "set_location": raw["set_location"],
                "int_ext": raw["int_ext"],
                "day_night": raw["day_night"],
                "eighths": max(2, len(text) // 60),
                "synopsis": text.splitlines()[0] if text else "Scene action",
                "cast": cast,
                "elements": elements
            })

        return {"scenes": extracted_scenes}
