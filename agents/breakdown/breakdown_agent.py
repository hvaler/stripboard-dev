import hashlib
import json
import logging
import os
from typing import Any, Dict, List, Optional

from fountain_parser import estimate_eighths
from gemini_client import GeminiClient, GeminiConfigError
from schema import (
    BREAKDOWN_JSON_SCHEMA,
    GeminiBreakdown,
    validate_breakdown_dict,
    validate_breakdown_verbose,
)
from screenplay_sources import load_screenplay

logger = logging.getLogger("BreakdownAgent")

CACHE_FILE = os.path.join(os.path.dirname(__file__), "demo_cache.json")

SYSTEM_INSTRUCTION = """You are a script supervisor preparing a production breakdown for a \
1st Assistant Director. You read screenplay scenes and identify, per scene, which characters \
appear and which physical elements the production must source.

Rules you must follow:
- Return every scene you are given, exactly once, keeping the scene numbers unchanged.
- `cast` lists characters who speak or are explicitly featured in the action, written the way \
they would appear on a call sheet (e.g. "Sherlock Holmes", not "HOLMES"). Do not invent \
characters who are only mentioned in dialogue but never present in the scene.
- `elements` lists concrete, sourceable production items, not abstractions. A revolver, a \
hansom cab and a fog effect are elements; "tension" and "Victorian atmosphere" are not.
- If the time of day is not stated, infer the most plausible one from the action.

Separating `location` from `set_name` is the judgement that matters most here, because the \
schedule counts a company move every time `location` changes, and a company move costs the \
production an hour of shooting.

- `location` is the place the unit physically travels to. Two scenes share a location only \
if the crew could shoot both without moving trucks.
- `set_name` is the specific space inside that location, or an empty string if the heading \
names none.

Judge this by what the words mean, not by how the heading is punctuated. \
"221B BAKER STREET - SITTING ROOM" and "221B BAKER STREET - LABORATORY" are two sets at one \
location: the unit parks once. "LONDON STREETS - COVENT GARDEN" and \
"LONDON STREETS - PICCADILLY" are two different locations that happen to share a prefix: \
the unit must move across London. Getting this wrong makes the schedule lie about its cost."""


def _screenplay_key(content: str) -> str:
    """Content hash identifying a screenplay revision in the cache."""
    return hashlib.sha256(content.encode("utf-8")).hexdigest()[:16]


def screenplay_key(scenes: List[Dict[str, Any]]) -> str:
    """
    Cache key for a parsed screenplay. Derived from the scenes rather than the file bytes,
    so the same script delivered as .fountain and as .fdx resolves to one entry — and
    editing the script still invalidates it.
    """
    return _screenplay_key(json.dumps(
        [[s["number"], s.get("heading", ""), s.get("raw_content", "")] for s in scenes],
        ensure_ascii=False))


def _read_cache(key: str) -> Optional[Dict[str, Any]]:
    if not os.path.exists(CACHE_FILE):
        return None
    try:
        with open(CACHE_FILE, "r", encoding="utf-8") as f:
            store = json.load(f)
    except (OSError, json.JSONDecodeError) as exc:
        logger.warning("Ignoring unreadable cache %s: %s", CACHE_FILE, exc)
        return None
    entry = store.get("breakdowns", {}).get(key)
    return entry.get("breakdown") if entry else None


