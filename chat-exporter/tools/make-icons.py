# Generates the extension icons (speech bubble with a download arrow).
#   icon<size>.png        default (blue): page is not a ChatGPT/Claude chat
#   icon-green-<size>.png the open chat is marked "Сохранять"
#   icon-red-<size>.png   the open chat is not marked
# Usage: python3 tools/make-icons.py   (requires Pillow)
from pathlib import Path
from PIL import Image, ImageDraw

out = Path(__file__).resolve().parent.parent / "icons"
out.mkdir(exist_ok=True)
S = 512
VARIANTS = {"": (36, 87, 197), "-green": (30, 142, 62), "-red": (210, 48, 40)}

for suffix, color in VARIANTS.items():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([16, 16, S - 16, S - 16], radius=110, fill=color + (255,))
    d.rounded_rectangle([96, 104, S - 96, 352], radius=60, fill=(255, 255, 255, 255))
    d.polygon([(150, 340), (150, 430), (240, 345)], fill=(255, 255, 255, 255))
    c = S // 2
    d.rectangle([c - 26, 140, c + 26, 250], fill=color + (255,))
    d.polygon([(c - 80, 240), (c + 80, 240), (c, 320)], fill=color + (255,))
    sizes = (16, 32, 48, 128) if suffix == "" else (16, 32)
    for size in sizes:
        img.resize((size, size), Image.LANCZOS).save(out / f"icon{suffix}{size}.png")
print("icons written to", out)
