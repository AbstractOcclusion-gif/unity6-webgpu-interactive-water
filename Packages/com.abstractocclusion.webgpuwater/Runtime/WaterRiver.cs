// WebGpuWater - the river facade: ONE component that owns a river's wiring, so authoring a river
// stops being a five-script hand-assembly (spline + current field + surface + optional fluid/foam
// + parent-volume link + ports), which today only the Connected Waters demo rig documents.
//
// Deliberately orchestration-only (reuse-never-rewrite): every setting keeps living on the
// component that consumes it - the facade OWNS the wiring between the trio (spline / current
// field / surface references and the parent-volume link are written by it, always, so there is
// one source of truth), keeps the generated seam objects consistent, and validates loudly.
// RequireComponent guarantees the spline/current/surface trio exists; WaterRiverFluid,
// WaterRiverFoam and WaterRiverDisturbance stay optional add-ons (their own RequireComponent
// chains handle their dependencies). The consolidated inspector (Editor/WaterRiverEditor.*)
// edits every sibling through this component.
//
// Each river end can name a receiving/feeding WaterVolume. A source can instead name an upstream
// river, whose mouth row is copied into this ribbon. The facade derives
// the port pair from the spline's terminal frame and owns the generated WaterConnectionPort /
// WaterConnection objects THROUGH SERIALIZED REFERENCES - regeneration reuses them, so their
// GUID-once portIds (the persistent identity streaming/saves key on) never change. The
// generated objects are hidden from the hierarchy (they are derived data, not authoring) and
// follow the spline and the end bodies automatically: the facade listens to the spline's
// Changed event, and the editor's change router forwards end-body moves.
//
// ExecuteAlways: the seam objects must follow knot and body edits in edit mode too, which needs
// the spline subscription and LateUpdate alive there. Edit-mode side effects are the same ones
// OnValidate already performed on every inspector nudge; console warnings stay play-mode only
// because the inspector shows them while editing.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Which end of the spline a connection hangs off.</summary>
    public enum WaterRiverEndKind
    {
        Source = 0,
        Mouth = 1,
    }

    /// <summary>Per-end connection authoring + the facade-owned generated seam objects.</summary>
    [Serializable]
    public sealed class WaterRiverEndConnection
    {
        [Tooltip("Water body this river end connects to (lake, ocean, reservoir). Empty = an " +
                 "unconnected end.")]
        [SerializeField] internal WaterVolume body;

        [Tooltip("Upstream river whose MOUTH is sewn into this river's SOURCE. The two ribbons " +
                 "must use the same parent volume. Mutually exclusive with Body.")]
        [SerializeField] internal WaterRiver upstreamRiver;

        [Tooltip("Half-width of the seam transition slab, metres (see WaterConnection).")]
        [Min(WaterConnection.MinTransitionRadiusMeters)]
        [SerializeField] internal float transitionRadiusMeters =
            WaterConnection.DefaultTransitionRadiusMeters;

        // Generated seam objects, owned by the facade THROUGH these references. Regeneration
        // reuses them (portId stability); removal destroys exactly these and nothing else, so a
        // hand-authored port elsewhere in the scene is untouchable by facade operations.
        [SerializeField] internal WaterConnectionPort riverPort;
        [FormerlySerializedAs("bodyPort")]
        [SerializeField] internal WaterConnectionPort targetPort;
        [SerializeField] internal WaterConnection connection;

        internal bool WantsConnection => body != null || upstreamRiver != null;
        internal bool HasAmbiguousTarget => body != null && upstreamRiver != null;
        internal bool IsGenerated => riverPort != null && targetPort != null && connection != null;
        internal bool IsPartiallyGenerated =>
            !IsGenerated && (riverPort != null || targetPort != null || connection != null);
    }

    [ExecuteAlways]
    [AddComponentMenu("Abstract Occlusion/WebGpuWater/Water River")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WaterRiverSpline), typeof(WaterRiverCurrentField),
                      typeof(WaterRiverSurface))]
    public sealed class WaterRiver : MonoBehaviour
    {
        // The border contract, enforced at authoring time: a terminal knot belongs ON (or just
        // outside) its body's footprint border. Deeper placement re-opens the mid-body seam the
        // contract exists to remove - such links should become waterfalls instead (scoped).
        internal const float MaxKnotInsetWarnMeters = 0.5f;

        const float DefaultMouthOutflowLengthMeters = 16f;
        const float DefaultMouthOutflowFoamLengthMeters = 4f;
        const float DefaultMouthOutflowSpreadPerMeter = 0.2f;
        const float DefaultMouthOutflowStrength = 1f;
        const float DefaultMouthFoamPatternSizeMeters = 1f;
        const float DefaultMouthFoamEdgeFeather = 0f;
        const float DefaultMouthFoamCoreCut = 0f;
        const int MouthProfileSampleCount = 5;
        // Time.frameCount is never negative, so -1 can never collide with a stamped frame.
        const int InvalidFrame = -1;

        const string SourceRiverPortName = "Port - River Source";
        const string MouthRiverPortName = "Port - River Mouth";
        const string TargetPortNamePrefix = "Port - ";
        const string ConnectionNamePrefix = "Connection - River to ";

        [Tooltip("Body supplying the ribbon's animated shader uniforms and (for the underwater " +
                 "path) the fog medium. Empty = a standalone ribbon: gameplay queries still work " +
                 "through the river provider, but there are no animated waves and no underwater " +
                 "fog until a parent is assigned.")]
        [SerializeField] internal WaterVolume parentVolume;

        [Tooltip("Connect the spline's FIRST knot (the source) to a feeding body.")]
        [SerializeField] internal WaterRiverEndConnection sourceEnd = new WaterRiverEndConnection();

        [Tooltip("Connect the spline's LAST knot (the mouth) to a receiving body.")]
        [SerializeField] internal WaterRiverEndConnection mouthEnd = new WaterRiverEndConnection();

        [Header("Mouth outflow")]
        [Tooltip("Distance into the receiving body over which the river current and visible flow decay.")]
        [Min(WaterConnection.MinTransitionRadiusMeters)]
        [SerializeField] internal float mouthOutflowLengthMeters = DefaultMouthOutflowLengthMeters;
        [Tooltip("How many metres the plume half-width expands per metre travelled downstream.")]
        [Min(0f)]
        [SerializeField] internal float mouthOutflowSpreadPerMeter = DefaultMouthOutflowSpreadPerMeter;
        [Tooltip("Multiplier applied to the terminal baked current in the receiving body.")]
        [Min(0f)]
        [SerializeField] internal float mouthOutflowCurrentStrength = DefaultMouthOutflowStrength;
        [Tooltip("Distance into the receiving body over which carried river foam dissolves.")]
        [Min(WaterConnection.MinTransitionRadiusMeters)]
        [SerializeField] internal float mouthOutflowFoamLengthMeters =
            DefaultMouthOutflowFoamLengthMeters;
        [Tooltip("Multiplier applied to terminal baked foam carried into the receiving body.")]
        [Min(0f)]
        [SerializeField] internal float mouthOutflowFoamStrength = DefaultMouthOutflowStrength;

        // Generated ports/connections are derived data: hidden by default so the hierarchy shows
        // only what was authored. The inspector's "Show generated objects" toggle flips this and
        // the facade re-applies it on every enable, so scenes saved before the flag existed get
        // their children hidden the next time they load.
        [SerializeField] internal bool showGeneratedObjects;

        WaterRiverSpline _spline;
        WaterRiverSpline _subscribedSpline;
        WaterRiverCurrentField _currentField;
        WaterRiverSurface _surface;
        WaterRiverFluid _fluid;
        WaterRiverFoam _foam;
        // The volume whose currentFields currently hold our field - remembered so detach removes
        // it from the RIGHT body even after parentVolume is retargeted mid-session.
        WaterVolume _currentFieldHost;
        WaterVolume _mouthCurrentFieldHost;
        WaterVolume _mouthOutflowHost;
        // TryBuildMouthOutflow runs several times per frame in play (our LateUpdate, the host
        // body's RefreshRiverMouthOutflowShaderData, the surface's own publish) on inputs that
        // settle before LateUpdate, so one build per frame is exact. Edit mode has no advancing
        // frame stamp, so it never takes the cache; the dirty flag covers the in-frame edits
        // (OnValidate, spline/bake/seam changes) that would otherwise be served stale in play.
        int _mouthOutflowFrame = InvalidFrame;
        bool _mouthOutflowValid;
        WaterRiverMouthOutflow _mouthOutflowCached;
        bool _mouthOutflowDirty = true;
        WaterRiverFluid _subscribedFluid;

        public WaterRiverSpline Spline => _spline != null ? _spline : GetComponent<WaterRiverSpline>();
        public WaterRiverSurface Surface => _surface != null ? _surface : GetComponent<WaterRiverSurface>();
        public WaterVolume ParentVolume => parentVolume;
        internal WaterRiverEndConnection SourceEnd => sourceEnd;
        internal WaterRiverEndConnection MouthEnd => mouthEnd;

        internal WaterRiverEndConnection EndFor(WaterRiverEndKind endKind)
            => endKind == WaterRiverEndKind.Source ? sourceEnd : mouthEnd;

        void Reset()
        {
            CacheSiblings();
            // Adoption must not clobber authored wiring: a hand-wired river dropped this facade
            // on keeps its configured parent - the facade pulls the value instead of pushing one.
            if (parentVolume == null && _surface != null) parentVolume = _surface.WaterVolume;
        }

        void OnEnable()
        {
            CacheSiblings();
            ApplyWiring();
            RebindSplineEvents();
            RebindFluidEvents();
            ApplyGeneratedObjectVisibility();
            SyncSeams();
            AttachCurrentFieldToParent();
            SyncMouthOutflowRegistration();
            // Edit mode surfaces the same findings in the Connections tab; the console is the
            // play-mode channel (a scene load in the editor must not spam per river).
            if (Application.isPlaying) WarnOnInvalidSetup();
        }

        void OnDisable()
        {
            UnsubscribeSplineEvents();
            UnsubscribeFluidEvents();
            DetachCurrentFieldFromParent();
            UnregisterMouthOutflow();
        }

        void LateUpdate()
        {
            if (_mouthOutflowHost == null || _currentField == null) return;
            _mouthOutflowHost.InvalidateRiverMouthOutflows();
            _currentField.ConfigureMouthOutflow(
                TryBuildMouthOutflow(out WaterRiverMouthOutflow outflow) ? outflow : default);
        }

        void OnValidate()
        {
            mouthOutflowLengthMeters = Mathf.Max(
                WaterConnection.MinTransitionRadiusMeters, mouthOutflowLengthMeters);
            mouthOutflowSpreadPerMeter = Mathf.Max(0f, mouthOutflowSpreadPerMeter);
            mouthOutflowCurrentStrength = Mathf.Max(0f, mouthOutflowCurrentStrength);
            mouthOutflowFoamLengthMeters = Mathf.Clamp(
                mouthOutflowFoamLengthMeters, WaterConnection.MinTransitionRadiusMeters,
                mouthOutflowLengthMeters);
            mouthOutflowFoamStrength = Mathf.Max(0f, mouthOutflowFoamStrength);
            _mouthOutflowDirty = true;
            CacheSiblings();
            ApplyWiring();
            // A disabled facade must not listen: OnDisable already dropped its subscription and
            // would have no chance to drop this one.
            if (isActiveAndEnabled)
            {
                RebindSplineEvents();
                RebindFluidEvents();
            }
            SyncGeneratedConnections();
        }

        /// <summary>Transform-only refresh of everything derived from the spline and the end
        /// targets. Never creates or destroys objects (Unity forbids that from OnValidate, and
        /// creation stays an editor gesture), so it only re-aims the EXISTING generated ports,
        /// re-pushes the seam descriptors and re-registers the mouth outflow. Mesh rebuilds are
        /// established OnValidate practice here (see RequestRebuild's own callers).</summary>
        internal void SyncGeneratedConnections()
        {
            _mouthOutflowDirty = true;
            SyncGeneratedConnectionAnchors();
            SyncSeams();
            SyncMouthOutflowRegistration();
        }

        // The surface rebuilds its mesh from the same event; the facade re-aims the ports so the
        // seam plane and the apron never lag behind a knot drag.
        void RebindSplineEvents()
        {
            if (_subscribedSpline == _spline) return;
            UnsubscribeSplineEvents();
            _subscribedSpline = _spline;
            if (_subscribedSpline != null) _subscribedSpline.Changed += SyncGeneratedConnections;
        }

        void UnsubscribeSplineEvents()
        {
            if (_subscribedSpline != null) _subscribedSpline.Changed -= SyncGeneratedConnections;
            _subscribedSpline = null;
        }

        // A bake assignment (WaterRiverFluid.AssignBakeData) changes the mouth profile the
        // outflow samples; same subscription shape WaterRiverFoam uses for its refresh.
        void RebindFluidEvents()
        {
            if (_subscribedFluid == _fluid) return;
            UnsubscribeFluidEvents();
            _subscribedFluid = _fluid;
            if (_subscribedFluid != null) _subscribedFluid.ConfigurationChanged += MarkMouthOutflowDirty;
        }

        void UnsubscribeFluidEvents()
        {
            if (_subscribedFluid != null) _subscribedFluid.ConfigurationChanged -= MarkMouthOutflowDirty;
            _subscribedFluid = null;
        }

        void MarkMouthOutflowDirty() => _mouthOutflowDirty = true;

        /// <summary>True when this river's source or mouth targets the body - the editor's change
        /// router uses it to re-aim ports after the body moves.</summary>
        internal bool HasEndBody(WaterVolume body)
            => body != null && (sourceEnd.body == body || mouthEnd.body == body);

        void CacheSiblings()
        {
            if (_spline == null) _spline = GetComponent<WaterRiverSpline>();
            if (_currentField == null) _currentField = GetComponent<WaterRiverCurrentField>();
            if (_surface == null) _surface = GetComponent<WaterRiverSurface>();
            if (_fluid == null) _fluid = GetComponent<WaterRiverFluid>();
            if (_foam == null) _foam = GetComponent<WaterRiverFoam>();
        }

        // The facade is the single source for the trio's wiring: the sibling spline is assigned
        // ALWAYS (not fill-null), so the three spline copies (facade, surface, current field) and
        // the current field's fluid link cannot drift once a facade is present. Sub-components
        // used without a facade (tests, hand rigs) keep their own references untouched. The
        // facade field is the authority for the parent too (Reset adopted it from the surface
        // first), so clearing it here deliberately clears the surface link - that is how a river
        // goes standalone.
        internal void ApplyWiring()
        {
            // Re-resolve first: the inspector calls this right after adding an optional sibling
            // (fluid) so the current field picks the new component up without a re-enable.
            CacheSiblings();
            if (_spline == null || _currentField == null || _surface == null) return;

            if (_currentField.spline != _spline) _currentField.Configure(_spline);
            _currentField.fluid = _fluid;
            if (_surface.Spline != _spline)
            {
                _surface.spline = _spline;
                _surface.RequestRebuild();
            }
            if (_surface.WaterVolume != parentVolume)
            {
                _surface.waterVolume = parentVolume;
                _surface.RequestRendererRefresh();
            }
        }

        // ---- parent current-field link -----------------------------------------------------
        // The parent samples river current near the seam through its own currentFields list
        // (WaterVolume.SampleCurrentFields). APPEND, never assign: the demo rig's original
        // `lake.currentFields = new[]{field}` replaced the array and would silently delete a
        // user's other authored fields.

        internal void AttachCurrentFieldToParent()
        {
            AttachCurrentField(parentVolume, ref _currentFieldHost);
            WaterVolume mouthBody = ConnectedMouthBody;
            AttachCurrentField(mouthBody != parentVolume ? mouthBody : null,
                               ref _mouthCurrentFieldHost);
        }

        void AttachCurrentField(WaterVolume host, ref WaterVolume rememberedHost)
        {
            if (_currentField == null || rememberedHost == host) return;
            DetachCurrentField(ref rememberedHost);
            if (host == null) return;

            WaterCurrentField[] fields = host.currentFields ?? Array.Empty<WaterCurrentField>();
            if (Array.IndexOf(fields, _currentField) < 0)
            {
                var grown = new WaterCurrentField[fields.Length + 1];
                Array.Copy(fields, grown, fields.Length);
                grown[fields.Length] = _currentField;
                host.currentFields = grown;
            }
            rememberedHost = host;
        }

        internal void DetachCurrentFieldFromParent()
        {
            DetachCurrentField(ref _mouthCurrentFieldHost);
            DetachCurrentField(ref _currentFieldHost);
        }

        void DetachCurrentField(ref WaterVolume rememberedHost)
        {
            if (rememberedHost == null || _currentField == null)
            {
                rememberedHost = null;
                return;
            }

            WaterCurrentField[] fields = rememberedHost.currentFields;
            int index = fields != null ? Array.IndexOf(fields, _currentField) : -1;
            if (index >= 0)
            {
                // Remove exactly our own entry; every other authored field stays untouched.
                var shrunk = new WaterCurrentField[fields.Length - 1];
                for (int i = 0, w = 0; i < fields.Length; i++)
                    if (i != index) shrunk[w++] = fields[i];
                rememberedHost.currentFields = shrunk;
            }
            rememberedHost = null;
        }

        WaterVolume ConnectedMouthBody =>
            mouthEnd != null && mouthEnd.IsGenerated ? mouthEnd.body : null;

        void SyncMouthOutflowRegistration()
        {
            _mouthOutflowDirty = true;
            CacheSiblings();
            WaterVolume target = ConnectedMouthBody;
            if (_mouthOutflowHost != target)
            {
                UnregisterMouthOutflow();
                _mouthOutflowHost = target;
                _mouthOutflowHost?.RegisterRiverMouthOutflow(this);
            }
            else _mouthOutflowHost?.InvalidateRiverMouthOutflows();

            AttachCurrentFieldToParent();
            if (_currentField == null) return;
            _currentField.ConfigureMouthOutflow(
                TryBuildMouthOutflow(out WaterRiverMouthOutflow outflow) ? outflow : default);
            _surface?.RequestRendererRefresh();
        }

        void UnregisterMouthOutflow()
        {
            _mouthOutflowHost?.UnregisterRiverMouthOutflow(this);
            _mouthOutflowHost = null;
            _currentField?.ConfigureMouthOutflow(default);
        }

        internal bool TryBuildMouthOutflow(out WaterRiverMouthOutflow outflow)
        {
            if (!_mouthOutflowDirty && Application.isPlaying &&
                _mouthOutflowFrame == Time.frameCount)
            {
                outflow = _mouthOutflowCached;
                return _mouthOutflowValid;
            }
            return CacheMouthOutflow(BuildMouthOutflow(out outflow), outflow);
        }

        bool CacheMouthOutflow(bool valid, in WaterRiverMouthOutflow outflow)
        {
            _mouthOutflowCached = outflow;
            _mouthOutflowValid = valid;
            _mouthOutflowFrame = Time.frameCount;
            _mouthOutflowDirty = false;
            return valid;
        }

        bool BuildMouthOutflow(out WaterRiverMouthOutflow outflow)
        {
            outflow = default;
            CacheSiblings();
            if (ConnectedMouthBody == null || _spline == null || _spline.KnotCount < 2)
                return false;

            WaterRiverRibbonMeshGenerator.BodySeamEnd seam =
                BodySeamFor(mouthEnd, WaterRiverEndKind.Mouth);
            if (!seam.IsActive) return false;

            WaterRiverKnot terminal = _spline.GetKnot(_spline.KnotCount - 1);
            float speed = terminal.Speed;
            float foamLateralSpeed = 0f;
            float foamCoverage = 0f;
            WaterRiverFluidBakeData bake = _fluid != null && _fluid.isActiveAndEnabled
                ? _fluid.BakeData : null;
            if (bake != null && bake.IsValid)
            {
                float speedSum = 0f;
                float lateralSpeedSum = 0f;
                float foamSum = 0f;
                int validSamples = 0;
                for (int sampleIndex = 0; sampleIndex < MouthProfileSampleCount; sampleIndex++)
                {
                    float lateral = sampleIndex / (float)(MouthProfileSampleCount - 1);
                    if (!bake.TrySample(lateral, 1f, out Vector2 ribbonVelocity,
                                        out float foam, out _))
                        continue;
                    speedSum += Mathf.Max(0f, ribbonVelocity.y);
                    lateralSpeedSum += ribbonVelocity.x;
                    foamSum += foam;
                    validSamples++;
                }
                if (validSamples > 0)
                {
                    speed = speedSum > 0f ? speedSum / validSamples : terminal.Speed;
                    foamLateralSpeed = lateralSpeedSum / validSamples;
                    foamCoverage = foamSum / validSamples;
                }
            }

            bool foamEnabled = _foam != null && _foam.isActiveAndEnabled;
            if (foamEnabled)
            {
                float bakedCoverage = foamCoverage * _foam.strength;
                float cascadeCoverage = _foam.TransportedCascadeAtMouth;
                foamCoverage = CombineCoverage(bakedCoverage, cascadeCoverage);
                float inheritedFoamStrength = _surface != null &&
                                              _surface.WaterVolume != null
                    ? _surface.WaterVolume.foamStrength : 1f;
                foamCoverage = Mathf.Clamp01(foamCoverage * inheritedFoamStrength) *
                               _foam.overallStrength;
            }
            else foamCoverage = 0f;
            Vector3 foamRight = _surface != null ? _surface.MouthRight : Vector3.zero;
            if (!WaterSurfaceKinematics.IsFinite(foamRight) || foamRight.sqrMagnitude <= 0f)
                foamRight = seam.Right;
            float foamLongitudinalMeters = _surface != null
                ? _surface.MouthLongitudinalMeters : 0f;
            if (foamLongitudinalMeters <= 0f && _foam != null)
                foamLongitudinalMeters = _foam.TransportLength;
            float foamPatternSizeMeters = _foam != null
                ? _foam.patternSize : DefaultMouthFoamPatternSizeMeters;
            float foamEdgeFeather = _foam != null
                ? _foam.edgeFeather : DefaultMouthFoamEdgeFeather;
            float foamCoreCut = _foam != null
                ? _foam.coreCut : DefaultMouthFoamCoreCut;
            float oceanMouthOverlapMeters = IsUnboundedOcean(ConnectedMouthBody)
                ? mouthEnd.transitionRadiusMeters : 0f;
            outflow = new WaterRiverMouthOutflow(
                seam.Centre, seam.Downstream, foamRight,
                terminal.HalfWidth, mouthOutflowLengthMeters,
                mouthOutflowSpreadPerMeter, speed, foamCoverage,
                Mathf.Min(mouthOutflowFoamLengthMeters, mouthOutflowLengthMeters),
                mouthOutflowCurrentStrength, mouthOutflowFoamStrength,
                foamLongitudinalMeters, foamLateralSpeed, foamPatternSizeMeters,
                foamEdgeFeather, foamCoreCut, oceanMouthOverlapMeters);
            return outflow.IsActive;
        }

        static float CombineCoverage(float first, float second)
            => Mathf.Clamp01(first + second - first * second);

        // ---- generated connections --------------------------------------------------------

        /// <summary>Terminal spline frame for one end: anchor (with height), the authored
        /// DOWNSTREAM direction (ports face downstream by convention on both ends), and the
        /// authored volumetric flow (width x speed at that end).</summary>
        internal bool TryGetEndFrame(WaterRiverEndKind endKind, out Vector3 anchor,
                                     out Vector3 downstream, out float flowRate)
        {
            anchor = Vector3.zero;
            downstream = Vector3.forward;
            flowRate = 0f;
            CacheSiblings();
            if (_spline == null || _spline.KnotCount < 2) return false;

            int segmentIndex = endKind == WaterRiverEndKind.Source ? 0 : _spline.KnotCount - 2;
            float segmentT = endKind == WaterRiverEndKind.Source ? 0f : 1f;
            if (!_spline.TryEvaluateSegment(segmentIndex, segmentT, out WaterRiverSplineSample sample))
                return false;
            if (!WaterSurfaceKinematics.IsFinite(sample.Position) ||
                !WaterSurfaceKinematics.IsFinite(sample.Tangent))
                return false;

            anchor = sample.Position;
            downstream = sample.Tangent.normalized;
            flowRate = sample.Width * sample.Speed;
            return true;
        }

        /// <summary>Where a bounded BODY-side port lands: the closest point ON the body's footprint
        /// BORDER to the river anchor, at the body's rest level, always horizontal. THE BORDER
        /// CONTRACT: surface-to-surface connections meet only at the footprint edge - the seam
        /// then coincides with the bank line, where the body's own edge feather
        /// (LargeWaveEdgeWeight) already calms the wave field, so the stitch has no open rippled
        /// water to cut across. (The previous step-toward-centre port kept mid-body seams alive;
        /// links that cannot meet a border belong to the scoped waterfall connection kind.)
        /// Pool space makes the footprint the unit square whatever the body's rotation; edge
        /// distances compare in WORLD metres per axis because the pool frame is anisotropic.</summary>
        internal static Vector3 DeriveBodyPortPosition(WaterVolume body, Vector3 riverAnchor)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            float restY = body.VolumeCenter.y;
            Vector3 pool = body.WorldToPool(new Vector3(riverAnchor.x, restY, riverAnchor.z));
            Vector3 extent = body.VolumeExtentSafe;
            float x = Mathf.Clamp(pool.x, -1f, 1f);
            float z = Mathf.Clamp(pool.z, -1f, 1f);
            if (x == pool.x && z == pool.z)
            {
                // Inside the footprint: push whichever axis is nearer its edge (in world metres)
                // onto that edge. An exactly-centred anchor snaps +x - deterministic, and the
                // authoring warning has already flagged anything this deep inside.
                float toEdgeX = (1f - Mathf.Abs(x)) * extent.x;
                float toEdgeZ = (1f - Mathf.Abs(z)) * extent.z;
                if (toEdgeX <= toEdgeZ) x = x >= 0f ? 1f : -1f;
                else z = z >= 0f ? 1f : -1f;
            }
            Vector3 border = body.PoolToWorld(new Vector3(x, 0f, z));
            return new Vector3(border.x, restY, border.z);
        }

        static bool IsUnboundedOcean(WaterVolume body)
            => body != null && body.openWater && body.unboundedOcean;

        static Vector3 DeriveOceanPortPosition(WaterVolume ocean, Vector3 riverAnchor,
                                               Vector3 downstream, float overlapMeters)
        {
            Vector3 up = ocean.VolumeUp;
            Vector3 planarDownstream = Vector3.ProjectOnPlane(downstream, up);
            if (planarDownstream.sqrMagnitude <= 0f)
                planarDownstream = ocean.VolumeRotation * Vector3.forward;
            planarDownstream.Normalize();
            Vector3 target = riverAnchor + planarDownstream * overlapMeters;
            return target + up * Vector3.Dot(ocean.VolumeCenter - target, up);
        }

        /// <summary>The border contract check (see MaxKnotInsetWarnMeters): true when the anchor
        /// sits deeper inside the body's footprint than the contract tolerates, with the inset in
        /// world metres. Shared by the generation-time console warning and the inspector.</summary>
        internal static bool IsAnchorDeepInsideFootprint(WaterVolume body, Vector3 anchor,
                                                         out float insetMeters)
        {
            insetMeters = 0f;
            Vector3 pool = body.WorldToPool(new Vector3(anchor.x, body.VolumeCenter.y, anchor.z));
            if (Mathf.Abs(pool.x) >= 1f || Mathf.Abs(pool.z) >= 1f) return false; // outside: fine
            Vector3 extent = body.VolumeExtentSafe;
            insetMeters = Mathf.Min((1f - Mathf.Abs(pool.x)) * extent.x,
                                    (1f - Mathf.Abs(pool.z)) * extent.z);
            return insetMeters > MaxKnotInsetWarnMeters;
        }

        /// <summary>The inset check for one end against its assigned body, from the spline's
        /// current terminal frame. False for unconnected ends and unbounded oceans (no border).</summary>
        internal bool IsEndKnotDeepInsideBody(WaterRiverEndKind endKind, out float insetMeters)
        {
            insetMeters = 0f;
            WaterRiverEndConnection end = EndFor(endKind);
            if (end.body == null || IsUnboundedOcean(end.body)) return false;
            if (!TryGetEndFrame(endKind, out Vector3 anchor, out _, out _)) return false;
            return IsAnchorDeepInsideFootprint(end.body, anchor, out insetMeters);
        }

        // Only called from explicit (re)generation - OnValidate-path syncs stay silent, or every
        // inspector nudge would spam the console.
        static void WarnIfAnchorDeepInsideFootprint(WaterRiverEndKind endKind, WaterVolume body,
                                                    Vector3 anchor)
        {
            if (!IsAnchorDeepInsideFootprint(body, anchor, out float insetMeters)) return;
            Debug.LogWarning(
                $"WaterRiver: the {endKind} terminal knot sits {insetMeters:0.0} m inside " +
                $"'{body.name}'s footprint. Surface-to-surface seams connect at the footprint " +
                "BORDER - move the knot to the bank line (a link that cannot meet a border " +
                "should be a waterfall instead).", body);
        }

        /// <summary>Create or update one end's seam. Reuses the serialized generated objects when
        /// they are alive - their GUID-once portIds survive retargeting and spline edits - and
        /// only creates what is missing. Caller (the editor button) owns Undo registration.</summary>
        internal void RegenerateConnection(WaterRiverEndKind endKind)
        {
            WaterRiverEndConnection end = EndFor(endKind);
            if (!end.WantsConnection)
                throw new InvalidOperationException(
                    $"WaterRiver: {endKind} end has no target assigned - nothing to connect to.");
            if (end.HasAmbiguousTarget)
                throw new InvalidOperationException(
                    $"WaterRiver: {endKind} end has both a body and an upstream river. " +
                    "Assign exactly one target.");
            if (end.upstreamRiver != null && endKind != WaterRiverEndKind.Source)
                throw new InvalidOperationException(
                    "WaterRiver: river-to-river stitching is authored on the downstream " +
                    "river's Source end.");
            if (endKind == WaterRiverEndKind.Source && IsUnboundedOcean(end.body))
                throw new InvalidOperationException(
                    "WaterRiver: an unbounded ocean can currently receive a river Mouth, but " +
                    "cannot feed a river Source. Author the ocean connection at the last knot.");
            if (end.upstreamRiver == this)
                throw new InvalidOperationException("WaterRiver: a river cannot connect to itself.");
            if (end.upstreamRiver != null && UpstreamChainContains(end.upstreamRiver, this))
                throw new InvalidOperationException(
                    "WaterRiver: this river-to-river connection would create a stitch cycle.");
            if (end.upstreamRiver != null && end.upstreamRiver.ParentVolume != parentVolume)
                throw new InvalidOperationException(
                    "WaterRiver: stitched rivers must share the same parent volume so their " +
                    "boundary row receives identical wave uniforms.");
            if (!TryGetEndFrame(endKind, out Vector3 anchor, out Vector3 downstream, out float flowRate))
                throw new InvalidOperationException(
                    "WaterRiver: the spline cannot provide a terminal frame (needs >= 2 valid knots).");
            if (end.body != null && !IsUnboundedOcean(end.body))
                WarnIfAnchorDeepInsideFootprint(endKind, end.body, anchor);

            string riverPortName = endKind == WaterRiverEndKind.Source
                ? SourceRiverPortName : MouthRiverPortName;
            end.riverPort = EnsureChildPort(end.riverPort, riverPortName);
            end.riverPort.river = _surface;
            end.riverPort.body = null;

            string targetName = end.body != null ? end.body.name : end.upstreamRiver.name;
            end.targetPort = EnsureChildPort(
                end.targetPort, TargetPortNamePrefix + targetName);
            end.targetPort.body = end.body;
            end.targetPort.river = end.upstreamRiver != null ? end.upstreamRiver.Surface : null;

            end.connection = EnsureChildConnection(
                end.connection, ConnectionNamePrefix + targetName);
            // Upstream side is port A (the WaterConnection sign convention): at the mouth the
            // river feeds the body; at the source the body feeds the river.
            bool riverIsUpstream = endKind == WaterRiverEndKind.Mouth;
            end.connection.portA = riverIsUpstream ? end.riverPort : end.targetPort;
            end.connection.portB = riverIsUpstream ? end.targetPort : end.riverPort;
            end.connection.transitionRadiusMeters = end.transitionRadiusMeters;
            end.connection.authoredFlowRate = flowRate;

            PlaceEndTransforms(end, endKind, anchor, downstream);
            ApplyGeneratedObjectVisibility();
            SyncSeams();
            SyncMouthOutflowRegistration();
        }

        // ---- render seams ------------------------------------------------------------------

        // A bounded body remains the sole owner of its footprint. An unbounded ocean has no useful
        // rectangle border, so its target frame extends one transition radius along the authored
        // river tangent. The conformed terminal band becomes the visible overlap apron while the
        // ocean shader relinquishes only that coincident surface strip.
        void SyncSeams()
        {
            _mouthOutflowDirty = true; // the seam frame is the outflow's origin and direction
            CacheSiblings();
            if (_surface == null) return;
            _surface.ConfigureBodySeams(
                BodySeamFor(sourceEnd, WaterRiverEndKind.Source),
                BodySeamFor(mouthEnd, WaterRiverEndKind.Mouth));
            _surface.ConfigureSourceBoundary(
                sourceEnd.IsGenerated && sourceEnd.upstreamRiver != null
                    ? sourceEnd.upstreamRiver.Surface : null,
                useTargetMouth: true);
        }

        static WaterRiverRibbonMeshGenerator.BodySeamEnd BodySeamFor(
            WaterRiverEndConnection end, WaterRiverEndKind endKind)
        {
            if (!end.IsGenerated || end.body == null) return default;

            WaterVolume body = end.body;
            Vector3 centre = end.targetPort.transform.position;
            Vector3 up = body.VolumeUp;
            if (IsUnboundedOcean(body))
            {
                Vector3 oceanDownstream = Vector3.ProjectOnPlane(
                    end.riverPort.transform.forward, up);
                if (oceanDownstream.sqrMagnitude <= 0f)
                    oceanDownstream = body.VolumeRotation * Vector3.forward;
                oceanDownstream.Normalize();
                return new WaterRiverRibbonMeshGenerator.BodySeamEnd
                {
                    Enabled = true,
                    LengthMeters = end.transitionRadiusMeters,
                    Centre = centre,
                    Downstream = oceanDownstream,
                    Right = Vector3.Cross(up, oceanDownstream).normalized,
                    Up = up.normalized,
                };
            }

            Vector3 pool = body.WorldToPool(centre);
            Vector3 extent = body.VolumeExtentSafe;
            float xEdgeDistance = Mathf.Abs(Mathf.Abs(pool.x) - 1f) * extent.x;
            float zEdgeDistance = Mathf.Abs(Mathf.Abs(pool.z) - 1f) * extent.z;
            Vector3 axisX = body.VolumeRotation * Vector3.right;
            Vector3 axisZ = body.VolumeRotation * Vector3.forward;
            Vector3 outward = xEdgeDistance <= zEdgeDistance
                ? axisX * (pool.x >= 0f ? 1f : -1f)
                : axisZ * (pool.z >= 0f ? 1f : -1f);
            Vector3 downstream = endKind == WaterRiverEndKind.Mouth ? -outward : outward;
            Vector3 right = Vector3.Cross(up, downstream).normalized;

            return new WaterRiverRibbonMeshGenerator.BodySeamEnd
            {
                Enabled = true,
                LengthMeters = end.transitionRadiusMeters,
                Centre = centre,
                Downstream = downstream.normalized,
                Right = right,
                Up = up.normalized,
            };
        }

        /// <summary>Re-aim already-generated ports after spline/authoring edits. Transform-only,
        /// so it is legal from OnValidate; ends that were never generated are untouched.</summary>
        internal void SyncGeneratedConnectionAnchors()
        {
            SyncEndAnchors(WaterRiverEndKind.Source);
            SyncEndAnchors(WaterRiverEndKind.Mouth);
        }

        void SyncEndAnchors(WaterRiverEndKind endKind)
        {
            WaterRiverEndConnection end = EndFor(endKind);
            if (!end.IsGenerated || !end.WantsConnection || end.HasAmbiguousTarget) return;
            if (!TryGetEndFrame(endKind, out Vector3 anchor, out Vector3 downstream, out _)) return;
            PlaceEndTransforms(end, endKind, anchor, downstream);
            end.connection.transitionRadiusMeters = end.transitionRadiusMeters;
            _mouthOutflowDirty = true; // the port anchor is the seam centre
            _mouthOutflowHost?.InvalidateRiverMouthOutflows();
        }

        void PlaceEndTransforms(WaterRiverEndConnection end, WaterRiverEndKind endKind,
                                Vector3 anchor, Vector3 downstream)
        {
            Quaternion facing = downstream.sqrMagnitude > 0f
                ? Quaternion.LookRotation(downstream, Vector3.up) : Quaternion.identity;
            end.riverPort.transform.SetPositionAndRotation(anchor, facing);
            Vector3 targetAnchor;
            Quaternion targetFacing = facing;
            if (end.body != null)
            {
                targetAnchor = IsUnboundedOcean(end.body)
                    ? DeriveOceanPortPosition(
                        end.body, anchor, downstream, end.transitionRadiusMeters)
                    : DeriveBodyPortPosition(end.body, anchor);
            }
            else
            {
                GetUpstreamRiverMouthFrame(
                    end.upstreamRiver, out targetAnchor, out Vector3 targetDownstream);
                targetFacing = Quaternion.LookRotation(targetDownstream, Vector3.up);
            }
            end.targetPort.transform.SetPositionAndRotation(targetAnchor, targetFacing);
            end.connection.transform.position = (anchor + targetAnchor) * 0.5f;
        }

        static void GetUpstreamRiverMouthFrame(WaterRiver upstreamRiver, out Vector3 anchor,
                                               out Vector3 downstream)
        {
            if (upstreamRiver == null || !upstreamRiver.TryGetEndFrame(
                    WaterRiverEndKind.Mouth, out anchor, out downstream, out _))
                throw new InvalidOperationException(
                    "WaterRiver: the upstream river cannot provide its mouth frame.");
        }

        static bool UpstreamChainContains(WaterRiver start, WaterRiver candidate)
        {
            WaterRiver current = start;
            var visited = new HashSet<WaterRiver>();
            while (current != null && visited.Add(current))
            {
                if (current == candidate) return true;
                current = current.sourceEnd != null ? current.sourceEnd.upstreamRiver : null;
            }
            return false;
        }

        /// <summary>Forget one end's generated objects. The caller destroys the GameObjects (with
        /// Undo in the editor); the facade only owns the references.</summary>
        internal void ClearConnectionRefs(WaterRiverEndKind endKind)
        {
            WaterRiverEndConnection end = EndFor(endKind);
            end.riverPort = null;
            end.targetPort = null;
            end.connection = null;
            SyncSeams();
            SyncMouthOutflowRegistration();
        }

        WaterConnectionPort EnsureChildPort(WaterConnectionPort existing, string childName)
        {
            if (existing != null)
            {
                existing.gameObject.name = childName;
                return existing;
            }
            WaterConnectionPort created = NewChild(childName).AddComponent<WaterConnectionPort>();
            // Runtime AddComponent skips Reset/OnValidate - mint the persistent id NOW so the
            // port never exists id-less (saves/streaming key on it from the first frame).
            created.EnsurePortId();
            return created;
        }

        WaterConnection EnsureChildConnection(WaterConnection existing, string childName)
        {
            if (existing != null)
            {
                existing.gameObject.name = childName;
                return existing;
            }
            return NewChild(childName).AddComponent<WaterConnection>();
        }

        GameObject NewChild(string childName)
        {
            var child = new GameObject(childName);
            child.transform.SetParent(transform, worldPositionStays: true);
            return child;
        }

        // ---- generated-object visibility ---------------------------------------------------
        // HideInHierarchy only: the objects still serialize with the scene (they carry the
        // persistent portIds) and stay selectable through the inspector's Select buttons.

        /// <summary>Editor entry point for the "Show generated objects" toggle. The caller owns
        /// Undo registration of the facade and the children.</summary>
        internal void SetGeneratedObjectsVisible(bool visible)
        {
            showGeneratedObjects = visible;
            ApplyGeneratedObjectVisibility();
        }

        void ApplyGeneratedObjectVisibility()
        {
            ApplyGeneratedObjectVisibility(sourceEnd);
            ApplyGeneratedObjectVisibility(mouthEnd);
        }

        void ApplyGeneratedObjectVisibility(WaterRiverEndConnection end)
        {
            ApplyGeneratedObjectVisibility(end.riverPort);
            ApplyGeneratedObjectVisibility(end.targetPort);
            ApplyGeneratedObjectVisibility(end.connection);
        }

        void ApplyGeneratedObjectVisibility(Component generated)
        {
            if (generated == null) return;
            HideFlags flags = showGeneratedObjects
                ? generated.gameObject.hideFlags & ~HideFlags.HideInHierarchy
                : generated.gameObject.hideFlags | HideFlags.HideInHierarchy;
            if (generated.gameObject.hideFlags != flags) generated.gameObject.hideFlags = flags;
        }

        // ---- validation (fail loud at the authoring boundary) ------------------------------

        void WarnOnInvalidSetup()
        {
            if (_spline == null || _spline.KnotCount < 2)
                Debug.LogWarning("WaterRiver: the spline needs at least two knots.", this);
            if (parentVolume == null)
                Debug.LogWarning("WaterRiver: no parent volume - standalone ribbon. Gameplay " +
                                 "queries work, but there are no animated waves and no underwater " +
                                 "fog until one is assigned.", this);
            else if (!parentVolume.fullscreenVolumeFog)
                Debug.LogWarning("WaterRiver: the parent volume has fullscreen volume fog " +
                                 "disabled, so a camera inside this river gets no underwater fog.",
                                 this);
            WarnOnHalfGeneratedEnd(sourceEnd, WaterRiverEndKind.Source);
            WarnOnHalfGeneratedEnd(mouthEnd, WaterRiverEndKind.Mouth);
            WarnOnAmbiguousEnd(sourceEnd, WaterRiverEndKind.Source);
            WarnOnAmbiguousEnd(mouthEnd, WaterRiverEndKind.Mouth);
            WarnOnUngeneratedEnd(sourceEnd, WaterRiverEndKind.Source);
            WarnOnUngeneratedEnd(mouthEnd, WaterRiverEndKind.Mouth);
        }

        // A target that was assigned by script (the inspector generates on assignment) but never
        // generated is the silent failure mode: no ports, no seam, no apron, no outflow.
        void WarnOnUngeneratedEnd(WaterRiverEndConnection end, WaterRiverEndKind endKind)
        {
            if (end.WantsConnection && !end.HasAmbiguousTarget && !end.IsGenerated &&
                !end.IsPartiallyGenerated)
                Debug.LogWarning($"WaterRiver: the {endKind} end has a target but no generated " +
                                 "connection. Regenerate it from the Water River inspector " +
                                 "(Connections tab).", this);
        }

        void WarnOnHalfGeneratedEnd(WaterRiverEndConnection end, WaterRiverEndKind endKind)
        {
            if (end.IsPartiallyGenerated)
                Debug.LogWarning($"WaterRiver: the {endKind} end's generated connection is " +
                                 "incomplete (some objects were deleted by hand). Regenerate or " +
                                 "remove it from the Water River inspector.", this);
        }

        void WarnOnAmbiguousEnd(WaterRiverEndConnection end, WaterRiverEndKind endKind)
        {
            if (end.HasAmbiguousTarget)
                Debug.LogWarning($"WaterRiver: the {endKind} end has both a body and an upstream " +
                                 "river assigned. Assign exactly one target.", this);
        }
    }
}
