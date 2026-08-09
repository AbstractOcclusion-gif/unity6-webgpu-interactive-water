# WebGpuWater - Feature Guide

This is the current feature map. It explains each system's normal authoring path and the
boundary to respect. Inspector tooltips remain the detailed per-field reference.

## Status labels

| Status | Meaning |
| --- | --- |
| Core | Normal package workflow; still validate the target build and quality tier. |
| Optional | Requires deliberate setup, with cost or scope constraints. |
| Experimental | Validate in the exact project scene and target hardware. |
| Tooling | Intended for authoring, diagnosis, or samples; not automatically gameplay architecture. |

## Body topology

| Feature | Status | Use it for | Boundary |
| --- | --- | --- | --- |
| Pool / bounded WaterVolume | Core | Pools, lakes, tanks, and authored footprints | Objects and queries must resolve to the intended body. |
| Large-body simulation window | Optional | Broad water with local interactive ripples | Near-field simulation is windowed, not infinite detail everywhere. |
| Open Water / clipmap / FFT ocean | Experimental | Horizon-scale ocean presentation | Validate horizon, buoyancy, underwater views, and performance in the shipping scene. |
| Multiple water bodies | Core | Separate pools, levels, and water types | Use WaterMembership when containment is ambiguous. |
| Look presets and foam profiles | Optional | Sharing a look across bodies | A driven field is overridden by its assigned preset/profile. |

Create the baseline with the Water Wizard. Immutable implementation assets come from the
package's `Runtime/Defaults` folder. The Wizard writes each water's independent materials under
`Assets/WebGpuWater/Waters/<Water Name>/` and keeps the shared editable foam profile under
`Assets/WebGpuWater/Profiles/`.

## Waves and weather

| Layer | Responsibility | Do not use it for |
| --- | --- | --- |
| Interactive ripple simulation | Local impacts, wakes, drag input, and near-field motion | Horizon-scale ambient sea |
| Small Wind Waves | Fine ambient wave layer that floaters ride | Long offshore rollers |
| FFT Ocean Sea State | Open-water wind sea, spectrum, clipmap, and whitecaps | A local boat wake |
| Swell | Long remote rollers layered on the wind sea | A substitute for local wind speed |
| Detail normals | Visual micro-ripple | Physics or buoyancy displacement |

**Wind Drives Ambient Sea State** is opt-in. Set **Reference Wind Speed** to the wind at
which the typed Significant Wave Height and Peak Wavelength are exact. It links the FFT wind
sea, small wind waves, detail normals, and wind-gated whitecaps. Wakes, impact ripples, and
swell stay independent.

Use a reference of 15 m/s to author a 15 m/s storm. Lower wind scales the local wind sea down.
Change wind gradually at runtime because changing the FFT wind-sea size refreshes its spectrum.
The system has one global wind per body, not spatial storm fronts. Swell currently follows the
body's Wind Heading and has no separate direction control.

## Physics and surface queries

| Feature | Status | Use it for | Boundary |
| --- | --- | --- | --- |
| WaterBuoyancy | Core | Rigidbody buoyancy, drag, righting, and wave drift | Test the probe layout on the real hull. |
| WaterProbe | Optional | Explicit sampling point or body association | Use where a centre/collider approximation is insufficient. |
| TryGetWaterHeight / TrySampleHeight | Core API | Gameplay height and depth tests | Always check the returned bool. |
| WaterMembership | Optional | Explicit body selection | Needed for overlapping or ambiguous water bodies. |

Live ripple height comes from asynchronous GPU readback. It can be unavailable before the first
result or outside the sampled footprint. Do not poll it every physics tick only to detect an
entry: that keeps a GPU-to-CPU transfer active. `WaterSplash` uses the immediate analytic
waterline for that reason.

If async readback is unavailable or repeatedly fails, buoyancy/sampling uses an analytic fallback
where possible. It is safe, but does not reproduce local interactive ripple displacement exactly.
Test readback-dependent behavior on target hardware.

## Local interaction, wakes, and splashes

| Component | Status | Use it for | Do not confuse it with |
| --- | --- | --- | --- |
| WaterInteractable | Core | Generic motion/depth disturbance | Directional boat wake |
| WaterSphereInteractor | Core | Continuous directional wake for boats and floaters | One-time entry splash |
| WaterSplash | Core | One-time Rigidbody entry splash | Repeated breach effect |
| WaterBreachSplash | Optional | Projectiles, fish, and diving birds crossing repeatedly | Boat wake or ordinary boat entry |
| WaterRippleEmitter | Optional | Scripted/timed ripple stamps | A particle splash |
| WaterInputRouter | Optional | Mouse/touch ripples on the primary body | Production gameplay input abstraction |

