import re
from typing import Any, Dict, List, Optional, Tuple

# Roughly 60 characters of screenplay text per eighth of a page. This is a crude
# proxy for the real measurement, which depends on rendered page layout; it is kept
# deterministic on purpose so the model never invents scene lengths (EV-18).
CHARS_PER_EIGHTH = 60
MIN_EIGHTHS = 2

# Time-of-day tokens screenwriters actually use, mapped onto the four the schedule
# understands. Anything else is not a time of day, which is the whole point: the old
# parser took the last hyphen-separated segment unconditionally, so
# "INT. BAKER STREET - SITTING ROOM" produced day_night="SITTING ROOM".
TIME_OF_DAY = {
    "DAY": "DAY", "MORNING": "DAY", "AFTERNOON": "DAY", "MIDDAY": "DAY", "NOON": "DAY",
    "NIGHT": "NIGHT", "MIDNIGHT": "NIGHT", "EVENING": "NIGHT", "LATE NIGHT": "NIGHT",
    "DAWN": "DAWN", "SUNRISE": "DAWN", "EARLY MORNING": "DAWN",
    "DUSK": "DUSK", "SUNSET": "DUSK", "MAGIC HOUR": "DUSK", "TWILIGHT": "DUSK",
}

# Fountain allows a scene to inherit the previous scene's time of day.
CONTINUATIONS = {"CONTINUOUS", "LATER", "MOMENTS LATER", "SAME", "SAME TIME", "CONT'D"}


def estimate_eighths(raw_text: str) -> int:
    """Deterministic page-length estimate for a scene, in eighths of a page."""
    return max(MIN_EIGHTHS, len(raw_text or "") // CHARS_PER_EIGHTH)


def split_heading(body: str, previous_day_night: Optional[str] = None) -> Tuple[str, str]:
    """
    Split the part of a scene heading after INT./EXT. into (set description, time of day).

    Only a recognised time-of-day token is treated as one. A heading that ends in anything
    else keeps its full text as the set description and inherits the previous scene's time
    of day, which is what a script supervisor would do reading it.
    """
    segments = [segment.strip() for segment in re.split(r"\s+[-–—]\s+|\s+--\s+", body) if segment.strip()]
    if not segments:
        return body.strip(), previous_day_night or "DAY"

    tail = segments[-1].upper()

    if tail in TIME_OF_DAY:
        return " - ".join(segments[:-1]).strip() or segments[0], TIME_OF_DAY[tail]

    if tail in CONTINUATIONS:
        return " - ".join(segments[:-1]).strip() or segments[0], previous_day_night or "DAY"

    return " - ".join(segments).strip(), previous_day_night or "DAY"


class FountainParser:
    """
    Parser for Fountain screenplays. Extracts scene headings and the text under each one.

    It deliberately stops at the syntax: what a heading means — which part of it is the
    location the unit travels to and which is the set within it — is a semantic judgement
    left to the breakdown agent (EV-28).
    """

    SCENE_HEADING_REGEX = re.compile(
        r"^(INT\.?/EXT\.?|EXT\.?/INT\.?|I/E\.?|INT\.?|EXT\.?)\s+(.+)$", re.IGNORECASE
    )

    def parse(self, text: str) -> List[Dict[str, Any]]:
        scenes: List[Dict[str, Any]] = []
        current: Optional[Dict[str, Any]] = None
        previous_day_night: Optional[str] = None
        scene_number = 0

        for line in text.splitlines():
            stripped = line.strip()
            match = self.SCENE_HEADING_REGEX.match(stripped)

            if match:
                if current:
                    current["raw_content"] = "\n".join(current["lines"]).strip()
                    scenes.append(current)

                scene_number += 1
                int_ext = self._normalise_int_ext(match.group(1))
                set_location, day_night = split_heading(match.group(2), previous_day_night)
                previous_day_night = day_night

                current = {
                    "number": scene_number,
                    "int_ext": int_ext,
                    "set_location": set_location,
                    "day_night": day_night,
                    "heading": stripped,
                    "lines": [line],
                }
            elif current:
                current["lines"].append(line)

        if current:
            current["raw_content"] = "\n".join(current["lines"]).strip()
            scenes.append(current)

        return scenes

    @staticmethod
    def _normalise_int_ext(token: str) -> str:
        cleaned = token.upper().replace(".", "").replace(" ", "")
        if cleaned in ("INT/EXT", "EXT/INT", "I/E"):
            return "INT/EXT"
        return "EXT" if cleaned == "EXT" else "INT"
