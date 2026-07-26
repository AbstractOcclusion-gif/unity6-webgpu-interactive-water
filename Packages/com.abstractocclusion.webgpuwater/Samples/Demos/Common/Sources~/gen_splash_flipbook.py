"""Generates the crown-splash flipbook set (8x8 grid, 64 frames, 128px per frame,
read left-to-right, top-to-bottom), KWS-style CHANNEL-PACKED data (import LINEAR,
not sRGB):

  SplashFlipbook_8x8.png
    R = mass      - splash opacity shape (what used to live in alpha)
    G = shine     - the large rim-droplet cores only; the shader CUBES this for
                    tight sun sparkle
    B = dissolve  - static smooth noise; the lifetime-erosion burn threshold, so
                    the splash disintegrates into organic patches instead of
                    fading uniformly
    A = thickness - blurred mass; stretches the soft-particle fade band on thick
                    parts so edges dissolve first at intersections

  SplashFlipbookLightA_8x8.png / SplashFlipbookLightB_8x8.png
    Six-way directional lightmaps (Unreal-style): average transmittance toward
    the light for sunlight arriving from each principal axis, so the shader can
    relight the crown for ANY sun direction instead of a single scalar.
      LightA: R = +X (billboard right)   G = +Y (top)     B = +Z (toward viewer)
      LightB: R = -X (billboard left)    G = -Y (below)   B = -Z (behind)
    Baked by voxelizing the true 3D particle field per frame and integrating
    extinction along each axis (see SIXWAY_* constants).

Consumed by SplashParticles.shader with _PackedChannels = 1; the light sheets
light up when the material also sets _SixWay = 1 (materials on the legacy path
still read old-style alpha sheets). Plays once over a particle's lifetime (see
WaterSplashEmitter.ConfigureCrown) and ends on empty frames so the particle
vanishes cleanly.

Built from a tiny crown-anatomy sim viewed as a billboard projection. A real
crown is not a cloud of dots - it is, in order:
  1. a CONNECTED thin liquid sheet (the crown wall), rendered here as a surface
     of revolution between an expanding base ring and a ballistic rim,
  2. which PERFORATES into lace as it stretches (hole mask grows with age, hole
     rims keep the mass the holes lose),
  3. while the rim destabilises into LIGAMENTS at regular azimuthal lobes
     (Plateau-Rayleigh): tapered strands strung between the receding rim and
     the fast detaching tip, beading up before they pinch off,
  4. shedding large tip DROPLETS (velocity-stretched; these alone feed the
     shine channel),
  5. over a brief fine impact MIST at the base.
Back-of-ring points are dimmed for depth, a soft-knee tonemap keeps mid-life
frames readable, and a floor cut removes background speckle.

Requires scipy.  Run:  python3 gen_splash_flipbook.py
"""
import os
import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

# ---- sheet layout -------------------------------------------------------------------
FRAME_SIZE = 128
COLS, ROWS = 8, 8
FRAME_COUNT = COLS * ROWS
DURATION = 0.8          # seconds of simulated splash across the sequence
GRAVITY = 5.0           # world units / s^2
WORLD_HEIGHT = 1.6      # world y span mapped to the frame height
SEED = 11

# ---- crown wall (the connected sheet) -----------------------------------------------
BASE_RADIUS = 0.14          # crater ring radius at birth
BASE_EXPAND_SPEED = 0.35    # how fast the base ring creeps outward
RIM_UP_SPEED = 3.30         # rim launch speed, vertical
RIM_OUT_SPEED = 1.05        # rim launch speed, radial
RIM_DRAG = 1.5              # 1/s exponential decay on the rim velocity (surface tension)
SHEET_AZ_SAMPLES = 384      # azimuthal tessellation of the wall
SHEET_SPAN_SAMPLES = 26     # samples from base (s=0) to rim (s=1)
SHEET_BULGE = 0.10          # outward mid-span bow of the wall profile
SHEET_WEIGHT = 0.050        # splat weight per wall sample at birth
SHEET_THINNING = 1.0        # 1/s - wall opacity decay as the sheet stretches
SHEET_SIGMA = 1.1           # splat blur for wall samples
SHEET_STRIATION = 0.35      # azimuthal thick/thin banding of the wall at the lobes
SHEET_RIM_COLLECT = 0.8     # extra mass collected at the rim (top of the wall)
SHEET_RIM_COLLECT_SPAN = 0.7  # rim brightening ramps in from this span fraction
LOBE_COUNT = 22             # azimuthal crown lobes (rim scallops + ligament roots)
LOBE_RIM_GAIN = 0.16        # lobes modulate rim height by +-this fraction

