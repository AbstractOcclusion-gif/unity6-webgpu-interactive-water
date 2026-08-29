// WebGpuWater - the 3D water-domain query contract (types only; rules live in WaterDomainResolver).
//
// WaterSample answers "where is the surface"; WaterDomainSample answers the gameplay question
// "WHICH water is this point in, and is it really water here": stable body identity, full-XYZ
// containment, signed depth, exclusion state and - near authored connections - transition info.
// Callers state an INTENT instead of inheriting one implicit nearest-water policy, and the
// Primary fallback that BodyContaining bakes in is available here only when explicitly requested.
//
// WebGPU-first like the height seam: every field is CPU-analytic, valid from frame 0.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>What the caller means by "the water at this point". The intent picks the candidate
    /// set and the fallback shape, so a buoyancy probe and a cast-hit test no longer share one
    /// ambiguous nearest-water rule.</summary>
    public enum WaterQueryIntent
    {
        /// <summary>The domain whose volume strictly contains the point (full XYZ). No implicit
        /// fallback: a point in the air gap between a pond and the sewer below it reports
        /// NoContainingBody instead of an arbitrary neighbour.</summary>
        ContainingVolume,
        /// <summary>The surface a floater at this point should ride: the containing domain, else
        /// the nearest surface directly BELOW the point within MaxVerticalDistance (a lure thrown
        /// over water must find the surface it is falling toward - and never a surface overhead,
        /// so nothing buried under a body's floor snaps upward).</summary>
        BuoyancySurface,
        /// <summary>The surface a ray/cast hit belongs to: the provider whose surface height is
        /// nearest the hit point (above or below) within MaxVerticalDistance.</summary>
        RayInteraction,
        /// <summary>Options.BodyHint is authoritative; other bodies are never consulted.</summary>
        ExplicitBody,
        /// <summary>The nearest valid surface within MaxVerticalDistance, above or below - the
        /// generic form of RayInteraction for callers that are not ray hits.</summary>
        NearestWithinVerticalLimits,
    }

    /// <summary>Explicit-only fallback. None is the default on purpose: the legacy resolver's
    /// unconditional Primary fallback is exactly what made stacked water unresolvable.</summary>
    public enum WaterFallbackPolicy
    {
        None,
        /// <summary>Reproduce the legacy behaviour: when nothing matches, use the primary body
        /// (or any live body). For callers that genuinely want "some water, always" - membership
        /// lighting, demo UI - and now say so.</summary>
        PrimaryBody,
    }

    /// <summary>Why a resolve returned what it did. Valid is the only success value; the failure
    /// values are distinct so callers can tell "dry land" from "excluded space" from "bad hint".</summary>
    public enum WaterDomainValidity
    {
        Valid,
        NoContainingBody,
        /// <summary>A domain matched but the point sits inside an exclusion volume - dry space.
        /// The surface fields are still filled for debug callers (IncludeExcludedSpace).</summary>
        Excluded,
        /// <summary>ExplicitBody intent with a null/dead hint.</summary>
        MissingHint,
        /// <summary>A vertical-search intent found no surface within MaxVerticalDistance.</summary>
        NoSurfaceInRange,
    }

    /// <summary>Transition data filled when the resolved point sits inside an authored
    /// connection's transition zone (see WaterTopology). Default (Active == false) elsewhere.</summary>
    public struct WaterConnectionInfo
    {
        public bool Active;
        public int ConnectionId;
        /// <summary>BodyId of the provider on the OTHER side of the seam.</summary>
        public int OtherBodyId;
        /// <summary>Weight of the other side folded into this sample (0 = none, 0.5 = the seam
        /// plane itself). Both sides converge to 0.5 at the plane, so the handoff is continuous.</summary>
        public float Blend;
    }

    /// <summary>Options for one domain resolve. A struct so batched callers pay no allocation;
    /// build with <see cref="ForIntent"/> and override fields as needed.</summary>
    public struct WaterDomainQueryOptions
    {
        public WaterQueryIntent Intent;
        /// <summary>ExplicitBody only: the authoritative body.</summary>
        public WaterVolume BodyHint;
        /// <summary>BodyId from the caller's previous sample (0 = none). Enables hysteresis: the
        /// previous domain keeps the point while it stays within DomainSwitchMarginMeters of its
        /// boundary, so a probe hovering on an edge does not chatter between results.</summary>
        public int PreviousBodyId;
        /// <summary>Vertical search reach for the surface-distance intents, metres. Values at or
        /// below 0 use WaterDomainResolver.DefaultVerticalSearchMeters.</summary>
        public float MaxVerticalDistance;
        public WaterFallbackPolicy Fallback;
        /// <summary>Debug/rendering callers: fill the sample even inside exclusions (Validity
        /// still reports Excluded; the resolve still returns false).</summary>
        public bool IncludeExcludedSpace;
        public WaterQueryFields Fields;
        /// <summary>Wavelength cut-off forwarded to the surface sample (object size; 0 = full).</summary>
        public float MinimumWaveLength;
        /// <summary>Sample the analytic surface only (see IWaterHeightSampler.SampleHeight).</summary>
        public bool ExcludeInteractiveRipples;

        public static WaterDomainQueryOptions ForIntent(WaterQueryIntent intent) =>
            new WaterDomainQueryOptions
            {
                Intent = intent,
                Fields = WaterQueryFields.HeightNormalVelocity,
            };
    }

    /// <summary>One resolved point: which water, where its surface is, and whether the point is
    /// really in water. Check <see cref="IsValid"/> (or the resolve's bool) before reading.</summary>
    public struct WaterDomainSample
    {
        /// <summary>Stable per-session body identity (see IWaterSurfaceProvider.BodyId).</summary>
        public int BodyId;
        /// <summary>The owning WaterVolume. Null for a future free-standing provider, and for a
        /// river ribbon authored without a parent volume - key gameplay off BodyId, not this.</summary>
        public WaterVolume Body;
        /// <summary>The provider that produced the surface fields (volume plane, ocean, ribbon).</summary>
        public IWaterSurfaceProvider Provider;
        public float SurfaceHeight;
        public Vector3 SurfaceNormal;
        /// <summary>Wave motion plus authored physical current, world m/s.</summary>
        public Vector3 Velocity;
        /// <summary>SurfaceHeight - point.y: positive = submerged by that many metres.</summary>
        public float SignedDepth;
        /// <summary>Full-XYZ containment of the queried point in the resolved domain.</summary>
        public bool InsideDomain;
        /// <summary>The point sits inside an exclusion volume (CPU policy: box/sphere exact,
        /// mesh via its authored proxy - see WaterExclusionVolume).</summary>
        public bool Excluded;
        public WaterDomainValidity Validity;
        public WaterConnectionInfo Connection;

        public bool IsValid => Validity == WaterDomainValidity.Valid;
    }
}
