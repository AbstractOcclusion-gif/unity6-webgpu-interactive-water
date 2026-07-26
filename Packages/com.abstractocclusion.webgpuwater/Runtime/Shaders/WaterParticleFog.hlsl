// WebGpuWater - per-particle underwater self-fog (the particle/fog SORTING fix).
//
// The fullscreen underwater fog runs at BeforeRenderingPostProcessing - AFTER every transparent -
// and integrates to the OPAQUE depth, so at queue time it painted the FULL water column's fog OVER
// every particle sprite: a droplet a metre from the submerged camera picked up the fog of the 30 m
// of water behind it and read as a flat fog-coloured blob. The fix is KWS's: on frames where that
// fog runs, the sprites are drawn AFTER it (WaterUnderwaterFogFeature's after-fog particle pass,
// rerouted from WaterFoamParticles.Draw) and price their OWN camera->particle wet path here with
// the same extinction / in-scatter uniforms the fog uses.
//
// The wet path is the flat closed-form waterline (WaterPathLength against the CPU-published
// _UnderwaterSurfaceY - the Simple fog tier's model): per-vertex on a sprite, the error vs the
// wavy crossing is below sprite size. The armed gate is the SAME UnderwaterFogActive the reroute
// keys on, so fog-off frames are byte-identical through the untouched queue-time path.
//
// Split into mul/add so the fragment applies it AFTER its texture multiply - exact, because
// lerp(inscatter, c, T) = c*T + inscatter*(1-T) is linear in c:
//   rgb' = rgb * fogMul + fogAdd
// Compute in the VERTEX stage (particles are per-vertex lit already); pass both as interpolants.
#ifndef WATER_PARTICLE_FOG_INCLUDED
#define WATER_PARTICLE_FOG_INCLUDED

#include "WaterFog.hlsl" // WaterPathLength + WaterInscatterColor + extinction/density/scatter uniforms

// Globals published by WaterUniformPublisher.PublishUnderwater (same values the exclusion wall
// and the fog gate read) - float-only, safe under both CGPROGRAM and HLSLPROGRAM.
float _UnderwaterFogArmed;   // 1 while the fullscreen fog pass runs this frame
float _UnderwaterSurfaceY;   // wave-aware surface height at the camera xz (the flat-fog waterline)

// lightDir/sunColor are passed in (not read as globals) so this include never fights the
// declaration each particle shader already carries for them.
void ParticleUnderwaterFog(float3 worldPos, float3 lightDir, float3 sunColor,
                           out float3 fogMul, out float3 fogAdd)
{
    fogMul = float3(1.0, 1.0, 1.0);
    fogAdd = float3(0.0, 0.0, 0.0);
    if (_UnderwaterFogArmed < 0.5) return; // fog off: queue-time draw path, untouched look
    float wet = WaterPathLength(worldPos, _WorldSpaceCameraPos.xyz, _UnderwaterSurfaceY);
    if (wet <= 0.0) return; // sprite and camera both in air (spray above a pond seen from above)
    float3 transmittance = exp(-_WaterExtinction.rgb * (_WaterFogDensity * wet));
    float3 viewDirWS = normalize(_WorldSpaceCameraPos.xyz - worldPos);
    float3 inscatter = WaterInscatterColor(viewDirWS, lightDir, sunColor, 0.0);
    fogMul = transmittance;
    fogAdd = inscatter * (1.0 - transmittance);
}

#endif // WATER_PARTICLE_FOG_INCLUDED
