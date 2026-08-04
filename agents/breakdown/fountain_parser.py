import re
from typing import List, Dict, Any

# Roughly 60 characters of screenplay text per eighth of a page. This is a crude
# proxy for the real measurement, which depends on rendered page layout; it is kept
# deterministic on purpose so the model never invents scene lengths (EV-18).
CHARS_PER_EIGHTH = 60
MIN_EIGHTHS = 2


def estimate_eighths(raw_text: str) -> int:
    """Deterministic page-length estimate for a scene, in eighths of a page."""
    return max(MIN_EIGHTHS, len(raw_text or "") // CHARS_PER_EIGHTH)


class FountainParser:
    """
    Parser for Fountain format screenplays (§5 requirement).
    Extracts raw scene headings and scene blocks.
    """
    SCENE_HEADING_REGEX = re.compile(r'^(INT|EXT|INT/EXT|EXT/INT)\.?\s+(.+)$', re.IGNORECASE)

    def parse(self, text: str) -> List[Dict[str, Any]]:
        lines = text.splitlines()
        scenes: List[Dict[str, Any]] = []
        current_scene: Dict[str, Any] = None
        scene_number = 0

        for line in lines:
            stripped = line.strip()
            match = self.SCENE_HEADING_REGEX.match(stripped)
            if match:
                scene_number += 1
                if current_scene:
                    current_scene['raw_content'] = '\n'.join(current_scene['lines']).strip()
                    scenes.append(current_scene)

                int_ext = match.group(1).upper()
                location_and_time = match.group(2)
                
                parts = location_and_time.rsplit('-', 1)
                set_location = parts[0].strip() if len(parts) > 1 else location_and_time.strip()
                day_night = parts[1].strip().upper() if len(parts) > 1 else "DAY"

                current_scene = {
                    'number': scene_number,
                    'int_ext': int_ext,
                    'set_location': set_location,
                    'day_night': day_night,
                    'lines': [line]
                }
            elif current_scene:
                current_scene['lines'].append(line)

        if current_scene:
            current_scene['raw_content'] = '\n'.join(current_scene['lines']).strip()
            scenes.append(current_scene)

        return scenes
