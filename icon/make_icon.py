"""Draws the mc-shared app icon (original pixel art): two blocky player heads, each with its own gamepad.
Writes mc-shared.ico (16/32/48/256, crisp nearest-neighbour scaling) and preview.png (512 px).
Run:  python -I make_icon.py   (needs Pillow)"""
from pathlib import Path
from PIL import Image

GRID = [
    "..oooooooooooo..",
    ".occcccccccccco.",
    "occcccccccccccco",
    "ocgggggccrrrrrco",
    "ocgkgkgccrkrkrco",
    "obkgggkbbkrrrkbo",
    "obgkkkgbbrkkkrbo",
    "obGGGGGbbRRRRRbo",
    "obbbbbbbbbbbbbbo",
    "obbpppbbbbqqqbbo",
    "obpwpypbbqwqxqbo",
    "obpppppbbqqqqqbo",
    "obPbbbPbbQbbbQbo",
    "obbbbbbbbbbbbbbo",
    ".obbbbbbbbbbbbo.",
    "..oooooooooooo..",
]
COLORS = {
    ".": (0, 0, 0, 0),
    "o": (27, 42, 74, 255),      # outline navy
    "c": (127, 219, 255, 255),   # sky, light
    "b": (76, 201, 240, 255),    # sky
    "g": (138, 226, 52, 255),    # lime head
    "G": (93, 168, 19, 255),
    "r": (255, 159, 28, 255),    # orange head
    "R": (217, 120, 10, 255),
    "k": (27, 42, 74, 255),      # eyes / smile
    "p": (155, 93, 229, 255),    # purple pad
    "P": (106, 53, 184, 255),
    "q": (241, 91, 181, 255),    # pink pad
    "Q": (184, 50, 132, 255),
    "w": (255, 255, 255, 255),   # d-pad
    "y": (255, 214, 10, 255),    # buttons
    "x": (255, 214, 10, 255),
}

base = Image.new("RGBA", (16, 16))
for y, row in enumerate(GRID):
    assert len(row) == 16, (y, row)
    for x, ch in enumerate(row):
        base.putpixel((x, y), COLORS[ch])

out = Path(__file__).parent
sizes = [16, 32, 48, 256]
imgs = {s: base.resize((s, s), Image.NEAREST) for s in sizes}
imgs[256].save(out / "mc-shared.ico", format="ICO", sizes=[(s, s) for s in sizes],
               append_images=[imgs[s] for s in sizes if s != 256])
base.resize((512, 512), Image.NEAREST).save(out / "preview.png")
# macOS app icon (Pillow writes all .icns sizes from the 1024 px image)
base.resize((1024, 1024), Image.NEAREST).save(out / "UncleCraft.icns", format="ICNS")
print("wrote", out / "mc-shared.ico", out / "preview.png", out / "UncleCraft.icns")
