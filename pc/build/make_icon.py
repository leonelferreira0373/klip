"""Generate the Klip .ico from a programmatic mark.

Replace icon.ico with brand art whenever it's ready — keep the same path.
Run: python build/make_icon.py
"""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


HERE = Path(__file__).parent
OUT = HERE / "icon.ico"

SIZES = [256, 128, 64, 48, 32, 16]
BG = (12, 14, 22, 255)
ACCENT = (0, 217, 255, 255)
ACCENT_DIM = (0, 140, 200, 255)


def _find_font(size: int) -> ImageFont.FreeTypeFont:
    for name in ("Arial Black.ttf", "ariblk.ttf", "arial.ttf", "Arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def render(size: int) -> Image.Image:
    img = Image.new("RGBA", (size, size), BG)
    draw = ImageDraw.Draw(img)

    inset = max(1, size // 20)
    draw.rounded_rectangle(
        (inset, inset, size - inset, size - inset),
        radius=size // 6,
        fill=BG,
        outline=ACCENT_DIM,
        width=max(1, size // 64),
    )

    font_size = int(size * 0.62)
    font = _find_font(font_size)
    text = "K"
    bbox = draw.textbbox((0, 0), text, font=font)
    tw = bbox[2] - bbox[0]
    th = bbox[3] - bbox[1]
    tx = (size - tw) // 2 - bbox[0]
    ty = (size - th) // 2 - bbox[1] - size // 24
    draw.text((tx, ty), text, fill=ACCENT, font=font)

    return img


def main() -> None:
    images = [render(s) for s in SIZES]
    images[0].save(OUT, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"Wrote {OUT} ({len(SIZES)} sizes)")


if __name__ == "__main__":
    main()
