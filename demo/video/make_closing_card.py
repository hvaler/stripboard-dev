"""
The last three seconds: the loop as one picture, and where to go and see it.

    python demo/video/make_closing_card.py     ->  demo/video/closing-card.png  (1920x1080)

**Why a card and not the ASCII diagram from the README.** That diagram is built to be read at
a terminal, and at video bitrates its box-drawing characters turn to mush. This says the same
thing in four boxes, sized to be legible on a laptop at 1080p with a viewer who is not leaning
in.

**Why it goes at the end and not the start.** The obvious instinct is to open on the
architecture so the viewer knows what they are looking at. That is backwards: a diagram before
the demonstration is four rectangles nobody has a reason to care about, and every second spent
on it is a second not spent showing the thing work. Placed last, the same four boxes are a
summary of what the viewer has just watched happen — which is when a diagram earns its keep.

Dark, because the shots before it are the light-background web app and the eye needs a full
stop.
"""

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

W, H = 1920, 1080

INK = (18, 18, 20)
PAPER = (247, 245, 240)
MUTED = (150, 150, 155)
ACCENT = (232, 179, 32)
GRAFANA = (245, 138, 40)

URL = "stripboard-web-wc7oib7k6q-ew.a.run.app"

# (title, line under it, colour of the title)
STEPS = [
    ("GRAFANA", "a rule on the shoot fires", GRAFANA),
    ("CONFLICT SENTINEL", "reads it back over MCP", PAPER),
    ("CP-SAT", "prices the options", PAPER),
    ("THE PRODUCER", "approves — agents cannot", ACCENT),
]


def font(size: int, bold: bool = False):
    for name in (("segoeuib.ttf", "arialbd.ttf") if bold else ("segoeui.ttf", "arial.ttf")):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def centred(draw, text, f, cx, y, fill):
    left, top, right, bottom = draw.textbbox((0, 0), text, font=f)
    draw.text((cx - (right - left) / 2, y), text, font=f, fill=fill)
    return bottom - top


def main() -> int:
    card = Image.new("RGB", (W, H), INK)
    draw = ImageDraw.Draw(card)

    centred(draw, "The LLM formulates, the solver decides, a human approves.",
            font(52, bold=True), W / 2, 150, PAPER)

    # Four boxes across the middle, with the arrows between them.
    box_w, box_h, gap = 380, 220, 66
    total = len(STEPS) * box_w + (len(STEPS) - 1) * gap
    x = (W - total) / 2
    top = 400

    for index, (title, caption, colour) in enumerate(STEPS):
        draw.rounded_rectangle([x, top, x + box_w, top + box_h], radius=10,
                               outline=(64, 64, 70), width=2)
        centred(draw, title, font(34, bold=True), x + box_w / 2, top + 68, colour)
        centred(draw, caption, font(24), x + box_w / 2, top + 126, MUTED)

        if index < len(STEPS) - 1:
            mid = top + box_h / 2
            draw.line([x + box_w + 14, mid, x + box_w + gap - 14, mid], fill=ACCENT, width=3)
            draw.polygon([(x + box_w + gap - 14, mid), (x + box_w + gap - 28, mid - 8),
                          (x + box_w + gap - 28, mid + 8)], fill=ACCENT)
        x += box_w + gap

    # The return leg. This is the half most demos leave out, so it gets drawn rather than
    # implied: the decision goes back to Grafana as an annotation, which is what makes the
    # integration bidirectional instead of a place results are posted.
    left_x = (W - total) / 2 + box_w / 2
    right_x = (W + total) / 2 - box_w / 2
    y = top + box_h + 92
    draw.line([right_x, top + box_h + 4, right_x, y], fill=(90, 90, 96), width=2)
    draw.line([left_x, y, right_x, y], fill=(90, 90, 96), width=2)
    draw.line([left_x, y, left_x, top + box_h + 4], fill=(90, 90, 96), width=2)
    draw.polygon([(left_x, top + box_h + 4), (left_x - 8, top + box_h + 20),
                  (left_x + 8, top + box_h + 20)], fill=(90, 90, 96))
    centred(draw, "the decision is written back as an annotation",
            font(23), W / 2, y + 16, MUTED)

    centred(draw, URL, font(40, bold=True), W / 2, H - 220, PAPER)
    centred(draw, "Gemini on Vertex AI  ·  Google OR-Tools CP-SAT  ·  Grafana Cloud MCP",
            font(26), W / 2, H - 150, MUTED)

    draw.rectangle([0, H - 12, W, H], fill=ACCENT)

    out = Path(__file__).with_name("closing-card.png")
    card.save(out)
    print(f"{out.name}: {W}x{H}, {out.stat().st_size / 1024:.0f} KB")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