For a boat, use `WaterSphereInteractor`. Its **Vertical Force Cap** limits the plunge/heave wave
from falling after a swell without weakening the horizontal travelling wake. **Wake Start Force
Cap** on WaterVolume is the shared cap for every interactor on that body. Overlapping interactors
can still add together, so test the complete hull.

`WaterBreachSplash` uses live surface readback and can wait before triggering. It is intentionally
not the default boat workflow. `WaterSplash` is the simple one-time Rigidbody entry path.

## Foam, spray, and wetness

| Feature | Status | Contribution | Boundary |
| --- | --- | --- | --- |
| Surface foam simulation | Core | Turbulence, wake, contact, crest, and shore foam | Separate simulated mask, not the splash particle system. |
| Wake foam | Optional | Crisp hull foam deposited by a Sphere Interactor | Requires Foam and a moving interactor. |
| Wetness memory | Optional | Ground stays wet after a wave recedes | Needs compatible receiver/terrain integration to be visible. |
| WaterFoamParticles | Optional | GPU foam, mist, and spray | Particle caps and behavior vary by quality tier. |
| WaterSplashEmitter | Core with splashes | Event droplets, jets, and crown | Uses GPU particles when available; otherwise Shuriken fallback. |
| WaterSprayPump | Optional | Hull/rock probe-driven spray | Tune probes against the real hull. |
| WaterFoamProfile | Optional | Shared foam/splash look | Enabled Drive sections override local component fields. |

Identify the source before tuning foam. An ambient spawn setting does not necessarily control a
splash or probe-pump burst. See **Particle & Foam System** for the complete routing.

## Shoreline, dry regions, and rendering

| Feature | Status | Use it for | Boundary |
| --- | --- | --- | --- |
| Bed Depth / WaterBedBaker | Optional | Shallow-water color, attenuation, and breaking response | Bake and validate against the real terrain/mesh and body frame. |
| Shore / surf response | Optional | Shoaling, fronts, foam, and swash near a baked bed | Not a general-purpose ocean coastline generator. |
| WaterObstacle | Optional | Solid object influence on local simulation | Validate footprint at the chosen ripple resolution. |
| WaterExclusionVolume | Optional | Dry interiors, holes, and hull interiors | Mesh volumes need a suitable proxy for CPU/non-camera consumers. |
| Screen-space reflections / refraction | Optional | Scene reflection and real refraction | Need URP Depth and Opaque Texture; a quality tier can gate them. |
| Planar reflection | Optional | One hero body's reflection | Full extra scene render; do not use it as a free global switch. |
| Caustics, god rays, underwater fog | Optional | Water light and underwater presentation | Validate moving and straddling-camera shots on the shipping tier. |
| WaterFogTransparent | Optional | Integrates a transparent renderer into the water medium | Custom transparent shaders are not integrated automatically. |

Terrain does not receive the package mesh depth-extinction shader. Use Bed Colour & Clarity for
terrain-based shallow water, or convert suitable mesh ground to a Water Receiver. For exclusion
walls in a build, register the required packaged shader through the package utility; editor shader
lookup is not a build guarantee.

## Quality, builds, and tooling

`WaterQuality` changes simulation and caustic resolution, render scale, mesh detail, wave count,
update intervals, reflection/refraction availability, and particle caps. Author at the tier you
ship; use **Force Low** for the mobile/WebGPU path.

- WebGL requires WebGPU for the compute simulation path.
- The optional Live Water Preview is experimental and continuously consumes GPU work. Disable it
  when profiling unrelated content or if the editor device becomes unstable.
- Wizard tools, debug views, overlays, stress spawners, showcase movers, splash range, and camera
  helpers are not automatically gameplay systems. Remove demo-only helpers before shipping.

## Practical test order

1. Verify body boundary, water level, and basic buoyancy.
2. Verify ambient wind, ocean/swell, and intended camera range.
3. Verify a boat at cruise speed, low speed, and falling after a crest.
4. Verify entry splash, wake foam, and particles separately.
5. Verify shore/bed/exclusions where present.
6. Verify reflections, refraction, underwater transition, caustics, and god rays.
7. Repeat on the selected quality tier and target hardware/build.

This sequence localizes failures: confirm the surface and physics before tuning secondary visuals.
