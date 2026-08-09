"""
Turns the narration into one audio file per shot, plus the subtitles, with Google Cloud
Text-to-Speech (EV-32).

    pip install google-cloud-texttospeech
    gcloud auth application-default login
    export GOOGLE_CLOUD_PROJECT=stripboard-hack
    python demo/video/make_voiceover.py

Writes `demo/video/audio/01-….mp3` … and `demo/video/subtitles.srt`, and prints each shot's
spoken length against the window its heading claims, so an overrun is visible before it is
baked into a video.

**One file per shot rather than one long take**, because that is what makes editing survivable:
if a screen recording runs three seconds long you nudge one clip instead of re-cutting a
three-minute waveform.

**`narration.md` is the only copy of the words.** This script used to carry its own transcript
with a comment asking whoever edited one to remember the other, and `subtitles.srt` was a third
copy with timings typed by hand. Three copies of the same sentences is a guarantee that the
captions eventually say something the voice does not — silently, and only visible to whoever
watches with sound off, which on a judging panel is most of them. The headings in that file give
the time windows; the paragraphs give the caption breaks.

**Subtitle timings come from the synthesised audio, not from an estimate.** Each shot is timed
as it is generated and its cues are laid out across that measured length in proportion to their
word counts. Hand-typed timings were only ever right for the take they were written against.

**Why Google's own speech API and not another.** The hackathon's AI restriction is about what
the *product* calls at runtime, not about editing tools — but a project whose thesis is "every
runtime model here is Google Cloud" has no reason to narrate itself with somebody else's. If a
judge asks what the voice was, the answer should add to the story rather than need explaining.

The voice is a Studio one: they read long-form prose with sentence rhythm instead of the
word-by-word cadence that makes a demo sound automated. Change VOICE below to audition others.
"""

import os
import re
import sys
from pathlib import Path

try:
    from google.cloud import texttospeech
except ImportError:
    raise SystemExit("pip install google-cloud-texttospeech")

VOICE = "en-GB-Studio-B"     # British, to match a shoot set in Salford. en-US-Studio-O also good.
LANGUAGE = "en-GB"
SPEAKING_RATE = 0.96         # A touch under natural: this narration is dense with figures.

#: Longest caption before it is split. Two lines of roughly seventy characters is what fits a
#: 1080p frame without covering the thing being narrated.
CUE_CHARS = 150

#: Silence left between shots in the edit. The narration is cut into one file per shot and the
#: cuts need air, so the subtitle timeline has to model that gap or every caption after the
#: first drifts earlier than the voice that goes with it.
PAUSE_BETWEEN_SHOTS = 4.0

HERE = Path(__file__).parent
NARRATION = HERE / "narration.md"
OUT = HERE / "audio"
SRT = HERE / "subtitles.srt"


def parse_narration(path: Path):
    """
    Read `narration.md` into shots: (stem, seconds budgeted, paragraphs).

    A heading gives the window and the title; the blockquote under it gives the words, and a
    blank quote line starts a new paragraph. Paragraphs survive into the subtitles as cue
    boundaries, because the person who wrote the sentence knows better than a character count
    where a caption should break.
    """
    heading = re.compile(r"^##\s+(\d+):(\d\d)\s*[–-]\s*(\d+):(\d\d)\s*·\s*(.+?)\s*$")
    shots, current = [], None

    for line in path.read_text(encoding="utf-8").splitlines():
        match = heading.match(line)
        if match:
            m1, s1, m2, s2, title = match.groups()
            start = int(m1) * 60 + int(s1)
            stem = f"{len(shots) + 1:02d}-" + re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")
            current = {"stem": stem, "title": title, "budget": int(m2) * 60 + int(s2) - start,
                       "paragraphs": [[]]}
            shots.append(current)
        elif current is not None and line.startswith(">"):
            text = line[1:].strip()
            if text:
                current["paragraphs"][-1].append(text)
            elif current["paragraphs"][-1]:
                current["paragraphs"].append([])

    if not shots:
        raise SystemExit(f"No shots found in {path}. Headings must read '## 0:00 – 0:22 · Title'.")

    for shot in shots:
        shot["paragraphs"] = [" ".join(p) for p in shot["paragraphs"] if p]
        shot["text"] = " ".join(shot["paragraphs"])
    return shots


def cues(paragraphs):
    """Split paragraphs into caption-sized chunks, breaking at sentence ends."""
    out = []
    for paragraph in paragraphs:
        sentences = re.findall(r"[^.!?]+[.!?]+\s*|[^.!?]+$", paragraph)
        buffer = ""
        for sentence in sentences:
            sentence = sentence.strip()
            if buffer and len(buffer) + 1 + len(sentence) > CUE_CHARS:
                out.append(buffer)
                buffer = sentence
            else:
                buffer = f"{buffer} {sentence}".strip()
        if buffer:
            out.append(buffer)
    return out