# ---- sheet perforation (lace) -------------------------------------------------------
LACE_START_TIME = 0.10      # holes start opening once the wall has stretched
LACE_FULL_TIME = 0.75       # by here the wall is all lace
LACE_NOISE_AZ_PERIODS = 9   # lobes of the hole noise around the ring
LACE_NOISE_SIGMA = 1.6      # smoothness of the hole noise (in noise-grid texels)
LACE_RIM_BAND = 0.10        # half-width of the bright band around each hole rim
LACE_RIM_GAIN = 1.6         # holes push their mass into their rims

# ---- ligaments (Plateau-Rayleigh strands at the lobes) ------------------------------
LIG_START_TIME = 0.10       # strands appear as the rim recedes
LIG_LIFE = 0.34             # seconds a strand lives before it has fully pinched
LIG_SAMPLES = 14            # sub-stamps along one strand
LIG_TIP_UP_BOOST = 1.30     # tip (detaching) end launches faster than the rim...
LIG_TIP_OUT_BOOST = 1.45    # ...so the strand stretches over time
LIG_WEIGHT = 1.5            # splat weight at the root, tapering to the tip
LIG_SIGMA = 1.3             # splat blur for strand samples
LIG_BEAD_PERIODS = 3.5      # Plateau-Rayleigh beads that develop along a strand
LIG_BEAD_DEPTH = 0.85       # how deeply the beads modulate the strand near pinch-off

# ---- rim droplets (ligament tips) ---------------------------------------------------
DROPLET_PER_LOBE = 5        # large droplets shed per lobe over the splash
DROPLET_BIRTH_MIN = 0.12    # first pinch-off
DROPLET_BIRTH_MAX = 0.50    # last pinch-off
DROPLET_SIZE_MIN = 1.6
DROPLET_SIZE_MAX = 3.0
DROPLET_AZ_JITTER = 0.10    # radians of scatter around the parent lobe azimuth
DROPLET_STRETCH_DT = 0.014  # velocity-stretch sub-stamp offset (3 stamps)
DROPLET_SIGMA = 2.0
DROPLET_SHINE_SIGMA = 1.2   # tight blur: shine stays on discrete cores
DROPLET_STAMP_GAIN = 0.9    # droplet mass contribution (they carry the mid/late frames)
DROPLET_FADE_HEAD = 1.35    # droplet fade start offset (>1 = most of life at full mass)

# ---- impact mist (brief fine spray at the base) -------------------------------------
MIST_COUNT = 1400
MIST_UP_SPEED_MEAN = 2.2
MIST_UP_SPEED_STD = 0.55
MIST_OUT_SPEED_MEAN = 1.1
MIST_OUT_SPEED_STD = 0.4
MIST_BIRTH_MAX = 0.05       # all mist is born in the first instants
MIST_LIFE = 0.60            # then rains out quickly
MIST_WEIGHT = 0.45
MIST_SIGMA = 1.0

# ---- Worthington jet (cavity-collapse rebound at the crater center) ------------------
# The missing act of most splash systems: after the crown, the air cavity collapses
# and fires a thick glassy column straight up, which pinches fat droplets at its tip
# and sinks back. Timed so the column is fully retracted BEFORE the end fade bites -
# the sequence must still end on empty frames.
JET_START_TIME = 0.22       # cavity collapse: well after the crown rim has receded
JET_UP_SPEED = 2.0          # tip launch speed -> apex at t ~= 0.62, near crown height
JET_RADIUS = 0.065          # column radius at the base (world units)
JET_TIP_TAPER = 0.45        # tip radius as a fraction of the base radius
JET_RING_SAMPLES = 10       # points around the column per height (real 3D for the bake)
JET_HEIGHT_SAMPLES = 26     # points along the column
JET_WEIGHT = 0.22           # per-point splat weight; the column reads near-solid
JET_SIGMA = 1.6
JET_WOBBLE = 0.012          # slight centerline wobble so the column isn't CG-straight
JET_RETRACT_TIME = 0.12     # seconds for the falling column to sink back into the water
JET_DROP_SIZES = (3.6, 2.7)     # fat tip droplets pinched off at apex
JET_DROP_KICKS = (0.55, 0.25)   # upward speed each droplet keeps at pinch
JET_DROP_DRIFT = (0.18, -0.13)  # slight sideways drift (world x) per droplet
JET_DROP_LIFE = 0.14        # tip droplets die this long after pinch (sequence must end empty)

