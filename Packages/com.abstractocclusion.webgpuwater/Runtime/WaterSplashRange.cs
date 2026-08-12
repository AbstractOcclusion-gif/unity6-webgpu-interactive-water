using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>The splash range: a catapult that lobs pooled throwables onto the water so every
    /// splash system fires at once - breach splash (droplets + crown + bubbles), ripple rings,
    /// crest flecks, buoyancy and wakes. A Unity port of the three.js splash-range demo scene,
    /// primitives and all. The trojan rabbit is load-bearing and must never be removed
    /// (sculpted by Claude, 2026 - "Fetchez la vache !").
    ///
    /// Throwables are BUILT FROM PRIMITIVES in code, so the demo needs zero model assets; swap
    /// any factory's mesh children for a real model later and the physics rig stays. Live
    /// objects are CAPPED: the oldest is recycled into its kind's pool rather than destroyed
    /// (no allocation churn, no unbounded physics cost).</summary>
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Splash Range")]
    public sealed class WaterSplashRange : MonoBehaviour
    {
        public enum ThrowableKind { Cannonball, Cow, Chicken, TrojanRabbit }

        // Ballistics: the launch velocity is solved from a chosen ARC APEX HEIGHT, not a fixed
        // flight time. Fixed time made near targets arrive fast and flat ("hard to control,
        // object comes very quick"): the same 1.35 s over a short distance is a bullet, over a
        // long one a lob. A fixed apex gives every throw the same readable high arc, and the
        // horizontal speed falls out naturally slower for near targets.
        const float DefaultLaunchHeight = 1.5f;
        // The apex must clear the higher endpoint by at least this much or the solve degenerates.
        const float MinApexClearance = 0.75f;
        // Launch point sits this fraction of the body extent OUTSIDE the water edge.
        const float LaunchEdgeFraction = 0.52f;
        const float TargetSpreadFraction = 0.55f;
        // Spin makes a lobbed cow read as a lobbed cow.
        const float SpinMaxRadiansPerSecond = 6f;
        // Retirement: below the kill floor (sank) or after this long (drifting flotsam).
        const float KillDepthBelowBody = 5f;
        const float MaxAgeSeconds = 30f;
        // HUD layout.
        const float HudX = 10f, HudY = 10f, HudWidth = 190f, HudRowHeight = 28f;
        // Click-to-throw accepts plane hits up to this factor OUTSIDE the footprint and clamps
        // them in (ThrowByKey clamps to the target margin anyway). The old exact-footprint gate
        // silently swallowed clicks that landed a whisker past the edge - the water's displaced
        // rim doesn't sit exactly on the flat pick plane - which read as "sometimes it throws,
        // sometimes not". Clicks further out than this are camera clicks and stay ignored.
        const float ClickCatchFraction = 1.35f;
        // Soft edge containment (see FixedUpdate): band width inside the border, inward push,
        // outward-velocity damping, and the height above the surface past which a throwable is
        // "in flight" and must not be steered (the launch arc crosses the border band).
        const float EdgeContainBand = 1.25f;
        const float EdgeContainAccel = 10f;
        const float EdgeContainDamping = 4f;
        const float EdgeContainMaxHeight = 1f;
        // Diagnostics: a single-FixedUpdate velocity change above this is not any legitimate
        // water force (buoyancy is capped ~2 g, drag+settle stay proportional to speed - all
        // well under 3 m/s per 0.02 s step) and gets logged with its circumstances.
        const float VelocitySpikeThreshold = 3f;
        // The pivot for wave-size compression: a throwable of this collider half-diagonal makes
        // its authored waves untouched; bigger ones get damped, smaller ones boosted. 0.65 m
        // sits between the chicken (~0.42) and the trojan rabbit (~1.0).
        const float ReferenceSizeMeters = 0.65f;

        [Tooltip("Water body the range throws onto. Auto-resolved to the primary body when empty.")]
        [SerializeField] internal WaterVolume waterBody;

        [Tooltip("Splash emitter used by every throwable's breach splash. Auto-resolved from the " +
                 "water body's children when empty.")]
        [SerializeField] internal WaterSplashEmitter splashEmitter;

        [Tooltip("Maximum live throwables; the oldest is recycled when a new throw exceeds it.")]
        [Range(4, 64)] [SerializeField] internal int maxLiveThrowables = 20;

        [Tooltip("Auto-catapult: seconds between volleys (randomised a little).")]
        [Range(0.5f, 6f)] [SerializeField] internal float autoInterval = 2f;

        [Header("Splash Tuning")]
        [Tooltip("Master heaviness of every breach splash (droplets, crown, bubbles). The " +
                 "default sits under 1 on purpose - full strength read as too heavy.")]
        [Range(0f, 2f)] [SerializeField] internal float splashHeaviness = 0.55f;

        [Tooltip("Scale on the splash's world radius.")]
        [Range(0.2f, 2f)] [SerializeField] internal float splashRadiusScale = 1f;

        [Tooltip("Scale on the breach ripple ring (strength AND radius together).")]
        [Range(0f, 2f)] [SerializeField] internal float rippleScale = 1f;

        [Tooltip("Scale on every throwable's wake dipole while it floats.")]
        [Range(0f, 2f)] [SerializeField] internal float wakeScale = 1f;

        [Tooltip("Evens out wave size across throwable sizes: at 0, big objects make " +
                 "proportionally huge waves and small ones tiny dimples (natural); at 1 every " +
                 "object waves like the reference size. In between it DAMPS the big and BOOSTS " +
                 "the small around the middle - one slider for both directions.")]
        [Range(0f, 1f)] [SerializeField] internal float waveSizeCompression = 0.6f;

        [Header("Ballistics")]
        [Tooltip("How high (metres above the launch point) every arc peaks. Higher = slower, " +
                 "lazier, more readable lobs; the flight time is derived per throw.")]
        [Range(1.5f, 10f)] [SerializeField] internal float arcApexHeight = 4f;

        [Tooltip("Fixed launch point: every throw originates EXACTLY here (move it like any " +
                 "object - it is the catapult). Empty = the computed edge point with a little " +
                 "sideways spread, the old behaviour.")]
        [SerializeField] internal Transform launchPoint;

        [Tooltip("Launch height above the surface.")]
        [Range(0.5f, 6f)] [SerializeField] internal float launchHeight = DefaultLaunchHeight;

        [Tooltip("Targets are clamped inside this fraction of the water footprint, so a volley " +
                 "can never be solved onto (or past) the edge - the 'object missed the water' fix.")]
        [Range(0.3f, 0.95f)] [SerializeField] internal float targetMargin = 0.8f;

        [Tooltip("Off (default): thrown objects pass through each other. On, rapid volleys can " +
                 "collide mid-air at the shared launch point and deflect wildly - that was the " +
                 "'weird bounce before entering the water'.")]
        [SerializeField] internal bool throwablesCollide = false;

        [Tooltip("Click the water to lob a random throwable at the clicked point. Off = the gate " +
                 "for when mouse clicks should belong to the camera and ripple input alone " +
                 "(also toggleable on the HUD). A throw fires on RELEASE of a press that did not " +
                 "travel - a drag is the camera's, exactly the ripple router's tap-vs-drag rule.")]
        [SerializeField] internal bool clickToThrow = true;

        [Header("Diagnostics")]
        [Tooltip("Console-log every throw, any single-step velocity spike, and every throwable-" +
                 "vs-throwable collision, tagged [SplashRange]. The measuring instrument for " +
                 "bounce reports: the log NAMES the culprit instead of anyone guessing. A spike " +
                 "with no collision line right before it is a water force; with one, a contact. " +
                 "OFF by default: every entry is retained by the console with a stack trace, and " +
                 "the CONTACT line fires per contact while the SPIKE line fires per throwable per " +
                 "physics step, so a long session pays a monotonically growing framerate for a " +
                 "diagnostic nobody is reading. Turn it on for the throw you are investigating.")]
        [SerializeField] internal bool logDiagnostics = false;

        [Header("Buoyancy Tuning")]
        [Tooltip("Master scale on every kind's buoyancy.")]
        [Range(0.3f, 2f)] [SerializeField] internal float buoyancyScale = 1f;

        [Tooltip("Water drag on floating throwables (WaterBuoyancy.waterLinearDamping). Small " +
                 "fast objects need far more than the component's boat-scale default to settle.")]
        [Range(0f, 12f)] [SerializeField] internal float floatDamping = 5f;

        [Tooltip("Extra vertical settle damping (WaterBuoyancy.verticalSettleDamping) - kills " +
                 "the residual bob after the entry.")]
        [Range(0f, 8f)] [SerializeField] internal float settleDamping = 4f;

        /// <summary>A user-supplied throwable: any prefab (an LP model) plus its physics
        /// character. The prefab is instantiated as the VISUAL CHILD of a pooled physics root,
        /// exactly like the primitive factories - swap-in parity by construction. A collider on
        /// the prefab's root is used as-is; without one, a box is fitted to the renderers.</summary>
        [System.Serializable]
        internal sealed class CustomThrowable
        {
            [Tooltip("HUD button label. Empty = 'Custom N'.")]
            public string label;
            [Tooltip("The model to throw. Colliders optional (a box is fitted when absent).")]
            public GameObject prefab;
            [Min(0.05f)] public float mass = 3f;
            [Tooltip("Below 1 sinks; ~2 wallows; 3+ bobs high.")]
            [Range(0.1f, 6f)] public float buoyancy = 2f;
            [Tooltip("Wake dipole gain while floating.")]
            [Range(0f, 4f)] public float wakeStrength = 1.5f;
        }

        [Header("Custom Throwables")]
        [Tooltip("Your own models join the catapult: one HUD button each, and they enter the " +
                 "auto/click rotation alongside the built-ins.")]
        [SerializeField] internal System.Collections.Generic.List<CustomThrowable> customThrowables
            = new System.Collections.Generic.List<CustomThrowable>();

        // Pool key: built-in kinds map to their enum value, customs to BuiltinKindCount + index.
        const int BuiltinKindCount = 4;

        sealed class Throwable
        {
            public int PoolKey;
            public GameObject Root;
            public Rigidbody Body;
            public Collider[] Colliders;     // for the per-activation collision re-ignore
            public Vector3 PrevVelocity;     // last FixedUpdate's velocity, for spike diagnostics
            public float SizeFactor;         // collider half-diagonal / reference, for wave compression
            public WaterBreachSplash Breach;
            public WaterSphereInteractor Wake;
            public WaterBuoyancy Buoyancy;
            public float BuoyancyBase;       // the kind's authored buoyancy, before buoyancyScale
            public float WakeBaseStrength;   // the kind's authored wake, before wakeScale
            public float BreachBaseRadius;   // the component default, before splashRadiusScale
            public float BreachBaseRippleStrength;
            public float BreachBaseRippleRadius;
            public float Age;
        }

        readonly List<Throwable> _live = new List<Throwable>();
        readonly Dictionary<int, Stack<Throwable>> _poolsByKey =
            new Dictionary<int, Stack<Throwable>>();
        Vector3 _lastTarget;
        bool _auto;
        float _autoTimer;
        // Click-to-throw press state: where button 0 went down and whether that press is a throw
        // candidate (started off the HUD, no GUI control hot). Resolved on RELEASE.
        Vector2 _pressPixel;
        bool _pressValid;
        // The HUD's real bottom edge, measured in OnGUI. The old exclusion rect GUESSED the
        // height from a row-count formula, so it drifted from the drawn HUD and either ate
        // water clicks below the strip or let clicks through onto buttons.
        float _hudBottom;
        Camera _cachedCamera;

        void OnEnable()
        {
            if (waterBody == null) waterBody = WaterVolume.Primary;
            if (splashEmitter == null && waterBody != null && waterBody.transform.parent != null)
                splashEmitter = waterBody.transform.parent.GetComponentInChildren<WaterSplashEmitter>();
            // Seed with the formula; OnGUI replaces it with the measured value on first paint.
            _hudBottom = HudY + HudRowHeight * (10f + customThrowables.Count);
        }

        void Update()
        {
            if (waterBody == null) return;

            if (_auto)
            {
                _autoTimer -= Time.deltaTime;
                if (_autoTimer <= 0f)
                {
                    _autoTimer = autoInterval * Random.Range(0.7f, 1.3f);
                    ThrowByKey(RandomKey(), RandomTarget());
                }
            }

            if (clickToThrow) HandleClickToThrow();

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Throwable t = _live[i];
                t.Age += Time.deltaTime;
                if (t.Age > MaxAgeSeconds ||
                    t.Root.transform.position.y < SurfaceY() - KillDepthBelowBody)
                    Retire(i);
            }
        }

        // Click-to-throw, sharing the water package's own pointer contract instead of fighting
        // it: WaterInputRouter already claims button 0 (tap = ripple, drag = ripple trail or
        // orbit), so the old throw-on-mouse-DOWN fired at the START of every camera drag over
        // water and also raced the GUI - "the system has trouble discerning the clicks". Now a
        // throw is a TAP: press begins off the HUD with no GUI control hot, and release lands
        // within the router's TapMaxTravelPixels. Camera drags, HUD presses and slider drags
        // fall out by construction. The tap still ripples via the router - a click that both
        // dimples the water and calls in a cow is the correct amount of Monty Python.
        void HandleClickToThrow()
        {
            if (Input.GetMouseButtonDown(0))
            {
                _pressPixel = Input.mousePosition;
                Vector2 gui = new Vector2(_pressPixel.x, Screen.height - _pressPixel.y);
                _pressValid = GUIUtility.hotControl == 0 &&
                              !new Rect(HudX, HudY, HudWidth, _hudBottom - HudY).Contains(gui);
            }
            if (!Input.GetMouseButtonUp(0) || !_pressValid) return;
            _pressValid = false;
            if (((Vector2)Input.mousePosition - _pressPixel).magnitude >
                WaterInputRouter.TapMaxTravelPixels) return; // travelled = a camera drag, not a throw
            Camera cam = ResolveCamera();
            if (cam == null) return;
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            var surface = new Plane(Vector3.up, new Vector3(0f, SurfaceY(), 0f));
            if (!surface.Raycast(ray, out float enter)) return;
            Vector3 hit = ray.GetPoint(enter);
            // Near misses count: the visible water rim is displaced off the flat pick plane, so
            // an exact-footprint gate swallowed edge clicks at random. Anything inside the catch
            // band throws (ThrowByKey clamps the target to the margin); far clicks stay ignored.
            if (!InsideFootprint(hit, ClickCatchFraction)) return;
            ThrowByKey(RandomKey(), hit);
        }

        // Camera.main requires the MainCamera tag; a scene whose camera lost it silently killed
        // every click ("sometimes throw, sometimes not" at its meanest). Fall back to any camera.
        Camera ResolveCamera()
        {
            if (_cachedCamera != null && _cachedCamera.isActiveAndEnabled) return _cachedCamera;
            _cachedCamera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            return _cachedCamera;
        }

        // Soft edge containment. Buoyancy support ends EXACTLY at the footprint line (outside
        // probes sample invalid on a bounded body), so a floater straddling the border alternates
        // between full lift and free fall every step its probes flicker across the line - the
        // "bounces at the border". Small dense throwables (cannonball, rabbit) flicker hardest:
        // few probes close together and a deep draft make each probe's crossing a large force
        // step. Rather than feathering the water package's edge gating (blast radius: every
        // buoyant object in every scene), the range keeps its own throwables clear of the line:
        // inside a band along the border they get a gentle inward push plus damping of any
        // outward velocity. In-flight objects are exempt (the launch arc crosses the band).
        void FixedUpdate()
        {
            if (waterBody == null) return;
            Vector3 center = waterBody.transform.position;
            Vector3 extent = waterBody.volumeExtent;
            float maxY = SurfaceY() + EdgeContainMaxHeight;
            for (int i = 0; i < _live.Count; i++)
            {
                Throwable t = _live[i];
                Rigidbody body = t.Body;
                if (logDiagnostics)
                {
                    Vector3 v = body.linearVelocity;
                    float dv = (v - t.PrevVelocity).magnitude;
                    if (dv > VelocitySpikeThreshold)
                    {
                        Vector3 local = body.position - center;
                        float edgeDist = Mathf.Min(extent.x - Mathf.Abs(local.x),
                                                   extent.z - Mathf.Abs(local.z));
                        Debug.LogWarning(
                            $"[SplashRange] SPIKE {t.Root.name}: dv {dv:0.0} m/s in one step " +
                            $"(now {v.magnitude:0.0} m/s) depth {SurfaceY() - body.position.y:0.00} " +
                            $"edgeDist {edgeDist:0.00} live {_live.Count}");
                    }
                    t.PrevVelocity = v;
                }
                if (body.position.y > maxY) continue; // still on the arc
                Vector3 localPos = body.position - center;
                AxisContain(body, Vector3.right, localPos.x, extent.x);
                AxisContain(body, Vector3.forward, localPos.z, extent.z);
            }
        }

        static void AxisContain(Rigidbody body, Vector3 axis, float coord, float extent)
        {
            float band = Mathf.Min(EdgeContainBand, extent * 0.5f);
            float edge = extent - band;
            float side = Mathf.Sign(coord);
            float penetration = (Mathf.Abs(coord) - edge) / band;
            if (penetration <= 0f) return;
            penetration = Mathf.Min(penetration, 1.5f); // past the line it keeps pushing, capped
            Vector3 push = axis * (-side * penetration * EdgeContainAccel);
            float outwardSpeed = Vector3.Dot(body.linearVelocity, axis) * side;
            if (outwardSpeed > 0f)
                push -= axis * (side * outwardSpeed * EdgeContainDamping * Mathf.Min(penetration, 1f));
            body.AddForce(push, ForceMode.Acceleration);
        }

        // ---------------------------------------------------------------- throwing
        public void Throw(ThrowableKind kind, Vector3 target) => ThrowByKey((int)kind, target);

        /// <summary>Throw entry N of the Custom Throwables list.</summary>
        public void ThrowCustom(int customIndex, Vector3 target)
        {
            if (customIndex < 0 || customIndex >= customThrowables.Count) return;
            if (customThrowables[customIndex].prefab == null) return;
            ThrowByKey(BuiltinKindCount + customIndex, target);
        }

        void ThrowByKey(int poolKey, Vector3 target)
        {
            if (waterBody == null) return;
            if (_live.Count >= maxLiveThrowables) Retire(0); // recycle the oldest, never refuse

            Stack<Throwable> pool = GetOrCreatePool(poolKey);
            Throwable t = pool.Count > 0 ? pool.Pop() : Build(poolKey);
            if (t == null) return;
            t.Age = 0f;
            t.Root.SetActive(true);
            // Physics.IgnoreCollision is NOT persistent: Unity drops the pair the moment either
            // collider is deactivated, and pooling deactivates every retired throwable. The
            // build-time pairwise ignore therefore silently expired on the first recycle, and
            // pooled objects collided again - at the shared launch point during volleys and in
            // floating clusters anywhere on the water. That was the returning "they bounce"
            // (worst for the dense spheres: a sinking cannonball has no buoyant force that could
            // kick it up, only a collision can). Re-ignore ON EVERY ACTIVATION, against the LIVE
            // set only - both colliders must be active for the call to stick, and any two
            // simultaneously-live objects are paired by whichever activated later.
            // Null-guarded: a destroyed collider (a custom prefab tearing its own down, or any
            // future factory slip) must degrade to "skip that pair", never to an exception that
            // aborts the throw and strands a ghost.
            if (!throwablesCollide)
                foreach (Collider own in t.Colliders)
                {
                    if (own == null) continue;
                    for (int liveIndex = 0; liveIndex < _live.Count; liveIndex++)
                        foreach (Collider other in _live[liveIndex].Colliders)
                            if (other != null) Physics.IgnoreCollision(own, other);
                }
            // Absolute assignment from the stored bases, never in-place multiplies: a pooled
            // object is re-tuned on every reuse and compounding would drift it.
            t.Breach.splashStrengthScale = splashHeaviness;
            t.Breach.splashRadius = t.BreachBaseRadius * splashRadiusScale;
            // Wave-size compression: the WAVE path (breach ripple + wake dipole) is what scales
            // with object size, so sizeFactor^-c bends it toward equal - big throwables damped,
            // small ones boosted, both around the reference size. The splash BURST stays on the
            // heaviness slider alone (it is speed-derived and already size-blind). Radius takes
            // the square root so the ring's footprint compresses gentler than its strength.
            float waveBalance = Mathf.Pow(Mathf.Max(t.SizeFactor, 0.05f), -waveSizeCompression);
            t.Breach.rippleStrength = t.BreachBaseRippleStrength * rippleScale * waveBalance;
            t.Breach.rippleRadius = t.BreachBaseRippleRadius * rippleScale * Mathf.Sqrt(waveBalance);
            t.Wake.strength = t.WakeBaseStrength * wakeScale * waveBalance;
            t.Buoyancy.buoyancy = t.BuoyancyBase * buoyancyScale;
            t.Buoyancy.waterLinearDamping = floatDamping;
            t.Buoyancy.verticalSettleDamping = settleDamping;

            target = ClampToFootprint(target, targetMargin);
            _lastTarget = target;
            Vector3 launch = ResolveLaunchPoint(withSpread: true);
            // Teleport through the RIGIDBODY, not just the transform: a transform teleport on an
            // INTERPOLATED body makes the renderer sweep from wherever the pooled object retired
            // (kill depth, the far edge) to the launch point - a one-frame streak that reads as a
            // monstrously hard launch. Only REUSED objects show it, which is why it surfaced once
            // the pool started cycling, on the fastest recyclers (cannonball, chicken).
            // Rigidbody.position/rotation apply instantly and restart interpolation clean.
            t.Body.position = launch;
            t.Body.rotation = Quaternion.identity;
            t.Root.transform.SetPositionAndRotation(launch, Quaternion.identity);

            Vector3 velocity = SolveBallisticVelocity(launch, target, arcApexHeight,
                                                      out float flightSeconds);
            t.Body.linearVelocity = velocity;
            t.Body.angularVelocity = Random.insideUnitSphere * SpinMaxRadiansPerSecond;
            t.PrevVelocity = velocity;
            if (logDiagnostics)
                Debug.Log($"[SplashRange] threw {t.Root.name} at {velocity.magnitude:0.0} m/s " +
                          $"(flight {flightSeconds:0.00}s) to ({target.x:0.0}, {target.z:0.0})");
            _live.Add(t);
        }

        public void ResetRange()
        {
            while (_live.Count > 0) Retire(0);
        }

        // Solve for a lob that peaks apexHeight above the launch: vertical speed from the apex
        // (vy = sqrt(2 g h)), flight time from the fall to the target's height, horizontal speed
        // from distance / time. Every throw shares the same arc height, so the catapult reads
        // consistently and near targets arrive SLOWLY instead of flat and fast.
        static Vector3 SolveBallisticVelocity(Vector3 from, Vector3 to, float apexHeight,
                                              out float flightSeconds)
        {
            float gravity = -Physics.gravity.y;
            float apex = Mathf.Max(apexHeight, (to.y - from.y) + MinApexClearance);
            float upSpeed = Mathf.Sqrt(2f * gravity * apex);
            // Time up to the apex plus time falling from the apex down to the target height.
            float fallHeight = apex - (to.y - from.y);
            flightSeconds = upSpeed / gravity + Mathf.Sqrt(2f * Mathf.Max(fallHeight, 0.01f) / gravity);
            return new Vector3((to.x - from.x) / flightSeconds, upSpeed,
                               (to.z - from.z) / flightSeconds);
        }

        void Retire(int index)
        {
            Throwable t = _live[index];
            t.Body.linearVelocity = Vector3.zero;
            t.Body.angularVelocity = Vector3.zero;
            t.Root.SetActive(false);
            GetOrCreatePool(t.PoolKey).Push(t);
            _live.RemoveAt(index);
        }

        Stack<Throwable> GetOrCreatePool(int poolKey)
        {
            if (!_poolsByKey.TryGetValue(poolKey, out Stack<Throwable> pool))
                _poolsByKey[poolKey] = pool = new Stack<Throwable>();
            return pool;
        }

        // Uniform pick over the built-ins AND every custom entry with a prefab assigned.
        int RandomKey()
        {
            int validCustoms = 0;
            for (int i = 0; i < customThrowables.Count; i++)
                if (customThrowables[i].prefab != null) validCustoms++;
            int pick = Random.Range(0, BuiltinKindCount + validCustoms);
            if (pick < BuiltinKindCount) return pick;
            for (int i = 0; i < customThrowables.Count; i++)
            {
                if (customThrowables[i].prefab == null) continue;
                if (pick == BuiltinKindCount) return BuiltinKindCount + i;
                pick--;
            }
            return 0;
        }

        Vector3 RandomTarget()
        {
            Vector3 extent = waterBody.volumeExtent;
            Vector3 center = waterBody.transform.position;
            return new Vector3(center.x + Random.Range(-0.6f, 1f) * extent.x * TargetSpreadFraction,
                               SurfaceY(),
                               center.z + Random.Range(-1f, 1f) * extent.z * TargetSpreadFraction);
        }

        float SurfaceY() => waterBody != null ? waterBody.transform.position.y : 0f;

        // A fixed catapult transform when assigned (exact - launchHeight and spread do not
        // apply, the transform IS the truth); otherwise the computed edge point. The gizmo
        // calls this with spread off so it never consumes Random in edit mode.
        Vector3 ResolveLaunchPoint(bool withSpread)
        {
            if (launchPoint != null) return launchPoint.position;
            Vector3 extent = waterBody.volumeExtent;
            Vector3 center = waterBody.transform.position;
            float sideways = withSpread ? Random.Range(-0.4f, 0.4f) * extent.z : 0f;
            return new Vector3(center.x - extent.x * 2f * LaunchEdgeFraction,
                               SurfaceY() + launchHeight,
                               center.z + sideways);
        }

        bool InsideFootprint(Vector3 world, float extentScale = 1f)
        {
            Vector3 local = world - waterBody.transform.position;
            Vector3 extent = waterBody.volumeExtent * extentScale;
            return Mathf.Abs(local.x) <= extent.x && Mathf.Abs(local.z) <= extent.z;
        }

        Vector3 ClampToFootprint(Vector3 world, float margin)
        {
            Vector3 center = waterBody.transform.position;
            Vector3 extent = waterBody.volumeExtent;
            return new Vector3(
                center.x + Mathf.Clamp(world.x - center.x, -extent.x * margin, extent.x * margin),
                world.y,
                center.z + Mathf.Clamp(world.z - center.z, -extent.z * margin, extent.z * margin));
        }

        // ---------------------------------------------------------------- factories
        // Physics rigs mirror the three.js scene: dense cannonball sinks, cow and rabbit wallow,
        // chicken bobs. Buoyancy/damping values are WaterBuoyancy's currency (boat demo scale).
        Throwable Build(int poolKey)
        {
            if (poolKey >= BuiltinKindCount) return BuildCustom(poolKey - BuiltinKindCount);
            switch ((ThrowableKind)poolKey)
            {
                case ThrowableKind.Cow: return BuildCow();
                case ThrowableKind.Chicken: return BuildChicken();
                case ThrowableKind.TrojanRabbit: return BuildTrojanRabbit();
                default: return BuildCannonball();
            }
        }

        Throwable BuildCustom(int customIndex)
        {
            CustomThrowable custom = customThrowables[customIndex];
            if (custom.prefab == null) return null;
            var root = new GameObject(CustomLabel(customIndex));
            GameObject visual = Instantiate(custom.prefab, root.transform, false);
            visual.transform.localPosition = Vector3.zero;
            // A collider somewhere on the prefab serves physics as-is; buoyancy however reads
            // the ROOT's collider for its probe box, so a rootless prefab gets a renderer-fit
            // box on the root (which also becomes the physics collider).
            if (root.GetComponentInChildren<Collider>() == null || root.GetComponent<Collider>() == null)
                FitRootBoxCollider(root);
            return Rig(BuiltinKindCount + customIndex, root, custom.mass, custom.buoyancy,
                       custom.wakeStrength);
        }

        string CustomLabel(int customIndex)
        {
            string label = customThrowables[customIndex].label;
            return string.IsNullOrEmpty(label) ? $"Custom {customIndex + 1}" : label;
        }

        // Fit a BoxCollider on the root from the children's combined renderer bounds, so any
        // model prefab is throwable with zero setup. World bounds -> root-local (root is at
        // identity here: fresh GameObject, visual at local zero).
        static void FitRootBoxCollider(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                root.AddComponent<BoxCollider>(); // unit box beats no probe box at all
                return;
            }
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            var box = root.AddComponent<BoxCollider>();
            box.center = root.transform.InverseTransformPoint(bounds.center);
            box.size = bounds.size;
        }

        Throwable Rig(int poolKey, GameObject root, float mass, float buoyancy,
                      float wakeStrength)
        {
            var body = root.AddComponent<Rigidbody>();
            body.mass = mass;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.interpolation = RigidbodyInterpolation.Interpolate;

            var floatComp = root.AddComponent<WaterBuoyancy>();
            floatComp.buoyancy = buoyancy;
            floatComp.drawDebugGizmos = false;
            // THE fix for "floats too high, bounces at the surface, never settles": the breach
            // splash stamps a ripple MOUND at the entry point and the wake interactor keeps
            // injecting under the object - with ripples in the buoyancy sample the object rides
            // ITS OWN splash and feeds back forever. Same reason the boat builder sets this
            // ("don't let the boat's own wake ripples propel it"). The probe reach stays at the
            // component default: shrinking it (an earlier attempt) only stiffened the entry
            // spring; the pre-entry 'bounce' was this feedback, not the reach.
            floatComp.ignoreInteractiveRipples = true;
            floatComp.waterLinearDamping = floatDamping;
            floatComp.verticalSettleDamping = settleDamping;
            // Cap the per-point buoyant acceleration (an opt-in WaterBuoyancy field built for
            // exactly this): a small high-buoyancy floater - the chicken, with probe spheres a
            // hand-span tall and buoyancy 3.5 - traverses its whole submersion curve in ~2
            // physics steps on a 10 m/s plunge, erupts at 3.5 g and breaches clean out: an entry
            // "bounce". The cap tames the deep-plunge rebound WITHOUT moving the float level;
            // it only bites while lift would exceed ~2 g (never for the sinking cannonball).
            floatComp.maxBuoyancyForce = 2f * Physics.gravity.magnitude;

            var breach = root.AddComponent<WaterBreachSplash>();
            breach.splashEmitter = splashEmitter;

            var wake = root.AddComponent<WaterSphereInteractor>();

            // Throwable-vs-throwable collision ignores are NOT set here: Physics.IgnoreCollision
            // expires whenever a collider deactivates (i.e. on every pool retire), so a build-time
            // pass only held until the first recycle. ThrowByKey re-ignores on every activation.
            root.AddComponent<CollisionReporter>().Range = this;
            root.transform.SetParent(transform);
            // Size for wave compression: the physics collider's half-diagonal, measured at build
            // time (root still at identity, so world bounds == authored size). A custom prefab
            // with only child colliders still measures via GetComponentInChildren.
            Collider sizeSource = root.GetComponentInChildren<Collider>();
            float halfDiagonal = sizeSource != null ? sizeSource.bounds.extents.magnitude
                                                    : ReferenceSizeMeters;
            return new Throwable
            {
                PoolKey = poolKey, Root = root, Body = body,
                Colliders = root.GetComponentsInChildren<Collider>(),
                SizeFactor = halfDiagonal / ReferenceSizeMeters,
                Breach = breach, Wake = wake,
                Buoyancy = floatComp,
                BuoyancyBase = buoyancy,
                WakeBaseStrength = wakeStrength,
                BreachBaseRadius = breach.splashRadius,
                BreachBaseRippleStrength = breach.rippleStrength,
                BreachBaseRippleRadius = breach.rippleRadius,
            };
        }

        /// <summary>Diagnostics: names what a throwable actually hit. A bounce with one of these
        /// lines right before it is a contact; a bounce without one is a water force.</summary>
        sealed class CollisionReporter : MonoBehaviour
        {
            internal WaterSplashRange Range;
            void OnCollisionEnter(Collision collision)
            {
                if (Range == null || !Range.logDiagnostics) return;
                Debug.LogWarning($"[SplashRange] CONTACT {name} hit {collision.collider.name} " +
                                 $"(impulse {collision.impulse.magnitude:0.0}) at " +
                                 $"{collision.GetContact(0).point}");
            }
        }

        static GameObject Primitive(PrimitiveType type, Transform parent, Vector3 localPos,
                                    Vector3 localScale, Color color,
                                    Quaternion? localRotation = null)
        {
            var go = GameObject.CreatePrimitive(type);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            // Nullable, NOT `= default`: default(Quaternion) is the INVALID zero quaternion and
            // its != operator compares by dot, which reads zero-vs-zero as "not equal".
            if (localRotation.HasValue) go.transform.localRotation = localRotation.Value;
            // The primitive's own collider would fight the root's single physics collider.
            // DestroyImmediate, NOT Destroy: Destroy defers to end of frame, so Rig() - which
            // runs in this same frame - captured these doomed colliders into the collision-
            // ignore set. One frame later they were dead references and every ignore pass threw
            // MissingReferenceException, ABORTING the throw ("sometimes throw, sometimes not")
            // and leaving half-activated ghosts that collided freely (the "weird bounces").
            DestroyImmediate(go.GetComponent<Collider>());
            var renderer = go.GetComponent<Renderer>();
            renderer.material.color = color;
            return go;
        }

        Throwable BuildCannonball()
        {
            var root = new GameObject("Cannonball");
            Primitive(PrimitiveType.Sphere, root.transform, Vector3.zero,
                      Vector3.one * 0.64f, new Color(0.15f, 0.19f, 0.23f));
            root.AddComponent<SphereCollider>().radius = 0.32f;
            return Rig((int)ThrowableKind.Cannonball, root, 12f, 0.35f, 1.5f); // buoyancy < 1: sinks
        }

        Throwable BuildCow()
        {
            var root = new GameObject("Cow");
            Color hide = new Color(0.96f, 0.94f, 0.9f), patch = new Color(0.17f, 0.17f, 0.17f);
            Primitive(PrimitiveType.Cube, root.transform, Vector3.zero,
                      new Vector3(1.1f, 0.6f, 0.5f), hide);
            Primitive(PrimitiveType.Cube, root.transform, new Vector3(0.22f, 0f, 0f),
                      new Vector3(0.5f, 0.62f, 0.52f), patch);
            Primitive(PrimitiveType.Cube, root.transform, new Vector3(0.72f, 0.25f, 0f),
                      new Vector3(0.35f, 0.35f, 0.35f), hide);
            foreach (var leg in new[] { new Vector3(-0.4f, -0.5f, -0.15f), new Vector3(-0.4f, -0.5f, 0.15f),
                                        new Vector3(0.4f, -0.5f, -0.15f), new Vector3(0.4f, -0.5f, 0.15f) })
                Primitive(PrimitiveType.Cube, root.transform, leg,
                          new Vector3(0.12f, 0.4f, 0.12f), hide);
            var box = root.AddComponent<BoxCollider>();
            box.size = new Vector3(1.4f, 1.1f, 0.55f); box.center = new Vector3(0.1f, -0.1f, 0f);
            return Rig((int)ThrowableKind.Cow, root, 6f, 2.2f, 2f);
        }

        Throwable BuildChicken()
        {
            var root = new GameObject("Chicken");
            Color feathers = new Color(0.97f, 0.95f, 0.92f);
            Primitive(PrimitiveType.Sphere, root.transform, Vector3.zero,
                      Vector3.one * 0.4f, feathers);
            Primitive(PrimitiveType.Sphere, root.transform, new Vector3(0.16f, 0.2f, 0f),
                      Vector3.one * 0.2f, feathers);
            Primitive(PrimitiveType.Capsule, root.transform, new Vector3(0.3f, 0.2f, 0f),
                      new Vector3(0.06f, 0.06f, 0.06f), new Color(0.85f, 0.54f, 0.17f),
                      Quaternion.Euler(0f, 0f, 90f));
            root.AddComponent<SphereCollider>().radius = 0.24f;
            return Rig((int)ThrowableKind.Chicken, root, 0.8f, 3.5f, 0.6f); // light + buoyant: bobs
        }

        Throwable BuildTrojanRabbit()
        {
            // The trojan rabbit. Sculpted by Claude (2026-08-08); per Bert's decree it stays.
            var root = new GameObject("Trojan Rabbit");
            Color wood = new Color(0.48f, 0.35f, 0.22f), darkWood = new Color(0.3f, 0.23f, 0.14f);
            Primitive(PrimitiveType.Cube, root.transform, Vector3.zero,
                      new Vector3(0.8f, 0.8f, 0.5f), wood);
            Primitive(PrimitiveType.Cube, root.transform, new Vector3(0f, 0.62f, 0f),
                      new Vector3(0.45f, 0.45f, 0.4f), wood);
            foreach (float side in new[] { -1f, 1f })
                Primitive(PrimitiveType.Cube, root.transform, new Vector3(side * 0.12f, 1.05f, 0f),
                          new Vector3(0.12f, 0.5f, 0.08f), wood);
            foreach (var wheel in new[] { new Vector3(-0.3f, -0.45f, -0.3f), new Vector3(0.3f, -0.45f, -0.3f),
                                          new Vector3(-0.3f, -0.45f, 0.3f), new Vector3(0.3f, -0.45f, 0.3f) })
                Primitive(PrimitiveType.Cylinder, root.transform, wheel,
                          new Vector3(0.32f, 0.04f, 0.32f), darkWood,
                          Quaternion.Euler(90f, 0f, 0f));
            var box = root.AddComponent<BoxCollider>();
            box.size = new Vector3(0.9f, 1.7f, 0.7f); box.center = new Vector3(0f, 0.3f, 0f);
            return Rig((int)ThrowableKind.TrojanRabbit, root, 9f, 1.4f, 2f);
        }

        // ---------------------------------------------------------------- HUD
        void OnGUI()
        {
            float y = HudY;
            GUI.Label(new Rect(HudX, y, HudWidth + 80f, HudRowHeight),
                      "Splash range — \"Fetchez la vache !\"");
            y += HudRowHeight * 0.8f;
            foreach (ThrowableKind kind in System.Enum.GetValues(typeof(ThrowableKind)))
            {
                if (GUI.Button(new Rect(HudX, y, HudWidth, HudRowHeight - 4f), kind.ToString()))
                    Throw(kind, RandomTarget());
                y += HudRowHeight;
            }
            for (int i = 0; i < customThrowables.Count; i++)
            {
                if (customThrowables[i].prefab == null) continue;
                if (GUI.Button(new Rect(HudX, y, HudWidth, HudRowHeight - 4f), CustomLabel(i)))
                    ThrowCustom(i, RandomTarget());
                y += HudRowHeight;
            }
            _auto = GUI.Toggle(new Rect(HudX, y, HudWidth, HudRowHeight - 4f), _auto,
                               " Auto catapult");
            y += HudRowHeight;
            clickToThrow = GUI.Toggle(new Rect(HudX, y, HudWidth, HudRowHeight - 4f), clickToThrow,
                                      " Click water to throw");
            y += HudRowHeight;
            if (GUI.Button(new Rect(HudX, y, HudWidth, HudRowHeight - 4f), "Reset"))
                ResetRange();
            y += HudRowHeight;
            GUI.Label(new Rect(HudX, y, HudWidth, HudRowHeight),
                      $"splash heaviness: {splashHeaviness:0.00}");
            y += HudRowHeight * 0.7f;
            splashHeaviness = GUI.HorizontalSlider(
                new Rect(HudX, y, HudWidth, HudRowHeight - 10f), splashHeaviness, 0f, 2f);
            y += HudRowHeight * 0.8f;
            GUI.Label(new Rect(HudX, y, HudWidth, HudRowHeight),
                      $"objects: {_live.Count} / {maxLiveThrowables}");
            // The click gate excludes the strip the HUD ACTUALLY covers, measured here on
            // repaint, not re-guessed from a row count that drifts every time a row is added.
            if (Event.current.type == EventType.Repaint) _hudBottom = y + HudRowHeight;
        }
