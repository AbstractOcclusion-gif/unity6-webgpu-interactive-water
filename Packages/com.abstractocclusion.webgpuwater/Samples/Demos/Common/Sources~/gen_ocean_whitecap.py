"""Generates Generated/OceanWhitecap.png (+ _N normal): ONE seamless 1024 foam tile
for the FFT ocean whitecap. A single periodic tile (not a flipbook atlas) so hardware
Repeat wrap + full mips tile it with zero seams - the ocean gets its motion from the
foam advection in OceanFft.compute, so it needs no flipbook churn. Same bubble recipe
as gen_foam_flipbook.py (bright milky body + rounded cells + lit rims), periodic by
FFT construction. Run:  python3 gen_ocean_whitecap.py
"""
import os
import numpy as np
from PIL import Image

SIZE = 1024
SEED = 11
GRADIENT_GAIN = 8.0
HERE = os.path.dirname(os.path.abspath(__file__))
DST = os.path.join(HERE, "..", "OceanWhitecap.png")
DST_N = os.path.join(HERE, "..", "OceanWhitecap_N.png")

rng = np.random.default_rng(SEED)
ky, kx = np.meshgrid(np.fft.fftfreq(SIZE) * SIZE, np.fft.fftfreq(SIZE) * SIZE, indexing="ij")
kmag = np.hypot(kx, ky); kmag[0, 0] = 1.0

def band(kmin, kmax, power=1.0):
    amp = np.where((kmag >= kmin) & (kmag <= kmax), kmag ** -power, 0.0)
    phase = rng.uniform(0, 2 * np.pi, (SIZE, SIZE))
    return np.real(np.fft.ifft2(amp * np.exp(1j * phase)))
def norm(f): return f / (np.std(f) + 1e-9)
def smooth(a, b, x): t = np.clip((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t)
def ridge(w, width, p): return np.clip(1.0 - np.abs(w) / width, 0, 1) ** p

b = norm(band(5, 9)); m = norm(band(11, 17)); s = norm(band(22, 32))
f = norm(band(40, 70)); d = norm(band(1, 4, 2.4))
fill = np.clip(0.55*smooth(-0.2,1.0,b) + 0.42*smooth(-0.1,1.0,m)
               + 0.30*smooth(0.1,1.1,s) + 0.12*smooth(0.2,1.1,f), 0, 1)
rims = np.clip(np.maximum.reduce([0.9*ridge(b,0.9,1.1), 0.75*ridge(m,0.8,1.1),
                                  0.55*ridge(s,0.7,1.1), 0.32*ridge(f,0.6,1.1)]), 0, 1)
body = 0.56 + 0.44 * fill
img = (body*0.72 + rims*0.38) * np.clip(0.92 + 0.10*d, 0.6, 1.1)
lo, hi = np.percentile(img, 1), np.percentile(img, 99.7)
img = np.clip((img - lo) / (hi - lo), 0, 1) ** 0.80

g = (img * 255).astype(np.uint8)
Image.fromarray(np.dstack([g, g, g, np.full_like(g, 255)]), "RGBA").save(os.path.abspath(DST))

dx = (np.roll(img, -1, 1) - np.roll(img, 1, 1)) * 0.5 * GRADIENT_GAIN
dy = (np.roll(img, -1, 0) - np.roll(img, 1, 0)) * 0.5 * GRADIENT_GAIN
n = np.dstack([-dx, -dy, np.ones_like(img)]); n /= np.linalg.norm(n, axis=2, keepdims=True)
rgb = ((n * 0.5 + 0.5) * 255).astype(np.uint8)
Image.fromarray(np.dstack([rgb, np.full(rgb.shape[:2], 255, np.uint8)]), "RGBA").save(os.path.abspath(DST_N))
print("wrote", os.path.abspath(DST), "and _N")