# ---- six-way light bake -------------------------------------------------------------
SIXWAY_GRID = 56            # voxel grid resolution per axis (x, z; y uses the same count)
SIXWAY_EXTINCTION = 90.0    # optical density of the splat field (higher = deeper shadows)
SIXWAY_CONTRAST = 1.6       # gamma on baked transmittance: pushes shading contrast so
                            # the thin wall still reads as a lit 3D form, not flat mist
SIXWAY_MIN_WEIGHT = 1e-4    # texels lighter than this fall back to fully-lit
# billboard axes in bake space: +X = right, +Y = up, +Z = toward the viewer
SIXWAY_AXES = ((0, +1), (1, +1), (2, +1), (0, -1), (1, -1), (2, -1))

# ---- post ---------------------------------------------------------------------------
SPECKLE_FLOOR = 0.02        # subtracted before the tonemap: kills background dust
TONEMAP_KNEE = 0.18
SHINE_KNEE = 0.15           # harder knee: shine stays confined to the bright cores
END_FADE_START = 0.75       # fraction of the sequence where the global fade-out begins
THICKNESS_SIGMA = 4.0       # blur that turns mass into the fake-depth thickness channel
NOISE_SIGMA = 2.5           # feature size of the dissolve noise
NOISE_FLOOR = 0.05          # keeps every texel erodable (never sticks at 0)
MASS_PRESENCE_GAMMA = 0.72  # <1 lifts lace/strand mid-tones so the crown keeps screen presence
OUT_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "Textures")
OUTPUT_PACKED = os.path.join(OUT_DIR, "SplashFlipbook_8x8.png")
OUTPUT_LIGHT_A = os.path.join(OUT_DIR, "SplashFlipbookLightA_8x8.png")
OUTPUT_LIGHT_B = os.path.join(OUT_DIR, "SplashFlipbookLightB_8x8.png")

rng = np.random.default_rng(SEED)

# Per-lobe randomness: azimuth (regular + jitter = Plateau-Rayleigh spacing), phase.
lobe_az = (np.arange(LOBE_COUNT) + rng.uniform(-0.25, 0.25, LOBE_COUNT)) \
    * (2.0 * np.pi / LOBE_COUNT)
lobe_phase = rng.uniform(0.0, 2.0 * np.pi, LOBE_COUNT)
lig_birth = rng.uniform(LIG_START_TIME, LIG_START_TIME + 0.12, LOBE_COUNT)

# Rim droplets: children of the lobes, so azimuths stay correlated with the strands.
# Each droplet INHERITS the rim's position and velocity at its birth instant (tip-boosted)
# instead of relaunching from the water - rim droplets continue the rim's motion.
drop_lobe = np.repeat(np.arange(LOBE_COUNT), DROPLET_PER_LOBE)
DROPLET_COUNT = drop_lobe.size
drop_az = lobe_az[drop_lobe] + rng.normal(0.0, DROPLET_AZ_JITTER, DROPLET_COUNT)
drop_birth = rng.uniform(DROPLET_BIRTH_MIN, DROPLET_BIRTH_MAX, DROPLET_COUNT)
drop_vout_boost = LIG_TIP_OUT_BOOST * rng.normal(1.0, 0.20, DROPLET_COUNT)
drop_vup_boost = LIG_TIP_UP_BOOST * rng.normal(1.0, 0.14, DROPLET_COUNT)
drop_size = rng.uniform(DROPLET_SIZE_MIN, DROPLET_SIZE_MAX, DROPLET_COUNT)

