"""Generates Generated/FoamFlipbook_4x4.png: 16 frames of surface-foam pattern,
256px each, laid out in a 4x4 grid (read left-to-right, top-to-bottom).

Guarantees the two properties the WaterSurface shader relies on:
- each frame tiles seamlessly in x/y (FFT synthesis is periodic by construction),
- the 16-frame sequence loops seamlessly in time (every spectral coefficient's
  phase advances an integer number of full cycles across the loop).

Look: packed rounded bubbles. Each of three size bands contributes a soft filled
cell body PLUS a bright zero-crossing rim (light catching a bubble wall), laid over
a milky floor so foam reads as a bright mass with darker holes - not sparse specks
on black (the failure mode of the previous grain-based version). NARROW spectral
bands keep the cells ROUND; broad bands would make hairy fibres instead of bubbles.
A slow density field breathes thick/thin coverage. All frames share one
normalization so playback doesn't flicker.

Run:  python3 gen_foam_flipbook.py  (writes into the parent Generated/ folder)
"""
import os
import numpy as np
from PIL import Image

FRAME_SIZE = 256
COLS, ROWS = 4, 4
FRAME_COUNT = COLS * ROWS
SEED = 7

# Bubble size bands (spectral radius kmin..kmax in cycles/frame) + temporal loop count.
# Narrow bands so zero-crossings curve into round cells; three scales = mixed bubbles.
BIG_BUBBLE_BAND = (5, 9, 1)
MED_BUBBLE_BAND = (11, 17, 2)
SMALL_BUBBLE_BAND = (22, 32, 2)
DENSITY_BAND = (1, 4, 1)     # slow thick/thin coverage breathing
SPARKLE_BAND = (44, 90, 3)   # faint high-frequency fizz inside the foam
DENSITY_POWER = 2.4          # steep falloff -> broad, smooth coverage blobs
SPARKLE_POWER = 1.2

# Per-band bubble-body fill window (smoothstep edges over the normalized field) and
# rim width. Fill = the milky cell interior; rim = the bright wall the light catches.
BIG_FILL, BIG_RIM_WIDTH = (-0.2, 1.0), 0.9
MED_FILL, MED_RIM_WIDTH = (-0.1, 1.0), 0.8
SMALL_FILL, SMALL_RIM_WIDTH = (0.1, 1.1), 0.7
RIM_POWER = 1.1

FILL_WEIGHTS = (0.55, 0.42, 0.30)  # big, med, small contribution to the bubble body
RIM_WEIGHTS = (0.90, 0.75, 0.55)   # big, med, small rim brightness (biggest wins via max)

FOAM_FLOOR = 0.52    # milky floor: foamy areas never fall to black (the "softer/milkier" look)
BODY_GAIN = 0.74     # weight of the milky bubble body
RIM_GAIN = 0.40      # weight of the bright bubble rims
SPARKLE_GAIN = 0.03  # faint fizz contribution
COVERAGE_BASE = 0.88            # mean coverage; 1 = fully covered
COVERAGE_AMP = 0.15             # how far the density field pushes coverage thick/thin
COVERAGE_MIN, COVERAGE_MAX = 0.55, 1.15
OUTPUT_GAMMA = 0.85             # <1 lifts midtones toward milky white
NORM_LO_PCT, NORM_HI_PCT = 1.0, 99.7  # shared tonal scale across all frames (no flicker)
# Crest-calibrated output window: Crest's shipped foam tile lives ENTIRELY in midtones
# (measured R channel: p5=42, p50=112, p95=177 of 255 - it never approaches white). The
# shader's sliding black-point threshold manufactures the whites, so the TEXTURE must not
# carry baked near-white blobs: distinct bright features are exactly what the eye locks
# onto when the tile repeats. The normalized field is compressed into this window instead
# of spanning full black..white (the old look's "too much contrast").
OUT_LO = 0.16   # ~= Crest p5 (42/255)
OUT_HI = 0.72   # ~= Crest p95 (183/255)

OUTPUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "FoamFlipbook_4x4.png")

