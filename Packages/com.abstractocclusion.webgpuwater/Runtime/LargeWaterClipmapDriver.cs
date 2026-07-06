// LargeWaterClipmapDriver - keeps the open-water clipmap mesh centred under the camera (Phase 2).
//
// EXPERIMENTAL, opt-in: compiles only under the WEBGPUWATER_LARGE_BODY scripting define, so it
// is inert in the shipped pool / small-body build.
//
// Recentres the surface object at the camera's XZ every frame and SNAPS that position to a grid
// (the innermost quad size). Snapping is the standard clipmap trick: it stops the fine vertices
// from "swimming" under the FFT field as the camera creeps, because the mesh only ever jumps by a
// whole texel. Height stays a function of world XZ, so nothing here perturbs buoyancy sampling.
#if WEBGPUWATER_LARGE_BODY
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Follows the camera in XZ (texel-snapped) at a fixed water-plane height.</summary>
    [DisallowMultipleComponent]
    internal sealed class LargeWaterClipmapDriver : MonoBehaviour
    {
        [Tooltip("Camera the surface follows. Falls back to Camera.main when empty.")]
        [SerializeField] Camera followCamera;

        [Tooltip("World Y of the resting water plane. The FFT displacement is added on top by the shader.")]
        [SerializeField] float surfaceHeight;

        [Tooltip("Snap step (world metres) for the follow position. Match this to the innermost quad " +
                 "size so fine vertices never swim under the wave field. Must be positive.")]
        [Min(MinSnapStep)] [SerializeField] float snapStep = 1f;

        const float MinSnapStep = 1e-3f;

        void LateUpdate()
        {
            Camera cam = ResolveCamera();
            if (cam == null) return; // no camera yet: hold position rather than snap to the origin

            Vector3 camPosition = cam.transform.position;
            float step = Mathf.Max(MinSnapStep, snapStep);
            float snappedX = Mathf.Round(camPosition.x / step) * step;
            float snappedZ = Mathf.Round(camPosition.z / step) * step;
            transform.position = new Vector3(snappedX, surfaceHeight, snappedZ);
        }

        Camera ResolveCamera() => followCamera != null ? followCamera : Camera.main;
    }
}
#endif // WEBGPUWATER_LARGE_BODY
