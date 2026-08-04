import json
from dataclasses import dataclass, field, asdict
from typing import List, Optional, Dict, Any, Literal, Tuple

from pydantic import BaseModel, Field

@dataclass
class ElementSchema:
    name: str
    category: str

@dataclass
class SceneSchema:
    number: int
    set_location: str
    int_ext: str
    day_night: str
    eighths: int
    synopsis: str
    cast: List[str] = field(default_factory=list)
    elements: List[ElementSchema] = field(default_factory=list)

BREAKDOWN_JSON_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "required": ["scenes"],
    "properties": {
        "scenes": {
            "type": "array",
            "items": {
                "type": "object",
                "required": ["number", "set_location", "int_ext", "day_night", "eighths", "synopsis", "cast", "elements"],
                "properties": {
                    "number": {"type": "integer"},
                    "set_location": {"type": "string"},
                    "int_ext": {"type": "string", "enum": ["INT", "EXT", "INT/EXT"]},
                    "day_night": {"type": "string", "enum": ["DAY", "NIGHT", "DAWN", "DUSK"]},
                    "eighths": {"type": "integer"},
                    "synopsis": {"type": "string"},
                    "cast": {"type": "array", "items": {"type": "string"}},
                    "elements": {
                        "type": "array",
                        "items": {
                            "type": "object",
                            "required": ["name", "category"],
                            "properties": {
                                "name": {"type": "string"},
                                "category": {"type": "string"}
                            }
                        }
                    }
                }
            }
        }
    }
}

def validate_breakdown_dict(data: Dict[str, Any]) -> bool:
    """
    Zero-dependency validation of breakdown dictionary against schema.
    """
    if not isinstance(data, dict) or "scenes" not in data:
        return False
    if not isinstance(data["scenes"], list):
        return False

    valid_int_ext = {"INT", "EXT", "INT/EXT"}
    valid_day_night = {"DAY", "NIGHT", "DAWN", "DUSK"}

    for scene in data["scenes"]:
        if not isinstance(scene.get("number"), int):
            return False
        if not isinstance(scene.get("set_location"), str):
            return False
        if scene.get("int_ext") not in valid_int_ext:
            return False
        if scene.get("day_night") not in valid_day_night:
            return False
        if not isinstance(scene.get("eighths"), int) or scene.get("eighths") <= 0:
            return False
        if not isinstance(scene.get("cast"), list):
            return False
        if not isinstance(scene.get("elements"), list):
            return False

    return True


def validate_breakdown_verbose(data: Dict[str, Any]) -> Tuple[bool, List[str]]:
    """
    Same rules as validate_breakdown_dict, but returns the reasons a payload was
    rejected so they can be fed back to the model on a retry (EV-18).
    """
    errors: List[str] = []

    if not isinstance(data, dict):
        return False, ["Top-level payload is not an object."]
    if "scenes" not in data:
        return False, ["Top-level object is missing the required 'scenes' key."]
    if not isinstance(data["scenes"], list):
        return False, ["'scenes' must be an array."]
    if not data["scenes"]:
        return False, ["'scenes' is empty; every screenplay has at least one scene."]

    valid_int_ext = {"INT", "EXT", "INT/EXT"}
    valid_day_night = {"DAY", "NIGHT", "DAWN", "DUSK"}

    for idx, scene in enumerate(data["scenes"]):
        where = f"scenes[{idx}]"
        if not isinstance(scene, dict):
            errors.append(f"{where} is not an object.")
            continue
        if not isinstance(scene.get("number"), int):
            errors.append(f"{where}.number must be an integer.")
        if not isinstance(scene.get("set_location"), str) or not scene.get("set_location"):
            errors.append(f"{where}.set_location must be a non-empty string.")
        if scene.get("int_ext") not in valid_int_ext:
            errors.append(f"{where}.int_ext is {scene.get('int_ext')!r}; must be one of {sorted(valid_int_ext)}.")
        if scene.get("day_night") not in valid_day_night:
            errors.append(f"{where}.day_night is {scene.get('day_night')!r}; must be one of {sorted(valid_day_night)}.")
        if not isinstance(scene.get("eighths"), int) or scene.get("eighths", 0) <= 0:
            errors.append(f"{where}.eighths must be a positive integer.")
        if not isinstance(scene.get("cast"), list):
            errors.append(f"{where}.cast must be an array of character names.")
        if not isinstance(scene.get("elements"), list):
            errors.append(f"{where}.elements must be an array.")

    return (not errors), errors


# --- Structured-output contract for Gemini (EV-18) -------------------------------
#
# This is what the model is asked to return, and it is deliberately NOT the same as
# BREAKDOWN_JSON_SCHEMA: `eighths` is a physical page-length measurement, not a
# semantic judgement, so it is computed deterministically from the script text and
# merged in afterwards. Consistent with the project rule that the model formulates
# and deterministic code decides.

ELEMENT_CATEGORIES = (
    "Prop",
    "Wardrobe",
    "Vehicle",
    "Fx",
    "Stunt",
    "Animal",
    "Extra",
    "SetDressing",
    "Makeup",
    "Sound",
    "Music",
    "SpecialEquipment",
)


class GeminiElement(BaseModel):
    name: str = Field(description="Short production-department name for the element.")
    category: Literal[ELEMENT_CATEGORIES] = Field(  # type: ignore[valid-type]
        description="Standard breakdown category this element belongs to."
    )


class GeminiScene(BaseModel):
    number: int = Field(description="Scene number, matching the input scene number exactly.")
    set_location: str = Field(description="The set/location name, uppercase, without the INT/EXT prefix or the time-of-day suffix.")
    int_ext: Literal["INT", "EXT", "INT/EXT"]
    day_night: Literal["DAY", "NIGHT", "DAWN", "DUSK"]
    synopsis: str = Field(description="One sentence describing what happens in the scene.")
    cast: List[str] = Field(description="Speaking and featured characters, as they would appear on a call sheet.")
    elements: List[GeminiElement] = Field(description="Physical production elements that must be sourced for this scene.")


class GeminiBreakdown(BaseModel):
    scenes: List[GeminiScene]
