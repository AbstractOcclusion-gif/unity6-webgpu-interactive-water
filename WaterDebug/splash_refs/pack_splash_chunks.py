"""Prototype channel packer: photo -> KWS-style packed splash chunk."""
import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter, label, sum as ndsum

CHUNK = 512
FLOOR = 0.06          # background floor cut on mass
LATEX_SAT = 0.45      # saturation above this = latex/color cast begins
LATEX_SOFT = 0.25     # softness of the latex suppression ramp
SHINE_PCT = 97.5      # percentile that seeds the shine cores
DEPTH_SIGMA = 22      # blur (in 512-space px) for the thickness channel
NOISE_CELLS = 6       # cellular noise frequency for the erosion channel
SEED = 7

def load_mass(path):
    a = np.asarray(Image.open(path)).astype(np.float32)/255.0
    lum = 0.2126*a[...,0] + 0.7152*a[...,1] + 0.0722*a[...,2]
    mx = a.max(-1); mn = a.min(-1)
    sat = np.where(mx>1e-4, (mx-mn)/np.maximum(mx,1e-4), 0)
    # suppress colored (latex) pixels smoothly; neutral water keeps full weight
    latex_w = np.clip((sat - LATEX_SAT)/LATEX_SOFT, 0, 1)
    mass = lum * (1.0 - 0.85*latex_w)
    # floor cut with soft knee
    mass = np.clip((mass - FLOOR)/(1-FLOOR), 0, 1)
    return mass

def square_crop(mass, pad=0.06):
    # bbox of significant mass, expanded to square
    ys, xs = np.where(mass > 0.10)
    y0,y1,x0,x1 = ys.min(), ys.max(), xs.min(), xs.max()
    cy, cx = (y0+y1)//2, (x0+x1)//2
    half = int(max(y1-y0, x1-x0)*(0.5+pad))
    H,W = mass.shape
    half = min(half, cy, H-1-cy, cx, W-1-cx)  # clamp inside image
    return mass[cy-half:cy+half, cx-half:cx+half]

def downscale(m, size=CHUNK):
    return np.asarray(Image.fromarray((m*255).astype(np.uint8)).resize((size,size), Image.LANCZOS)).astype(np.float32)/255.0

def shine_channel(m):
    thr = np.percentile(m[m>0.05], SHINE_PCT) if (m>0.05).any() else 1.0
    cores = np.clip((m-thr)/max(1e-4,1-thr), 0, 1)
    cores = gaussian_filter(cores, 1.2)
    return np.clip(cores*1.8, 0, 1)

def cellular_noise(size=CHUNK, cells=NOISE_CELLS, rng=None):
    # soft worley-ish: distance to nearest feature point, inverted, band-passed
    rng = rng or np.random.default_rng(SEED)
    pts = rng.uniform(0, size, (cells*cells, 2))
    yy, xx = np.mgrid[0:size, 0:size]
    d = np.full((size,size), 1e9, np.float32)
    for p in pts:
        d = np.minimum(d, (yy-p[0])**2 + (xx-p[1])**2)
    d = np.sqrt(d); d /= d.max()
    n = 1.0 - d
    n = (n - n.min())/(n.max()-n.min())
    n = gaussian_filter(n, 6)
    n = (n - n.min())/(n.max()-n.min())
    return n.astype(np.float32)

def depth_channel(m):
    d = gaussian_filter(m, DEPTH_SIGMA)
    d /= max(1e-4, d.max())
    return d


def thin(m):
    """Remove low-frequency fill so only structure survives (KWS-like sparsity)."""
    base = gaussian_filter(m, 18)
    detail = np.clip(m - 0.55*base, 0, 1)
    out = np.clip((detail - 0.04)/0.96, 0, 1)**1.35
    return np.clip(out*1.6, 0, 1)

def pack(path, rng):
    m = load_mass(path)
    m = square_crop(m)
    m = downscale(m)
    m = thin(m)
    g = shine_channel(m)
    b = cellular_noise(rng=rng)
    aa = depth_channel(m)
    return np.stack([m, g, b, aa], -1)

if __name__ == '__main__':
    import sys
    base = '/mnt/user-data/uploads/ThreeJSWaterPort/WaterDebug/splash_refs/'
    picks = ['splash_cc-by-2.0_tombullock_yellowexplosion.jpg',
             'splash_cc-by-2.0_tombullock_whiteshell.jpg',
             'splash_cc-by-2.0_tombullock_whiteredcloud.jpg',
             'splash_cc-by-2.0_tombullock_greenshell.jpg']
    rng = np.random.default_rng(SEED)
    chunks = [pack(base+p, np.random.default_rng(SEED+i)) for i,p in enumerate(picks)]
    atlas = np.concatenate(chunks, axis=1)  # 512 x 2048 x 4
    Image.fromarray((atlas*255).astype(np.uint8), 'RGBA').save('/tmp/pack/WaterSplashChunks_candidate.png')
    # channel grid preview (like the KWS inspection image): R|G over B|A
    r = atlas[...,0]; g = atlas[...,1]; b = atlas[...,2]; a = atlas[...,3]
    top = np.concatenate([r,g],1); bot = np.concatenate([b,a],1)
    grid = np.concatenate([top,bot],0)
    Image.fromarray((grid*255).astype(np.uint8)).resize((2048,1024), Image.LANCZOS).save('/tmp/pack/candidate_channels.png')
    print('atlas + preview written')
