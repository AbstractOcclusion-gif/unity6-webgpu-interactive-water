"""Generates Generated/FoamParticleAtlas_2x2.png: four foam-clump sprite variants,
256px each, in a 2x2 grid. FoamParticles.shader picks a variant per particle by its
seed. Alpha carries the shape; RGB carries SUBTLE per-pixel shading the shader tints
and lights (litColor * sprite.rgb) - bright bubble walls, slightly darker bubble
bodies, darkest in the crevices between bubbles.

v2 look: a BUBBLE RAFT instead of filtered noise. Real foam clumps are rafts of
packed bubbles: a few big bubbles, many small ones (power-law radii), bright
zero-thickness walls where bubbles meet air, and dark interstices. The alpha keeps
its holes/thin regions so FoamErosionLace still crumbles a dying particle through
its own structure. Shading stays radially unbiased (particles spawn with random
roll) - no directional light is baked in.

Budget-locked: mean alpha is normalized to the v1 atlas (0.109 overall / 0.428
in-shape) so scene foam density does not shift when the sheet is swapped.

Run:  python3 gen_foam_particle_atlas.py
"""
import os
import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

CELL = 256
COLS, ROWS = 2, 2
SEED = 21
TINT = (242, 250, 255)     # cool white; the shader's _Tint does the real coloring

# ---- bubble raft ---------------------------------------------------------------------
BUBBLE_COUNT = 380         # bubbles per variant before silhouette culling
RADIUS_MIN_PX = 3.0
RADIUS_MAX_PX = 34.0
RADIUS_POWER = 2.2         # p(r) ~ r^-power: many small bubbles, few big ones
CLUSTER_PULL = 0.45        # bias bubble centers toward the clump center (0 = uniform)
BODY_DOME = 0.55           # exponent shaping each bubble's filled body (<1 = domed)
WALL_WIDTH_FRACTION = 0.16 # bright wall thickness relative to bubble radius
WALL_WIDTH_MIN_PX = 0.9
WALL_WIDTH_MAX_PX = 2.6
WALL_GAIN = 0.65           # wall brightness added over the body in alpha

# ---- silhouette (keeps the v1 "noisy edge, not a disc" rule) -------------------------
EDGE_BASE = 0.62
EDGE_NOISE_AMP = 0.16
EDGE_SOFTNESS = 0.26
EDGE_SHAPE_POWER = 1.4

# ---- RGB shading (multiplied by the lit tint in-shader; keep it subtle) --------------
SHADE_WALL = 1.0           # bubble walls: full brightness
SHADE_BODY = 0.86          # bubble interiors: slightly milky-dark
SHADE_CREVICE = 0.70       # interstices between bubbles: darkest
SHADE_BLUR_PX = 1.2        # soften the shading so it never bands

# ---- budget lock (measured on the v1 atlas) -------------------------------------------
TARGET_MEAN_ALPHA_IN_SHAPE = 0.428
ALPHA_SHAPE_THRESHOLD = 0.05

OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                      "..", "Assets", "Textures", "FoamParticleAtlas_2x2.png")

rng = np.random.default_rng(SEED)
yy, xx = np.meshgrid(np.linspace(-1, 1, CELL), np.linspace(-1, 1, CELL), indexing="ij")
radius_field = np.hypot(xx, yy)

ky, kx = np.meshgrid(np.fft.fftfreq(CELL) * CELL, np.fft.fftfreq(CELL) * CELL, indexing="ij")
kmag = np.hypot(kx, ky)
kmag[0, 0] = 1.0


def noise(power, kmin, kmax):
    amp = np.where((kmag >= kmin) & (kmag <= kmax), kmag ** -power, 0.0)
    field = np.real(np.fft.ifft2(amp * np.exp(1j * rng.uniform(0, 2 * np.pi, (CELL, CELL)))))
    return field / (np.std(field) + 1e-9)


def power_law_radii(count):
    """Inverse-CDF sample of p(r) ~ r^-RADIUS_POWER on [RADIUS_MIN_PX, RADIUS_MAX_PX]."""
    u = rng.uniform(0.0, 1.0, count)
    exponent = 1.0 - RADIUS_POWER
    lo, hi = RADIUS_MIN_PX ** exponent, RADIUS_MAX_PX ** exponent
    return (lo + u * (hi - lo)) ** (1.0 / exponent)


