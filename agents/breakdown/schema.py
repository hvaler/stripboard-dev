import json
from dataclasses import dataclass, field, asdict
from typing import List, Optional, Dict, Any

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
