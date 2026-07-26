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
        MaterialPropertyBlock _mpb;

        // Lazy init (not Awake): with ExecuteAlways the first edit-mode tick can arrive
        // before Awake after a domain reload.
        void EnsureInitialized()
        {
            if (_renderer == null) _renderer = GetComponent<Renderer>();
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
        }

        // LateUpdate so the containing body has finished this frame's sim/caustic pass
        // (its Update runs at DefaultExecutionOrder -50) before we copy its uniforms.
        void LateUpdate()
        {
            WaterVolume body = WaterVolume.BodyContaining(transform.position);
            if (body == null) return; // no water in the scene; keep the material's defaults

            EnsureInitialized();
            body.WriteBodyProps(_mpb);
            _renderer.SetPropertyBlock(_mpb);
        }
    }
}