# Impact mist.
mist_az = rng.uniform(0.0, 2.0 * np.pi, MIST_COUNT)
mist_r0 = rng.normal(BASE_RADIUS, 0.02, MIST_COUNT)
mist_vup = rng.normal(MIST_UP_SPEED_MEAN, MIST_UP_SPEED_STD, MIST_COUNT)
mist_vout = rng.normal(MIST_OUT_SPEED_MEAN, MIST_OUT_SPEED_STD, MIST_COUNT)
mist_birth = rng.uniform(0.0, MIST_BIRTH_MAX, MIST_COUNT)

# Lace noise lives on the (azimuth, span) surface of the wall so holes are attached
# to the sheet, not to the screen. Periodic along azimuth.
_lace_grid = rng.uniform(0.0, 1.0, (128, 64))
_lace_grid = gaussian_filter(_lace_grid, LACE_NOISE_SIGMA, mode=("wrap", "nearest"))
_lace_grid = (_lace_grid - _lace_grid.min()) / max(np.ptp(_lace_grid), 1e-6)


def rim_state(az, t):
    """Rim ring position at time t: drag-decayed launch + gravity, scalloped by lobes."""
    decay = (1.0 - np.exp(-RIM_DRAG * t)) / RIM_DRAG
    lobes = 1.0 + LOBE_RIM_GAIN * np.cos(LOBE_COUNT * az + np.interp(
        az, lobe_az, lobe_phase, period=2.0 * np.pi))
    radius = BASE_RADIUS + RIM_OUT_SPEED * decay
    height = (RIM_UP_SPEED * decay - 0.5 * GRAVITY * t * t) * lobes
    return radius, np.maximum(height, 0.0)


def rim_velocity(t):
    """Rim ring velocity at time t (radial, vertical) - what a strand or droplet
    inherits at the moment it leaves the rim."""
    damped = np.exp(-RIM_DRAG * t)
    return RIM_OUT_SPEED * damped, RIM_UP_SPEED * damped - GRAVITY * t


# Droplet birth state: rim position + tip-boosted rim velocity at the pinch instant.
drop_r0, drop_h0 = rim_state(drop_az, drop_birth)
_drop_rim_vout, _drop_rim_vup = rim_velocity(drop_birth)
drop_vout = _drop_rim_vout * drop_vout_boost
drop_vup = np.maximum(_drop_rim_vup, 0.0) * drop_vup_boost


def ballistic(az, r0, h0, vout, vup, birth, t):
    """3D position of free droplets/mist continuing from a launch state; alive mask
    included (dead = not yet born, or fallen back through the surface)."""
    age = np.maximum(t - birth, 0.0)
    alive = (t >= birth)
    radius = r0 + vout * age
    y = h0 + vup * age - 0.5 * GRAVITY * age * age
    return np.sin(az) * radius, y, np.cos(az) * radius, age, alive & (y > -0.02)


def lace_mask(az, span, t):
    """Wall opacity from the perforation noise: 1 = intact, 0 = hole. Hole rims get
    the mass the holes lose (bright band around each hole edge)."""
    u = (az / (2.0 * np.pi) * _lace_grid.shape[0]).astype(int) % _lace_grid.shape[0]
    v = np.clip((span * (_lace_grid.shape[1] - 1)).astype(int), 0, _lace_grid.shape[1] - 1)
    noise = _lace_grid[u, v]
    burn = np.clip((t - LACE_START_TIME) / (LACE_FULL_TIME - LACE_START_TIME), 0.0, 1.0)
    intact = noise > burn
    rim_band = np.abs(noise - burn) < LACE_RIM_BAND
    return intact * (1.0 + rim_band * (LACE_RIM_GAIN - 1.0))


