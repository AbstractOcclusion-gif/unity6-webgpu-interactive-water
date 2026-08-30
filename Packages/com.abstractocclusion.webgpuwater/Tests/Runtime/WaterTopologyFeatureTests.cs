// WebGpuWater - connected-water topology acceptance tests (2026-08-29 round, case 11).
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterTopologyFeatureTests
    {
        const float SeamZ = 10f;
        const float TransitionRadius = 4f;
        const float RiverHeight = 6f;
        const float LakeHeight = 5f;

        readonly List<GameObject> _objects = new List<GameObject>();
        FakeWaterSurfaceProvider _riverSide;
        FakeWaterSurfaceProvider _lakeSide;
        WaterConnection _connection;

        [SetUp]
        public void SetUp()
        {
            WaterSurfaceProviders.ResetStaticState();
            WaterTopology.ResetStaticState();
            WaterExclusionVolume.ResetStaticState();
            _objects.Clear();

            // Two abutting domains meeting at z = SeamZ, with a deliberate height step the
            // seam blend must smooth over.
            _riverSide = new FakeWaterSurfaceProvider(
                new Bounds(new Vector3(0f, 5f, 5f), new Vector3(10f, 4f, 10f)), RiverHeight);
            _lakeSide = new FakeWaterSurfaceProvider(
                new Bounds(new Vector3(0f, 5f, 15f), new Vector3(10f, 4f, 10f)), LakeHeight);
            WaterSurfaceProviders.Register(_riverSide);
            WaterSurfaceProviders.Register(_lakeSide);

            WaterConnectionPort portA = CreatePort("RiverPort", new Vector3(0f, 5f, SeamZ - 1f),
                                                   _riverSide);
            WaterConnectionPort portB = CreatePort("LakePort", new Vector3(0f, 5f, SeamZ + 1f),
                                                   _lakeSide);

            GameObject connectionObject = new GameObject("Connection");
            _objects.Add(connectionObject);
            connectionObject.SetActive(false);
            _connection = connectionObject.AddComponent<WaterConnection>();
            _connection.portA = portA;
            _connection.portB = portB;
            _connection.transitionRadiusMeters = TransitionRadius;
            connectionObject.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.DestroyImmediate(go);
            WaterSurfaceProviders.ResetStaticState();
            WaterTopology.ResetStaticState();
        }

        WaterConnectionPort CreatePort(string name, Vector3 position,
                                       IWaterSurfaceProvider provider)
        {
            GameObject go = new GameObject(name);
            _objects.Add(go);
            go.transform.position = position;
            WaterConnectionPort port = go.AddComponent<WaterConnectionPort>();
            port.providerOverride = provider;
            return port;
        }

        static WaterDomainQueryOptions Containing()
            => WaterDomainQueryOptions.ForIntent(WaterQueryIntent.ContainingVolume);

        [Test]
        public void Ports_KeepTheirAuthoredIdentity()
        {
            Assert.That(_connection.PortA.PortId, Is.Not.Empty);
            Assert.That(_connection.PortA.PortId, Is.Not.EqualTo(_connection.PortB.PortId));
            Assert.That(_connection.ConnectionId, Is.GreaterThan(0));
        }

        [Test]
        public void ConnectionsOf_EnumeratesBothSides_AllocationFree()
        {
            var results = new WaterConnection[4];
            Assert.That(WaterTopology.ConnectionsOf(_riverSide.Id, results), Is.EqualTo(1));
            Assert.That(results[0], Is.SameAs(_connection));
            Assert.That(WaterTopology.ConnectionsOf(_lakeSide.Id, results), Is.EqualTo(1));
            Assert.That(WaterTopology.ConnectionsOf(9999, results), Is.Zero);
        }

        [Test]
        public void QueryHandoff_BlendsHeightAcrossTheSeam_ContinuousFromBothSides()
        {
            // Far from the seam: pure river height, no connection info.
            Assert.That(WaterDomainResolver.Resolve(new Vector3(0f, 5f, 1f), Containing(),
                                                    out WaterDomainSample farRiver), Is.True);
            Assert.That(farRiver.SurfaceHeight, Is.EqualTo(RiverHeight));
            Assert.That(farRiver.Connection.Active, Is.False);

            // Just before the seam plane, river side: blended toward the lake, tagged with the
            // connection and the other side's id.
            Assert.That(WaterDomainResolver.Resolve(new Vector3(0f, 5f, SeamZ - 0.1f), Containing(),
                                                    out WaterDomainSample nearRiver), Is.True);
            Assert.That(nearRiver.Connection.Active, Is.True);
            Assert.That(nearRiver.Connection.OtherBodyId, Is.EqualTo(_lakeSide.Id));
            Assert.That(nearRiver.SurfaceHeight, Is.LessThan(RiverHeight));
            Assert.That(nearRiver.SurfaceHeight, Is.GreaterThan((RiverHeight + LakeHeight) * 0.5f - 0.1f));

            // Just after the seam plane, lake side: blended toward the river by the mirrored
            // weight, so the two sides meet at the plane instead of stepping.
            Assert.That(WaterDomainResolver.Resolve(new Vector3(0f, 5f, SeamZ + 0.1f), Containing(),
                                                    out WaterDomainSample nearLake), Is.True);
            Assert.That(nearLake.BodyId, Is.EqualTo(_lakeSide.Id));
            Assert.That(nearLake.Connection.Active, Is.True);
            Assert.That(nearLake.Connection.OtherBodyId, Is.EqualTo(_riverSide.Id));
            float seamMidpoint = (RiverHeight + LakeHeight) * 0.5f;
            Assert.That(Mathf.Abs(nearRiver.SurfaceHeight - seamMidpoint), Is.LessThan(0.15f));
            Assert.That(Mathf.Abs(nearLake.SurfaceHeight - seamMidpoint), Is.LessThan(0.15f));
            Assert.That(Mathf.Abs(nearRiver.SurfaceHeight - nearLake.SurfaceHeight),
                        Is.LessThan(0.2f), "the handoff must be continuous across the seam");
        }

        [Test]
        public void QueryHandoff_UsesPortFlowDirectionWhenSharedRowsMakePortsCoincident()
        {
            Vector3 coincidentAnchor = new Vector3(0f, 5f, SeamZ);
            _connection.PortA.transform.SetPositionAndRotation(
                coincidentAnchor, Quaternion.LookRotation(Vector3.forward));
            _connection.PortB.transform.SetPositionAndRotation(
                coincidentAnchor, Quaternion.LookRotation(Vector3.forward));

            Assert.That(WaterDomainResolver.Resolve(
                new Vector3(0f, 5f, SeamZ - 0.1f), Containing(),
                out WaterDomainSample nearRiver), Is.True);
            Assert.That(nearRiver.Connection.Active, Is.True);
            Assert.That(nearRiver.Connection.OtherBodyId, Is.EqualTo(_lakeSide.Id));
        }
    }
}
