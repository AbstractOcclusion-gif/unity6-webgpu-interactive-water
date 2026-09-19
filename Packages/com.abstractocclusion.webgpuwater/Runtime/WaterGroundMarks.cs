// WebGpuWater - GROUND MARKS: footprints and keel grooves pressed into sand, mud and snow.
//
// WHY A MODULE OF ITS OWN, and not the foam buffer's wet-mark channel: that buffer is water-height
// semantics, exists only on windowed bodies, and stops stepping when the sim sleeps - so a player
// walking through snow 200 m from the nearest pond would leave nothing. This is the same PATTERN
// (a following window, integer-texel scroll, a decaying per-column memory, a GPU stamp queue -
// see WaterSimWindow / WaterSimulation.Scroll / AddSphereInteraction) with its own tiny field.
//
// ONE PER SCENE. The field follows one point: `follow` when set, else the water eye camera. The
// terrain reads it through the globals published here (WaterGroundMarks.hlsl is the reader).
//
// EMITTERS never touch the texture: they call Stamp() in world metres, this converts to texels
// AFTER the window has tracked this frame, so a stamp can never be slid off by one window step
// (the ordering bug WaterSimulation.FlushInjections warns about). DefaultExecutionOrder puts this
// LateUpdate after every emitter's (WaterSphereInteractor, the footstep bridge).
//
// COST: one 256^2 rg32float ping-pong (512 KB), one dispatch per frame while marks may exist, one
// more on the frames the window moves a texel. Nothing while the field is known to be empty.
using System.Runtime.InteropServices;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Ground Marks")]
    [DefaultExecutionOrder(100)]
    public sealed class WaterGroundMarks : MonoBehaviour
    {
        // MUST equal THREAD_GROUP_SIZE in WaterGroundMarks.compute.
        const int ThreadGroupSize = 8;
        const string KernelStep = "Step";
        const string KernelScroll = "Scroll";
        const int MaxQueuedStamps = 64;
        // 1/60 s: the foam's reference step, so "dry time" reads the same way it does there.
        const float ReferenceStepsPerSecond = 60f;
        // A mark is considered gone when its depth has decayed to this fraction of the authored one.
        const float DecayedFraction = 0.01f;
        // Softness is floored to one texel: a zero-width edge aliases into a stair-step outline.
        const float MinSoftnessTexels = 1f;
        // Below this per-frame time the decay is skipped (paused editor / frame 0), never a NaN pow.
        const float MinDeltaTime = 1e-5f;

        static readonly int ID_Src = Shader.PropertyToID("Src");
        static readonly int ID_Dst = Shader.PropertyToID("Dst");
        static readonly int ID_Stamps = Shader.PropertyToID("Stamps");
        static readonly int ID_StampCount = Shader.PropertyToID("_StampCount");
        static readonly int ID_DepthSurvival = Shader.PropertyToID("_DepthSurvival");
        static readonly int ID_FreshSurvival = Shader.PropertyToID("_FreshSurvival");
        static readonly int ID_ScrollOffset = Shader.PropertyToID("_ScrollOffset");
        static readonly int ID_Size = Shader.PropertyToID("_Size");
        static readonly int ID_GroundMarkTex = Shader.PropertyToID("_GroundMarkTex");
        static readonly int ID_GroundMarkWindow = Shader.PropertyToID("_GroundMarkWindow");
        static readonly int ID_GroundMarkTexel = Shader.PropertyToID("_GroundMarkTexel");
        static readonly int ID_GroundMarkActive = Shader.PropertyToID("_GroundMarkActive");

        // Layout MUST match struct GroundStamp in the compute (8 floats, 32 bytes).
        [StructLayout(LayoutKind.Sequential)]
        struct GroundStamp
        {
            public Vector2 Center;
            public Vector2 Axis;
            public Vector2 HalfSize;
            public float Depth;
            public float Softness;
        }
        static readonly int GroundStampStride = Marshal.SizeOf<GroundStamp>();

        // What emitters queue: WORLD metres. Converted to texels in Step(), after Track() has moved
        // the window for this frame - emitters run earlier in LateUpdate than this component.
        struct PendingStamp
        {
            public Vector2 CenterXZ;
            public Vector2 Axis;
            public float HalfLength, HalfWidth, Depth, Softness;
        }

        /// <summary>The enabled instance, or null. One per scene; a second one logs and disables.</summary>
        public static WaterGroundMarks Active { get; private set; }

        [Tooltip("The ground-marks compute (Runtime/Shaders/WaterGroundMarks.compute).")]
        [SerializeField] ComputeShader compute;

        [Tooltip("Field resolution (texels per side). 256 over an 8 m window is ~3 cm/texel - footprint " +
                 "grade. Must be a multiple of 8.")]
        [SerializeField] int resolution = 256;

        [Tooltip("Half-size of the following window in metres. Marks outside it are forgotten.")]
        [Min(1f)] [SerializeField] float halfSizeMeters = 8f;

        [Tooltip("What the window follows. Empty = the water eye camera (WaterEye.Resolve).")]
        [SerializeField] Transform follow;

        [Tooltip("Seconds until a mark has faded to 1% of its depth. The terrain material scales the " +
                 "LOOK per substrate; this is how long the memory itself lasts.")]
        [Min(1f)] [SerializeField] float dryTimeSeconds = 120f;

        [Tooltip("Seconds until the FRESH channel (the just-made sheen) has faded to 1%.")]
        [Min(0.1f)] [SerializeField] float freshTimeSeconds = 6f;

        [Tooltip("Draw the raw field in the corner of the game view (R = depth, G = fresh).")]
        [SerializeField] bool showDebugOverlay;

        RenderTexture _a, _b;
        ComputeBuffer _stampBuffer;
        readonly PendingStamp[] _pending = new PendingStamp[MaxQueuedStamps];
        readonly GroundStamp[] _stampQueue = new GroundStamp[MaxQueuedStamps];
        int _stampCount;
        int _kStep, _kScroll, _groups;
        int _cellX, _cellZ;
        bool _centerInit;
        Vector2 _windowMin;      // world XZ of texel (0,0)'s corner
        float _texelMeters;
        // Wall-clock time after which the field is certainly empty (last stamp + full dry time), so
        // the per-frame dispatch can stop. Marks re-arm it.
        float _emptyAfterTime;
        bool _live;   // the field currently holds (or may hold) marks
        bool _ready;

        public float HalfSizeMeters => halfSizeMeters;
        public int Resolution => resolution;
        /// <summary>The live field (R = depth, G = fresh). Null when not running.</summary>
        public Texture Field => _ready ? _a : null;

        // ---- lifecycle ---------------------------------------------------------------------

        void OnEnable()
        {
            if (Active != null && Active != this)
            {
                Debug.LogWarning($"WaterGroundMarks: '{Active.name}' is already the scene's ground-marks " +
                                 $"field; disabling '{name}'.", this);
                enabled = false;
                return;
            }
            if (!TryInitialize()) { enabled = false; return; }
            Active = this;
        }

        void OnDisable()
        {
            if (Active == this)
            {
                Active = null;
                Shader.SetGlobalFloat(ID_GroundMarkActive, 0f);
            }
            Release();
        }

        bool TryInitialize()
        {
            if (compute == null)
            {
                Debug.LogError("WaterGroundMarks: assign the WaterGroundMarks compute shader.", this);
                return false;
            }
            if (!compute.HasKernel(KernelStep) || !compute.HasKernel(KernelScroll))
            {
                Debug.LogError($"WaterGroundMarks: compute '{compute.name}' lacks the Step/Scroll kernels - " +
                               "assign Runtime/Shaders/WaterGroundMarks.compute.", this);
                return false;
            }
            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogWarning("WaterGroundMarks: device lacks compute shaders; ground marks disabled.", this);
                return false;
            }
            resolution = Mathf.Max(ThreadGroupSize, (resolution / ThreadGroupSize) * ThreadGroupSize);
            _kStep = compute.FindKernel(KernelStep);
            _kScroll = compute.FindKernel(KernelScroll);
            _groups = resolution / ThreadGroupSize;
            _a = Create("WaterGroundMarks");
            _b = Create("WaterGroundMarks");
            Clear(_a); Clear(_b);
            _centerInit = false;
            _stampCount = 0;
            _emptyAfterTime = 0f;
            _live = false;
            _ready = true;
            Track();
            Publish();
            return true;
        }

        RenderTexture Create(string rtName)
        {
            // rg32float: a core WebGPU storage format (the foam buffer uses the same).
            var rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.RGFloat)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                name = rtName,
                hideFlags = HideFlags.HideAndDontSave
            };
            rt.Create();
            return rt;
        }

        static void Clear(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        void Release()
        {
            _ready = false;
            if (_a != null) { _a.Release(); Destroy(_a); _a = null; }
            if (_b != null) { _b.Release(); Destroy(_b); _b = null; }
            _stampBuffer?.Release();
            _stampBuffer = null;
        }

        // ---- per frame ---------------------------------------------------------------------

        void LateUpdate()
        {
            if (!_ready) return;
            // Move + scroll FIRST: queued stamps convert against this frame's window in Step().
            if (!Track()) { _stampCount = 0; return; }   // nothing to follow: no window, no marks
            bool mayHoldMarks = _stampCount > 0 || Time.time < _emptyAfterTime;
            if (mayHoldMarks) { Step(); _live = true; }
            else if (_live)
            {
                // The field just went quiet: wipe the 1% residue once, so a later window move that
                // skips the scroll (nothing to preserve) cannot leave ghost marks at wrong positions.
                Clear(_a);
                _live = false;
            }
            Publish();
        }

        // Window centre snapped to the texel lattice; scroll the field by the integer delta so marks
        // stay pinned in world space (Dst[p] = Src[p - offset], offset = -delta - see WaterSimulation.Scroll).
        // False when there is nothing to follow (no focus, no eye camera).
        bool Track()
        {
            _texelMeters = 2f * halfSizeMeters / resolution;
            Transform target = follow;
            if (target == null)
            {
                Camera eye = WaterEye.Resolve();
                if (eye == null) return false;
                target = eye.transform;
            }
            Vector3 pos = target.position;
            int cellX = Mathf.RoundToInt(pos.x / _texelMeters);
            int cellZ = Mathf.RoundToInt(pos.z / _texelMeters);

            if (!_centerInit)
            {
                _cellX = cellX; _cellZ = cellZ;
                _centerInit = true;
            }
            else
            {
                int dx = cellX - _cellX;
                int dz = cellZ - _cellZ;
                if (dx != 0 || dz != 0)
                {
                    // A jump larger than the field (teleport, respawn) has nothing to preserve.
                    if (Mathf.Abs(dx) >= resolution || Mathf.Abs(dz) >= resolution) { Clear(_a); }
                    else if (Time.time < _emptyAfterTime) Scroll(-dx, -dz);
                    _cellX = cellX; _cellZ = cellZ;
                }
            }
            _windowMin = new Vector2(_cellX * _texelMeters - halfSizeMeters,
                                     _cellZ * _texelMeters - halfSizeMeters);
            return true;
        }

        void Scroll(int offsetX, int offsetZ)
        {
            compute.SetInt(ID_Size, resolution);
            compute.SetInts(ID_ScrollOffset, offsetX, offsetZ);
            compute.SetTexture(_kScroll, ID_Src, _a);
            compute.SetTexture(_kScroll, ID_Dst, _b);
            compute.Dispatch(_kScroll, _groups, _groups, 1);
            (_a, _b) = (_b, _a);
        }

        void Step()
        {
            float dt = Time.deltaTime;
            float dtSteps = dt > MinDeltaTime ? dt * ReferenceStepsPerSecond : 0f;
            compute.SetInt(ID_Size, resolution);
            compute.SetFloat(ID_DepthSurvival, Mathf.Pow(SurvivalPerStep(dryTimeSeconds), dtSteps));
            compute.SetFloat(ID_FreshSurvival, Mathf.Pow(SurvivalPerStep(freshTimeSeconds), dtSteps));

            int count = ConvertPending();
            _stampBuffer ??= new ComputeBuffer(MaxQueuedStamps, GroundStampStride);
            if (count > 0) _stampBuffer.SetData(_stampQueue, 0, 0, count);
            compute.SetBuffer(_kStep, ID_Stamps, _stampBuffer);
            compute.SetInt(ID_StampCount, count);
            compute.SetTexture(_kStep, ID_Src, _a);
            compute.SetTexture(_kStep, ID_Dst, _b);
            compute.Dispatch(_kStep, _groups, _groups, 1);
            (_a, _b) = (_b, _a);
            _stampCount = 0;
        }

        // World metres -> texels against the window Track() just settled. Stamps that fall entirely
        // outside the window are dropped here (they would never touch a texel).
        int ConvertPending()
        {
            float invTexel = 1f / _texelMeters;
            int count = 0;
            for (int i = 0; i < _stampCount; i++)
            {
                PendingStamp p = _pending[i];
                Vector2 center = (p.CenterXZ - _windowMin) * invTexel;
                float reach = (Mathf.Max(p.HalfLength, p.HalfWidth) + p.Softness) * invTexel;
                if (center.x < -reach || center.y < -reach ||
                    center.x > resolution + reach || center.y > resolution + reach) continue;
                _stampQueue[count++] = new GroundStamp
                {
                    Center = center,
                    Axis = p.Axis,
                    HalfSize = new Vector2(p.HalfLength, p.HalfWidth) * invTexel,
                    Depth = p.Depth,
                    Softness = Mathf.Max(MinSoftnessTexels, p.Softness * invTexel),
                };
            }
            return count;
        }

        // Survival per reference step such that a mark reaches DecayedFraction after `seconds`.
        static float SurvivalPerStep(float seconds)
        {
            float steps = Mathf.Max(1f, seconds) * ReferenceStepsPerSecond;
            return Mathf.Exp(Mathf.Log(DecayedFraction) / steps);
        }

        void Publish()
        {
            float size = 2f * halfSizeMeters;
            Shader.SetGlobalTexture(ID_GroundMarkTex, _a);
            Shader.SetGlobalVector(ID_GroundMarkWindow, new Vector4(_windowMin.x, _windowMin.y, 1f / size, size));
            Shader.SetGlobalVector(ID_GroundMarkTexel, new Vector4(1f / resolution, 1f / resolution, resolution, resolution));
            Shader.SetGlobalFloat(ID_GroundMarkActive, Time.time < _emptyAfterTime ? 1f : 0f);
        }

        // ---- emitter API -------------------------------------------------------------------

        /// <summary>Press a rounded rectangle into the ground. World metres: <paramref name="worldPos"/>
        /// is the centre (Y ignored), <paramref name="headingXZ"/> the along-axis (a boot's toe
        /// direction, a keel's travel direction; zero = world +Z), <paramref name="halfLength"/> /
        /// <paramref name="halfWidth"/> the half-extents along / across it, <paramref name="depth"/>
        /// 0..1 the pressed depth (the terrain material decides what 1 means per substrate) and
        /// <paramref name="softness"/> the edge fade in metres. Queued; applied at this frame's step.
        /// Returns false when no field is running or the point lies outside the window.</summary>
        public static bool TryStamp(Vector3 worldPos, Vector2 headingXZ, float halfLength, float halfWidth,
                                    float depth, float softness)
        {
            WaterGroundMarks marks = Active;
            return marks != null && marks.Stamp(worldPos, headingXZ, halfLength, halfWidth, depth, softness);
        }

        public bool Stamp(Vector3 worldPos, Vector2 headingXZ, float halfLength, float halfWidth,
                          float depth, float softness)
        {
            if (!_ready || depth <= 0f) return false;

            // Coarse window test against LAST frame's window (the honest one available now; the
            // exact test runs again at conversion). Keeps far-away emitters from filling the queue.
            Vector2 centerXZ = new Vector2(worldPos.x, worldPos.z);
            float reach = Mathf.Max(halfLength, halfWidth) + softness + _texelMeters;
            float size = 2f * halfSizeMeters;
            if (_centerInit &&
                (centerXZ.x < _windowMin.x - reach || centerXZ.y < _windowMin.y - reach ||
                 centerXZ.x > _windowMin.x + size + reach || centerXZ.y > _windowMin.y + size + reach))
                return false;

            // Never drop a stamp: a frame that fills the queue steps what it has and keeps going.
            if (_stampCount >= MaxQueuedStamps) { if (Track()) Step(); else _stampCount = 0; }
            _pending[_stampCount++] = new PendingStamp
            {
                CenterXZ = centerXZ,
                // The window is world-axis aligned, so the heading needs no rotation.
                Axis = headingXZ.sqrMagnitude > 1e-8f ? headingXZ.normalized : Vector2.up,
                HalfLength = Mathf.Max(0f, halfLength),
                HalfWidth = Mathf.Max(0f, halfWidth),
                Depth = Mathf.Clamp01(depth),
                Softness = Mathf.Max(0f, softness),
            };
            _emptyAfterTime = Time.time + dryTimeSeconds;
            return true;
        }

        // ---- debug -------------------------------------------------------------------------

        void OnGUI()
        {
            if (!showDebugOverlay || !_ready) return;
            const float Size = 256f, Margin = 8f;
            var rect = new Rect(Margin, Screen.height - Size - Margin, Size, Size);
            GUI.DrawTexture(rect, _a, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(rect.x, rect.y - 18f, 400f, 18f),
                      $"ground marks  {resolution}^2  {2f * halfSizeMeters:0.#} m  " +
                      $"{(Time.time < _emptyAfterTime ? "live" : "empty")}");
        }

        void OnDrawGizmosSelected()
        {
            float half = halfSizeMeters;
            Vector3 c = Application.isPlaying && _centerInit
                ? new Vector3(_windowMin.x + half, transform.position.y, _windowMin.y + half)
                : (follow != null ? follow.position : transform.position);
            Gizmos.color = new Color(0.9f, 0.7f, 0.3f, 0.8f);
            Gizmos.DrawWireCube(c, new Vector3(2f * half, 0.05f, 2f * half));
        }

#if UNITY_EDITOR
        // Auto-wire the packaged compute when the component is added, so the inspector does not
        // start with an empty required slot.
        void Reset()
        {
            if (compute == null)
                compute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders/WaterGroundMarks.compute");
        }
#endif
    }
}
