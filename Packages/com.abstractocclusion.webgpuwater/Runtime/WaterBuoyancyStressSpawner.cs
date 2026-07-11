// WebGpuWater - buoyancy stress-test spawner.
//
// Spawns a parametric grid of buoyant cubes over the water at play start so the probe-buoyancy budget can
// be measured with the metrics overlay. Every knob is a field, so the grid can be dialled from a handful to
// hundreds and re-played. Probe tier only (WaterBuoyancy); the mesh-slicing hull is a later tier.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [AddComponentMenu("AbstractOcclusion/Water/Buoyancy Stress Spawner")]
    [DisallowMultipleComponent]
    public sealed class WaterBuoyancyStressSpawner : MonoBehaviour
    {
        const float MinSpacing = 0.1f;
        const int MinSamplesPerAxis = 1;
        const int MaxSamplesPerAxis = 3;

        [Header("Grid")]
        [SerializeField, Min(1)] int gridX = 8;
        [SerializeField, Min(1)] int gridZ = 8;                       // 8x8 = 64 floaters by default
        [SerializeField] float spacing = 2.5f;
        [SerializeField] float dropHeight = 1.5f;                     // spawn this far above the surface
        [SerializeField] Vector3 floaterSize = new Vector3(0.8f, 0.8f, 0.8f);
        [SerializeField, Min(0.01f)] float mass = 20f;

        [Header("Buoyancy tuning (applied to every floater)")]
        [SerializeField] float buoyancy = 2.5f;
        [SerializeField, Range(MinSamplesPerAxis, MaxSamplesPerAxis)] int samplesPerAxis = 2;
        [SerializeField] float objectWidth = 0f;
        [SerializeField] bool surfaceRelativeDrag = false;

        [Header("Extra per-floater load (heavier stress)")]
        [Tooltip("Add a ripple emitter to every floater (each stamps the sim - much heavier).")]
        [SerializeField] bool addRippleInteractor = false;
        [SerializeField] bool addSplash = false;

        [SerializeField] internal WaterMetricsOverlay overlay;

        void Start()
        {
            int count = SpawnGrid();
            if (overlay != null) overlay.SetFloaterCount(count);
        }

        int SpawnGrid()
        {
            float halfX = (gridX - 1) * spacing * 0.5f;
            float halfZ = (gridZ - 1) * spacing * 0.5f;
            float step = Mathf.Max(MinSpacing, spacing);

            int index = 0;
            for (int ix = 0; ix < gridX; ix++)
                for (int iz = 0; iz < gridZ; iz++)
                {
                    Vector3 local = new Vector3(ix * step - halfX, dropHeight, iz * step - halfZ);
                    CreateFloater(transform.position + local, index++);
                }
            return index;
        }

        void CreateFloater(Vector3 position, int index)
        {
            var floater = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floater.name = $"StressFloater_{index}";
            floater.transform.SetParent(transform, worldPositionStays: true);
            floater.transform.position = position;
            floater.transform.localScale = floaterSize;

            var body = floater.AddComponent<Rigidbody>();
            body.mass = mass;

            var buoyant = floater.AddComponent<WaterBuoyancy>();
            buoyant.buoyancy = buoyancy;
            buoyant.samplesPerAxis = Mathf.Clamp(samplesPerAxis, MinSamplesPerAxis, MaxSamplesPerAxis);
            buoyant.objectWidth = objectWidth;
            buoyant.surfaceRelativeDrag = surfaceRelativeDrag;

            floater.AddComponent<WaterMembership>();
            if (addRippleInteractor) floater.AddComponent<WaterInteractable>();
            if (addSplash) floater.AddComponent<WaterSplash>();
        }
    }
}
