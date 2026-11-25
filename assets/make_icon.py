"""Builds app.ico from assets/icon-master.png (the "Sonic Funbox": a speaker box with headphones and a spark).
Run from the project root:  python assets/make_icon.py
Needs Pillow. Writes app.ico (16 to 256 px) and assets/icon-256.png (preview)."""
import os
from PIL import Image

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
master = Image.open(os.path.join(root, 'assets', 'icon-master.png')).convert('RGBA')

# The artwork itself, without the master's padding.
art = master.crop(master.getchannel('A').point(lambda v: 255 if v > 40 else 0).getbbox())

def render(size):
    # Small sizes (the tray) use less padding, so the artwork is as big as possible there.
    pad = 0.03 if size <= 32 else 0.08
    inner = size * (1 - 2 * pad)
    scale = inner / max(art.size)
    w, h = max(1, round(art.width * scale)), max(1, round(art.height * scale))
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    small = art.resize((w, h), Image.LANCZOS)
    img.paste(small, ((size - w) // 2, (size - h) // 2), small)
    return img

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [render(s) for s in sizes]
# Classic bitmap frames (not PNG): every Windows component and .NET's Icon class read them the same way.
images[-1].save(os.path.join(root, 'app.ico'), sizes=[(s, s) for s in sizes], append_images=images[:-1],
                bitmap_format='bmp')
images[-1].save(os.path.join(root, 'assets', 'icon-256.png'))
print('wrote app.ico', sizes)
