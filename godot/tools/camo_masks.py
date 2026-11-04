"""The camouflage masks the 3D tanks are painted with (Toon, camo_tex).

Each pattern comes from the jeep game's (jackal_port/ratel,
tools/camo_patterns.py: resources/3d/camo/patterns/<id>.png): one channel,
1024 px, tileable, its pixel the layer it is in - 0 the ground, 1, 2, 3 each
over the last - as 0, 85, 170, 255. Here it becomes an RGB mask, a layer a
channel - R is layer 1 and up, G layer 2 and up, B layer 3 - its edges
smoothed a little and the tile brought down to SIZE, so that the shader blends
its four colours by the masks with the texture's own filter, crisp at any
zoom, and no texture is made per colour. Written to assets/Images/Camo/.

The amoeba is left out: it is cut from a bought pack (Resource Boy,
"Camouflage Patterns") whose files may not be passed on apart from the game
they are in.

    py -3 godot/tools/camo_masks.py <ratel's resources/3d/camo/patterns>
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

PATTERNS = ["woodland", "digital", "splinter", "dazzle", "twotone"]
SIZE = 512
OUT = os.path.join(os.path.dirname(__file__), "..", "..", "assets", "Images", "Camo")


def mask(path: str) -> Image.Image:
    v = np.asarray(Image.open(path).convert("L"), dtype=np.int32)
    layer = (v + 42) // 85  # 0..3, the nearest of the four greys
    channels = []
    for k in (1, 2, 3):
        m = Image.fromarray(((layer >= k) * 255).astype(np.uint8))
        # Down to SIZE with a box filter: the edge comes out as the share of
        # the cell it covers, which is the antialiasing; a touch of blur on top
        # so that the mipmaps' first steps keep it round, wrapped by tiling
        # the tile three by three first, so the seam is no seam.
        big = Image.new("L", (m.width * 3, m.height * 3))
        for i in range(3):
            for j in range(3):
                big.paste(m, (i * m.width, j * m.height))
        big = big.filter(ImageFilter.GaussianBlur(1.2))
        big = big.crop((m.width, m.height, m.width * 2, m.height * 2))
        channels.append(big.resize((SIZE, SIZE), Image.BOX))
    return Image.merge("RGB", channels)


def main() -> None:
    src = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        "D:/Projects/Godot/jackal_port/ratel/resources/3d/camo/patterns")
    os.makedirs(OUT, exist_ok=True)
    for p in PATTERNS:
        out = os.path.join(OUT, p + ".png")
        mask(os.path.join(src, p + ".png")).save(out, optimize=True)
        print(out, os.path.getsize(out) // 1024, "KB")


if __name__ == "__main__":
    main()
