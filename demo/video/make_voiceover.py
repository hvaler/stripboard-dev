"""
Turns the narration into six audio files with Google Cloud Text-to-Speech (EV-32).

One file per shot rather than one long take, because that is what makes editing survivable:
if a screen recording runs three seconds long, you nudge one clip instead of re-cutting a
three-minute waveform.

    pip install google-cloud-texttospeech
    gcloud auth application-default login
    export GOOGLE_CLOUD_PROJECT=stripboard-hack
    python demo/video/make_voiceover.py

Writes `demo/video/audio/01.mp3` … `06.mp3` and prints the duration of each against the budget
it was written to, so an overrun is visible before it is baked into a video.

**Why Google's own speech API and not another.** The hackathon's AI restriction is about what
the *product* calls at runtime, not about editing tools — but a project whose thesis is "every
runtime model here is Google Cloud" has no reason to narrate itself with somebody else's. If a
judge asks what the voice was, the answer should add to the story rather than need explaining.

The voice is a Studio one: they read long-form prose with sentence rhythm instead of the
word-by-word cadence that makes a demo sound automated. Change VOICE below to audition others.
"""

import os
import sys
from pathlib import Path

try:
    from google.cloud import texttospeech
except ImportError:
    raise SystemExit("pip install google-cloud-texttospeech")

VOICE = "en-GB-Studio-B"     # British, to match a shoot set in Salford. en-US-Studio-O also good.
LANGUAGE = "en-GB"
SPEAKING_RATE = 0.96         # A touch under natural: this narration is dense with figures.

OUT = Path(__file__).parent / "audio"

# (file stem, seconds budgeted, text). The text is the narration verbatim — if you edit one,
# edit demo/video/narration.md too, or the subtitles stop matching what is said.
SHOTS = [
    ("01-mission-control", 25, """
     This is not our application's latency. It is a film shoot.
     Days remaining. Budget burning down. Union violations. And the one that costs real money:
     which actors we are paying to sit in a trailer.
     Every other project pointing Grafana at something this week is pointing it at itself.
     We pointed it at the production.
     """),
    ("02-screenplay", 25, """
     A screenplay goes in. Gemini, on Vertex AI, reads it into typed scenes: location, set,
     interior or exterior, day or night, cast, page count.
     Then a constraint solver builds the schedule. Google OR-Tools, CP-SAT. Twelve hour
     turnaround holds by construction, not by checking afterwards and apologising.
     """),
    ("03-grafana-fires", 40, """
     Now watch what starts this.
     The shoot exports its own metrics to Grafana Cloud. A rule evaluates them, and it is
     firing: three cast members are called on a quarter of the shooting days. That is a
     contract being paid against days nobody works.
     Nobody is watching a screen. The Conflict Sentinel asks Grafana which rules are firing,
     through the official Grafana MCP server, and reads that alert back.
     This is the direction that matters. Grafana does not receive the result. Grafana starts
     the work.
     """),
    ("04-agents", 45, """
     The orchestrator routes it. A root agent with no tools of its own, so nothing it reports
     can be a figure it invented.
     The replanner does no arithmetic at all. Every number it states comes from a separate
     CP-SAT run. Two options, with real cost deltas between two solved schedules.
     And when the two options come out identical, it says so, instead of dressing one up as a
     recommendation. Extending the window only permits extra days. The solver still minimises
     them. The honest answer was: you do not need the extra day.
     """),
    ("05-refusal", 30, """
     Now the agent tries to commit its own recommendation.
     Refused.
     Not because we withheld the tool. The governance agent has the commit tool, is told to use
     it, and the scheduling service turns it down. The check lives behind an HTTP boundary
     where no prompt can argue with it.
     An identity in a request body is a claim. A claim cannot commit.
     """),
    ("06-human", 15, """
     The Producer approves. New version. New audit entry, naming who proposed it and who
     approved it.
     And the decision is written back to Grafana as an annotation, so the timeline shows why
     the schedule changed.
     """),
]


def tidy(text: str) -> str:
    return " ".join(text.split())


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if not os.getenv("GOOGLE_CLOUD_PROJECT"):
        print("warning: GOOGLE_CLOUD_PROJECT is not set; using whatever the default credentials point at.")

    client = texttospeech.TextToSpeechClient()
    OUT.mkdir(exist_ok=True)

    voice = texttospeech.VoiceSelectionParams(language_code=LANGUAGE, name=VOICE)
    config = texttospeech.AudioConfig(
        audio_encoding=texttospeech.AudioEncoding.MP3,
        speaking_rate=SPEAKING_RATE)

    total = 0.0
    print(f"{'shot':<22} {'words':>6} {'budget':>8} {'spoken':>8}")
    print("-" * 48)

    for stem, budget, raw in SHOTS:
        text = tidy(raw)
        response = client.synthesize_speech(
            input=texttospeech.SynthesisInput(text=text), voice=voice, audio_config=config)

        path = OUT / f"{stem}.mp3"
        path.write_bytes(response.audio_content)

        # No audio library needed: MP3 at this bitrate is close enough to linear that
        # bytes/bitrate is a usable estimate, and the point is to spot an overrun, not to
        # measure to the frame.
        spoken = len(response.audio_content) * 8 / 32000
        total += spoken
        flag = "" if spoken <= budget else "  ← OVER"
        print(f"{stem:<22} {len(text.split()):>6} {budget:>7}s {spoken:>7.1f}s{flag}")

    print("-" * 48)
    print(f"{'total':<22} {'':>6} {'180':>7}s {total:>7.1f}s")
    print(f"\nWritten to {OUT}. Anything marked OVER should be trimmed in narration.md")
    print("before you record, not stretched in the edit.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