def variant():
    """One clump: silhouette-masked bubble raft. Returns (alpha, shade) in 0..1."""
    edge = EDGE_BASE + EDGE_NOISE_AMP * noise(1.8, 2, 8)
    silhouette = np.clip((edge - radius_field) / EDGE_SOFTNESS, 0, 1) ** EDGE_SHAPE_POWER

    radii = power_law_radii(BUBBLE_COUNT)
    # centers biased toward the middle so big holes stay near the fringe
    cx = rng.uniform(-1, 1, BUBBLE_COUNT) * (1.0 - CLUSTER_PULL * rng.uniform(0, 1, BUBBLE_COUNT))
    cy = rng.uniform(-1, 1, BUBBLE_COUNT) * (1.0 - CLUSTER_PULL * rng.uniform(0, 1, BUBBLE_COUNT))

    body = np.zeros((CELL, CELL))
    wall = np.zeros((CELL, CELL))
    px_per_unit = CELL / 2.0
    for bx, by, r_px in zip(cx, cy, radii):
        r_units = r_px / px_per_unit
        # local patch only: keep it O(bubbles * patch), not O(bubbles * frame)
        x0 = max(int((bx - r_units * 1.6 + 1) * 0.5 * CELL), 0)
        x1 = min(int((bx + r_units * 1.6 + 1) * 0.5 * CELL) + 1, CELL)
        y0 = max(int((by - r_units * 1.6 + 1) * 0.5 * CELL), 0)
        y1 = min(int((by + r_units * 1.6 + 1) * 0.5 * CELL) + 1, CELL)
        if x0 >= x1 or y0 >= y1:
            continue
        d = np.hypot(xx[y0:y1, x0:x1] - bx, yy[y0:y1, x0:x1] - by)
        inside = np.clip(1.0 - d / r_units, 0.0, 1.0) ** BODY_DOME
        body[y0:y1, x0:x1] = np.maximum(body[y0:y1, x0:x1], inside)
        w_units = np.clip(r_px * WALL_WIDTH_FRACTION,
                          WALL_WIDTH_MIN_PX, WALL_WIDTH_MAX_PX) / px_per_unit
        ring = np.exp(-((d - r_units) / w_units) ** 2)
        wall[y0:y1, x0:x1] = np.maximum(wall[y0:y1, x0:x1], ring)

    alpha = np.clip(body + WALL_GAIN * wall, 0.0, 1.0) * silhouette

    # Shading: walls bright, bodies milky, crevices (in-shape but low body) darkest.
    crevice = np.clip(1.0 - body, 0.0, 1.0)
    shade = SHADE_BODY + (SHADE_WALL - SHADE_BODY) * wall \
        - (SHADE_BODY - SHADE_CREVICE) * crevice
    shade = gaussian_filter(np.clip(shade, SHADE_CREVICE, SHADE_WALL), SHADE_BLUR_PX)
    return alpha, shade


def lock_alpha_budget(alpha):
    """Scale so the in-shape mean matches the v1 atlas: swap-in with no density shift."""
    in_shape = alpha[alpha > ALPHA_SHAPE_THRESHOLD]
    if in_shape.size == 0:
        return alpha
    return np.clip(alpha * (TARGET_MEAN_ALPHA_IN_SHAPE / in_shape.mean()), 0.0, 1.0)


alpha_sheet = np.zeros((CELL * ROWS, CELL * COLS))
shade_sheet = np.ones((CELL * ROWS, CELL * COLS))
for i in range(COLS * ROWS):
    r, c = divmod(i, COLS)
    a, s = variant()
    alpha_sheet[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = lock_alpha_budget(a)
    shade_sheet[r * CELL:(r + 1) * CELL, c * CELL:(c + 1) * CELL] = s

alpha8 = (alpha_sheet * 255).astype(np.uint8)
rgb = [(np.clip(shade_sheet * (t / 255.0), 0, 1) * 255).astype(np.uint8) for t in TINT]
rgba = np.dstack(rgb + [alpha8])
Image.fromarray(rgba, "RGBA").save(os.path.abspath(OUTPUT))
print("wrote", os.path.abspath(OUTPUT))
