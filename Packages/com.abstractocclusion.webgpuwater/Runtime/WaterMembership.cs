// WebGpuWater - per-object water membership (Unity 6 / URP port)
// Lights a floating object with the lake it is actually inside. The receiver shader
// reads the sim/caustic textures, the volume frame and the fog params as GLOBALS,
// which the primary body publishes - so without this component every object shows the
// primary lake. This pushes the CONTAINING body's uniforms onto the object's own
// MaterialPropertyBlock each frame, so a crate in lake B shows lake B's caustics/fog.
// Additive: objects without it fall back to the global (primary) body.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways] // edit-mode preview: floating objects show live water uniforms without Play
    [RequireComponent(typeof(Renderer))]
    public class WaterMembership : MonoBehaviour
    {
        Renderer _renderer;

        // Lazy init (not Awake): with ExecuteAlways the first edit-mode tick can arrive
        // before Awake after a domain reload.
        void EnsureInitialized()
        {
            if (_renderer == null) _renderer = GetComponent<Renderer>();
        }

        // LateUpdate so the containing body has finished this frame's sim/caustic pass
        // (its Update runs at DefaultExecutionOrder -50) before we copy its uniforms.
        void LateUpdate()
        {
            EnsureInitialized();

            WaterVolume body = WaterVolume.BodyContaining(transform.position);
            if (body == null)
            {
                // No body contains this object any more (the lake was disabled, or it drifted out).
                // Returning early used to LEAVE THE LAST BLOCK in place, so a floating crate kept
                // rendering the dead body's caustics forever. Drop it and fall back to the material.
                _renderer.SetPropertyBlock(null);
                return;
            }

            // The body builds this block at most once a frame and hands the SAME instance to every
            // member; SetPropertyBlock copies it into the renderer, so sharing is safe. Writing our
            // own was ~138 native property writes per object per frame for identical values.
            _renderer.SetPropertyBlock(body.MembershipBlock);
        }
    }
}