def wrap(text: str, width: int = 72) -> str:
    """Two short lines read better on screen than one long one."""
    words, lines, line = text.split(), [], ""
    for word in words:
        if line and len(line) + 1 + len(word) > width:
            lines.append(line)
            line = word
        else:
            line = f"{line} {word}".strip()
    if line:
        lines.append(line)
    return "\n".join(lines)


def measure(path: Path, payload: bytes) -> float:
    """
    How long the file actually is.

    `ffprobe` when it is on PATH, because the arithmetic fallback below was wrong for a week:
    it assumed 32 kbps and this API returns 64, so every shot was reported at twice its length
    and the whole narration looked like it ran to four and a half minutes. A measurement nobody
    can check is a guess wearing a decimal point.
    """
    try:
        import subprocess
        out = subprocess.run(
            ["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0",
             str(path)], capture_output=True, text=True, timeout=30)
        if out.returncode == 0 and out.stdout.strip():
            return float(out.stdout.strip())
    except (OSError, ValueError, ImportError):
        pass
    # No ffprobe: fall back to bytes over the bitrate this API actually returns, and say so,
    # because a silent fallback to a different measurement is how the last error survived.
    print("  note: ffprobe not found; lengths below are estimated from file size at 64 kbps.")
    return len(payload) * 8 / 64000


def stamp(seconds: float) -> str:
    ms = int(round(seconds * 1000))
    h, ms = divmod(ms, 3_600_000)
    m, ms = divmod(ms, 60_000)
    s, ms = divmod(ms, 1000)
    return f"{h:02d}:{m:02d}:{s:02d},{ms:03d}"


def main() -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if not os.getenv("GOOGLE_CLOUD_PROJECT"):
        print("warning: GOOGLE_CLOUD_PROJECT is not set; using whatever the default credentials point at.")

    shots = parse_narration(NARRATION)
    client = texttospeech.TextToSpeechClient()
    OUT.mkdir(exist_ok=True)

    voice = texttospeech.VoiceSelectionParams(language_code=LANGUAGE, name=VOICE)
    config = texttospeech.AudioConfig(
        audio_encoding=texttospeech.AudioEncoding.MP3, speaking_rate=SPEAKING_RATE)

    clock, blocks, overruns = 0.0, [], []
    print(f"{'shot':<26} {'words':>6} {'window':>8} {'spoken':>8}  {'lands at':>10}")
    print("-" * 66)

    for shot in shots:
        response = client.synthesize_speech(
            input=texttospeech.SynthesisInput(text=shot["text"]), voice=voice, audio_config=config)
        path = OUT / f"{shot['stem']}.mp3"
        path.write_bytes(response.audio_content)
        spoken = measure(path, response.audio_content)

        # Lay this shot's cues across its measured length, by share of words. Proportional
        # rather than equal: "Refused." is one word and should not hold the screen as long as
        # a twenty-word sentence.
        shot_cues = cues(shot["paragraphs"])
        counts = [max(1, len(c.split())) for c in shot_cues]
        at = clock
        for cue, count in zip(shot_cues, counts):
            span = spoken * count / sum(counts)
            blocks.append((at, at + span, wrap(cue)))
            at += span

        starts_at, clock = clock, clock + spoken + PAUSE_BETWEEN_SHOTS
        over = spoken > shot["budget"]
        overruns.append(shot["title"]) if over else None
        print(f"{shot['stem']:<26} {len(shot['text'].split()):>6} "
              f"{shot['budget']:>7}s {spoken:>7.1f}s  "
              f"{stamp(starts_at)[3:8]}–{stamp(starts_at + spoken)[3:8]}"
              f"{'  ← OVER' if over else ''}")

    SRT.write_text("".join(
        f"{i}\n{stamp(a)} --> {stamp(b)}\n{text}\n\n" for i, (a, b, text) in enumerate(blocks, 1)
    ), encoding="utf-8")

    clock -= PAUSE_BETWEEN_SHOTS      # no trailing pause after the last shot
    print("-" * 66)
    print(f"{'total, with pauses':<26} {'':>6} {'180':>7}s {clock:>7.1f}s  ends {stamp(clock)[3:8]}")
    print(f"\n{len(blocks)} cues written to {SRT.name}, timed against the audio above")
    print(f"and a {PAUSE_BETWEEN_SHOTS:.0f}-second gap between shots.")
    if clock > 180:
        print("OVER THREE MINUTES. Judges are told to watch the first three minutes only.")
    if overruns:
        print("Trim in narration.md, not in the edit: " + ", ".join(overruns))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
