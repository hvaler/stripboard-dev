"""
Loads a screenplay from the formats a production actually receives, and reduces all of
them to one thing: screenplay text (EV-28).

Everything converges on `FountainParser`, so scene segmentation, time-of-day handling and
page-length measurement have exactly one implementation. The formats differ only in how
the text is obtained:

  .fountain / .txt   read it
  .fdx               Final Draft XML — parse the paragraph elements
  .pdf               Gemini multimodal transcription, because a scanned page has no text
                     layer to read
"""

import os
from dataclasses import dataclass
from typing import Any, Dict, List

# A screenplay arrives from outside the system, so the XML parser must not be the stdlib
# one: xml.etree expands entities and is vulnerable to billion-laughs, which turns "a
# producer sent us a script" into a denial of service.
from defusedxml import ElementTree as ET
from defusedxml.common import DefusedXmlException

from fountain_parser import FountainParser

SUPPORTED_EXTENSIONS = (".fountain", ".txt", ".fdx", ".pdf")

TRANSCRIPTION_PROMPT = """Transcribe this screenplay into plain text, preserving its structure.

Rules:
- Put each scene heading on its own line, starting with INT. or EXT. exactly as written.
- Keep character names, dialogue and action under the heading they belong to.
- Do not summarise, reorder, correct or invent anything. Transcribe only what is on the page.
- Omit page numbers, headers, footers and revision marks.
"""


@dataclass
class LoadedScreenplay:
    """Raw scenes plus a record of how they were obtained."""
    scenes: List[Dict[str, Any]]
    source_format: str
    transcription_tokens: int = 0


class UnsupportedScreenplayError(ValueError):
    """The file is not a screenplay format this agent can read."""


def load_screenplay(path: str, gemini_client=None) -> LoadedScreenplay:
    extension = os.path.splitext(path)[1].lower()

    if extension in (".fountain", ".txt"):
        with open(path, "r", encoding="utf-8") as f:
            return LoadedScreenplay(_parse(f.read()), "fountain")

    if extension == ".fdx":
        with open(path, "r", encoding="utf-8-sig") as f:
            return LoadedScreenplay(_parse(fdx_to_text(f.read())), "final-draft")

    if extension == ".pdf":
        if gemini_client is None:
            raise UnsupportedScreenplayError(
                "Reading a PDF screenplay needs Gemini: a scanned page has no text layer. "
                "Configure Google Cloud credentials, or convert the script to .fountain/.fdx."
            )
        with open(path, "rb") as f:
            pdf_bytes = f.read()

        result = gemini_client.transcribe_document(
            pdf_bytes, mime_type="application/pdf", prompt=TRANSCRIPTION_PROMPT)
        scenes = _parse(result.raw_text)
        if not scenes:
            raise UnsupportedScreenplayError(
                "Gemini transcribed the PDF but no scene headings were found. Is this a screenplay?")
        return LoadedScreenplay(scenes, "pdf-gemini", result.total_tokens)

    raise UnsupportedScreenplayError(
        f"Unsupported screenplay format '{extension}'. Supported: {', '.join(SUPPORTED_EXTENSIONS)}.")


def _parse(text: str) -> List[Dict[str, Any]]:
    return FountainParser().parse(text)


def fdx_to_text(xml_text: str) -> str:
    """
    Convert Final Draft XML into plain screenplay text.

    An .fdx stores each line as a <Paragraph Type="..."> holding one or more <Text> runs
    (Final Draft splits a line whenever styling changes, so the runs must be joined before
    the line means anything).
    """
    try:
        root = ET.fromstring(xml_text)
    except DefusedXmlException as exc:
        raise UnsupportedScreenplayError(
            f"This Final Draft file uses XML features that are refused for safety: {exc}") from exc
    except Exception as exc:  # ParseError and friends
        raise UnsupportedScreenplayError(f"This is not readable Final Draft XML: {exc}") from exc

    content = root.find("Content")
    if content is None:
        raise UnsupportedScreenplayError("Final Draft file has no <Content> element.")

    lines: List[str] = []
    for paragraph in content.findall("Paragraph"):
        text = "".join(node.text or "" for node in paragraph.iter("Text")).strip()
        if not text:
            continue

        paragraph_type = (paragraph.get("Type") or "").strip()
        if paragraph_type == "Scene Heading":
            lines.extend(["", text, ""])
        elif paragraph_type == "Character":
            lines.extend(["", text])
        elif paragraph_type in ("General", "Transition", "Shot"):
            lines.extend(["", text])
        else:
            lines.append(text)

    return "\n".join(lines).strip()
