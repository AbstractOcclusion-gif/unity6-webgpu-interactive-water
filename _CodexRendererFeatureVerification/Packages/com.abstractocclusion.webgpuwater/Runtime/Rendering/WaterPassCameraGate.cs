// WebGpuWater - ONE definition of "should a water render pass run for this camera".
//
// WHY: none of the water features filtered by camera type, so the fullscreen fog, the caustic
// projection, the ocean god-ray march and both mesh-depth prepasses all recorded for cameras that
// have no business seeing water - most visibly Unity's material/prefab PREVIEW cameras, which render
// thumbnails in the Project and Inspector windows. That is not just wasted work: a preview draws the
// package's procedural shaders with none of the per-body buffers bound, which is the same class of
// bufferless-preview problem that produced the "_Particles SRV none provided" d3d12 error.
//
// SCOPE - TWO questions, because the passes do two different kinds of work:
//   * SkipCamera - the DEPTH PREPASSES. Only Preview is skipped. SceneView must keep running (the
//     fog, the waterline and the shell are what you author against), and Reflection must keep
//     running too: those targets feed the chunk and exclusion WALL shaders, which would otherwise
//     read a stale prepass while a probe face renders.
//   * SkipCameraFullscreen - the passes that PAINT the camera colour (underwater fog, ocean god
//     rays, screen-space caustics). Preview and Reflection both.
//
// WHY Reflection was added (2026-07-27, the deliberate revisit this file's old comment asked for -
// and there IS now a scene to compare against). PlanarMirror renders its mirror with a camera at
// the MIRRORED position, i.e. BELOW the water plane by construction. Nothing marked it as a
// reflection camera, so the fullscreen fog armed on it and painted the water's scatter colour
// straight into the mirror RT: a black boat came back TEAL in its own reflection, carrying a wave
// pattern evaluated at the mirrored position - which read as the reflection drifting against an
// inverted wave pattern. No sampling change could ever fix that, because the corruption is in the
// RT before the water shader reads a single texel. Crest gates the same way
// (UnderwaterRenderer.Effect.cs: `camera.cameraType != CameraType.Reflection`).
// A reflection must never contain the volume effects of the medium doing the reflecting.
#if WEBGPUWATER_URP
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal static class WaterPassCameraGate
    {
        /// <summary>True when a water DEPTH PREPASS should NOT be recorded for this camera.</summary>
        internal static bool SkipCamera(CameraType cameraType) => cameraType == CameraType.Preview;

        /// <summary>True when a FULLSCREEN water paint (fog / god rays / caustic projection) should
        /// NOT be recorded for this camera. Strictly wider than <see cref="SkipCamera"/>: it also
        /// excludes reflection cameras, whose colour must stay free of the water's own volume
        /// effects. Only ever REMOVES paints, so a probe's depth targets are unaffected.</summary>
        internal static bool SkipCameraFullscreen(CameraType cameraType)
            => SkipCamera(cameraType) || cameraType == CameraType.Reflection;
    }
}
#endif
