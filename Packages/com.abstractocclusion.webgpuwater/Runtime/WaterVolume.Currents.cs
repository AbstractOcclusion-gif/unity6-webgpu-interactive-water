// WebGpuWater - WaterVolume current composition and public current query.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        [Tooltip("Additive current sources for this body. Empty preserves still-water behaviour. " +
                 "Each source returns full world-space velocity, so fields may also describe falls.")]
        [SerializeField] internal WaterCurrentField[] currentFields = Array.Empty<WaterCurrentField>();

        /// <summary>
        /// Samples this body's authored physical current in world metres per second. Returns true with
        /// zero velocity inside a body that has no active fields, and false outside its footprint.
        /// </summary>
        public bool SampleCurrent(Vector3 worldPoint, out Vector3 worldVelocity)
        {
            worldVelocity = Vector3.zero;
            if (!WaterSurfaceKinematics.IsFinite(worldPoint)) return false;

            Vector3 surfaceProbe = new Vector3(worldPoint.x, VolumeCenter.y, worldPoint.z);
            if (!QueryPoolXZ(surfaceProbe, out _, out _)) return false;

            worldVelocity = SampleCurrentFields(worldPoint);
            return true;
        }

        // The containing-body test has already run on surface-query paths. Keeping composition here
        // avoids a second transform/footprint calculation for every buoyancy probe.
        internal Vector3 SampleCurrentFields(Vector3 worldPoint)
        {
            Vector3 worldVelocity = Vector3.zero;
            if (currentFields == null) return worldVelocity;

            for (int i = 0; i < currentFields.Length; i++)
            {
                WaterCurrentField field = currentFields[i];
                if (field == null || !field.SampleCurrent(worldPoint, out Vector3 fieldVelocity))
                    continue;

                worldVelocity += fieldVelocity;
            }

            return worldVelocity;
        }
    }

    internal readonly struct WaterRiverMouthOutflow
    {
        internal const int MaximumShaderOutflows = 4;

        const float MinimumExtentMeters = 0.01f;
        const float LateralFullWeight = 0.65f;

        internal readonly Vector3 Origin;
        internal readonly Vector3 Downstream;
        internal readonly Vector3 Right;
        internal readonly float HalfWidthMeters;
        internal readonly float LengthMeters;
        internal readonly float SpreadPerMeter;
        internal readonly float SpeedMetersPerSecond;
        internal readonly float FoamCoverage;
        internal readonly float FoamLengthMeters;
        internal readonly float CurrentStrength;
        internal readonly float FoamStrength;
        internal readonly float FoamLongitudinalMeters;
        internal readonly float FoamLateralSpeedMetersPerSecond;
        internal readonly float FoamPatternSizeMeters;
        internal readonly float FoamEdgeFeather;
        internal readonly float FoamCoreCut;

        internal WaterRiverMouthOutflow(
            Vector3 origin, Vector3 downstream, Vector3 right,
            float halfWidthMeters, float lengthMeters, float spreadPerMeter,
            float speedMetersPerSecond, float foamCoverage, float foamLengthMeters,
            float currentStrength, float foamStrength, float foamLongitudinalMeters,
            float foamLateralSpeedMetersPerSecond, float foamPatternSizeMeters,
            float foamEdgeFeather, float foamCoreCut)
        {
            Origin = origin;
            Downstream = downstream.normalized;
            Right = right.normalized;
            HalfWidthMeters = halfWidthMeters;
            LengthMeters = lengthMeters;
            SpreadPerMeter = spreadPerMeter;
            SpeedMetersPerSecond = speedMetersPerSecond;
            FoamCoverage = foamCoverage;
            FoamLengthMeters = foamLengthMeters;
            CurrentStrength = currentStrength;
            FoamStrength = foamStrength;
            FoamLongitudinalMeters = foamLongitudinalMeters;
            FoamLateralSpeedMetersPerSecond = foamLateralSpeedMetersPerSecond;
            FoamPatternSizeMeters = foamPatternSizeMeters;
            FoamEdgeFeather = foamEdgeFeather;
            FoamCoreCut = foamCoreCut;
        }

        internal bool IsActive =>
            WaterSurfaceKinematics.IsFinite(Origin) &&
            WaterSurfaceKinematics.IsFinite(Downstream) &&
            WaterSurfaceKinematics.IsFinite(Right) &&
            Downstream.sqrMagnitude > 0f && Right.sqrMagnitude > 0f &&
            float.IsFinite(HalfWidthMeters) && HalfWidthMeters >= MinimumExtentMeters &&
            float.IsFinite(LengthMeters) && LengthMeters >= MinimumExtentMeters &&
            float.IsFinite(SpreadPerMeter) && SpreadPerMeter >= 0f &&
            float.IsFinite(SpeedMetersPerSecond) && SpeedMetersPerSecond >= 0f &&
            float.IsFinite(FoamCoverage) && FoamCoverage >= 0f &&
            float.IsFinite(FoamLengthMeters) && FoamLengthMeters >= MinimumExtentMeters &&
            float.IsFinite(CurrentStrength) && CurrentStrength >= 0f &&
            float.IsFinite(FoamStrength) && FoamStrength >= 0f &&
            float.IsFinite(FoamLongitudinalMeters) && FoamLongitudinalMeters >= 0f &&
            float.IsFinite(FoamLateralSpeedMetersPerSecond) &&
            float.IsFinite(FoamPatternSizeMeters) &&
            FoamPatternSizeMeters >= MinimumExtentMeters &&
            float.IsFinite(FoamEdgeFeather) && FoamEdgeFeather >= 0f &&
            float.IsFinite(FoamCoreCut) && FoamCoreCut >= 0f;

        internal bool TrySampleCurrent(Vector3 worldPoint, out Vector3 worldVelocity)
        {
            worldVelocity = Vector3.zero;
            if (!IsActive || CurrentStrength <= 0f || SpeedMetersPerSecond <= 0f) return false;

            Vector3 offset = worldPoint - Origin;
            float downstreamDistance = Vector3.Dot(offset, Downstream);
            if (downstreamDistance < 0f || downstreamDistance >= LengthMeters) return false;

            float localHalfWidth = HalfWidthMeters + SpreadPerMeter * downstreamDistance;
            float lateralDistance = Mathf.Abs(Vector3.Dot(offset, Right));
            if (lateralDistance >= localHalfWidth) return false;

            float downstreamWeight = 1f - Mathf.SmoothStep(
                0f, 1f, downstreamDistance / LengthMeters);
            float lateralRatio = lateralDistance / Mathf.Max(localHalfWidth, MinimumExtentMeters);
            float lateralWeight = 1f - Mathf.SmoothStep(
                LateralFullWeight, 1f, lateralRatio);
            float speed = SpeedMetersPerSecond * CurrentStrength *
                          downstreamWeight * lateralWeight;
            worldVelocity = Downstream * speed;
            return WaterSurfaceKinematics.IsFinite(worldVelocity) && speed > 0f;
        }

        internal void WriteShaderData(
            Vector4[] origins, Vector4[] directions, Vector4[] parameters,
            Vector4[] foamFrames, Vector4[] foamAppearances, int index)
        {
            origins[index] = new Vector4(Origin.x, Origin.z, HalfWidthMeters, FoamCoverage);
            directions[index] = new Vector4(
                Downstream.x, Downstream.z, LengthMeters, SpreadPerMeter);
            parameters[index] = new Vector4(
                SpeedMetersPerSecond, CurrentStrength, FoamStrength, FoamLengthMeters);
            foamFrames[index] = new Vector4(
                Right.x, Right.z, FoamLongitudinalMeters,
                FoamLateralSpeedMetersPerSecond);
            foamAppearances[index] = new Vector4(
                FoamPatternSizeMeters, FoamEdgeFeather, FoamCoreCut, 0f);
        }
    }

    public partial class WaterVolume
    {
        readonly List<WaterRiver> _riverMouthOutflows = new();
        readonly Vector4[] _riverMouthOutflowOrigins =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _riverMouthOutflowDirections =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _riverMouthOutflowParameters =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _riverMouthOutflowFoamFrames =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        readonly Vector4[] _riverMouthOutflowFoamAppearances =
            new Vector4[WaterRiverMouthOutflow.MaximumShaderOutflows];
        int _riverMouthOutflowFrame = -1;
        int _riverMouthOutflowCount;

        internal Vector4[] RiverMouthOutflowOrigins => _riverMouthOutflowOrigins;
        internal Vector4[] RiverMouthOutflowDirections => _riverMouthOutflowDirections;
        internal Vector4[] RiverMouthOutflowParameters => _riverMouthOutflowParameters;
        internal Vector4[] RiverMouthOutflowFoamFrames => _riverMouthOutflowFoamFrames;
        internal Vector4[] RiverMouthOutflowFoamAppearances =>
            _riverMouthOutflowFoamAppearances;
        internal int RiverMouthOutflowCount
        {
            get
            {
                RefreshRiverMouthOutflowShaderData();
                return _riverMouthOutflowCount;
            }
        }

        internal void RegisterRiverMouthOutflow(WaterRiver river)
        {
            if (river == null) throw new ArgumentNullException(nameof(river));
            if (!_riverMouthOutflows.Contains(river)) _riverMouthOutflows.Add(river);
            InvalidateRiverMouthOutflows();
        }

        internal void UnregisterRiverMouthOutflow(WaterRiver river)
        {
            if (river != null) _riverMouthOutflows.Remove(river);
            InvalidateRiverMouthOutflows();
        }

        internal void InvalidateRiverMouthOutflows() => _riverMouthOutflowFrame = -1;

        void RefreshRiverMouthOutflowShaderData()
        {
            if (Application.isPlaying && _riverMouthOutflowFrame == Time.frameCount) return;

            Array.Clear(_riverMouthOutflowOrigins, 0, _riverMouthOutflowOrigins.Length);
            Array.Clear(_riverMouthOutflowDirections, 0, _riverMouthOutflowDirections.Length);
            Array.Clear(_riverMouthOutflowParameters, 0, _riverMouthOutflowParameters.Length);
            Array.Clear(
                _riverMouthOutflowFoamFrames, 0, _riverMouthOutflowFoamFrames.Length);
            Array.Clear(
                _riverMouthOutflowFoamAppearances, 0,
                _riverMouthOutflowFoamAppearances.Length);
            _riverMouthOutflowCount = 0;

            for (int index = 0;
                 index < _riverMouthOutflows.Count &&
                 _riverMouthOutflowCount < WaterRiverMouthOutflow.MaximumShaderOutflows;
                 index++)
            {
                WaterRiver river = _riverMouthOutflows[index];
                if (river == null || !river.isActiveAndEnabled ||
                    !river.TryBuildMouthOutflow(out WaterRiverMouthOutflow outflow))
                    continue;

                outflow.WriteShaderData(
                    _riverMouthOutflowOrigins, _riverMouthOutflowDirections,
                    _riverMouthOutflowParameters, _riverMouthOutflowFoamFrames,
                    _riverMouthOutflowFoamAppearances, _riverMouthOutflowCount);
                _riverMouthOutflowCount++;
            }

            _riverMouthOutflowFrame = Time.frameCount;
        }
    }
}
