# Generates the extension icons (speech bubble with a download arrow).
# Usage: python3 tools/make-icons.py   (requires Pillow)
from pathlib import Path
from PIL import Image, ImageDraw

out = Path(__file__).resolve().parent.parent / "icons"
out.mkdir(exist_ok=True)
S = 512
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([16, 16, S - 16, S - 16], radius=110, fill=(36, 87, 197, 255))
# speech bubble
d.rounded_rectangle([96, 104, S - 96, 352], radius=60, fill=(255, 255, 255, 255))
d.polygon([(150, 340), (150, 430), (240, 345)], fill=(255, 255, 255, 255))
# download arrow
c = S // 2
d.rectangle([c - 26, 140, c + 26, 250], fill=(36, 87, 197, 255))
d.polygon([(c - 80, 240), (c + 80, 240), (c, 320)], fill=(36, 87, 197, 255))
for size in (16, 32, 48, 128):
    img.resize((size, size), Image.LANCZOS).save(out / f"icon{size}.png")
print("icons written to", out)
