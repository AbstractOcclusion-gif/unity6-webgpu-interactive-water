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
        // Set when targetCamera names a camera that cannot render (destroyed prefab rig, disabled
        // object, disabled Camera) and nothing else is wired to cover for it. This is the exact
        // shape of the failure the WaterEye rewrite exists to end: the reference is non-null, so
        // the old `targetCamera == null` check reported a clean scene while the underwater fog was
        // dead. Always accompanied by MissingCamera; it is the one that says WHY.
        StaleLegacyCamera = 1 << 8,
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
            // "No eye" is now about what will actually RENDER, not about a field being filled:
            // a live WaterEye, or a legacy targetCamera that is itself active and enabled.
            // ResolveWired deliberately stops short of the Camera.main fallback, so a scene that
            // only works by accident of the MainCamera tag still reports as unwired - which is
            // what this flag has always meant.
            if (WaterEye.ResolveWired() == null)
                flags |= WaterRuntimeValidationFlags.MissingCamera;
            if (WaterEye.HasStaleLegacyCamera(body))
                flags |= WaterRuntimeValidationFlags.StaleLegacyCamera;
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
