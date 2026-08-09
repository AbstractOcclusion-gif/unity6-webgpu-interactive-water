# WebGpuWater - Authoring, Limitations, and Experimental Features

This page is the practical guide to the package's boundaries. It is deliberately direct: use
the stable workflow first, then opt into the more ambitious paths with a representative build
and target device.

## Recommended workflow

1. Create the body with the Water Wizard and first verify it in Play mode.
2. Tune the look at the quality tier you intend to ship. Use **Force Low** to preview the
   mobile/WebGPU path; a look tuned only at High is not expected to match Low.
3. Tune one layer at a time: ambient sea state first, then foam, then local gameplay ripples,
   wakes, and splash effects.
4. Test a development build on the target device before committing a look. Editor timing,
   resolution, and supported GPU features are not the same as every build target.

## Wind and ocean authoring

**Wind Drives Ambient Sea State** is an opt-in local-weather mode for an open-water body. It
links the FFT wind sea, small wind waves, detail normals, and wind-gated whitecaps. It does not
change wakes, impact ripples, or splash events.

Set **Reference Wind Speed** to the wind at which you want the typed Significant Wave Height and
Peak Wavelength to be exact. For a storm authored at 15 m/s, set both Wind Speed and Reference
Wind Speed to 15 m/s while tuning. Lower wind scales the local wind sea down; 0 m/s removes it.

Important limits:

- This is a global wind per water body, not a spatial weather or storm-front simulation.
- Change wind gradually at runtime. Altering the FFT wind sea's height or wavelength refreshes
  the spectrum, which is suitable for authoring and slow weather changes, not frame-by-frame
  gust noise.
- Swell remains independent of the wind-speed coupling. This is intentional: calm local weather
  can retain a distant storm swell. Swell direction currently follows the body's Wind Heading;
  there is no separate swell-direction control.
- Open Water / FFT ocean authoring remains experimental. Verify buoyancy, underwater visuals,
  horizon quality, and performance in the exact scene and quality tier you plan to ship.

## Live surface queries and GPU readback

The visible interactive ripple field lives on the GPU. `TryGetWaterHeight`, `TrySampleHeight`,
and their static variants need an asynchronous GPU readback, so they can return `false` before
the first result arrives or outside the sampled footprint. Treat their boolean return as required
control flow, not an optional convenience.

Do not use a readback query every physics frame just to decide whether an object crossed the
surface. It keeps a GPU-to-CPU transfer active and adds avoidable cost. `WaterSplash` uses the
immediate analytic waterline for exactly this reason.

`WaterBreachSplash`, `WaterRippleEmitter`, and similar live-surface effects may wait until a
readback is available. Use `WaterBreachSplash` for projectiles, fish, or diving birds that need
repeated crossings; it is not the default boat splash or wake solution.

On platforms where async readback is unavailable or persistently fails, gameplay sampling falls
back to the analytic waterline where possible. It is a safe fallback, but it does not include
interactive ripple displacement. Test readback-dependent effects on the target hardware.

## Wakes, ripples, and splashes are different systems

- **WaterSphereInteractor** is the continuous directional wake for boats and moving floaters.
  Use its **Vertical Force Cap** to limit a boat falling after a swell without weakening the
  horizontal travelling wake.
- **Wake Start Force Cap** on the WaterVolume is the shared safety cap for every wake interactor
  on that water body. Use it for a body-wide ceiling; use Vertical Force Cap for a particular boat.
- **WaterSplash** is the one-time Rigidbody entry splash. Its ripple cap controls the impact ring,
  not its particle burst.
- **WaterBreachSplash** is an optional repeated crossing effect and may do nothing until a live
  surface readback has landed.

Do not add multiple overlapping sphere interactors to a hull without testing them together. Each
is capped individually and their overlapping disturbance can still add up.

## Rendering and integration boundaries

- WebGL builds require **WebGPU**. A browser or device without WebGPU cannot run the compute
  simulation path.
- Depth extinction does not darken Unity Terrain. Use Bed Colour & Clarity for terrain-based
  shallow-water looks, or convert mesh ground to a Water Receiver where appropriate.
- Custom transparent materials are not automatically part of the water medium. They need the
  package's fog integration path to receive the same water attenuation and glow as package-aware
  materials.
- The optional Live Water Preview is experimental. It continuously runs GPU work in the editor;
  turn it off if the editor device becomes unstable or while profiling unrelated content.

## Before shipping

- Test High, Medium, and Low if the project can select them. Quality tiers change simulation
  resolution, FFT/update intervals, render scale, mesh detail, and particle limits.
- Test the exact camera ranges used in gameplay, especially for an open ocean and underwater shots.
- Test an object spawned directly on water, an object entering at speed, and a boat cresting then
  falling through a large wave.
- Keep diagnostic/demo components out of shipping scenes unless they are intentionally part of
  the game.

The package is designed to fail safely where possible, but safe fallback and exact visual parity
are different promises. Treat GPU readback, open-ocean FFT, and editor-preview behavior as things
to validate in your build rather than assumptions carried from the Inspector.
