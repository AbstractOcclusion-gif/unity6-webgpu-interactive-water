// WebGpuWater - placeable wave staff backed by the canonical buoyancy-height sampler.
using System;
using System.Text;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [DisallowMultipleComponent]
    [AddComponentMenu("WebGpuWater/Wave Height Gauge")]
    public sealed class WaterWaveGauge : MonoBehaviour
    {
        const string VisualRootName = "Wave Gauge Visuals";
        const string StaffName = "Staff";
        const string SurfaceMarkerName = "Surface";
        const string CrestMarkerName = "Crest";
        const string TroughMarkerName = "Trough";
        const string ReadoutName = "Readout";
        const string DefaultLineShaderName = "Universal Render Pipeline/Unlit";
        const string GaugeTextShaderName = "Hidden/AbstractOcclusion/WebGpuWater/WaveGaugeText";
        const int MinimumObservedWaveCapacity = 8;
        const int MaximumObservedWaveCapacity = 512;
        const int HighestThirdDivisor = 3;
        const int LinePointCount = 2;
        const float HalfMarkerWidth = 0.65f;
        const float LabelHorizontalOffset = 0.85f;
        const float LabelVerticalOffset = 0.25f;
        const float DefaultMinimumProminence = 0.05f;
        const float DefaultReversalThreshold = 0.01f;
        const float DefaultRollingWindowSeconds = 60f;
        const float DefaultStaffBelowMean = 10f;
        const float DefaultStaffAboveMean = 15f;
        const float DefaultStaffWidth = 0.06f;
        const float DefaultMarkerWidth = 0.045f;
        const float DefaultLabelScale = 0.08f;

        [Header("Water")]
        [SerializeField] WaterVolume volume;
        [Tooltip("World-space XZ is taken from this transform. Y placement does not affect sampling.")]
        [SerializeField] bool resolveContainingVolume = true;

        [Header("Measurement")]
        [Min(0f)] [SerializeField] float minimumWaveProminence = DefaultMinimumProminence;
        [Min(0f)] [SerializeField] float reversalThreshold = DefaultReversalThreshold;
        [Min(1f)] [SerializeField] float rollingWindowSeconds = DefaultRollingWindowSeconds;
        [Range(MinimumObservedWaveCapacity, MaximumObservedWaveCapacity)]
        [SerializeField] int observedWaveCapacity = 128;

        [Header("Physical Staff")]
        [Min(0f)] [SerializeField] float metresBelowMean = DefaultStaffBelowMean;
        [Min(0f)] [SerializeField] float metresAboveMean = DefaultStaffAboveMean;
        [Min(0.001f)] [SerializeField] float staffWidth = DefaultStaffWidth;
        [Min(0.001f)] [SerializeField] float markerWidth = DefaultMarkerWidth;
        [Min(0.001f)] [SerializeField] float labelScale = DefaultLabelScale;
        [SerializeField] Material lineMaterial;
        [SerializeField] Color staffColor = Color.white;
        [SerializeField] Color surfaceColor = Color.white;
        [SerializeField] Color crestColor = new Color(1f, 0.22f, 0.12f, 1f);
        [SerializeField] Color troughColor = new Color(0.1f, 0.75f, 1f, 1f);
        [SerializeField] bool showReadout = true;
        [SerializeField] Camera labelCamera;

        Transform _visualRoot;
        LineRenderer _staff;
        LineRenderer _surfaceMarker;
        LineRenderer _crestMarker;
        LineRenderer _troughMarker;
        TextMesh _readout;
        Material _runtimeLineMaterial;
        Material _runtimeTextMaterial;

        // Readout throttle (perf audit 2026-08-11). The six lines are metre readings that a human
        // reads; rebuilding them EVERY LateUpdate cost six interpolated strings plus their concat
        // intermediates of managed garbage per frame, and assigning TextMesh.text re-tessellates
        // the glyph mesh even when the characters are identical. Unity's heap never shrinks, so
        // that steady drip is one of the mechanisms behind "fps falls the longer the scene runs".
        // The billboard transform below still updates every frame - only the TEXT is throttled.
        const float ReadoutRefreshIntervalSeconds = 0.25f;
        const int ReadoutBuilderCapacity = 160;
        // Real time, not Time.time: the gauge runs in edit mode too, where the game clock stalls.
        float _nextReadoutRefreshTime;
        string _readoutText;
        readonly StringBuilder _readoutBuilder = new StringBuilder(ReadoutBuilderCapacity);

        float[] _waveHeights;
        float[] _waveTimes;
        float[] _sortScratch;
        int _waveStart;
        int _waveCount;

        bool _sampleReady;
        bool _rising;
        bool _haveLeadingTrough;
        float _previousElevation;
        float _candidateExtreme;
        float _leadingTrough;
        float _capturedCrest;
        float _capturedTrough;

        public float CurrentElevation { get; private set; }
        public float CurrentSurfaceWorldY { get; private set; }
        public float CapturedCrestElevation => _capturedCrest;
        public float CapturedTroughElevation => _capturedTrough;
        public float LastWaveHeight { get; private set; }
        public float RollingMaximumHeight { get; private set; }
        public float ObservedSignificantHeight { get; private set; }
        public int CompletedWaveCount { get; private set; }

        void OnEnable()
        {
            AllocateHistory();
            EnsureVisuals();
            ResetMeasurement();
            // Drop the throttle's cached string: EnsureVisuals may have just built a FRESH
            // TextMesh, whose text is empty, and the cache is what decides whether to assign.
            _readoutText = null;
        }

        void OnDisable()
        {
            if (_runtimeLineMaterial != null) Destroy(_runtimeLineMaterial);
            if (_runtimeTextMaterial != null) Destroy(_runtimeTextMaterial);
            _runtimeLineMaterial = null;
            _runtimeTextMaterial = null;
        }

        void OnValidate()
        {
            observedWaveCapacity = Mathf.Clamp(observedWaveCapacity,
                                               MinimumObservedWaveCapacity,
                                               MaximumObservedWaveCapacity);
            minimumWaveProminence = Mathf.Max(0f, minimumWaveProminence);
            reversalThreshold = Mathf.Max(0f, reversalThreshold);
            rollingWindowSeconds = Mathf.Max(1f, rollingWindowSeconds);
            if (Application.isPlaying) AllocateHistory();
        }

        void LateUpdate()
        {
            WaterVolume target = ResolveVolume();
            if (target == null || !target.TrySampleHeight(transform.position, out float surfaceY))
            {
                SetVisualsActive(false);
                return;
            }

            SetVisualsActive(true);
            float meanWaterY = target.VolumeCenter.y;
            CurrentSurfaceWorldY = surfaceY;
            CurrentElevation = surfaceY - meanWaterY;
            Measure(CurrentElevation);
            RetireExpiredWaves(Time.time);
            UpdateVisuals(meanWaterY);
        }

        WaterVolume ResolveVolume()
        {
            if (volume != null && volume.isActiveAndEnabled) return volume;
            if (!resolveContainingVolume) return null;
            volume = WaterVolume.BodyContaining(transform.position);
            return volume;
        }

        void Measure(float elevation)
        {
            if (!_sampleReady)
            {
                _sampleReady = true;
                _previousElevation = elevation;
                _candidateExtreme = elevation;
                _capturedCrest = elevation;
                _capturedTrough = elevation;
                return;
            }

            float delta = elevation - _previousElevation;
            _previousElevation = elevation;

            if (_rising)
            {
                _candidateExtreme = Mathf.Max(_candidateExtreme, elevation);
                if (delta >= -reversalThreshold) return;
                CaptureCrest(_candidateExtreme);
                _rising = false;
                _candidateExtreme = elevation;
                return;
            }

            _candidateExtreme = Mathf.Min(_candidateExtreme, elevation);
            if (delta <= reversalThreshold) return;
            CaptureTrough(_candidateExtreme);
            _rising = true;
            _candidateExtreme = elevation;
        }

        void CaptureCrest(float crest)
        {
            _capturedCrest = crest;
        }

        void CaptureTrough(float trough)
        {
            _capturedTrough = trough;
            if (_haveLeadingTrough)
            {
                float meanTrough = (_leadingTrough + trough) * 0.5f;
                float waveHeight = _capturedCrest - meanTrough;
                if (waveHeight >= minimumWaveProminence) RecordWave(waveHeight, Time.time);
            }
            _leadingTrough = trough;
            _haveLeadingTrough = true;
        }

        void RecordWave(float height, float time)
        {
            int capacity = _waveHeights.Length;
            int index = (_waveStart + _waveCount) % capacity;
            if (_waveCount == capacity)
            {
                _waveStart = (_waveStart + 1) % capacity;
                index = (_waveStart + _waveCount - 1) % capacity;
            }
            else
            {
                _waveCount++;
            }

            _waveHeights[index] = height;
            _waveTimes[index] = time;
            LastWaveHeight = height;
            CompletedWaveCount++;
            RecalculateStatistics();
        }

        void RetireExpiredWaves(float time)
        {
            float oldestAllowedTime = time - rollingWindowSeconds;
            bool changed = false;
            while (_waveCount > 0 && _waveTimes[_waveStart] < oldestAllowedTime)
            {
                _waveStart = (_waveStart + 1) % _waveHeights.Length;
                _waveCount--;
                changed = true;
            }
            if (changed) RecalculateStatistics();
        }

        void RecalculateStatistics()
        {
            RollingMaximumHeight = 0f;
            for (int i = 0; i < _waveCount; i++)
            {
                float height = _waveHeights[(_waveStart + i) % _waveHeights.Length];
                _sortScratch[i] = height;
                RollingMaximumHeight = Mathf.Max(RollingMaximumHeight, height);
            }

            if (_waveCount == 0)
            {
                ObservedSignificantHeight = 0f;
                return;
            }

            Array.Sort(_sortScratch, 0, _waveCount);
            int highestThirdCount = Mathf.Max(1, Mathf.CeilToInt(_waveCount / (float)HighestThirdDivisor));
            float sum = 0f;
            for (int i = _waveCount - highestThirdCount; i < _waveCount; i++) sum += _sortScratch[i];
            ObservedSignificantHeight = sum / highestThirdCount;
        }

        public void ResetMeasurement()
        {
            _sampleReady = false;
            _rising = false;
            _haveLeadingTrough = false;
            _waveStart = 0;
            _waveCount = 0;
            CompletedWaveCount = 0;
            LastWaveHeight = 0f;
            RollingMaximumHeight = 0f;
            ObservedSignificantHeight = 0f;
        }

        void AllocateHistory()
        {
            if (_waveHeights != null && _waveHeights.Length == observedWaveCapacity) return;
            _waveHeights = new float[observedWaveCapacity];
            _waveTimes = new float[observedWaveCapacity];
            _sortScratch = new float[observedWaveCapacity];
            ResetMeasurement();
        }

        void EnsureVisuals()
        {
            _visualRoot = FindOrCreateChild(transform, VisualRootName);
            _staff = CreateLine(_visualRoot, StaffName, staffColor, staffWidth);
            _surfaceMarker = CreateLine(_visualRoot, SurfaceMarkerName, surfaceColor, markerWidth);
            _crestMarker = CreateLine(_visualRoot, CrestMarkerName, crestColor, markerWidth);
            _troughMarker = CreateLine(_visualRoot, TroughMarkerName, troughColor, markerWidth);
            Transform readoutTransform = FindOrCreateChild(_visualRoot, ReadoutName);
            _readout = readoutTransform.GetComponent<TextMesh>();
            if (_readout == null) _readout = readoutTransform.gameObject.AddComponent<TextMesh>();
            _readout.anchor = TextAnchor.MiddleLeft;
            _readout.alignment = TextAlignment.Left;
            _readout.characterSize = 1f;
            _readout.fontSize = 48;
            _readout.color = Color.white;
            AssignReadoutMaterial();
        }

        LineRenderer CreateLine(Transform parent, string childName, Color color, float width)
        {
            Transform child = FindOrCreateChild(parent, childName);
            LineRenderer line = child.GetComponent<LineRenderer>();
            if (line == null) line = child.gameObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = LinePointCount;
            line.startWidth = width;
            line.endWidth = width;
            line.startColor = color;
            line.endColor = color;
            line.sharedMaterial = ResolveLineMaterial();
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            return line;
        }

        Material ResolveLineMaterial()
        {
            if (lineMaterial != null) return lineMaterial;
            if (_runtimeLineMaterial != null) return _runtimeLineMaterial;
            Shader shader = Shader.Find(DefaultLineShaderName);
            if (shader == null)
            {
                Debug.LogError($"WaterWaveGauge: shader '{DefaultLineShaderName}' was not found. Assign Line Material.", this);
                return null;
            }
            _runtimeLineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return _runtimeLineMaterial;
        }

        void AssignReadoutMaterial()
        {
            MeshRenderer renderer = _readout.GetComponent<MeshRenderer>();
            if (renderer == null || _readout.font == null) return;

            // Guarded exactly like ResolveLineMaterial (which always was): unguarded, a second
            // EnsureVisuals - a re-enable, a domain reload, any re-init - overwrote the field and
            // stranded the previous instance, since only OnDisable destroys what the field holds.
            if (_runtimeTextMaterial == null)
            {
                Shader shader = Shader.Find(GaugeTextShaderName);
                if (shader == null)
                {
                    Debug.LogError($"WaterWaveGauge: shader '{GaugeTextShaderName}' was not found.", this);
                    return;
                }

                _runtimeTextMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _runtimeTextMaterial.mainTexture = _readout.font.material.mainTexture;
            }

            renderer.sharedMaterial = _runtimeTextMaterial;
        }

        static Transform FindOrCreateChild(Transform parent, string childName)
        {
            Transform child = parent.Find(childName);
            if (child != null) return child;
            var childObject = new GameObject(childName);
            childObject.transform.SetParent(parent, false);
            return childObject.transform;
        }

        void UpdateVisuals(float meanWaterY)
        {
            Vector3 gaugeWorld = transform.position;
            _visualRoot.SetPositionAndRotation(new Vector3(gaugeWorld.x, meanWaterY, gaugeWorld.z), Quaternion.identity);
            _visualRoot.localScale = Vector3.one;
            SetVerticalLine(_staff, -metresBelowMean, metresAboveMean);
            SetMarker(_surfaceMarker, CurrentElevation);
            SetMarker(_crestMarker, _capturedCrest);
            SetMarker(_troughMarker, _capturedTrough);

            _readout.gameObject.SetActive(showReadout);
            if (!showReadout) return;
            RefreshReadoutText();
            _readout.transform.localPosition = new Vector3(LabelHorizontalOffset,
                                                           CurrentElevation + LabelVerticalOffset, 0f);
            _readout.transform.localScale = Vector3.one * labelScale;
            Camera facingCamera = labelCamera != null ? labelCamera : Camera.main;
            if (facingCamera != null)
                _readout.transform.rotation = Quaternion.LookRotation(
                    _readout.transform.position - facingCamera.transform.position,
                    facingCamera.transform.up);
        }

        // Rebuild the readout at ReadoutRefreshIntervalSeconds into a reused StringBuilder, and
        // hand it to the TextMesh only when the characters actually changed - assigning an
        // identical string still re-tessellates the glyph mesh. See the throttle field block.
        void RefreshReadoutText()
        {
            float now = Time.realtimeSinceStartup;
            if (_readoutText != null && now < _nextReadoutRefreshTime) return;
            _nextReadoutRefreshTime = now + ReadoutRefreshIntervalSeconds;

            _readoutBuilder.Clear();
            _readoutBuilder.AppendFormat("SURFACE {0:+0.0;-0.0;0.0} m\n", CurrentElevation);
            _readoutBuilder.AppendFormat("CREST   {0:+0.0;-0.0;0.0} m\n", _capturedCrest);
            _readoutBuilder.AppendFormat("TROUGH  {0:+0.0;-0.0;0.0} m\n", _capturedTrough);
            _readoutBuilder.AppendFormat("WAVE    {0:0.0} m\n", LastWaveHeight);
            _readoutBuilder.AppendFormat("MAX {0:0}s  {1:0.0} m\n", rollingWindowSeconds,
                                         RollingMaximumHeight);
            _readoutBuilder.AppendFormat("Hs OBS  {0:0.0} m", ObservedSignificantHeight);

            string text = _readoutBuilder.ToString();
            if (text == _readoutText) return;
            _readoutText = text;
            _readout.text = text;
        }

        static void SetVerticalLine(LineRenderer line, float bottom, float top)
        {
            line.SetPosition(0, new Vector3(0f, bottom, 0f));
            line.SetPosition(1, new Vector3(0f, top, 0f));
        }

        static void SetMarker(LineRenderer line, float elevation)
        {
            line.SetPosition(0, new Vector3(-HalfMarkerWidth, elevation, 0f));
            line.SetPosition(1, new Vector3(HalfMarkerWidth, elevation, 0f));
        }

        void SetVisualsActive(bool active)
        {
            if (_visualRoot != null && _visualRoot.gameObject.activeSelf != active)
                _visualRoot.gameObject.SetActive(active);
        }
    }
}
