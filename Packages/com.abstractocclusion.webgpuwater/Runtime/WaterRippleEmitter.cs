// WebGpuWater - ripple spawner (Unity 6 / URP port)
// Spawns ripples into whichever water body contains the emit point. Call Emit() on demand
// (footstep animation events, projectile impacts, a splash) or enable "emit on move" for a
// continuous wake as the object travels (boats, a swimmer). Built on WaterVolume's world-space
// façade, so it needs no reference to a specific body.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public class WaterRippleEmitter : MonoBehaviour
    {
        const float MinMoveSpacing = 1e-3f; // guards the move test against a zero/near-zero spacing

        [Tooltip("Local-space offset from this object's origin where ripples are spawned.")]
        [SerializeField] Vector3 emitPoint = Vector3.zero;

        [Tooltip("Ripple radius in world units (volume-scale independent).")]
        [SerializeField] float rippleRadius = 0.05f;

        [Tooltip("Ripple height in world units (volume-scale independent).")]
        [SerializeField] float rippleStrength = 0.02f;

        [Tooltip("Only spawn when the emit point is within Surface Band of the water surface, so " +
                 "an airborne or deep object doesn't ripple. Turn off to always spawn.")]
        [SerializeField] bool requireNearSurface = true;

        [Tooltip("Vertical distance (world units) from the surface within which the emit point " +
                 "counts as 'at the waterline'.")]
        [SerializeField] float surfaceBand = 0.1f;

        [Tooltip("Spawn a wake automatically as the object moves (boats, a swimmer). Leave off " +
                 "to emit only via Emit() calls.")]
        [SerializeField] bool emitOnMove = false;

        [Tooltip("World distance the emit point must travel between automatic wake ripples.")]
        [SerializeField] float moveSpacing = 0.1f;

        Vector3 _lastEmitPosition;

        void OnEnable() => _lastEmitPosition = EmitWorldPoint();

        /// <summary>Spawn one ripple now at the emit point, on whichever body contains it.
        /// Skipped if Require Near Surface is on and the point isn't at the waterline.</summary>
        public void Emit()
        {
            Vector3 point = EmitWorldPoint();
            if (requireNearSurface && !IsNearSurface(point)) return;
            WaterVolume.TrySpawnRippleAt(point, rippleRadius, rippleStrength);
        }

        void Update()
        {
            if (!emitOnMove) return;

            Vector3 point = EmitWorldPoint();
            float spacing = Mathf.Max(MinMoveSpacing, moveSpacing);
            if ((point - _lastEmitPosition).sqrMagnitude < spacing * spacing) return;

            _lastEmitPosition = point;
            Emit();
        }

        Vector3 EmitWorldPoint() => transform.TransformPoint(emitPoint);

        bool IsNearSurface(Vector3 point)
        {
            if (!WaterVolume.TrySampleHeightAt(point, out float surfaceY)) return false;
            return Mathf.Abs(point.y - surfaceY) <= surfaceBand;
        }
    }
}
