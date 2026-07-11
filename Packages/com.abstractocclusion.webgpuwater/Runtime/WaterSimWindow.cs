// WebGpuWater - camera-following sim window for large bodies.
// Extracted from WaterVolume: owns the scrolling window's texel-snapped centre and
// tracks the camera - project it onto the surface plane, clamp into the footprint,
// snap to the sim-texel lattice, and scroll the sim state by the integer texel delta
// so ripples stay pinned in world space.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterSimWindow
    {
        readonly WaterVolume _body;

        int _cellX, _cellZ; // window centre as integer texel indices in the volume's local frame
        bool _centerInit;

        /// <summary>World centre of the window, on the surface plane, texel-snapped.
        /// Defaults to the volume centre until the first Track().</summary>
        internal Vector3 Center { get; private set; }

        internal WaterSimWindow(WaterVolume body)
        {
            _body = body ?? throw new System.ArgumentNullException(nameof(body));
            Center = body.VolumeCenter;
        }

        // Offset in the follow target's horizontal frame (x = right, y = forward), projected onto the
        // surface plane. Degenerate axes (target looking straight down) contribute nothing.
        static Vector3 HorizontalOffset(Transform follow, Vector3 up, Vector2 offset)
        {
            if (offset == Vector2.zero) return Vector3.zero;
            Vector3 right = FlattenAndNormalize(follow.right, up);
            Vector3 forward = FlattenAndNormalize(follow.forward, up);
            return right * offset.x + forward * offset.y;
        }

        static Vector3 FlattenAndNormalize(Vector3 v, Vector3 up)
        {
            Vector3 flat = Vector3.ProjectOnPlane(v, up);
            return flat.sqrMagnitude > 1e-6f ? flat.normalized : Vector3.zero;
        }

        // World -> sim-window normalised coords (.xz in [-1,1] inside the window). Shares the
        // volume rotation; centred on the scrolling window centre.
        internal Vector3 WorldToSim(Vector3 world)
        {
            Vector3 e = _body.SimHalfExtent;
            Vector3 local = Quaternion.Inverse(_body.VolumeRotation) * (world - Center);
            return new Vector3(local.x / e.x, local.y / e.y, local.z / e.z);
        }

        // Move the window to the camera. Called once per simulated frame (and once at init
        // to prime the centre before the first publish).
        internal void Track()
        {
            WaterSimulation sim = _body.Simulation;
            if (sim == null) return;

            // Follow an explicit focus (e.g. the boat) when set, otherwise the target camera.
            Transform focus = _body.simWindowFocus;
            Camera cam = _body.targetCamera;
            if (focus == null && cam == null) return;
            Transform follow = focus != null ? focus : cam.transform;

            // Follow point projected onto the surface plane (through the volume centre, along up), then an
            // optional lead/lateral offset in the follow target's own horizontal frame.
            Vector3 up = _body.VolumeUp;
            Vector3 followPos = follow.position;
            Vector3 onPlane = followPos - Vector3.Dot(followPos - _body.VolumeCenter, up) * up;
            onPlane += HorizontalOffset(follow, up, _body.simWindowOffset);

            // Work in the volume's local horizontal frame so the lattice is axis-aligned.
            Vector3 local = Quaternion.Inverse(_body.VolumeRotation) * (onPlane - _body.VolumeCenter);

            float texel = 2f * _body.simWindowMeters / _body.SimResolution;
            // Clamp the window centre so it stays inside the footprint (or may overhang the edge). An
            // unbounded ocean has no footprint edge - its surface spans everywhere - so the window must
            // scroll FREELY with the camera; clamping it to the bounded extent pins it at the edge and it
            // stops following the boat once it drives past ~extent metres.
            float clampedX, clampedZ;
            if (_body.IsOceanClipmap)
            {
                clampedX = local.x;
                clampedZ = local.z;
            }
            else
            {
                Vector3 e = _body.VolumeExtentSafe;
                float limitX = _body.clampWindowToShore ? Mathf.Max(0f, e.x - _body.simWindowMeters) : e.x;
                float limitZ = _body.clampWindowToShore ? Mathf.Max(0f, e.z - _body.simWindowMeters) : e.z;
                clampedX = Mathf.Clamp(local.x, -limitX, limitX);
                clampedZ = Mathf.Clamp(local.z, -limitZ, limitZ);
            }

            int cellX = Mathf.RoundToInt(clampedX / texel);
            int cellZ = Mathf.RoundToInt(clampedZ / texel);

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
                    // Local x -> sim texel u, local z -> sim texel v. The kernel does
                    // Dst[p] = Src[p - offset]; offsetting by -delta keeps world features fixed
                    // (see WaterSimulation.Scroll).
                    sim.Scroll(-dx, -dz);
                    _cellX = cellX; _cellZ = cellZ;
                }
            }

            Center = _body.VolumeCenter + _body.VolumeRotation * new Vector3(_cellX * texel, 0f, _cellZ * texel);
        }
    }
}