def sheet_points(t):
    """The crown wall as (x, y, z, weight) samples: a lofted surface between the
    expanding base ring and the ballistic rim, bowed outward, eaten by the lace."""
    az = np.linspace(0.0, 2.0 * np.pi, SHEET_AZ_SAMPLES, endpoint=False)
    span = np.linspace(0.0, 1.0, SHEET_SPAN_SAMPLES)
    az_g, span_g = np.meshgrid(az, span, indexing="ij")
    az_f, span_f = az_g.ravel(), span_g.ravel()

    rim_r, rim_h = rim_state(az_f, t)
    base_r = BASE_RADIUS + BASE_EXPAND_SPEED * t
    bulge = SHEET_BULGE * np.sin(np.pi * span_f)
    radius = base_r + (rim_r - base_r) * span_f + bulge
    y = rim_h * span_f

    thinning = np.exp(-SHEET_THINNING * t)
    # Azimuthal thick/thin banding at the lobes (visible striations even while the
    # young wall is near-opaque) and extra mass collected at the rim.
    striation = 1.0 - SHEET_STRIATION * 0.5 * (1.0 + np.cos(
        LOBE_COUNT * az_f + np.interp(az_f, lobe_az, lobe_phase, period=2.0 * np.pi)))
    rim_collect = 1.0 + SHEET_RIM_COLLECT * np.clip(
        (span_f - SHEET_RIM_COLLECT_SPAN) / (1.0 - SHEET_RIM_COLLECT_SPAN), 0.0, 1.0)
    weight = SHEET_WEIGHT * thinning * striation * rim_collect \
        * lace_mask(az_f, span_f, t) * (rim_h > 1e-3)
    return np.sin(az_f) * radius, y, np.cos(az_f) * radius, weight


def ligament_points(t):
    """Strands strung between the receding rim (root) and the faster detaching tip
    (a ballistic continuation of the rim state at the strand's birth). The tip
    outruns the root so the strand stretches; near pinch-off it beads up."""
    xs, ys, zs, ws = [], [], [], []
    s = np.linspace(0.0, 1.0, LIG_SAMPLES)
    for i in range(LOBE_COUNT):
        age = t - lig_birth[i]
        if age < 0.0 or age > LIG_LIFE:
            continue
        life_frac = age / LIG_LIFE

        root_r, root_h = rim_state(lobe_az[i], t)
        birth_r, birth_h = rim_state(lobe_az[i], lig_birth[i])
        birth_vout, birth_vup = rim_velocity(lig_birth[i])
        tip_r = birth_r + birth_vout * LIG_TIP_OUT_BOOST * age
        tip_h = birth_h + max(birth_vup, 0.0) * LIG_TIP_UP_BOOST * age \
            - 0.5 * GRAVITY * age * age

        radius = root_r + (tip_r - root_r) * s
        y = root_h + (tip_h - root_h) * s
        if np.all(y <= 0.0):
            continue
        # Mass conservation: the strand thins as it stretches; beads deepen with age.
        taper = (1.0 - 0.55 * s) * (1.0 - life_frac) * LIG_WEIGHT
        beads = 1.0 - LIG_BEAD_DEPTH * life_frac * \
            (0.5 + 0.5 * np.sin(2.0 * np.pi * LIG_BEAD_PERIODS * s + lobe_phase[i]))
        weight = taper * beads * (y > 0.0)
        xs.append(np.sin(lobe_az[i]) * radius)
        ys.append(y)
        zs.append(np.cos(lobe_az[i]) * radius)
        ws.append(weight)
    if not xs:
        empty = np.zeros(0)
        return empty, empty, empty, empty
    return (np.concatenate(xs), np.concatenate(ys),
            np.concatenate(zs), np.concatenate(ws))


JET_APEX_TIME = JET_START_TIME + JET_UP_SPEED / GRAVITY
JET_APEX_HEIGHT = JET_UP_SPEED * JET_UP_SPEED / (2.0 * GRAVITY)


def jet_column_height(t):
    """Column top: ballistic tip while rising, quadratic sink-back after apex."""
    if t < JET_START_TIME:
        return 0.0
    if t <= JET_APEX_TIME:
        age = t - JET_START_TIME
        return JET_UP_SPEED * age - 0.5 * GRAVITY * age * age
    retract = (t - JET_APEX_TIME) / JET_RETRACT_TIME
    return JET_APEX_HEIGHT * max(0.0, 1.0 - retract * retract)


