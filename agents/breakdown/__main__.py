"""
CLI entry point:  python -m agents.breakdown --file demo/screenplay.fountain

The sys.path insert keeps this package importable both as `agents.breakdown` and via
the flat, path-based imports the other agents and the demo harness still use. EV-24
unifies all of them under one package layout.
"""

import argparse
import json
import logging
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))

from breakdown_agent import BreakdownAgent  # noqa: E402
from gemini_client import GeminiClient, GeminiConfigError  # noqa: E402


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="python -m agents.breakdown",
        description="Break a screenplay down into typed scenes, cast and production elements.",
    )
    parser.add_argument("--file", required=True,
                        help="Screenplay to break down: .fountain, .txt, .fdx (Final Draft) "
                             "or .pdf (read via Gemini multimodal).")
    parser.add_argument("--model", default=None, help="Gemini model (default: gemini-2.5-flash).")
    parser.add_argument("--project", default=None, help="GCP project for the Vertex AI backend.")
    parser.add_argument("--location", default=None, help="Vertex AI location (default: global).")
    parser.add_argument(
        "--offline",
        action="store_true",
        help="Do not call Gemini. Replays the cached breakdown if present, otherwise "
             "returns a parser-only result with empty cast and elements.",
    )
    parser.add_argument("--json", action="store_true", help="Print the raw JSON breakdown.")
    parser.add_argument("-v", "--verbose", action="store_true", help="Show SDK and retry logging.")
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=logging.INFO if args.verbose else logging.WARNING,
        format="%(levelname)-8s %(name)s: %(message)s",
    )

    if args.offline:
        agent = BreakdownAgent(client=_UnavailableClient())
        result = agent.process_screenplay(args.file, use_cache=True, allow_fallback=True)
    else:
        client = GeminiClient(model=args.model, project=args.project, location=args.location)
        agent = BreakdownAgent(client=client)
        try:
            result = agent.process_screenplay(args.file, use_cache=False, allow_fallback=False)
        except GeminiConfigError as exc:
            print(f"error: {exc}\n\nOr run with --offline to replay a cached breakdown.",
                  file=sys.stderr)
            return 2

    if args.json:
        print(json.dumps(result, indent=2, ensure_ascii=False))
        return 0

    _render(result)
    return 0


class _UnavailableClient(GeminiClient):
    """Forces the offline path without touching the network."""

    def generate_structured(self, *_args, **_kwargs):
        raise GeminiConfigError("--offline was requested; no model call was made.")

    def transcribe_document(self, *_args, **_kwargs):
        raise GeminiConfigError(
            "--offline was requested, and reading a PDF needs a model call.")


def _render(result) -> None:
    source = result.get("source", "unknown")
    banner = {
        "gemini": f"source=gemini  model={result.get('model')}  backend={result.get('backend')}  "
                  f"attempts={result.get('attempts')}  tokens={result.get('total_tokens')}",
        "cache": "source=cache  (replayed, no model call)",
        "fallback": "source=fallback  (NO model call — cast and elements are empty)",
    }.get(source, f"source={source}")

    if result.get("source_format"):
        extra = (f"  transcription_tokens={result['transcription_tokens']}"
                 if result.get("transcription_tokens") else "")
        banner = f"{banner}\nformat={result['source_format']}{extra}"

    print(banner)
    print("=" * max(len(line) for line in banner.splitlines()))

    locations = {(s.get("location") or s["set_location"]) for s in result["scenes"]}
    print(f"{len(result['scenes'])} scene(s) across {len(locations)} location(s) "
          f"— each change of location costs the shoot a company move")

    for scene in result["scenes"]:
        where = scene.get("location") or scene["set_location"]
        if scene.get("set_name"):
            where = f"{where} / {scene['set_name']}"
        unit = f"{scene['int_ext']} {scene['day_night']}"
        print(f"\nScene {scene['number']}  [{unit}]  {where}   ({scene['eighths']}/8)")
        print(f"  {scene['synopsis']}")
        if scene["cast"]:
            print(f"  {'Cast:':<18}{', '.join(scene['cast'])}")
        if scene["elements"]:
            grouped = {}
            for el in scene["elements"]:
                grouped.setdefault(el["category"], []).append(el["name"])
            for category in sorted(grouped):
                print(f"  {category + ':':<18}{', '.join(grouped[category])}")


if __name__ == "__main__":
    sys.exit(main())
