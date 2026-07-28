"""Generates Generated/FoamFlipbookNormal_4x4.png: a relief normal map derived
frame-by-frame from FoamFlipbook_4x4.png (run gen_foam_flipbook.py first), so the
foam's lighting relief animates and tiles exactly like the pattern it shades.

Encoding: raw RGB (n * 0.5 + 0.5), imported LINEAR (sRGB off) and NOT as a Unity
"Normal map" - WaterSurface.shader decodes it manually (rg * 2 - 1), which keeps
the decode identical on every backend. Gradients use wrapped differences so each
frame's normals tile seamlessly like the source frames.

Run:  python3 gen_foam_normal_flipbook.py
"""
import os
import numpy as np
from PIL import Image

CELL = 256
COLS, ROWS = 4, 4
GRADIENT_GAIN = 8.0  # baked relief steepness; _FoamNormalStrength scales it in-shader
HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "FoamFlipbook_4x4.png")
DST = os.path.join(HERE, "..", "FoamFlipbookNormal_4x4.png")

sheet = np.asarray(Image.open(SRC).convert("L"), dtype=np.float64) / 255.0
out = np.zeros((CELL * ROWS, CELL * COLS, 3))
for i in range(COLS * ROWS):
    r, c = divmod(i, COLS)
    height = sheet[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL]
    # wrapped central differences: frames tile, so the normals tile too
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5 * GRADIENT_GAIN
    dy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5 * GRADIENT_GAIN
    n = np.dstack([-dx, -dy, np.ones_like(height)])
    n /= np.linalg.norm(n, axis=2, keepdims=True)
    out[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = n

rgb = ((out * 0.5 + 0.5) * 255).astype(np.uint8)
rgba = np.dstack([rgb, np.full(rgb.shape[:2], 255, np.uint8)])
Image.fromarray(rgba, "RGBA").save(os.path.abspath(DST))
print("wrote", os.path.abspath(DST))