def jet_points(t):
    """The Worthington column as rings of 3D points (so the 6-way bake shades it as a
    cylinder), tapering toward the tip, with a slight centerline wobble."""
    top = jet_column_height(t)
    if top <= 1e-3:
        empty = np.zeros(0)
        return empty, empty, empty, empty
    heights = np.linspace(0.0, top, JET_HEIGHT_SAMPLES)
    theta = np.linspace(0.0, 2.0 * np.pi, JET_RING_SAMPLES, endpoint=False)
    h_g, th_g = np.meshgrid(heights, theta, indexing="ij")
    h_f, th_f = h_g.ravel(), th_g.ravel()

    span = h_f / max(top, 1e-6)
    radius = JET_RADIUS * (1.0 - (1.0 - JET_TIP_TAPER) * span)
    wobble = JET_WOBBLE * np.sin(3.0 * np.pi * span + 5.0 * t)
    ramp_in = min((t - JET_START_TIME) / 0.06, 1.0)
    weight = np.full_like(h_f, JET_WEIGHT * ramp_in)
    return wobble + np.sin(th_f) * radius, h_f, np.cos(th_f) * radius, weight


def jet_tip_droplet_points(t):
    """Fat droplets pinched off the column tip at apex, continuing ballistically."""
    if t < JET_APEX_TIME:
        empty = np.zeros(0)
        return empty, empty, empty, empty
    age = t - JET_APEX_TIME
    fade = max(0.0, 1.0 - age / JET_DROP_LIFE)
    xs, ys, ws = [], [], []
    for size, kick, drift in zip(JET_DROP_SIZES, JET_DROP_KICKS, JET_DROP_DRIFT):
        y = JET_APEX_HEIGHT + kick * age - 0.5 * GRAVITY * age * age
        if y <= 0.0 or fade <= 0.0:
            continue
        xs.append(drift * age)
        ys.append(y)
        ws.append(size * 0.5 * fade)
    if not xs:
        empty = np.zeros(0)
        return empty, empty, empty, empty
    return np.array(xs), np.array(ys), np.zeros(len(xs)), np.array(ws)


def project_to_frame(x, y):
    """Bake-space (x, y) to integer pixel coordinates + in-frame mask."""
    px = ((x + 1.0) * 0.5 * (FRAME_SIZE - 1)).astype(int)
    py = ((1.0 - y / WORLD_HEIGHT) * (FRAME_SIZE - 1)).astype(int)
    ok = (px >= 0) & (px < FRAME_SIZE) & (py >= 0) & (py < FRAME_SIZE)
    return px, py, ok


def stamp(img, x, y, weights, sigma):
    """Additive gaussian splat: histogram the points, then blur once."""
    px, py, ok = project_to_frame(x, y)
    acc = np.zeros_like(img)
    np.add.at(acc, (py[ok], px[ok]), weights[ok])
    img += gaussian_filter(acc, sigma)


def depth_dim(z, radius_hint=1.0):
    """Back-of-ring dimming: nearer (bigger z) = brighter."""
    return 0.55 + 0.45 * np.clip(z / max(radius_hint, 1e-4), -1.0, 1.0)


def sixway_transmittance(x, y, z, weights):
    """Voxelize this frame's particles and return, per particle, the transmittance
    toward a light on each of the six principal axes (occlusion by the splash's own
    mass). Directional cumsum of extinction over a coarse grid - cheap and stable."""
    grid = np.zeros((SIXWAY_GRID, SIXWAY_GRID, SIXWAY_GRID))
    ix = np.clip(((x + 1.0) * 0.5 * (SIXWAY_GRID - 1)).astype(int), 0, SIXWAY_GRID - 1)
    iy = np.clip((y / WORLD_HEIGHT * (SIXWAY_GRID - 1)).astype(int), 0, SIXWAY_GRID - 1)
    iz = np.clip(((z + 1.0) * 0.5 * (SIXWAY_GRID - 1)).astype(int), 0, SIXWAY_GRID - 1)
    np.add.at(grid, (ix, iy, iz), weights)
    cell = SIXWAY_EXTINCTION / SIXWAY_GRID

    result = []
    for axis, sign in SIXWAY_AXES:
        # Optical depth accumulated from the lit boundary down to each voxel,
        # excluding the voxel itself (a particle never shadows itself).
        depth = np.cumsum(np.flip(grid, axis) if sign > 0 else grid, axis=axis)
        depth = depth - (np.flip(grid, axis) if sign > 0 else grid)
        if sign > 0:
            depth = np.flip(depth, axis)
        result.append(np.exp(-cell * depth)[ix, iy, iz] ** SIXWAY_CONTRAST)
    return result  # ordered as SIXWAY_AXES: +X +Y +Z -X -Y -Z


