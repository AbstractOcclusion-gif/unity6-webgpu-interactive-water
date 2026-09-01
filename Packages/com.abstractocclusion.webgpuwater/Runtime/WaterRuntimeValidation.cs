// WebGpuWater - allocation-free validation rules shared by diagnostics and EditMode tests.
using System;

namespace AbstractOcclusion.WebGpuWater
{
    [Flags]
    internal enum WaterRuntimeValidationFlags
    {
        None = 0,
        MissingRequiredWiring = 1 << 0,
        MissingCamera = 1 << 1,
        MissingPrimary = 1 << 2,
        MultiplePrimaries = 1 << 3,
        NonPositiveActivationDistance = 1 << 4,
        CullingDisabled = 1 << 5,
        RetainsPausedResources = 1 << 6,
        UnwiredConnection = 1 << 7,
    }

    internal static class WaterRuntimeValidation
    {
        internal static WaterRuntimeValidationFlags ValidateBody(
            WaterVolume body, int primaryCount)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));

            WaterRuntimeValidationFlags flags = WaterRuntimeValidationFlags.None;
            if (!body.HasRequiredWiringForDiagnostics)
                flags |= WaterRuntimeValidationFlags.MissingRequiredWiring;
            if (body.targetCamera == null)
                flags |= WaterRuntimeValidationFlags.MissingCamera;
            if (primaryCount == 0)
                flags |= WaterRuntimeValidationFlags.MissingPrimary;
            else if (primaryCount > 1)
                flags |= WaterRuntimeValidationFlags.MultiplePrimaries;
            if (body.EnableCulling && body.activationDistance <= 0f)
                flags |= WaterRuntimeValidationFlags.NonPositiveActivationDistance;
            if (!body.EnableCulling)
                flags |= WaterRuntimeValidationFlags.CullingDisabled;
            if (body.RetainsPausedResources)
                flags |= WaterRuntimeValidationFlags.RetainsPausedResources;
            return flags;
        }

        internal static WaterRuntimeValidationFlags ValidateConnection(WaterConnection connection)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            return connection.IsWired
                ? WaterRuntimeValidationFlags.None
                : WaterRuntimeValidationFlags.UnwiredConnection;
        }
    }
}
