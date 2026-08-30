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
    public class WaterMembership : MonoBehaviour
    {
        // ALL renderers under the object, root included: rigs like the boat keep physics on a
        // bare root with the visuals as children (often several meshes), and every one of them
        // must show the containing body's water. This is also why there is no RequireComponent
        // gate any more - it made AddComponent FAIL on a bare root, which is the boat's normal
        // shape, not an error.
        Renderer[] _renderers;

        // How long an EMPTY renderer set is trusted before the hierarchy is walked again.
        // Real time, not Time.time: this component runs under ExecuteAlways and the editor's
        // game clock does not advance outside Play.
        const float EmptyRescanIntervalSeconds = 0.5f;
        float _nextRescanTime;
        int _domainBodyId; // hysteresis hint for the domain resolver (0 = none)

        // Lazy init (not Awake): with ExecuteAlways the first edit-mode tick can arrive
        // before Awake after a domain reload. Re-tried while empty so visuals that spawn a
        // frame later are still picked up - but ON A TIMER (perf audit 2026-08-11): the retry
        // condition is "the set is empty", and an object that legitimately has NO renderers
        // (a bare physics root - the boat's normal shape, see above) never leaves it, so this
        // walked the whole hierarchy AND allocated a fresh array every single LateUpdate, in
        // edit mode too, forever.
        void EnsureInitialized()
        {
            if (_renderers != null && _renderers.Length > 0) return;

            float now = Time.realtimeSinceStartup;
            if (_renderers != null && now < _nextRescanTime) return;
            _nextRescanTime = now + EmptyRescanIntervalSeconds;
            _renderers = GetComponentsInChildren<Renderer>();
        }

        // LateUpdate so the containing body has finished this frame's sim/caustic pass
        // (its Update runs at DefaultExecutionOrder -50) before we copy its uniforms.
        void LateUpdate()
        {
            EnsureInitialized();
            if (_renderers.Length == 0) return; // nothing to tint yet

            // Domain-resolved (river round, 2026-08-29): full-XYZ containment, so a crate in a
            // sloped ribbon lights from the ribbon's PARENT body (the resolver maps a river win
            // to its owning volume), and stacked bodies pick by elevation. PrimaryBody fallback
            // preserves the lighting doctrine this component kept BodyContaining for: an object
            // out of every domain falls back to the primary's uniforms rather than flickering.
            // Inside an exclusion carve the resolve fails ("dry wins") and the block drops -
            // a crate in a dry room no longer shows water caustics.
            WaterVolume body = WaterDomainResolver.GameplayBodyAt(
                transform.position, WaterQueryIntent.ContainingVolume,
                WaterFallbackPolicy.PrimaryBody, ref _domainBodyId);
            if (body == null)
            {
                // No body contains this object any more (the lake was disabled, or it drifted out).
                // Returning early used to LEAVE THE LAST BLOCK in place, so a floating crate kept
                // rendering the dead body's caustics forever. Drop it and fall back to the material.
                foreach (Renderer renderer in _renderers)
                    if (renderer != null) renderer.SetPropertyBlock(null);
                return;
            }

            // The body builds this block at most once a frame and hands the SAME instance to every
            // member; SetPropertyBlock copies it into the renderer, so sharing is safe. Writing our
            // own was ~138 native property writes per object per frame for identical values.
            foreach (Renderer renderer in _renderers)
                if (renderer != null) renderer.SetPropertyBlock(body.MembershipBlock);
        }
    }
}