def _write_cache(key: str, screenplay: str, breakdown: Dict[str, Any]) -> None:
    store: Dict[str, Any] = {"breakdowns": {}}
    if os.path.exists(CACHE_FILE):
        try:
            with open(CACHE_FILE, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            if isinstance(loaded.get("breakdowns"), dict):
                store = loaded
        except (OSError, json.JSONDecodeError):
            pass

    store["breakdowns"][key] = {"screenplay": screenplay, "breakdown": breakdown}
    with open(CACHE_FILE, "w", encoding="utf-8") as f:
        json.dump(store, f, indent=2, ensure_ascii=False)


class BreakdownAgent:
    """
    Extracts a typed production breakdown from a screenplay.

    Cast, elements and synopsis are extracted by Gemini using structured output, with a
    schema-validation retry loop. Scene headings and `eighths` (a page-length
    measurement) stay deterministic: the model formulates, deterministic code decides.

    If Gemini is unreachable or keeps returning invalid payloads, the agent degrades to
    a parser-only breakdown with empty cast/elements and marks the result
    `source="fallback"` so a caller can never mistake it for a real extraction.
    """

    def __init__(self, client: Optional[GeminiClient] = None, max_attempts: int = 3):
        self.client = client or GeminiClient()
        self.max_attempts = max_attempts

    def process_screenplay(
        self,
        filepath: str,
        use_cache: bool = False,
        allow_fallback: bool = True,
    ) -> Dict[str, Any]:
        """
        Break down a screenplay in any supported format — Fountain, Final Draft or PDF.

        `use_cache` is off by default: a cached breakdown must never silently stand in
        for a live extraction. It exists so a demo can be replayed offline.
        """
        loaded = load_screenplay(filepath, gemini_client=self.client)
        if not loaded.scenes:
            raise ValueError(f"No scene headings found in {filepath}.")

        # The cache is keyed by the parsed scene headings rather than raw file bytes, so
        # the same script delivered as .fountain and as .fdx resolves to one entry — and
        # editing the script still invalidates it.
        key = screenplay_key(loaded.scenes)

        if use_cache:
            cached = _read_cache(key)
            if cached is not None and validate_breakdown_dict(cached):
                cached["source"] = "cache"
                logger.info("Loaded breakdown from cache for %s (key %s)", os.path.basename(filepath), key)
                return cached
            logger.info("No cached breakdown for %s (key %s)", os.path.basename(filepath), key)

        result = self._extract(loaded.scenes, allow_fallback=allow_fallback)
        result["source_format"] = loaded.source_format
        if loaded.transcription_tokens:
            result["transcription_tokens"] = loaded.transcription_tokens

        if not validate_breakdown_dict(result):
            raise ValueError("Extracted screenplay breakdown failed JSON Schema validation.")

        # Only a real extraction is worth caching. Caching a fallback would let an
        # empty breakdown masquerade as a good one on the next run.
        if use_cache and result.get("source") == "gemini":
            _write_cache(key, os.path.basename(filepath), result)

        return result

    # Kept so existing callers and the demo harness keep working after the rename.
    process_fountain_file = process_screenplay

    # --- extraction ---------------------------------------------------------------

    def _extract(self, raw_scenes: List[Dict[str, Any]], allow_fallback: bool) -> Dict[str, Any]:
        try:
            return self._extract_with_gemini(raw_scenes)
        except Exception as exc:
            if isinstance(exc, GeminiConfigError):
                logger.warning("Gemini not configured: %s", exc)
            else:
                logger.warning("Gemini extraction failed: %s: %s", type(exc).__name__, exc)
            if not allow_fallback:
                raise
            return self._extract_deterministic(raw_scenes)

    def _extract_with_gemini(self, raw_scenes: List[Dict[str, Any]]) -> Dict[str, Any]:
        """
        Structured-output extraction with a schema-validation retry loop: on a rejected
        payload the validation errors are fed back to the model on the next attempt.
        """
        prompt = self._build_prompt(raw_scenes)
        by_number = {s["number"]: s for s in raw_scenes}
        last_errors: List[str] = []

        for attempt in range(1, self.max_attempts + 1):
            attempt_prompt = prompt
            if last_errors:
                attempt_prompt = (
                    f"{prompt}\n\nYour previous answer was rejected by schema validation:\n"
                    + "\n".join(f"- {e}" for e in last_errors)
                    + "\n\nReturn a corrected breakdown for all scenes."
                )

            generated = self.client.generate_structured(
                prompt=attempt_prompt,
                response_schema=GeminiBreakdown,
                system_instruction=SYSTEM_INSTRUCTION,
            )

            candidate, missing = self._merge(generated.parsed, by_number)
            ok, errors = validate_breakdown_verbose(candidate)
            if missing:
                ok = False
                errors = [
                    f"You omitted scene number(s) {missing}. Return every scene you were given."
                ] + errors
            if ok:
                candidate.update(
                    source="gemini",
                    model=generated.model,
                    backend=generated.backend,
                    attempts=attempt,
                    total_tokens=generated.total_tokens,
                )
                logger.info(
                    "Gemini breakdown OK on attempt %d/%d (%s, %s, %d tokens)",
                    attempt, self.max_attempts, generated.model, generated.backend,
                    generated.total_tokens,
                )
                return candidate

            last_errors = errors
            logger.warning(
                "Attempt %d/%d rejected: %s", attempt, self.max_attempts, "; ".join(errors[:5])
            )

        raise ValueError(
            f"Gemini returned a schema-invalid breakdown {self.max_attempts} times. "
            f"Last errors: {'; '.join(last_errors[:5])}"
        )

    def _merge(self, parsed: GeminiBreakdown, by_number: Dict[int, Dict[str, Any]]):
        """
        Combine the model's semantic extraction with the parser's deterministic facts.
        `eighths` always comes from the script text, never from the model.

        Returns (breakdown, missing_scene_numbers).
        """
        scenes: List[Dict[str, Any]] = []
        for scene in (parsed.scenes if parsed else []):
            raw = by_number.get(scene.number)
            if raw is None:
                # Model invented a scene number that was not in the input; drop it.
                continue
            location = (scene.location or "").strip().upper()
            set_name = (scene.set_name or "").strip().upper()
            scenes.append({
                "number": scene.number,
                "location": location or raw["set_location"],
                "set_name": set_name,
                "set_location": f"{location} - {set_name}" if location and set_name else (location or raw["set_location"]),
                "int_ext": scene.int_ext,
                "day_night": scene.day_night,
                "eighths": estimate_eighths(raw.get("raw_content", "")),
                "synopsis": scene.synopsis,
                "cast": list(scene.cast),
                "elements": [{"name": e.name, "category": e.category} for e in scene.elements],
            })

        scenes.sort(key=lambda s: s["number"])
        missing = sorted(set(by_number) - {s["number"] for s in scenes})
        return {"scenes": scenes}, missing

    def _extract_deterministic(self, raw_scenes: List[Dict[str, Any]]) -> Dict[str, Any]:
        """
        Parser-only fallback. Scene headings and lengths are real; cast and elements are
        left empty because identifying them without a model would mean guessing.
        """
        scenes = []
        for raw in raw_scenes:
            text = raw.get("raw_content", "")
            day_night = raw.get("day_night", "DAY")
            if day_night not in ("DAY", "NIGHT", "DAWN", "DUSK"):
                day_night = "DAY"
            int_ext = raw.get("int_ext", "INT")
            if int_ext not in ("INT", "EXT", "INT/EXT"):
                int_ext = "INT"

            body = [ln for ln in text.splitlines()[1:] if ln.strip()]
            scenes.append({
                "number": raw["number"],
                # Without a model there is no way to tell a set apart from the location it
                # sits in, so the whole heading becomes the location. That over-counts
                # company moves, which is the safe direction: it makes the schedule look
                # more expensive than it is, never cheaper.
                "location": raw["set_location"],
                "set_name": "",
                "set_location": raw["set_location"],
                "int_ext": int_ext,
                "day_night": day_night,
                "eighths": estimate_eighths(text),
                "synopsis": body[0].strip() if body else "Scene action",
                "cast": [],
                "elements": [],
            })

        logger.warning("Returning parser-only breakdown: cast and elements are EMPTY.")
        return {"scenes": scenes, "source": "fallback", "model": None, "attempts": 0}

    # --- prompt -------------------------------------------------------------------

    @staticmethod
    def _build_prompt(raw_scenes: List[Dict[str, Any]]) -> str:
        payload = [
            {
                "number": s["number"],
                # The heading exactly as written, so the model judges the location/set
                # split from the screenwriter's words rather than from our reformatting.
                "heading": s.get("heading") or f"{s['int_ext']}. {s['set_location']}",
                "text": s.get("raw_content", ""),
            }
            for s in raw_scenes
        ]
        return (
            "Break down the following screenplay scenes for production.\n\n"
            f"{json.dumps(payload, indent=2, ensure_ascii=False)}"
        )
