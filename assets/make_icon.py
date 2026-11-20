"""Builds app.ico: the Material Icons "headphones" glyph (Apache License 2.0, Google) in white on a dark gray
rounded square. Run from the project root:  python assets/make_icon.py
Needs Pillow. Writes app.ico (16 to 256 px) and assets/icon-256.png (preview)."""
import os, re
from PIL import Image, ImageDraw

# Material Icons "headphones", 24 x 24 viewBox (see assets/headphones.svg and THIRD-PARTY-NOTICES.md).
PATH = ("M12 3c-4.97 0-9 4.03-9 9v7c0 1.1.9 2 2 2h4v-8H5v-1c0-3.87 3.13-7 7-7s7 3.13 7 7v1h-4v8h4"
        "c1.1 0 2-.9 2-2v-7c0-4.97-4.03-9-9-9z")
BACKGROUND = (48, 48, 48, 255)   # dark gray
GLYPH = (255, 255, 255, 255)     # white

def parse(d):
    """SVG path (M m C c S s H h V v L l Z z) -> list of closed polygons, in viewBox units."""
    tokens = re.findall(r'[MmCcSsHhVvLlZz]|-?(?:\d+\.?\d*|\.\d+)(?:e-?\d+)?', d)
    polys, cur, x, y, cmd, i, last_ctrl = [], [], 0.0, 0.0, None, 0, None
    def num():
        nonlocal i
        v = float(tokens[i]); i += 1; return v
    def bezier(p0, p1, p2, p3, steps=24):
        for k in range(1, steps + 1):
            t = k / steps; u = 1 - t
            yield (u**3*p0[0] + 3*u*u*t*p1[0] + 3*u*t*t*p2[0] + t**3*p3[0],
                   u**3*p0[1] + 3*u*u*t*p1[1] + 3*u*t*t*p2[1] + t**3*p3[1])
    while i < len(tokens):
        if re.match(r'[A-Za-z]', tokens[i]): cmd = tokens[i]; i += 1
        rel = cmd.islower(); c = cmd.upper()
        if c == 'Z':
            if cur: polys.append(cur); cur = []
            last_ctrl = None; continue
        if c == 'M':
            nx, ny = num(), num()
            x, y = (x + nx, y + ny) if rel else (nx, ny)
            if cur: polys.append(cur)
            cur = [(x, y)]; cmd = 'l' if rel else 'L'; last_ctrl = None
        elif c == 'L':
            nx, ny = num(), num(); x, y = (x + nx, y + ny) if rel else (nx, ny); cur.append((x, y)); last_ctrl = None
        elif c == 'H':
            n = num(); x = x + n if rel else n; cur.append((x, y)); last_ctrl = None
        elif c == 'V':
            n = num(); y = y + n if rel else n; cur.append((x, y)); last_ctrl = None
        elif c in 'CS':
            if c == 'C':
                x1, y1 = num(), num()
                if rel: x1, y1 = x + x1, y + y1
            else:
                x1, y1 = (2*x - last_ctrl[0], 2*y - last_ctrl[1]) if last_ctrl else (x, y)
            x2, y2, ex, ey = num(), num(), num(), num()
            if rel: x2, y2, ex, ey = x + x2, y + y2, x + ex, y + ey
            cur.extend(bezier((x, y), (x1, y1), (x2, y2), (ex, ey)))
            last_ctrl = (x2, y2); x, y = ex, ey
    if cur: polys.append(cur)
    return polys

def render(size):
    scale = 8                                     # draw big, then shrink: smooth edges
    big = size * scale
    img = Image.new('RGBA', (big, big), (0, 0, 0, 0))
    g = ImageDraw.Draw(img)
    g.rounded_rectangle([0, 0, big - 1, big - 1], radius=big * 0.18, fill=BACKGROUND)
    pad = big * (0.14 if size <= 24 else 0.17)    # small sizes: bigger glyph, so it stays readable
    k = (big - 2 * pad) / 18.0                    # the glyph spans x 3..21, y 3..21 of its 24-unit box
    for poly in parse(PATH):
        g.polygon([(pad + (px - 3) * k, pad + (py - 3) * k) for px, py in poly], fill=GLYPH)
    return img.resize((size, size), Image.LANCZOS)

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [render(s) for s in sizes]
# Classic bitmap frames (not PNG): every Windows component and .NET's Icon class read them the same way.
images[-1].save(os.path.join(root, 'app.ico'), sizes=[(s, s) for s in sizes], append_images=images[:-1],
                bitmap_format='bmp')
images[-1].save(os.path.join(root, 'assets', 'icon-256.png'))
print('wrote app.ico', sizes)
