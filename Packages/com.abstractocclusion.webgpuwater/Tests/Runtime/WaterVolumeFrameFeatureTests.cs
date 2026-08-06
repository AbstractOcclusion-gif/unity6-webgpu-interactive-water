using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterVolumeFrameFeatureTests
    {
        const float FloatTolerance = 0.0001f;
        const float OutsideFootprintOffset = 0.01f;
        const float RayOriginHeight = 10f;
        const float OutsideRayOffset = 1f;
        const string TestVolumeName = "Water Volume Frame Test";
        static readonly Vector3 VolumePosition = new Vector3(10f, 2f, -4f);
        static readonly Vector3 VolumeExtent = new Vector3(4f, 2f, 6f);
        static readonly Vector3 PoolPoint = new Vector3(0.25f, -0.5f, 0.75f);
        static readonly Quaternion VolumeRotation = Quaternion.Euler(0f, 35f, 0f);

        [Test]
        public void PoolAndWorldFrames_RoundTripWithNonUniformExtentAndRotation()
        {
            GameObject host = CreateInactiveVolume(out WaterVolume volume);
            try
            {
                volume.transform.SetPositionAndRotation(VolumePosition, VolumeRotation);
                volume.volumeExtent = VolumeExtent;

                Vector3 worldPoint = volume.PoolToWorld(PoolPoint);
                Vector3 returnedPoolPoint = volume.WorldToPool(worldPoint);

                AssertVector3(returnedPoolPoint, PoolPoint);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void WorldToPoolXZ_RejectsOutsidePointsAndKeepsSurfaceCoordinates()
        {
            GameObject host = CreateInactiveVolume(out WaterVolume volume);
            try
            {
                volume.volumeExtent = Vector3.one;

                Assert.That(volume.WorldToPoolXZ(new Vector3(1f, 0f, -1f), out float poolX, out float poolZ), Is.True);
                Assert.That(poolX, Is.EqualTo(1f));
                Assert.That(poolZ, Is.EqualTo(-1f));
                Assert.That(volume.WorldToPoolXZ(new Vector3(1f + OutsideFootprintOffset, 0f, 0f), out _, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void RaycastSurface_OnlyAcceptsForwardRaysInsideTheFootprint()
        {
            GameObject host = CreateInactiveVolume(out WaterVolume volume);
            try
            {
                volume.transform.position = VolumePosition;
                volume.volumeExtent = VolumeExtent;
                Ray insideRay = new Ray(VolumePosition + Vector3.up * RayOriginHeight, Vector3.down);
                Ray outsideRay = new Ray(VolumePosition + Vector3.right * (VolumeExtent.x + OutsideRayOffset) + Vector3.up, Vector3.down);
                Ray parallelRay = new Ray(VolumePosition + Vector3.up, Vector3.right);

                Assert.That(volume.TryRaycastSurface(insideRay, out Vector3 hit), Is.True);
                AssertVector3(hit, VolumePosition);
                Assert.That(volume.TryRaycastSurface(outsideRay, out _), Is.False);
                Assert.That(volume.TryRaycastSurface(parallelRay, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        static GameObject CreateInactiveVolume(out WaterVolume volume)
        {
            var host = new GameObject(TestVolumeName);
            host.SetActive(false);
            volume = host.AddComponent<WaterVolume>();
            return host;
        }

        static void AssertVector3(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(FloatTolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(FloatTolerance));
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(FloatTolerance));
        }
    }
}
