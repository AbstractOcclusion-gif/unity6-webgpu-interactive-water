using NUnit.Framework;
using System.Reflection;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class ClipmapWindowResizeTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(1f, 1)]
        [TestCase(4f, 1)]
        [TestCase(4f, 6)]
        [TestCase(12f, 14)]
        [TestCase(32f, 30)]
        public void PatchContainsEntireHoleAtWorstCaseGridSnap(float halfSize, int holeCells)
        {
            float cell = WaterVolume.ResolveClipmapBaseCell(halfSize, holeCells);
            // Snapping to a 2-cell grid can move the center by one cell per axis.
            Assert.That(holeCells * cell + cell, Is.LessThan(halfSize));
        }

        [Test]
        public void ResizeAndDistantFocusKeepOceanHoleInsideActualPatch()
        {
            var root = new GameObject("Clipmap resize regression");
            root.SetActive(false);
            var authoredFocus = new GameObject("Old camera focus");
            try
            {
                var body = root.AddComponent<WaterVolume>();
                root.transform.SetPositionAndRotation(new Vector3(10f, 2f, -8f),
                    Quaternion.Euler(0f, 37f, 0f));
                authoredFocus.transform.position = new Vector3(-500f, 20f, 800f);
                body.simWindowFocus = authoredFocus.transform;
                var module = new SimWindowModule(body);
                module.Initialize(default);
                typeof(WaterVolume).GetField("_simWindowModule", PrivateInstance).SetValue(body, module);
                var window = module.SimWindow;
                var halfProperty = typeof(WaterSimWindow).GetProperty("HalfSize", PrivateInstance);
                var centerProperty = typeof(WaterSimWindow).GetProperty("Center", PrivateInstance);
                var baseCellProperty = typeof(WaterVolume).GetProperty("ClipmapBaseCell", PrivateInstance);
                var holeProperty = typeof(WaterVolume).GetProperty("ClipmapHoleHalfCells", PrivateInstance);
                var countProperty = typeof(WaterVolume).GetProperty("ClipmapLevelCount", PrivateInstance);
                var snapMethod = typeof(WaterVolume).GetMethod("ClipmapLevelSnappedCenter", PrivateInstance);
                int originalCount = 0;
                foreach (float half in new[] { 32f, 4f, 12f, 32f })
                {
                    halfProperty.SetValue(window, half);
                    Vector3 patch = root.transform.position + root.transform.rotation
                        * new Vector3(81.73f, 0f, -49.17f);
                    centerProperty.SetValue(window, patch);
                    float cell = (float)baseCellProperty.GetValue(body);
                    int holeCells = (int)holeProperty.GetValue(body);
                    Vector3 holeCenter = (Vector3)snapMethod.Invoke(body, new object[] { cell });
                    Vector3 offset = Quaternion.Inverse(root.transform.rotation) * (holeCenter - patch);
                    Assert.That(Mathf.Abs(offset.x) + holeCells * cell, Is.LessThan(half));
                    Assert.That(Mathf.Abs(offset.z) + holeCells * cell, Is.LessThan(half));
                    int count = (int)countProperty.GetValue(body);
                    if (originalCount == 0) originalCount = count;
                    if (half == 4f) Assert.That(count, Is.GreaterThan(originalCount));
                    if (half == 32f) Assert.That(count, Is.EqualTo(originalCount));
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(authoredFocus);
            }
        }
    }
}
