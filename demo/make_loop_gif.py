"""
Builds `docs/img/00-the-loop.gif` — the loop this project is about, in four frames.

Each frame is a real screenshot of the deployed demo or of the public Grafana dashboard,
captioned. It is a composed sequence rather than a screen recording, and the README says so:
a GIF that looks like a recording but is not would be the same kind of small lie this
codebase spends its time removing.

    python demo/make_loop_gif.py

Requires Pillow. The screenshots come from `docs/img/`; retake them with a browser against
the deployed URL when the UI changes.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

WIDTH = 1100
CAPTION_HEIGHT = 78
FRAME_MS = 3000

IMG = Path(__file__).resolve().parent.parent / "docs" / "img"

FRAMES = [
    ("05-mission-control.png",
     "1 · Grafana watches the shoot, not the app",
     "Days, budget, company moves and who is being paid to wait"),
    ("02-proposals.png",
     "2 · Every option is a separate CP-SAT run",
     "The deltas are the difference between two solved schedules, not estimates"),
    ("01-stripboard.png",
     "3 · A human Producer approves — agents cannot",
     "Proposed by sa-replanner, approved by Producer. The service refuses anyone else"),
    ("04-audit-trail.png",
     "4 · And the decision is on the record",
     "Disruption -> proposal -> approval, append-only"),
]

INK = (17, 17, 17)
PAPER = (250, 249, 246)
ACCENT = (232, 179, 32)


def font(size: int, bold: bool = False):
    """Pillow's default font ignores size, so try the usual Windows faces first."""
    for name in (("segoeuib.ttf", "arialbd.ttf") if bold else ("segoeui.ttf", "arial.ttf")):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def frame(path: Path, title: str, subtitle: str) -> Image.Image:
    shot = Image.open(path).convert("RGB")

    # Scale to a common width, then crop to a 16:10-ish window so a tall full-page capture
    # does not shrink its own text into illegibility.
    scale = WIDTH / shot.width
    shot = shot.resize((WIDTH, int(shot.height * scale)), Image.LANCZOS)
    body_height = min(shot.height, int(WIDTH * 0.62))
    shot = shot.crop((0, 0, WIDTH, body_height))

    canvas = Image.new("RGB", (WIDTH, body_height + CAPTION_HEIGHT), PAPER)
    canvas.paste(shot, (0, CAPTION_HEIGHT))

    draw = ImageDraw.Draw(canvas)
    draw.rectangle([0, 0, WIDTH, CAPTION_HEIGHT - 1], fill=PAPER)
    draw.rectangle([0, CAPTION_HEIGHT - 4, WIDTH, CAPTION_HEIGHT - 1], fill=ACCENT)
    draw.text((28, 14), title, font=font(27, bold=True), fill=INK)
    draw.text((28, 48), subtitle, font=font(17), fill=(90, 90, 90))
    return canvas


def main() -> int:
    missing = [name for name, _, _ in FRAMES if not (IMG / name).exists()]
    if missing:
        raise SystemExit(f"Missing screenshots in docs/img: {', '.join(missing)}")

    images = [frame(IMG / name, title, subtitle) for name, title, subtitle in FRAMES]

    # One canvas size for every frame, or the GIF jumps about as it plays.
    height = max(image.height for image in images)
    padded = []
    for image in images:
        canvas = Image.new("RGB", (WIDTH, height), PAPER)
        canvas.paste(image, (0, 0))
        padded.append(canvas.convert("P", palette=Image.ADAPTIVE, colors=192))

    out = IMG / "00-the-loop.gif"
    padded[0].save(out, save_all=True, append_images=padded[1:],
                   duration=FRAME_MS, loop=0, optimize=True)

    print(f"{out.relative_to(IMG.parent.parent)}: {len(padded)} frames, "
          f"{len(padded) * FRAME_MS / 1000:.0f}s, {out.stat().st_size / 1024:.0f} KB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
