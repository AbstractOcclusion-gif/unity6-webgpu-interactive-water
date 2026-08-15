using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterCurrentFeatureTests
    {
        const float FloatTolerance = 0.0001f;
        const float FirstCurrentSpeed = 2f;
        const float SecondCurrentSpeed = 3f;
        const float WaterfallPitchDegrees = 90f;
        const float OutsideVolumeCoordinate = 2f;
        const string VolumeHostName = "Current Test Water Volume";
        const string FirstFieldHostName = "First Current Test Field";
        const string SecondFieldHostName = "Second Current Test Field";

        static readonly Vector3 InsidePoint = Vector3.zero;
        static readonly Vector3 OutsidePoint = new Vector3(OutsideVolumeCoordinate, 0f, 0f);
        static readonly Vector3 ExpectedCombinedVelocity =
            Vector3.right * FirstCurrentSpeed + Vector3.down * SecondCurrentSpeed;
        static readonly Quaternion FirstFieldRotation =
            Quaternion.LookRotation(Vector3.right, Vector3.up);
        static readonly Quaternion SecondFieldRotation =
            Quaternion.Euler(WaterfallPitchDegrees, 0f, 0f);

        [Test]
        public void VolumeCurrent_WithoutFieldsReturnsZeroInsideItsFootprint()
        {
            GameObject volumeHost = CreateInactiveVolume(out WaterVolume volume);
            try
            {
                Assert.That(volume.SampleCurrent(InsidePoint, out Vector3 velocity), Is.True);
                AssertVector3(velocity, Vector3.zero);
            }
            finally
            {
                Object.DestroyImmediate(volumeHost);
            }
        }

        [Test]
        public void VolumeCurrent_AddsActiveWorldSpaceFieldsAndRejectsOutsidePoints()
        {
            GameObject volumeHost = CreateInactiveVolume(out WaterVolume volume);
            GameObject firstFieldHost = CreateConstantField(
                FirstFieldHostName, FirstFieldRotation, FirstCurrentSpeed,
                out WaterConstantCurrentField firstField);
            GameObject secondFieldHost = CreateConstantField(
                SecondFieldHostName, SecondFieldRotation, SecondCurrentSpeed,
                out WaterConstantCurrentField secondField);
            try
            {
                volume.currentFields = new WaterCurrentField[] { firstField, null, secondField };

                Assert.That(volume.SampleCurrent(InsidePoint, out Vector3 combinedVelocity), Is.True);
                AssertVector3(combinedVelocity, ExpectedCombinedVelocity);
                Assert.That(volume.SampleCurrent(OutsidePoint, out Vector3 outsideVelocity), Is.False);
                AssertVector3(outsideVelocity, Vector3.zero);
            }
            finally
            {
                Object.DestroyImmediate(secondFieldHost);
                Object.DestroyImmediate(firstFieldHost);
                Object.DestroyImmediate(volumeHost);
            }
        }

        static GameObject CreateInactiveVolume(out WaterVolume volume)
        {
            var host = new GameObject(VolumeHostName);
            host.SetActive(false);
            volume = host.AddComponent<WaterVolume>();
            volume.volumeExtent = Vector3.one;
            return host;
        }

        static GameObject CreateConstantField(string hostName, Quaternion rotation, float speed,
                                              out WaterConstantCurrentField field)
        {
            var host = new GameObject(hostName);
            host.transform.rotation = rotation;
            field = host.AddComponent<WaterConstantCurrentField>();
            field.speed = speed;
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
