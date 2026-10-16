"""Draws PingTool/PingTool.ico: a dark rounded square with a green ping pulse (a flat line, one spike) and a dot.
Run:  python3 tools/make-icon.py   (needs Pillow), from any folder: the file is written next to the project, not to the folder you are in.
The .ico is committed; this script is how it was made."""
from pathlib import Path

import PIL
from PIL import Image, ImageChops, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
BACK, EDGE, GREEN = (48, 52, 58, 255), (88, 96, 108, 255), (80, 220, 100, 255)

def draw(size: int) -> Image.Image:
    s = size * 8                                   # drawn big, then reduced: smooth edges at every size
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    pad = s * 0.04
    d.rounded_rectangle([pad, pad, s - pad, s - pad], radius=s * 0.22, fill=BACK, outline=EDGE, width=max(2, int(s * 0.025)))
    w = max(3, int(s * (0.085 if size >= 32 else 0.13)))      # thicker line on small icons so it stays visible
    y = s * 0.58
    pts = [(s * 0.16, y), (s * 0.36, y), (s * 0.45, s * 0.28), (s * 0.55, s * 0.78), (s * 0.63, y), (s * 0.70, y)]
    d.line(pts, fill=GREEN, width=w, joint="curve")
    r = w * 0.95
    dot_x = 0.80
    if size < 32:
        # On the small sizes the line is thick (0.13) and the dot as thick: it reached back to the end of the line (0.70) and the two came out
        # as one blot. A smaller dot, a little further right, leaves an empty column between them (checked below).
        r = min(r, s * 0.07)
        dot_x = 0.83
    d.ellipse([s * dot_x - r, y - r, s * dot_x + r, y + r], fill=GREEN)
    return im.resize((size, size), Image.LANCZOS)


def dot_is_apart(frame: Image.Image, size: int) -> bool:
    """On the row of the flat line, the last stretch of green (the dot) is not joined to the one before it, with an empty column between."""
    row = round(0.58 * size)
    px = frame.convert("RGBA").load()
    runs = []
    for x in range(size):
        r, g, _, _ = px[x, row]
        if g > 150 and r < 150:
            if runs and x == runs[-1][1] + 1:
                runs[-1][1] = x
            else:
                runs.append([x, x])
    return len(runs) >= 2 and runs[-1][0] - runs[-2][1] >= 2

# Where the project is, found from where this script is (tools/ is next to PingTool/), not from the folder it was started in: from
# anywhere else the relative name failed, or wrote an icon in a PingTool folder that was not this one.
OUT = Path(__file__).resolve().parent.parent / "PingTool" / "PingTool.ico"
if not OUT.parent.is_dir():
    raise SystemExit(f"The folder {OUT.parent} is not there: this script belongs in tools/, next to the PingTool/ project folder.")
frames = [draw(n) for n in SIZES]
for n, frame in zip(SIZES, frames):
    if not dot_is_apart(frame, n):
        raise SystemExit(f"In the {n}px icon the dot touches the end of the line: the pulse would read as one blot at that size.")
frames[-1].save(OUT, format="ICO", sizes=[(n, n) for n in SIZES], append_images=frames[:-1])

# Every size is drawn on its own (a thicker line on the small ones), and only a Pillow that takes append_images for ICO keeps them: an
# older one reduces the biggest image to the other sizes instead, and the small icons come out thin with no sign of it. The file is read
# back and each size compared with the picture drawn for it, so that the fault shows here and not in a notification area.
written = Image.open(OUT)
for n, frame in zip(SIZES, frames):
    stored = written.ico.getimage((n, n)).convert("RGBA")
    if ImageChops.difference(stored, frame.convert("RGBA")).getbbox() is not None:
        raise SystemExit(f"The {n}px icon in {OUT} is not the one drawn for that size: this Pillow ({PIL.__version__}) does not keep "
                         "append_images for ICO and reduced the big image instead. Update Pillow and run again.")
print("wrote", OUT, SIZES, "- each size checked against the picture drawn for it")