#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (waterBody == null) return;
            Vector3 center = waterBody.transform.position;
            Vector3 extent = waterBody.volumeExtent;
            // Water footprint (blue) and the clamped target zone (green).
            Gizmos.color = new Color(0.25f, 0.55f, 1f, 0.8f);
            Gizmos.DrawWireCube(center, new Vector3(extent.x * 2f, 0.02f, extent.z * 2f));
            Gizmos.color = new Color(0.3f, 1f, 0.5f, 0.8f);
            Gizmos.DrawWireCube(center, new Vector3(extent.x * 2f * targetMargin, 0.02f,
                                                    extent.z * 2f * targetMargin));
            // Launch origin and the last solved arc.
            Vector3 launch = ResolveLaunchPoint(withSpread: false);
            Gizmos.color = new Color(1f, 0.7f, 0.2f, 0.9f);
            Gizmos.DrawWireSphere(launch, 0.3f);
            if (_lastTarget != Vector3.zero)
            {
                Vector3 v0 = SolveBallisticVelocity(launch, _lastTarget, arcApexHeight,
                                                    out float arcSeconds);
                Vector3 prev = launch;
                const int ArcSegments = 24;
                for (int i = 1; i <= ArcSegments; i++)
                {
                    float t = arcSeconds * i / ArcSegments;
                    Vector3 p = launch + v0 * t + 0.5f * Physics.gravity * t * t;
                    Gizmos.DrawLine(prev, p);
                    prev = p;
                }
            }
        }
#endif
    }
}
