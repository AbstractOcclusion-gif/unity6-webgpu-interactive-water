using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterSimulationFocusTests
    {
        private GameObject root;
        private WaterVolume body;
        private Transform original;
        private Transform first;
        private Transform second;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Simulation focus test");
            root.SetActive(false); // No GPU initialization needed for focus policy.
            body = root.AddComponent<WaterVolume>();
            original = Child("Authored focus");
            first = Child("Lure");
            second = Child("Other camera focus");
            body.simWindowFocus = original;
            body.simWindowOffset = new Vector2(2f, 5f);
        }

        private Transform Child(string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(root.transform);
            return child;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void OverrideCentersExactlyAndRestoresAuthoredSettings()
        {
            var request = body.PushSimulationFocus(root, first);
            body.ResolveSimulationFocus(out var focus, out var offset);
            Assert.That(focus, Is.EqualTo(first));
            Assert.That(offset, Is.EqualTo(Vector2.zero));
            Assert.That(body.simWindowFocus, Is.EqualTo(original));
            request.Dispose();
            request.Dispose();
            body.ResolveSimulationFocus(out focus, out offset);
            Assert.That(focus, Is.EqualTo(original));
            Assert.That(offset, Is.EqualTo(new Vector2(2f, 5f)));
        }

        [Test]
        public void LatestRequestWinsAndReleasingItRestoresPrevious()
        {
            using (body.PushSimulationFocus(root, first))
            {
                var newer = body.PushSimulationFocus(root, second);
                body.ResolveSimulationFocus(out var focus, out _);
                Assert.That(focus, Is.EqualTo(second));
                newer.Dispose();
                body.ResolveSimulationFocus(out focus, out _);
                Assert.That(focus, Is.EqualTo(first));
            }
        }

        [Test]
        public void ReleasingOlderRequestDoesNotStealNewerFocus()
        {
            var older = body.PushSimulationFocus(root, first);
            using (body.PushSimulationFocus(root, second))
            {
                older.Dispose();
                body.ResolveSimulationFocus(out var focus, out _);
                Assert.That(focus, Is.EqualTo(second));
            }
        }

        [Test]
        public void DestroyedTargetFallsBackToAuthoredFocus()
        {
            using (body.PushSimulationFocus(root, first))
            {
                Object.DestroyImmediate(first.gameObject);
                body.ResolveSimulationFocus(out var focus, out _);
                Assert.That(focus, Is.EqualTo(original));
            }
        }

        [Test]
        public void DestroyedOwnerReleasesItsFocus()
        {
            var requester = Child("Requester");
            using (body.PushSimulationFocus(requester, first))
            {
                Object.DestroyImmediate(requester.gameObject);
                body.ResolveSimulationFocus(out var focus, out _);
                Assert.That(focus, Is.EqualTo(original));
            }
        }
    }
}