rng = np.random.default_rng(SEED)
ky, kx = np.meshgrid(np.fft.fftfreq(FRAME_SIZE) * FRAME_SIZE,
                     np.fft.fftfreq(FRAME_SIZE) * FRAME_SIZE, indexing="ij")
kmag = np.hypot(kx, ky)
kmag[0, 0] = 1.0


def band(kmin, kmax, max_cycles, power=1.0):
    """Random time-looping spectral field, amplitude ~ 1/k^power inside [kmin, kmax]."""
    amp = np.where((kmag >= kmin) & (kmag <= kmax), kmag ** -power, 0.0)
    phase = rng.uniform(0, 2 * np.pi, (FRAME_SIZE, FRAME_SIZE))
    cycles = rng.integers(-max_cycles, max_cycles + 1, (FRAME_SIZE, FRAME_SIZE))
    return lambda t: np.real(np.fft.ifft2(amp * np.exp(1j * (phase + 2 * np.pi * cycles * t))))


def norm(field):
    return field / (np.std(field) + 1e-9)


def smoothstep(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def ridge(field, width, power):
    """Bright thin line along the field's zero crossing (a lit bubble wall)."""
    return np.clip(1.0 - np.abs(field) / width, 0.0, 1.0) ** power


big = band(*BIG_BUBBLE_BAND)
med = band(*MED_BUBBLE_BAND)
small = band(*SMALL_BUBBLE_BAND)
density = band(*DENSITY_BAND, power=DENSITY_POWER)
sparkle = band(*SPARKLE_BAND, power=SPARKLE_POWER)

frames = []
for i in range(FRAME_COUNT):
    t = i / FRAME_COUNT
    b, m, s = norm(big(t)), norm(med(t)), norm(small(t))
    d, z = norm(density(t)), norm(sparkle(t))

    fill = np.clip(FILL_WEIGHTS[0] * smoothstep(BIG_FILL[0], BIG_FILL[1], b)
                   + FILL_WEIGHTS[1] * smoothstep(MED_FILL[0], MED_FILL[1], m)
                   + FILL_WEIGHTS[2] * smoothstep(SMALL_FILL[0], SMALL_FILL[1], s), 0.0, 1.0)
    rims = np.clip(np.maximum(np.maximum(RIM_WEIGHTS[0] * ridge(b, BIG_RIM_WIDTH, RIM_POWER),
                                         RIM_WEIGHTS[1] * ridge(m, MED_RIM_WIDTH, RIM_POWER)),
                              RIM_WEIGHTS[2] * ridge(s, SMALL_RIM_WIDTH, RIM_POWER)), 0.0, 1.0)
    body = FOAM_FLOOR + (1.0 - FOAM_FLOOR) * fill
    value = body * BODY_GAIN + rims * RIM_GAIN + SPARKLE_GAIN * np.clip(z, 0.0, None)
    coverage = np.clip(COVERAGE_BASE + COVERAGE_AMP * d, COVERAGE_MIN, COVERAGE_MAX)
    frames.append(value * coverage)

stack = np.array(frames)
lo, hi = np.percentile(stack, NORM_LO_PCT), np.percentile(stack, NORM_HI_PCT)
stack = np.clip((stack - lo) / (hi - lo), 0.0, 1.0) ** OUTPUT_GAMMA
stack = OUT_LO + stack * (OUT_HI - OUT_LO)  # compress into the Crest-like midtone window

sheet = np.zeros((FRAME_SIZE * ROWS, FRAME_SIZE * COLS))
for i, frame in enumerate(stack):
    r, c = divmod(i, COLS)
    sheet[r * FRAME_SIZE:(r + 1) * FRAME_SIZE, c * FRAME_SIZE:(c + 1) * FRAME_SIZE] = frame

gray = (sheet * 255).astype(np.uint8)
rgba = np.dstack([gray, gray, gray, np.full_like(gray, 255)])
Image.fromarray(rgba, "RGBA").save(os.path.abspath(OUTPUT))
print("wrote", os.path.abspath(OUTPUT))