class FrameAccumulator:
    """Mass, shine and the six weighted light channels for one frame."""

    def __init__(self):
        shape = (FRAME_SIZE, FRAME_SIZE)
        self.mass = np.zeros(shape)
        self.shine = np.zeros(shape)
        self.light = [np.zeros(shape) for _ in SIXWAY_AXES]
        self.light_norm = np.zeros(shape)
        self._batch = []

    def add(self, x, y, z, weight, sigma):
        """Queue one particle group; weights already include depth dimming."""
        keep = weight > 0.0
        if not np.any(keep):
            return
        self._batch.append((x[keep], y[keep], z[keep], weight[keep], sigma))

    def resolve(self):
        """Splat mass, then bake this frame's 6-way lighting over ALL queued groups
        at once (so the sheet shadows the droplets and vice versa)."""
        if not self._batch:
            return
        all_x = np.concatenate([b[0] for b in self._batch])
        all_y = np.concatenate([b[1] for b in self._batch])
        all_z = np.concatenate([b[2] for b in self._batch])
        all_w = np.concatenate([b[3] for b in self._batch])
        trans = sixway_transmittance(all_x, all_y, all_z, all_w)

        cursor = 0
        for x, y, z, w, sigma in self._batch:
            stamp(self.mass, x, y, w, sigma)
            stamp(self.light_norm, x, y, w, sigma)
            for k in range(len(SIXWAY_AXES)):
                lit = trans[k][cursor:cursor + w.size]
                stamp(self.light[k], x, y, w * lit, sigma)
            cursor += w.size

    def normalized_light(self):
        """Weighted-average transmittance per texel; empty texels read fully lit."""
        norm = np.maximum(self.light_norm, SIXWAY_MIN_WEIGHT)
        return [np.where(self.light_norm > SIXWAY_MIN_WEIGHT, ch / norm, 1.0)
                for ch in self.light]


mass_frames, shine_frames = [], []
light_frames = [[] for _ in SIXWAY_AXES]
for i in range(FRAME_COUNT):
    t = (i + 0.5) / FRAME_COUNT * DURATION
    acc = FrameAccumulator()

    sx, sy, sz, sw = sheet_points(t)
    acc.add(sx, sy, sz, sw * depth_dim(sz, BASE_RADIUS + RIM_OUT_SPEED * t), SHEET_SIGMA)

    lx, ly, lz, lw = ligament_points(t)
    if lx.size:
        acc.add(lx, ly, lz, lw * depth_dim(lz, BASE_RADIUS + RIM_OUT_SPEED * t), LIG_SIGMA)

    mx, my, mz, m_age, m_alive = ballistic(mist_az, mist_r0, 0.0, mist_vout, mist_vup,
                                           mist_birth, t)
    mist_fade = np.clip(1.0 - m_age / MIST_LIFE, 0.0, 1.0)
    acc.add(mx, my, mz, MIST_WEIGHT * mist_fade * m_alive * depth_dim(mz), MIST_SIGMA)

    dx, dy, dz, d_age, d_alive = ballistic(drop_az, drop_r0, drop_h0, drop_vout, drop_vup,
                                           drop_birth, t)
    d_fade = np.clip(DROPLET_FADE_HEAD - d_age / DURATION, 0.0, 1.0) ** 0.8
    d_weight = d_fade * d_alive * drop_size * DROPLET_STAMP_GAIN * depth_dim(dz)
    d_vy = drop_vup - GRAVITY * d_age
    for k in (-1, 0, 1):  # velocity stretch: 3 sub-stamps along the motion direction
        dt = k * DROPLET_STRETCH_DT
        acc.add(dx + np.sin(drop_az) * drop_vout * dt, dy + d_vy * dt, dz,
                d_weight, DROPLET_SIGMA)

    jx, jy, jz, jw = jet_points(t)
    if jx.size:
        acc.add(jx, jy, jz, jw * depth_dim(jz, JET_RADIUS * 2.0), JET_SIGMA)
    tx, ty, tz, tw = jet_tip_droplet_points(t)
    if tx.size:
        acc.add(tx, ty, tz, tw * depth_dim(tz), DROPLET_SIGMA)

    acc.resolve()
    # shine: only the droplet CORES (single unstretched stamp, tight blur) so the
    # cubed-shine sparkle lands on discrete droplets, not the whole curtain
    stamp(acc.shine, dx, dy, d_weight, DROPLET_SHINE_SIGMA)
    if tx.size:  # the jet's fat tip droplets sparkle too
        stamp(acc.shine, tx, ty, tw, DROPLET_SHINE_SIGMA)

    frac = (i + 0.5) / FRAME_COUNT
    envelope = min(t / 0.04, 1.0) * \
        (1.0 - np.clip((frac - END_FADE_START) / (1.0 - END_FADE_START), 0, 1) ** 1.3)
    mass_frames.append(acc.mass * envelope)
    shine_frames.append(acc.shine * envelope)
    for k, ch in enumerate(acc.normalized_light()):
        light_frames[k].append(ch)

