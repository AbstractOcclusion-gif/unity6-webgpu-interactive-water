using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterStressScenarioControllerTests
    {
        const int ScenarioCount = 4;
        const int OneBodyCount = 1;
        const int FifteenBodyCount = 15;
        const int ThirtyBodyCount = 30;
        const int UnsupportedBodyCount = 12;
        const int InitialScenarioIndex = ScenarioCount - 1;
        const float RouteRadiusX = 70f;
        const float RouteRadiusZ = 58f;
        const float RouteHeight = 30f;
        const float RouteHeightVariation = 6f;
        const float RouteDurationSeconds = 24f;
        const string RootName = "Stress Controller Test";
        const string CameraName = "Stress Camera Test";
        const string TierNamePrefix = "Stress Tier Test ";

        readonly List<GameObject> _objects = new List<GameObject>();
        WaterStressScenarioController _controller;
        GameObject[] _tierRoots;

        [SetUp]
        public void SetUp()
        {
            GameObject root = CreateObject(RootName);
            _controller = root.AddComponent<WaterStressScenarioController>();
            Camera camera = CreateObject(CameraName).AddComponent<Camera>();
            _tierRoots = new GameObject[ScenarioCount];
            for (int i = 0; i < _tierRoots.Length; i++)
                _tierRoots[i] = CreateObject(TierNamePrefix + i);
            _controller.Configure(camera, _tierRoots, Vector3.zero,
                new Vector2(RouteRadiusX, RouteRadiusZ), RouteHeight, RouteHeightVariation,
                RouteDurationSeconds, InitialScenarioIndex);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void BodyCountScenarios_EnableCumulativeGroupsExactly()
        {
            _controller.SetBodyCount(OneBodyCount);
            Assert.That(_tierRoots[0].activeSelf, Is.True);
            Assert.That(_tierRoots[1].activeSelf, Is.False);
            Assert.That(_tierRoots[2].activeSelf, Is.False);
            Assert.That(_tierRoots[3].activeSelf, Is.False);

            _controller.SetBodyCount(FifteenBodyCount);
            Assert.That(_tierRoots[0].activeSelf, Is.True);
            Assert.That(_tierRoots[1].activeSelf, Is.True);
            Assert.That(_tierRoots[2].activeSelf, Is.True);
            Assert.That(_tierRoots[3].activeSelf, Is.False);

            _controller.SetBodyCount(ThirtyBodyCount);
            for (int i = 0; i < _tierRoots.Length; i++)
                Assert.That(_tierRoots[i].activeSelf, Is.True);
        }

        [Test]
        public void UnsupportedBodyCount_FailsFastWithoutChangingTheScenario()
        {
            int bodyCountBefore = _controller.ActiveBodyCount;

            Assert.That(() => _controller.SetBodyCount(UnsupportedBodyCount),
                        Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(_controller.ActiveBodyCount, Is.EqualTo(bodyCountBefore));
        }

        GameObject CreateObject(string name)
        {
            var gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }
    }
}
