"""Generates Generated/FoamParticleAtlas_2x2.png: four foam-clump sprite variants,
256px each, in a 2x2 grid. FoamParticles.shader picks a variant per particle by its
seed. Alpha carries the shape (irregular lacy patch with a noisy radial falloff);
RGB is a flat cool white the shader tints and lights.

Run:  python3 gen_foam_particle_atlas.py  (writes into the parent Generated/ folder)
"""
import os
import numpy as np
from PIL import Image

CELL = 256
COLS, ROWS = 2, 2
SEED = 21
TINT = (242, 250, 255)
OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "FoamParticleAtlas_2x2.png")

rng = np.random.default_rng(SEED)
ky, kx = np.meshgrid(np.fft.fftfreq(CELL) * CELL, np.fft.fftfreq(CELL) * CELL, indexing="ij")
kmag = np.hypot(kx, ky)
kmag[0, 0] = 1.0
yy, xx = np.meshgrid(np.linspace(-1, 1, CELL), np.linspace(-1, 1, CELL), indexing="ij")
radius = np.hypot(xx, yy)


def noise(power, kmin, kmax):
    amp = np.where((kmag >= kmin) & (kmag <= kmax), kmag ** -power, 0.0)
    field = np.real(np.fft.ifft2(amp * np.exp(1j * rng.uniform(0, 2 * np.pi, (CELL, CELL)))))
    return field / (np.std(field) + 1e-9)


def variant():
    w = noise(1.5, 4, 30)
    w2 = noise(1.4, 10, 60)
    lace = np.clip(1 - np.abs(w) / 2.0, 0, 1) ** 2.2 * 0.8 \
         + np.clip(1 - np.abs(w2) / 2.0, 0, 1) ** 2.2 * 0.5
    edge = 0.62 + 0.16 * noise(1.8, 2, 8)  # noisy edge: not a clean disc
    shape = np.clip((edge - radius) / 0.28, 0, 1) ** 1.4
    alpha = np.clip(lace * 1.15, 0, 1) * shape
    return np.clip(alpha / max(np.percentile(alpha, 99.5), 1e-6), 0, 1)


sheet = np.zeros((CELL * ROWS, CELL * COLS))
for i in range(COLS * ROWS):
    r, c = divmod(i, COLS)
    sheet[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = variant()

alpha8 = (sheet * 255).astype(np.uint8)
rgba = np.dstack([np.full_like(alpha8, TINT[0]), np.full_like(alpha8, TINT[1]),
                  np.full_like(alpha8, TINT[2]), alpha8])
Image.fromarray(rgba, "RGBA").save(os.path.abspath(OUTPUT))
print("wrote", os.path.abspath(OUTPUT))