mass = np.array(mass_frames)
mass = np.maximum(mass - SPECKLE_FLOOR, 0.0)
mass = mass / (mass + TONEMAP_KNEE)                        # soft knee lifts mid-life frames
mass = np.clip(mass / np.percentile(mass, 99.9), 0, 1) ** MASS_PRESENCE_GAMMA

shine = np.array(shine_frames)
shine = np.maximum(shine - SPECKLE_FLOOR, 0.0)
shine = shine / (shine + SHINE_KNEE)
shine = np.clip(shine / max(np.percentile(shine, 99.9), 1e-6), 0, 1)

# thickness: per-frame blurred mass, normalized once across the sequence
thickness = np.array([gaussian_filter(f, THICKNESS_SIGMA) for f in mass])
thickness = np.clip(thickness / max(thickness.max(), 1e-6), 0, 1)

# dissolve noise: ONE static smooth field tiled into every frame cell, so the
# erosion eats each frame in the same organic patches (texture-space burn)
noise_cell = gaussian_filter(rng.uniform(0.0, 1.0, (FRAME_SIZE, FRAME_SIZE)), NOISE_SIGMA)
noise_cell = (noise_cell - noise_cell.min()) / max(np.ptp(noise_cell), 1e-6)
noise_cell = NOISE_FLOOR + (1.0 - NOISE_FLOOR) * noise_cell


def assemble(stack):
    sheet = np.zeros((FRAME_SIZE * ROWS, FRAME_SIZE * COLS))
    for i, frame in enumerate(stack):
        r, c = divmod(i, COLS)
        sheet[r * FRAME_SIZE:(r + 1) * FRAME_SIZE, c * FRAME_SIZE:(c + 1) * FRAME_SIZE] = frame
    return sheet


def to_byte(sheet):
    return (np.clip(sheet, 0.0, 1.0) * 255).astype(np.uint8)


packed = np.dstack([to_byte(assemble(mass)),
                    to_byte(assemble(shine)),
                    to_byte(np.tile(noise_cell, (ROWS, COLS))),
                    to_byte(assemble(thickness))])
Image.fromarray(packed, "RGBA").save(os.path.abspath(OUTPUT_PACKED))
print("wrote", os.path.abspath(OUTPUT_PACKED))

# Six-way sheets: A = light from +X/+Y/+Z, B = light from -X/-Y/-Z (alpha unused).
OPAQUE_ALPHA = np.full((FRAME_SIZE * ROWS, FRAME_SIZE * COLS), 255, np.uint8)
light_a = np.dstack([to_byte(assemble(light_frames[0])),
                     to_byte(assemble(light_frames[1])),
                     to_byte(assemble(light_frames[2])), OPAQUE_ALPHA])
light_b = np.dstack([to_byte(assemble(light_frames[3])),
                     to_byte(assemble(light_frames[4])),
                     to_byte(assemble(light_frames[5])), OPAQUE_ALPHA])
Image.fromarray(light_a, "RGBA").save(os.path.abspath(OUTPUT_LIGHT_A))
Image.fromarray(light_b, "RGBA").save(os.path.abspath(OUTPUT_LIGHT_B))
print("wrote", os.path.abspath(OUTPUT_LIGHT_A))
print("wrote", os.path.abspath(OUTPUT_LIGHT_B))
