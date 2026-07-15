// WebGpuWater - surfable hero wave (rise -> roll -> remerge, or an endless peeling wave).
// Drop this beside an open-water WaterVolume and place/rotate its transform where the wave should
// live: the crest line spans the transform's LEFT<->RIGHT axis and the wave travels along FORWARD.
//
// The component only drives STATE: each frame it advances the wave's lifecycle on the CPU and
// publishes closed-form shader uniforms through the body (WaterVolume.PublishHeroWave), which the
// uniform publisher writes into every per-body property block - so the ocean surface itself (full
// plane, window patch, clipmap) rises, leans and collapses with the wave. The only geometry this
// component owns is the dense strip mesh that renders the OVERTURNING LIP (a heightfield cannot
// overhang), mirroring the volume's own patch/clipmap runtime-renderer pattern.
//
// Composition, not inheritance: reads the volume through its internal seam, spawns plain
// MeshRenderers over the body's own surface materials, and cleans up after itself on disable.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/Water/Water Hero Wave")]
    // Must publish BEFORE WaterVolume (-50) writes its per-body blocks each frame, so the base
    // offset on the ocean and the lip sheet always carry the SAME frame's wave state.
    [DefaultExecutionOrder(-60)]
    public sealed class WaterHeroWave : MonoBehaviour
    {
        public enum HeroWaveMode
        {
            SingleWave,   // rise -> travel + peel -> decay/remerge (optionally looping)
            InfinitePeel, // rise once, then hold an endless breaking section (wave-pool style)
        }

        // --- strip mesh / rendering constants (mesh builder + renderer recipe live in WaterLipSheetRig) ---
        const string StripObjectName = "WaterHeroWaveSheet";
        const string StripUnderObjectName = "WaterHeroWaveSheetUnder";
        const string StripMeshName = "WaterHeroWaveStrip";
        // View-space metres the sheet is pulled toward the camera, so where its foot coincides with
        // the base surface it wins the depth test. Reuses the shader's _PatchDepthBias mechanism but
        // is its own value - it does NOT need to match WaterVolume.PatchDepthBiasMeters.
        const float SheetDepthBiasMeters = 0.02f;
        // Strip sizing from the shape fields: length margin past the crest half-length, and the
        // across span as profile lengths + curl reach (pivot + lip landing) in amplitudes.
        const float StripLengthMargin = 1.1f;
        const float StripProfileSpan = 3f;
        const float StripCurlSpan = 2.5f;
        const float MinDuration = 1e-3f;
        const float MinSpeed = 1e-3f;

        [Tooltip("The open-water body this wave rides. Defaults to a WaterVolume on this object or its parents.")]
        [SerializeField] WaterVolume volume;

        [Tooltip("Single Wave = rise, travel + peel, then decay back into the ocean (looping if enabled). " +
                 "Infinite Peel = rise once and hold an endless breaking section in place (wave-pool style).")]
        [SerializeField] HeroWaveMode mode = HeroWaveMode.SingleWave;

        [Header("Shape")]
        [Tooltip("Crest height in metres at full envelope.")]
        [Min(0f)] [SerializeField] float amplitude = 3f;
        [Tooltip("Profile length (m) of the steep FRONT face (travel side). Shorter = steeper.")]
        [Min(0.1f)] [SerializeField] float faceLength = 5f;
        [Tooltip("Profile length (m) of the long BACK of the wave.")]
        [Min(0.1f)] [SerializeField] float backLength = 12f;
        [Tooltip("Half-length (m) of the crest line along the transform's left<->right axis.")]
        [Min(1f)] [SerializeField] float crestHalfLength = 30f;
        [Tooltip("Fraction of the crest half-length where the shoulder falloff begins (height dies " +
                 "smoothly from here to the crest ends, merging the wave into the ocean).")]
        [Range(0f, 0.95f)] [SerializeField] float shoulderStartFraction = 0.5f;

        [Header("Motion")]
        [Tooltip("Travel speed (m/s) along the transform's forward axis. Single Wave only; an " +
                 "Infinite Peel holds its position.")]
        [Min(0f)] [SerializeField] float travelSpeed = 6f;
        [Tooltip("Slow height undulation travelling along the crest, as a fraction of the amplitude. " +
                 "Keeps a held wave feeling alive.")]
        [Range(0f, 0.5f)] [SerializeField] float undulationAmplitude = 0.1f;
        [Tooltip("Wavelength (m) of the crest undulation.")]
        [Min(1f)] [SerializeField] float undulationWavelength = 25f;
        [Tooltip("Period (s) of one undulation cycle.")]
        [Min(0.1f)] [SerializeField] float undulationPeriod = 9f;

        [Header("Curl (the overturning lip)")]
        [Tooltip("Maximum roll of the lip in degrees. ~90 pitches the crest forward; ~180-220 throws a " +
                 "full plunging barrel.")]
        [Range(0f, 270f)] [SerializeField] float curlMaxRollDegrees = 200f;
        [Tooltip("Profile fraction where the curl begins: only the part of the wave above this fraction " +
                 "of the crest rolls. Lower = thicker lip.")]
        [Range(0.05f, 0.95f)] [SerializeField] float curlStartFraction = 0.5f;
        [Tooltip("Curl pivot distance ahead of the crest, as a fraction of the face length. Further " +
                 "ahead = the lip throws further before plunging (a wider tube).")]
        [Range(0f, 1.5f)] [SerializeField] float pivotAheadFraction = 0.55f;
        [Tooltip("Curl pivot height as a fraction of the amplitude. Higher = a rounder, more open tube.")]
        [Range(0f, 1f)] [SerializeField] float pivotHeightFraction = 0.7f;
        [Tooltip("Forward shear (m) of the crest top before it rolls (Fournier-Reeves lean) - steepens " +
                 "the face as the wave stands up.")]
        [Min(0f)] [SerializeField] float leanDistance = 1.5f;

        [Header("Peel (how the break travels along the crest)")]
        [Tooltip("Speed (m/s) the break point travels along the crest.")]
        [Min(0.1f)] [SerializeField] float peelSpeed = 8f;
        [Tooltip("Length (m) of the transition from unbroken face to fully-rolled lip around the peel " +
                 "point. Longer = a lazier, more open pocket.")]
        [Min(0.5f)] [SerializeField] float peelBlendLength = 10f;
        [Tooltip("Break from the transform's RIGHT end of the crest instead of the left.")]
        [SerializeField] bool peelFromPositiveEnd = false;

        [Header("Lifecycle")]
        [Tooltip("Seconds for the wave to rise out of the ocean before it starts breaking.")]
        [Min(0.1f)] [SerializeField] float riseDuration = 6f;
        [Tooltip("Seconds for the spent wave to sink back into the ocean after the peel completes. " +
                 "Single Wave only.")]
        [Min(0.1f)] [SerializeField] float decayDuration = 6f;
        [Tooltip("Single Wave: respawn at the anchor and run again after each life completes.")]
        [SerializeField] bool loopSingleWave = true;
        [Tooltip("Infinite Peel: where the held break point sits along the crest (-1 = left end, " +
                 "0 = centre, +1 = right end).")]
        [Range(-1f, 1f)] [SerializeField] float infinitePeelHoldFraction = 0f;
        [Tooltip("Infinite Peel: how far (m) the held break point wanders back and forth, so the " +
                 "barrel section breathes instead of freezing.")]
        [Min(0f)] [SerializeField] float peelWanderAmplitude = 8f;
        [Tooltip("Infinite Peel: period (s) of one wander cycle.")]
        [Min(0.5f)] [SerializeField] float peelWanderPeriod = 14f;

        [Header("Whitewater")]
        [Tooltip("Foam the wave writes into the ripple-sim foam buffer: the rolling lip plus the " +
                 "whitewash mound on the broken section. It advects and decays with the sim, so the " +
                 "wave leaves an organic trail, and surface foam, foam particles and density foam " +
                 "all pick it up. 0 = off. (Only lives inside the sim window.)")]
        [Range(0f, 3f)] [SerializeField] float whitewaterStrength = 1f;

        [Header("Lip sheet mesh")]
        [Tooltip("Strip mesh segments along the crest. Higher = smoother lip silhouette, more vertices.")]
        [Range(16, 512)] [SerializeField] int stripAlongSegments = 160;
        [Tooltip("Strip mesh segments across the wave (through the curl). Higher = rounder tube.")]
        [Range(16, 512)] [SerializeField] int stripAcrossSegments = 128;

        float _elapsed;
        float _traveled;
        Mesh _stripMesh;
        MeshRenderer _stripRenderer;
        MeshRenderer _stripUnderRenderer;
        MaterialPropertyBlock _stripBlock;
        MaterialPropertyBlock _stripUnderBlock;

        static readonly int ID_IsClipmap = Shader.PropertyToID("_IsClipmap");
        static readonly int ID_IsHeroWave = Shader.PropertyToID("_IsHeroWave");
        static readonly int ID_PatchDepthBias = Shader.PropertyToID("_PatchDepthBias");

        void OnEnable()
        {
            if (volume == null) volume = GetComponentInParent<WaterVolume>();
            if (volume == null)
            {
                Debug.LogError("WaterHeroWave: no WaterVolume assigned or found in parents - disabling.", this);
                enabled = false;
                return;
            }
            if (!volume.openWater)
            {
                Debug.LogError("WaterHeroWave: the assigned body is not open water (Open Water off) - " +
                               "a hero wave needs the large-body wave path. Disabling.", this);
                enabled = false;
                return;
            }
            _elapsed = 0f;
            _traveled = 0f;
        }

        void OnDisable()
        {
            if (volume != null) volume.ClearHeroWave();
            DestroyStrip();
        }

        // Update publishes the wave STATE (execution order -60, before WaterVolume's -50 writes its
        // per-body blocks); LateUpdate then places the strip and writes ITS block, after the volume
        // has refreshed its own per-frame state - so both sides of the seam carry this frame's values.
        void Update()
        {
            _elapsed += Time.deltaTime;
            float envelope;
            float peelPosition;
            AdvanceLifecycle(out envelope, out peelPosition);
            if (!enabled) return; // a finished non-looping wave disabled itself (state already cleared)

            volume.PublishHeroWave(BuildState(envelope, peelPosition));
        }

        void LateUpdate()
        {
            EnsureStrip();
            UpdateStrip();
        }

        // --- lifecycle -------------------------------------------------------------------------

        // Envelope (0..1 height/roll gate) + peel position (m along the crest, in the direction the
        // break travels) for the current time. Also advances travel and handles looping.
        void AdvanceLifecycle(out float envelope, out float peelPosition)
        {
            // The peel starts one blend length before the crest and finishes one past it.
            float peelStart = -(crestHalfLength + peelBlendLength);
            float peelSpan = 2f * (crestHalfLength + peelBlendLength);

            if (mode == HeroWaveMode.InfinitePeel)
            {
                envelope = SmoothStep01(_elapsed / Mathf.Max(riseDuration, MinDuration));
                float wander = peelWanderAmplitude
                    * Mathf.Sin(2f * Mathf.PI * _elapsed / Mathf.Max(peelWanderPeriod, MinDuration));
                peelPosition = infinitePeelHoldFraction * crestHalfLength + wander;
                return;
            }

            float peelDuration = peelSpan / Mathf.Max(peelSpeed, MinSpeed);
            float lifeDuration = riseDuration + peelDuration + decayDuration;
            if (_elapsed >= lifeDuration)
            {
                if (!loopSingleWave)
                {
                    envelope = 0f;
                    peelPosition = peelStart;
                    enabled = false; // OnDisable clears the body state + strip
                    return;
                }
                _elapsed -= lifeDuration;
                _traveled = 0f;
            }

            _traveled += travelSpeed * Time.deltaTime;

            if (_elapsed < riseDuration)
            {
                envelope = SmoothStep01(_elapsed / Mathf.Max(riseDuration, MinDuration));
                peelPosition = peelStart;
                return;
            }
            float sinceRise = _elapsed - riseDuration;
            if (sinceRise < peelDuration)
            {
                envelope = 1f;
                peelPosition = peelStart + peelSpeed * sinceRise;
                return;
            }
            envelope = 1f - SmoothStep01((sinceRise - peelDuration) / Mathf.Max(decayDuration, MinDuration));
            peelPosition = peelStart + peelSpan;
        }

        HeroWaveShaderState BuildState(float envelope, float peelPosition)
        {
            Vector2 along = AlongDirXZ();
            Vector2 travel = TravelFromAlong(along);
            Vector2 anchor = new Vector2(transform.position.x, transform.position.z);
            Vector2 center = anchor + travel * _traveled;
            float undulationPhase = 2f * Mathf.PI * _elapsed / Mathf.Max(undulationPeriod, MinDuration);

            return new HeroWaveShaderState
            {
                Active = true,
                Frame = new Vector4(center.x, center.y, along.x, along.y),
                Shape = new Vector4(amplitude * envelope, faceLength, backLength, crestHalfLength),
                Curl = new Vector4(peelPosition, peelBlendLength,
                                   curlMaxRollDegrees * Mathf.Deg2Rad * envelope, curlStartFraction),
                Curl2 = new Vector4(pivotAheadFraction, pivotHeightFraction, leanDistance, shoulderStartFraction),
                Motion = new Vector4(undulationAmplitude, undulationWavelength, undulationPhase,
                                     peelFromPositiveEnd ? -1f : 1f),
                FoamStrength = whitewaterStrength,
            };
        }

        // Crest-line direction (world xz) from the transform's yaw only, so a tilted authoring
        // transform can never shear the wave frame. KEEP IN SYNC with WaterHeroWave.hlsl
        // (HeroLocalCoords): along = right, travel = (-along.y, along.x) = forward.
        Vector2 AlongDirXZ()
        {
            float yawRad = transform.eulerAngles.y * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(yawRad), -Mathf.Sin(yawRad));
        }

        static Vector2 TravelFromAlong(Vector2 along) => new Vector2(-along.y, along.x);

        static float SmoothStep01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        // --- lip-sheet strip renderers ---------------------------------------------------------

        // Lazily spawn the strips once the body's per-instance surface materials exist (the volume
        // creates them during its own init, which may land after our first frames). Above and under
        // are attempted independently: their materials can appear on different frames.
        void EnsureStrip()
        {
            if (_stripRenderer == null
                && volume.surfaceAbove != null && volume.surfaceAbove.sharedMaterial != null)
            {
                if (_stripMesh == null)
                    _stripMesh = WaterLipSheetRig.BuildStripGrid(StripMeshName,
                                                                 stripAlongSegments, stripAcrossSegments);
                _stripRenderer = WaterLipSheetRig.CreateStripRenderer(volume, _stripMesh,
                    StripObjectName, volume.surfaceAbove.sharedMaterial);
            }
            if (_stripUnderRenderer == null && _stripMesh != null
                && volume.surfaceUnder != null && volume.surfaceUnder.sharedMaterial != null)
            {
                _stripUnderRenderer = WaterLipSheetRig.CreateStripRenderer(volume, _stripMesh,
                    StripUnderObjectName, volume.surfaceUnder.sharedMaterial);
            }
        }

        void UpdateStrip()
        {
            if (_stripRenderer == null) return;

            HeroWaveShaderState state = volume.HeroWaveState;
            Vector2 center = new Vector2(state.Frame.x, state.Frame.y);
            float halfLength = crestHalfLength * StripLengthMargin;
            float halfWidth = Mathf.Max(backLength * StripProfileSpan,
                                        faceLength * StripProfileSpan + amplitude * StripCurlSpan);

            PositionStrip(_stripRenderer, ref _stripBlock, center, halfLength, halfWidth);
            PositionStrip(_stripUnderRenderer, ref _stripUnderBlock, center, halfLength, halfWidth);
        }

        // Park one strip renderer over the wave and feed it the body uniforms (which include this
        // frame's hero state - we published before the write) plus the strip's own flags. The strip
        // rides the clipmap vertex mapping (_IsClipmap: verts in world metres via ObjectToWorld).
        void PositionStrip(Renderer strip, ref MaterialPropertyBlock block,
                           Vector2 center, float halfLength, float halfWidth)
        {
            if (strip == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            volume.WriteBodyProps(block);
            block.SetFloat(ID_IsClipmap, 1f);
            block.SetFloat(ID_IsHeroWave, 1f);
            block.SetFloat(ID_PatchDepthBias, SheetDepthBiasMeters);
            strip.SetPropertyBlock(block);

            Transform stripTransform = strip.transform;
            stripTransform.SetPositionAndRotation(
                new Vector3(center.x, volume.VolumeCenter.y, center.y),
                Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
            stripTransform.localScale = new Vector3(halfLength, 1f, halfWidth);
        }

        void DestroyStrip()
        {
            WaterLipSheetRig.DestroyRuntimeObject(_stripRenderer != null ? _stripRenderer.gameObject : null);
            WaterLipSheetRig.DestroyRuntimeObject(_stripUnderRenderer != null ? _stripUnderRenderer.gameObject : null);
            WaterLipSheetRig.DestroyRuntimeObject(_stripMesh);
            _stripRenderer = null;
            _stripUnderRenderer = null;
            _stripMesh = null;
            _stripBlock = null;
            _stripUnderBlock = null;
        }
    }
}
