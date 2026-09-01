using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRuntimeRelevanceFeatureTests
    {
        readonly List<GameObject> _objects = new List<GameObject>();
        Camera _camera;

        [SetUp]
        public void SetUp()
        {
            WaterVolume.Bodies.Clear();
            WaterRuntimeRelevance.ResetStaticState();
            GameObject cameraObject = CreateObject("Relevance Camera");
            _camera = cameraObject.AddComponent<Camera>();
            _camera.fieldOfView = 60f;
            _camera.aspect = 1f;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 1000f;
        }

        [TearDown]
        public void TearDown()
        {
            WaterVolume.Bodies.Clear();
            WaterRuntimeRelevance.ResetStaticState();
            for (int i = _objects.Count - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void NearestBoundsDistance_UsesNearestPointInsteadOfCenter()
        {
            Bounds bounds = new Bounds(new Vector3(10f, 0f, 0f), new Vector3(4f, 2f, 2f));
            float distanceSquared = WaterRuntimeRelevance.NearestBoundsDistanceSquared(
                bounds, Vector3.zero);

            Assert.That(distanceSquared, Is.EqualTo(64f).Within(1e-5f));
        }

        [Test]
        public void EqualRelevance_UsesStableRegistrationOrder()
        {
            WaterVolume first = CreateBody("First", new Vector3(0f, 0f, 10f), Vector3.one);
            WaterVolume second = CreateBody("Second", new Vector3(0f, 0f, 10f), Vector3.one);

            IReadOnlyList<WaterRuntimeBodySnapshot> snapshot =
                WaterRuntimeRelevance.RebuildForTests(_camera, 1, 0);

            Assert.That(snapshot[0].Body, Is.SameAs(first));
            Assert.That(snapshot[0].SimulationGranted, Is.True);
            Assert.That(snapshot[1].Body, Is.SameAs(second));
            Assert.That(snapshot[1].SimulationGranted, Is.False);
        }

        [Test]
        public void ImportancePin_OutranksOrdinaryVisibleBodyDeterministically()
        {
            WaterVolume near = CreateBody("Near", new Vector3(0f, 0f, 5f), Vector3.one);
            WaterVolume pinned = CreateBody("Pinned", new Vector3(0f, 0f, 20f), Vector3.one);
            pinned.RuntimeImportancePin = true;

            IReadOnlyList<WaterRuntimeBodySnapshot> snapshot =
                WaterRuntimeRelevance.RebuildForTests(_camera, 1, 0);

            Assert.That(snapshot[0].Body, Is.SameAs(pinned));
            Assert.That(snapshot[0].SimulationGranted, Is.True);
            Assert.That(Find(snapshot, near).SimulationGranted, Is.False);
        }

        [Test]
        public void SimulationBudget_GrantsOnlyTopEligibleBodies()
        {
            WaterVolume first = CreateBody("First", new Vector3(0f, 0f, 5f), Vector3.one);
            WaterVolume second = CreateBody("Second", new Vector3(0f, 0f, 10f), Vector3.one);
            WaterVolume third = CreateBody("Third", new Vector3(0f, 0f, 15f), Vector3.one);

            IReadOnlyList<WaterRuntimeBodySnapshot> snapshot =
                WaterRuntimeRelevance.RebuildForTests(_camera, 2, 0);

            Assert.That(Find(snapshot, first).SimulationGranted, Is.True);
            Assert.That(Find(snapshot, second).SimulationGranted, Is.True);
            Assert.That(Find(snapshot, third).SimulationGranted, Is.False);
        }

        [Test]
        public void CausticBudget_UsesTheSameRankingAndExactLimit()
        {
            var states = new List<WaterRuntimeBodySnapshot>
            {
                Candidate(registrationOrder: 2, distance: 30f),
                Candidate(registrationOrder: 0, distance: 10f),
                Candidate(registrationOrder: 1, distance: 20f),
            };

            WaterRuntimeRelevance.ApplyGrantsForTests(states, simulationBudget: 0,
                                                      causticBudget: 2);

            Assert.That(states[0].RegistrationOrder, Is.EqualTo(0));
            Assert.That(states[0].CausticGranted, Is.True);
            Assert.That(states[1].RegistrationOrder, Is.EqualTo(1));
            Assert.That(states[1].CausticGranted, Is.True);
            Assert.That(states[2].CausticGranted, Is.False);
        }

        [Test]
        public void Validation_ReportsOwnershipAndResourceRisksWithoutMutation()
        {
            WaterVolume body = CreateBody("Invalid", new Vector3(0f, 0f, 10f), Vector3.one);
            body.EnableCulling = false;

            WaterRuntimeValidationFlags flags = WaterRuntimeValidation.ValidateBody(
                body, primaryCount: 0);

            Assert.That(flags.HasFlag(WaterRuntimeValidationFlags.MissingRequiredWiring), Is.True);
            Assert.That(flags.HasFlag(WaterRuntimeValidationFlags.MissingCamera), Is.True);
            Assert.That(flags.HasFlag(WaterRuntimeValidationFlags.MissingPrimary), Is.True);
            Assert.That(flags.HasFlag(WaterRuntimeValidationFlags.CullingDisabled), Is.True);
        }

        [Test]
        public void WarmSnapshotLookup_AllocatesNoManagedMemory()
        {
            CreateBody("Warm", new Vector3(0f, 0f, 10f), Vector3.one);
            WaterRuntimeRelevance.RebuildForTests(_camera, 1, 1);
            WaterRuntimeRelevance.GetSnapshot(_camera);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) WaterRuntimeRelevance.GetSnapshot(_camera);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.That(after - before, Is.Zero);
        }

        WaterVolume CreateBody(string name, Vector3 position, Vector3 extent)
        {
            GameObject gameObject = CreateObject(name);
            gameObject.SetActive(false);
            WaterVolume body = gameObject.AddComponent<WaterVolume>();
            body.transform.position = position;
            body.volumeExtent = extent;
            body.activationDistance = 1000f;
            body.EnableCulling = true;
            WaterVolume.Bodies.Add(body);
            WaterRuntimeRelevance.InvalidateRegistrations();
            return body;
        }

        GameObject CreateObject(string name)
        {
            var gameObject = new GameObject(name);
            _objects.Add(gameObject);
            return gameObject;
        }

        static WaterRuntimeBodySnapshot Find(
            IReadOnlyList<WaterRuntimeBodySnapshot> snapshot, WaterVolume body)
        {
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Body == body) return snapshot[i];
            Assert.Fail($"Body '{body.name}' was not present in the relevance snapshot.");
            return default;
        }

        static WaterRuntimeBodySnapshot Candidate(int registrationOrder, float distance)
            => new WaterRuntimeBodySnapshot
            {
                RegistrationOrder = registrationOrder,
                FrustumVisible = true,
                RenderVisible = true,
                NearestBoundsDistance = distance,
                CausticWanted = true,
                CausticInfluenceVisible = true,
            };
    }
}
