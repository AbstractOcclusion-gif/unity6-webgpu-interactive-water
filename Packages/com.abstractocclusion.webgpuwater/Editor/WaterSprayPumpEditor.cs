// WebGpuWater - WaterSprayPump inspector diagnostics and scene handles.
//
// Probe generation and whole-array behaviour live in the Water Wizard so boats, rocks and mixed-mode
// objects have one authoring path. This inspector remains the detailed per-probe view and runtime debugger.
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [CustomEditor(typeof(WaterSprayPump))]
    internal sealed class WaterSprayPumpEditor : UnityEditor.Editor
    {
        // Internal so the wizard's hull fit shares this ceiling instead of copying the number: one place
        // decides how many probes a pump may be authored with.
        internal const int MaxProbeCount = 128;
        const float FallbackHalfExtent = 0.5f;      // when the object has neither collider nor renderer
        const float GizmoRadiusFraction = 0.03f;     // probe gizmo size as a fraction of the object's size
        const float GizmoRadiusFloor = 0.02f;
        const float MinOutwardLengthSquared = 1e-8f; // below this a probe's direction is the zero sentinel
        const float OutwardArrowThickness = 2f;
        const float OutwardArrowTipFraction = 0.3f;  // tip marker size, as a fraction of the probe radius
        const float PetalWedgeRadii = 4f;            // wedge length, in probe-gizmo radii
        const int PetalWedgeSegments = 24;           // rim resolution; even, so the mid index is the centre
        const float FullRingDegrees = 360f;
        // The strength the wedge is drawn at. Launch angle rises with strength, so previewing the
        // FASTEST case shows the steepest petal the pump will throw rather than an average nobody hits.
        const float PreviewStrength = 1f;
        // Enough silent probes to see the pattern (one side? the tail of the array?) without the readout
        // turning into a wall of indices on a 40-probe hull.
        const int MaxListedSilentProbes = 12;
        // Share of probes out of band that stops being noise and starts being the explanation.
        const float BandTroubleFraction = 0.5f;
        const string SignalReadoutFormat = "water {0:0.###}, boat {1:0.###}, selected {2:0.###}";

        static readonly Color BoatColor = new Color(0.4f, 0.8f, 1.0f);
        static readonly Color RockColor = new Color(1.0f, 0.75f, 0.35f);
        static readonly Color BothColor = new Color(0.7f, 1.0f, 0.6f);

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Generate probes and apply Boat / Rock / Both behaviour globally from Water Wizard > " +
                "Fit Spray To Object. Use this inspector only for detailed per-probe overrides.",
                MessageType.None);

            DrawPetalReadout();
            DrawBurstDiagnostics((WaterSprayPump)target);
        }

        // Why this exists: a burst refused by the per-frame cap and a probe that never triggered look
        // EXACTLY the same on screen - nothing sprays - and they want opposite fixes. Reading both
        // numbers side by side is the only way to tell them apart without guessing.
        void DrawBurstDiagnostics(WaterSprayPump pump)
        {
            if (!Application.isPlaying)
            {
                DrawStaleProbeWarning(pump);
                EditorGUILayout.HelpBox("Enter play mode to see which probes are firing and whether the " +
                                        "particle pool is dropping their bursts.", MessageType.None);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Burst diagnostics", EditorStyles.boldLabel);

            DrawProbeFiringReadout(pump);
            DrawSignalReadout(pump);
            DrawCrownReadout(pump);
            DrawPoolBudgetReadout(pump);
        }

        // Placement BAKES the waterline into local offsets, so scaling or lifting the object afterwards
        // carries every probe off the water and the pump goes silent with no other symptom. Catching it
        // at edit time is the difference between a one-line fix and an evening.
        void DrawStaleProbeWarning(WaterSprayPump pump)
        {
            SerializedProperty probes = serializedObject.FindProperty("probes");
            if (probes == null || probes.arraySize == 0) return;

            float band = serializedObject.FindProperty("surfaceBand").floatValue;
            float waterY = ResolveWaterY(pump.transform.position);

            int offBand = 0;
            float furthest = 0f;
            for (int i = 0; i < probes.arraySize; i++)
            {
                SerializedProperty element = probes.GetArrayElementAtIndex(i);
                // A probe that opted out of the height gate can never be silenced by placement.
                if (element.FindPropertyRelative("ignoreSurfaceBand").boolValue) continue;
                Vector3 local = element.FindPropertyRelative("localOffset").vector3Value;
                float distance = Mathf.Abs(pump.transform.TransformPoint(local).y - waterY);
                furthest = Mathf.Max(furthest, distance);
                if (distance > band) offBand++;
            }
            if (offBand == 0) return;

            EditorGUILayout.HelpBox(
                $"{offBand} of {probes.arraySize} probes sit further than Surface Band ({band:0.##} m) from the " +
                $"rest waterline - the furthest is {furthest:0.##} m, so those probes will never spray.\n\n" +
                "Probe offsets are LOCAL, so scaling or moving the object AFTER placing them carries them off " +
                "the water. Re-run Water Wizard > Fit Spray To Object at the current size, or raise Surface " +
                "Band past the figure above.", MessageType.Warning);
        }

        static void DrawProbeFiringReadout(WaterSprayPump pump)
        {
            int[] counts = pump.ProbeEmitCounts;
            if (counts == null || counts.Length == 0)
            {
                EditorGUILayout.LabelField("Probes firing", "no probe buffers yet");
                return;
            }

            int firing = 0;
            for (int i = 0; i < counts.Length; i++)
                if (counts[i] > 0) firing++;

            EditorGUILayout.LabelField("Probes firing", $"{firing} of {counts.Length}");
            DrawGateBreakdown(pump, counts.Length);

            if (firing == counts.Length) return;
            EditorGUILayout.LabelField("Silent probes", DescribeSilentProbes(counts));
        }

        static void DrawSignalReadout(WaterSprayPump pump)
        {
            float[] waterSignals = pump.ProbeWaterSignals;
            float[] boatSignals = pump.ProbeBoatSignals;
            float[] triggerSignals = pump.ProbeTriggerSignals;
            if (waterSignals == null || boatSignals == null || triggerSignals == null) return;

            float waterPeak = 0f;
            float boatPeak = 0f;
            float triggerPeak = 0f;
            int count = Mathf.Min(waterSignals.Length, Mathf.Min(boatSignals.Length, triggerSignals.Length));
            for (int index = 0; index < count; index++)
            {
                waterPeak = Mathf.Max(waterPeak, waterSignals[index]);
                boatPeak = Mathf.Max(boatPeak, boatSignals[index]);
                triggerPeak = Mathf.Max(triggerPeak, triggerSignals[index]);
            }

            EditorGUILayout.LabelField("Peak trigger signal",
                string.Format(SignalReadoutFormat, waterPeak, boatPeak, triggerPeak));
        }

        static void DrawCrownReadout(WaterSprayPump pump)
        {
            SprayCrownGate[] crownGates = pump.ProbeCrownGates;
            if (crownGates == null || crownGates.Length == 0) return;

            var tally = new int[System.Enum.GetValues(typeof(SprayCrownGate)).Length];
            for (int index = 0; index < crownGates.Length; index++) tally[(int)crownGates[index]]++;

            var line = new System.Text.StringBuilder();
            for (int index = 0; index < tally.Length; index++)
            {
                if (tally[index] == 0) continue;
                if (line.Length > 0) line.Append(",  ");
                line.Append($"{(SprayCrownGate)index} {tally[index]}");
            }
            EditorGUILayout.LabelField("Last crown event", line.ToString());
        }

        // This frame's rejection reasons, counted. Every gate looks the same on screen - no spray - so
        // naming the one that actually fired is the difference between fixing it and guessing at it.
        static void DrawGateBreakdown(WaterSprayPump pump, int probeCount)
        {
            SprayProbeGate[] gates = pump.ProbeGates;
            if (gates == null) return;

            var tally = new int[System.Enum.GetValues(typeof(SprayProbeGate)).Length];
            for (int i = 0; i < gates.Length; i++) tally[(int)gates[i]]++;

            var line = new System.Text.StringBuilder();
            for (int i = 0; i < tally.Length; i++)
            {
                if (tally[i] == 0) continue;
                if (line.Length > 0) line.Append(",  ");
                line.Append($"{(SprayProbeGate)i} {tally[i]}");
            }
            EditorGUILayout.LabelField("This frame", line.ToString());

            DrawBandReadout(pump, tally[(int)SprayProbeGate.OutOfBand], probeCount);
        }

        // The one number that turns "nothing sprays" into an action: how far the probes actually sit from
        // the waterline, against the band that is meant to admit them.
        static void DrawBandReadout(WaterSprayPump pump, int outOfBand, int probeCount)
        {
            float[] distances = pump.ProbeBandDistances;
            if (distances == null || distances.Length == 0) return;

            float furthest = 0f;
            for (int i = 0; i < distances.Length; i++) furthest = Mathf.Max(furthest, distances[i]);
            EditorGUILayout.LabelField("Furthest from waterline", $"{furthest:0.###} m");

            if (outOfBand < probeCount * BandTroubleFraction) return;
            EditorGUILayout.HelpBox($"{outOfBand} of {probeCount} probes are outside Surface Band right now. " +
                                    "On a long hull the water is at different heights along its length, so a " +
                                    "band tight enough for a clustered bow row can silence most of a " +
                                    "waterline ring. Raise Surface Band toward the figure above.",
                                    MessageType.Info);
        }

        static string DescribeSilentProbes(int[] counts)
        {
            var listed = new System.Text.StringBuilder();
            int shown = 0;
            for (int i = 0; i < counts.Length && shown < MaxListedSilentProbes; i++)
            {
                if (counts[i] > 0) continue;
                if (shown > 0) listed.Append(", ");
                listed.Append(i);
                shown++;
            }
            return shown < MaxListedSilentProbes ? listed.ToString() : listed.Append(", ...").ToString();
        }

        void DrawPoolBudgetReadout(WaterSprayPump pump)
        {
            WaterFoamParticles pool = ResolvePreviewPool(pump);
            if (pool == null)
            {
                EditorGUILayout.LabelField("Particle pool", "none on this body - Shuriken fallback path");
                return;
            }

            EditorGUILayout.LabelField("Bursts this frame",
                $"{pool.BurstsRequestedThisFrame} asked, {pool.BurstsDroppedThisFrame} dropped " +
                $"(cap {WaterFoamParticles.MaxBurstsPerFrame})");
            EditorGUILayout.LabelField("Peak / total dropped",
                $"{pool.PeakBurstsRequestedPerFrame} peak per frame, {pool.BurstsDroppedTotal} dropped so far");

            if (pool.BurstsSuppressedTotal > 0)
                EditorGUILayout.HelpBox($"{pool.BurstsSuppressedTotal} burst(s) were refused because the body's " +
                                        "Use Particles is off, or the pool is disabled. That is a switch, not a " +
                                        "budget.", MessageType.Warning);

            if (pool.BurstsDroppedTotal == 0) return;

            EditorGUILayout.HelpBox("Bursts ARE being dropped. The cap is per BODY and shared by every caller " +
                                    "on it - this pump, any other pump, WaterSplash impacts and mouse splashes " +
                                    "all draw from the same 16. WaterSplash queues in FixedUpdate, before this " +
                                    "pump's LateUpdate, so it takes slots first. Lower the probe count, raise " +
                                    "Emit Cooldown to spread them, or accept the drops.", MessageType.Warning);
        }

        // The pool the pump's bursts actually land in: the body under it, matching how the emitter routes.
        static WaterFoamParticles ResolvePreviewPool(WaterSprayPump pump)
        {
            WaterVolume body = WaterVolume.BodyContaining(pump.transform.position);
            return body != null ? body.GetComponent<WaterFoamParticles>() : null;
        }

        // The petal knobs are hard to judge from the numbers alone, because the launch angle is a SUM:
        // the emitter's own Upward Bias / Outward Spread set the base, and this pump only tilts it. The
        // Scene wedge shows the result; this says it in degrees, so "did it take" has a text answer.
        void DrawPetalReadout()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Petal preview", EditorStyles.boldLabel);

            WaterSplashEmitter emitter = ResolvePreviewEmitter();
            float tiltDegrees = serializedObject.FindProperty("petalElevationDegrees").floatValue;
            float launchDegrees = ResolveLaunchElevation() * Mathf.Rad2Deg;
            float arcDegrees = serializedObject.FindProperty("petalArcDegrees").floatValue;

            EditorGUILayout.LabelField("Launch angle", emitter != null
                ? $"{launchDegrees:0.#}° above horizontal  ({launchDegrees - tiltDegrees:0.#}° from " +
                  $"'{emitter.name}' + {tiltDegrees:0.#}° tilt), at full trigger speed"
                : $"{launchDegrees:0.#}° - tilt only, no splash emitter found to read a base angle from");

            EditorGUILayout.LabelField("Arc", arcDegrees >= FullRingDegrees
                ? "360° - full ring. Narrow it to see petals."
                : $"{arcDegrees:0.#}° wedge, on probes that carry a direction");

            EditorGUILayout.HelpBox(
                "The Scene wedges show the arc and the launch angle. They do NOT show the rake: that is " +
                "driven by each probe's measured velocity, which only exists in play mode. A probe drawn " +
                "as a full circle has no direction - use Water Wizard > Fit Spray To Object to give it " +
                "one, or type an outwardLocal by hand.", MessageType.None);
        }

        /// <summary>Stamps a probe array onto a pump through SerializedProperty - which is what makes Undo
        /// and prefab overrides work. Internal and static because the Water Wizard's hull fit writes the
        /// same array from its own SerializedObject; a second copy of this loop would drift the first time
        /// the probe struct grows a field.</summary>
        /// <param name="outwardLocals">Per-probe outward direction, or null for the bounding-box layouts
        /// that have no outline to face out of. Null writes zero, which is the legacy full-ring burst.</param>
        internal static void WriteProbes(SerializedObject pumpObject, Vector3[] localOffsets,
                                         Vector3[] outwardLocals, WaterSprayMode mode,
                                         WaterSprayEmission emission, WaterSprayWaterMotion waterMotion,
                                         bool ignoreSurfaceBand)
        {
            pumpObject.Update();
            SerializedProperty probes = pumpObject.FindProperty("probes");
            probes.arraySize = localOffsets.Length;
            for (int i = 0; i < localOffsets.Length; i++)
            {
                SerializedProperty element = probes.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("localOffset").vector3Value = localOffsets[i];
                element.FindPropertyRelative("outwardLocal").vector3Value =
                    outwardLocals != null ? outwardLocals[i] : Vector3.zero;
                WriteProbeBehaviour(element, mode, emission, waterMotion, ignoreSurfaceBand);
            }
            pumpObject.ApplyModifiedProperties(); // registers Undo + marks the scene/prefab dirty
        }

        /// <summary>Applies one behaviour to every existing probe without rebuilding its placement.</summary>
        internal static int ApplyProbeBehaviour(SerializedObject pumpObject, WaterSprayMode mode,
                                                 WaterSprayEmission emission,
                                                 WaterSprayWaterMotion waterMotion,
                                                 bool ignoreSurfaceBand)
        {
            pumpObject.Update();
            SerializedProperty probes = pumpObject.FindProperty("probes");
            if (probes == null) return 0;

            for (int index = 0; index < probes.arraySize; index++)
                WriteProbeBehaviour(probes.GetArrayElementAtIndex(index), mode, emission,
                                    waterMotion, ignoreSurfaceBand);

            pumpObject.ApplyModifiedProperties();
            return probes.arraySize;
        }

        static void WriteProbeBehaviour(SerializedProperty probe, WaterSprayMode mode,
                                        WaterSprayEmission emission, WaterSprayWaterMotion waterMotion,
                                        bool ignoreSurfaceBand)
        {
            probe.FindPropertyRelative("mode").enumValueIndex = (int)mode;
            probe.FindPropertyRelative("emission").enumValueIndex = (int)emission;
            probe.FindPropertyRelative("waterMotion").enumValueIndex = (int)waterMotion;
            probe.FindPropertyRelative("ignoreSurfaceBand").boolValue = ignoreSurfaceBand;
        }

        // Rest waterline = the resolved body's plane (its transform Y), matching the surface plane the
        // obstacle/caustics code uses. Falls back to the object's own height when there is no water body.
        // Internal because the wizard's hull-draft preview reads the same plane: a second copy of this
        // rule is exactly the kind of drift that puts probes and preview on different waterlines.
        internal static float ResolveWaterY(Vector3 worldPosition)
        {
            WaterVolume body = WaterVolume.BodyContaining(worldPosition);
            return body != null ? body.transform.position.y : worldPosition.y;
        }

        static Bounds ResolveWorldBounds(WaterSprayPump pump)
        {
            Collider collider = pump.GetComponent<Collider>();
            if (collider != null) return collider.bounds;

            Renderer renderer = pump.GetComponentInChildren<Renderer>();
            if (renderer != null) return renderer.bounds;

            Debug.LogWarning($"{nameof(WaterSprayPump)} on '{pump.name}' has no Collider or Renderer to size " +
                             "probe placement from; using a default extent. Move the probes by hand if needed.", pump);
            return new Bounds(pump.transform.position, Vector3.one * (FallbackHalfExtent * 2f));
        }

        // Each probe gets a draggable sphere handle: grab it in the Scene view to place the point directly,
        // instead of typing local offsets. Edits go through SerializedProperty, so Undo and prefab
        // overrides work the same as Wizard-authored probes.
        void OnSceneGUI()
        {
            var pump = (WaterSprayPump)target;
            serializedObject.Update(); // reflect the latest points (e.g. right after an Undo) before drawing
            SerializedProperty probes = serializedObject.FindProperty("probes");
            if (probes == null || probes.arraySize == 0) return;

            Transform t = pump.transform;
            float radius = Mathf.Max(GizmoRadiusFloor, ResolveWorldBounds(pump).size.magnitude * GizmoRadiusFraction);
            float arcDegrees = serializedObject.FindProperty("petalArcDegrees").floatValue;
            float launchElevation = ResolveLaunchElevation();

            EditorGUI.BeginChangeCheck();
            for (int i = 0; i < probes.arraySize; i++)
            {
                SerializedProperty element = probes.GetArrayElementAtIndex(i);
                SerializedProperty offset = element.FindPropertyRelative("localOffset");
                var mode = (WaterSprayMode)element.FindPropertyRelative("mode").enumValueIndex;

                Vector3 world = t.TransformPoint(offset.vector3Value);
                Handles.color = ModeColor(mode);
                Vector3 moved = Handles.FreeMoveHandle(world, radius, Vector3.zero, Handles.SphereHandleCap);
                if (moved != world)
                    offset.vector3Value = t.InverseTransformPoint(moved); // store back in the object's local space

                DrawPetalWedge(t, world, radius, element.FindPropertyRelative("outwardLocal").vector3Value,
                               arcDegrees, launchElevation);
            }
            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties(); // one Undo step + marks the scene/prefab dirty
        }

        /// <summary>
        /// The petal a probe actually throws: a wedge <paramref name="arcDegrees"/> wide around its
        /// outward direction, lifted to the launch angle the emitter will really use. A probe with no
        /// direction is the legacy full ring, and draws as a full circle rather than nothing - "this one
        /// sprays everywhere" is information too.
        /// </summary>
        /// <remarks>The RAKE is deliberately absent: it is driven by the probe's measured velocity, which
        /// does not exist until play mode, so drawing it here would be an invention.</remarks>
        static void DrawPetalWedge(Transform owner, Vector3 world, float radius, Vector3 outwardLocal,
                                   float arcDegrees, float launchElevationRadians)
        {
            Vector3 center = owner.TransformDirection(outwardLocal);
            center.y = 0f;

            bool fullRing = center.sqrMagnitude < MinOutwardLengthSquared;
            // No direction means no centre to sweep from, so the ring is swept from an arbitrary but
            // stable axis - it closes on itself either way.
            center = fullRing ? Vector3.forward : center.normalized;
            float sweep = fullRing ? FullRingDegrees : Mathf.Clamp(arcDegrees, 0f, FullRingDegrees);

            float lift = Mathf.Sin(launchElevationRadians);
            float reach = Mathf.Cos(launchElevationRadians);
            float length = radius * PetalWedgeRadii;

            // Cached scratch: this ran per probe per scene-GUI EVENT (layout + repaint),
            // allocating up to 128 x 25 vectors per frame on a fully-probed boat while selected.
            Vector3[] rim = _petalRim ??= new Vector3[PetalWedgeSegments + 1];
            for (int i = 0; i <= PetalWedgeSegments; i++)
            {
                float azimuth = Mathf.Lerp(-0.5f * sweep, 0.5f * sweep, i / (float)PetalWedgeSegments);
                Vector3 heading = Quaternion.AngleAxis(azimuth, Vector3.up) * center;
                rim[i] = world + (heading * reach + Vector3.up * lift) * length;
            }
            Handles.DrawAAPolyLine(OutwardArrowThickness, rim);

            // The two edges are what make the WIDTH readable; without them a narrow arc is just a dash
            // floating off the hull. A full ring has no edges to draw - it never opens.
            if (fullRing) return;
            Handles.DrawAAPolyLine(OutwardArrowThickness, world, rim[0]);
            Handles.DrawAAPolyLine(OutwardArrowThickness, world, rim[PetalWedgeSegments]);
            Handles.SphereHandleCap(0, rim[PetalWedgeSegments / 2], Quaternion.identity,
                                    radius * OutwardArrowTipFraction, EventType.Repaint);
        }

        // The angle the wedge is drawn at. It is the EMITTER'S launch angle plus this pump's tilt, because
        // that is what the burst actually leaves at - drawing the tilt alone would show a flat petal for a
        // pump whose spray in fact flies at 40 degrees. Falls back to the tilt alone when no emitter can
        // be found, which is the one case the preview cannot know the base angle for.
        float ResolveLaunchElevation()
        {
            float tiltDegrees = serializedObject.FindProperty("petalElevationDegrees").floatValue;
            float sprayRadius = serializedObject.FindProperty("sprayRadius").floatValue;

            WaterSplashEmitter emitter = ResolvePreviewEmitter();
            return emitter != null
                ? emitter.PreviewLaunchElevationRadians(PreviewStrength, sprayRadius, tiltDegrees)
                : Mathf.Max(0f, tiltDegrees) * Mathf.Deg2Rad;
        }

        // Preview-only resolve: the explicit override, else the scene's emitter. The runtime resolve can
        // also CREATE one on first impact, which edit time cannot and must not do.
        // The scene lookup is CACHED: FindFirstObjectByType walks the whole scene, and this
        // resolve runs from the inspector readout AND every scene-GUI event (per probe on a
        // multi-probe boat), so a selected pump used to pay many scene walks per repaint.
        // Re-resolved at most once a second; a destroyed emitter fails the fake-null check and
        // re-resolves on the same schedule.
        WaterSplashEmitter _cachedSceneEmitter;
        double _cachedSceneEmitterTime = double.NegativeInfinity;
        const double SceneEmitterCacheSeconds = 1.0;

        WaterSplashEmitter ResolvePreviewEmitter()
        {
            var assigned = serializedObject.FindProperty("emitter").objectReferenceValue as WaterSplashEmitter;
            if (assigned != null) return assigned;
            double now = EditorApplication.timeSinceStartup;
            if (_cachedSceneEmitter == null || now - _cachedSceneEmitterTime > SceneEmitterCacheSeconds)
            {
                _cachedSceneEmitter = Object.FindFirstObjectByType<WaterSplashEmitter>();
                _cachedSceneEmitterTime = now;
            }
            return _cachedSceneEmitter;
        }

        // Petal-wedge rim scratch buffer - see the DrawAAPolyLine site for why this is cached.
        static Vector3[] _petalRim;

        static Color ModeColor(WaterSprayMode mode) => mode switch
        {
            WaterSprayMode.Boat => BoatColor,
            WaterSprayMode.Rock => RockColor,
            _ => BothColor,
        };
    }
}
