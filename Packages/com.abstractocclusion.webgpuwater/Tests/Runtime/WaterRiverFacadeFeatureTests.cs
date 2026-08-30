// WebGpuWater - WaterRiver facade acceptance tests (river harmonization round, 2026-08-29).
// Pins the authoring contracts code review cannot: RequireComponent completes the trio, the
// terminal frames drive port derivation, regeneration is id-stable (the GUID-once portId is the
// persistent identity saves key on), and the parent current-field link APPENDS - the demo rig's
// original array overwrite is exactly the bug this suite exists to keep dead.
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class WaterRiverFacadeFeatureTests
    {
        const float StartHeight = 6f;
        const float EndHeight = 1f;
        const float RiverLength = 18f;
        const float RiverWidth = 4f;
        const float RiverSpeed = 2f;

        readonly List<GameObject> _objects = new List<GameObject>();
        WaterRiver _river;
        GameObject _lakeGO;
        WaterVolume _lake;

        [SetUp]
        public void SetUp()
        {
            WaterSurfaceProviders.ResetStaticState();
            WaterTopology.ResetStaticState();
            _objects.Clear();

            // Inactive while wiring so no OnEnable runs against half-built state.
            GameObject riverGO = new GameObject("FacadeRiver");
            _objects.Add(riverGO);
            riverGO.SetActive(false);
            WaterRiverSpline spline = riverGO.AddComponent<WaterRiverSpline>();
            spline.knots = new List<WaterRiverKnot>
            {
                new WaterRiverKnot(new Vector3(0f, StartHeight, -RiverLength),
                                   new Vector3(0f, 0f, RiverLength / 3f), RiverWidth, RiverSpeed),
                new WaterRiverKnot(new Vector3(0f, EndHeight, 0f),
                                   new Vector3(0f, 0f, RiverLength / 3f), RiverWidth, RiverSpeed),
            };
            _river = riverGO.AddComponent<WaterRiver>();

            _lakeGO = new GameObject("Lake");
            _objects.Add(_lakeGO);
            _lakeGO.SetActive(false);
            _lakeGO.transform.position = new Vector3(0f, 1f, 10f);
            _lake = _lakeGO.AddComponent<WaterVolume>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _objects)
                if (go != null) Object.DestroyImmediate(go);
            WaterTopology.ResetStaticState();
            WaterSurfaceProviders.ResetStaticState();
        }

        [Test]
        public void RequireComponent_CompletesTheRiverTrio()
        {
            // The facade's whole promise: one AddComponent produces a working river skeleton.
            Assert.That(_river.GetComponent<WaterRiverSpline>(), Is.Not.Null);
            Assert.That(_river.GetComponent<WaterRiverCurrentField>(), Is.Not.Null);
            Assert.That(_river.GetComponent<WaterRiverSurface>(), Is.Not.Null);
        }

        [Test]
        public void EndFrames_ComeFromTheSplineTerminals()
        {
            Assert.That(_river.TryGetEndFrame(WaterRiverEndKind.Source, out Vector3 sourceAnchor,
                                              out Vector3 sourceDownstream, out float sourceFlow),
                        Is.True);
            Assert.That(_river.TryGetEndFrame(WaterRiverEndKind.Mouth, out Vector3 mouthAnchor,
                                              out Vector3 mouthDownstream, out float mouthFlow),
                        Is.True);

            Assert.That(sourceAnchor.y, Is.EqualTo(StartHeight).Within(1e-3f));
            Assert.That(mouthAnchor.y, Is.EqualTo(EndHeight).Within(1e-3f));
            // Ports face DOWNSTREAM on both ends (the WaterConnectionPort convention).
            Assert.That(sourceDownstream.z, Is.GreaterThan(0f));
            Assert.That(mouthDownstream.z, Is.GreaterThan(0f));
            // Authored volumetric flow = width x speed at the end.
            Assert.That(sourceFlow, Is.EqualTo(RiverWidth * RiverSpeed).Within(1e-3f));
            Assert.That(mouthFlow, Is.EqualTo(RiverWidth * RiverSpeed).Within(1e-3f));
        }

        [Test]
        public void RegenerateConnection_IsIdStableAndDoesNotDuplicate()
        {
            _river.mouthEnd.body = _lake;
            _river.RegenerateConnection(WaterRiverEndKind.Mouth);
            WaterRiverEndConnection end = _river.MouthEnd;
            Assert.That(end.IsGenerated, Is.True);
            string firstPortId = end.riverPort.PortId;
            WaterConnectionPort firstRiverPort = end.riverPort;
            WaterConnection firstConnection = end.connection;
            int childCountAfterFirst = _river.transform.childCount;

            // Regeneration must REUSE the generated objects: same instances, same persistent id,
            // no extra children - a re-mint would orphan everything keyed on the portId.
            _river.RegenerateConnection(WaterRiverEndKind.Mouth);
            Assert.That(_river.MouthEnd.riverPort, Is.SameAs(firstRiverPort));
            Assert.That(_river.MouthEnd.connection, Is.SameAs(firstConnection));
            Assert.That(_river.MouthEnd.riverPort.PortId, Is.EqualTo(firstPortId));
            Assert.That(_river.transform.childCount, Is.EqualTo(childCountAfterFirst));

            // The mouth feeds the body: the river side is upstream (port A) and the authored
            // flow is the terminal width x speed.
            Assert.That(end.connection.PortA, Is.SameAs(end.riverPort));
            Assert.That(end.connection.PortB, Is.SameAs(end.targetPort));
            Assert.That(end.connection.AuthoredFlowRate,
                        Is.EqualTo(RiverWidth * RiverSpeed).Within(1e-3f));
        }

        [Test]
        public void SourceConnection_PutsTheBodyUpstream()
        {
            _river.sourceEnd.body = _lake;
            _river.RegenerateConnection(WaterRiverEndKind.Source);
            WaterRiverEndConnection end = _river.SourceEnd;
            Assert.That(end.connection.PortA, Is.SameAs(end.targetPort),
                        "a source-end body FEEDS the river, so the body side is upstream");
            Assert.That(end.connection.PortB, Is.SameAs(end.riverPort));
        }

        [Test]
        public void SourceRiverConnection_PutsUpstreamRiverFirstAndTargetsItsSurface()
        {
            GameObject upstreamGO = new GameObject("UpstreamRiver");
            _objects.Add(upstreamGO);
            upstreamGO.SetActive(false);
            WaterRiver upstream = upstreamGO.AddComponent<WaterRiver>();
            upstream.parentVolume = _lake;
            _river.parentVolume = _lake;
            _river.ApplyWiring();
            upstream.ApplyWiring();

            _river.sourceEnd.upstreamRiver = upstream;
            _river.RegenerateConnection(WaterRiverEndKind.Source);

            WaterRiverEndConnection end = _river.SourceEnd;
            Assert.That(end.connection.PortA, Is.SameAs(end.targetPort));
            Assert.That(end.connection.PortB, Is.SameAs(end.riverPort));
            Assert.That(end.targetPort.river, Is.SameAs(upstream.Surface));
            Assert.That(end.targetPort.body, Is.Null);
        }

        [Test]
        public void AttachCurrentField_AppendsAndDetachRemovesOnlyItsOwn()
        {
            // The regression this test keeps dead: the demo rig used to ASSIGN currentFields,
            // silently deleting every other authored field on the lake.
            GameObject otherGO = new GameObject("OtherField");
            _objects.Add(otherGO);
            otherGO.SetActive(false);
            WaterRiverSpline otherSpline = otherGO.AddComponent<WaterRiverSpline>();
            WaterRiverCurrentField otherField = otherGO.AddComponent<WaterRiverCurrentField>();
            otherField.Configure(otherSpline);
            _lake.currentFields = new WaterCurrentField[] { otherField };

            _river.parentVolume = _lake;
            _river.ApplyWiring();
            _river.AttachCurrentFieldToParent();
            Assert.That(_lake.currentFields.Length, Is.EqualTo(2));
            Assert.That(_lake.currentFields[0], Is.SameAs(otherField), "existing fields survive");

            // Idempotent: attaching again must not grow the list.
            _river.AttachCurrentFieldToParent();
            Assert.That(_lake.currentFields.Length, Is.EqualTo(2));

            _river.DetachCurrentFieldFromParent();
            Assert.That(_lake.currentFields.Length, Is.EqualTo(1));
            Assert.That(_lake.currentFields[0], Is.SameAs(otherField));
        }

        [Test]
        public void MouthOutflow_ExtendsCurrentIntoReceivingBodyAndDecays()
        {
            _river.mouthEnd.body = _lake;
            _river.mouthOutflowLengthMeters = 12f;
            _river.RegenerateConnection(WaterRiverEndKind.Mouth);

            Assert.That(_river.TryBuildMouthOutflow(out WaterRiverMouthOutflow outflow), Is.True);
            WaterRiverCurrentField field = _river.GetComponent<WaterRiverCurrentField>();
            Assert.That(_lake.currentFields, Does.Contain(field),
                        "the actual mouth body owns the downstream jet even without a parent link");

            Vector3 nearPoint = outflow.Origin + outflow.Downstream * 2f;
            Vector3 farPoint = outflow.Origin + outflow.Downstream * 8f;
            Assert.That(field.SampleCurrent(nearPoint, out Vector3 nearVelocity), Is.True);
            Assert.That(field.SampleCurrent(farPoint, out Vector3 farVelocity), Is.True);
            Assert.That(Vector3.Dot(nearVelocity, outflow.Downstream), Is.GreaterThan(0f));
            Assert.That(farVelocity.magnitude, Is.LessThan(nearVelocity.magnitude));

            Vector3 beyondPlume = outflow.Origin + outflow.Downstream * 13f;
            Assert.That(field.SampleCurrent(beyondPlume, out _), Is.False);
        }

        [Test]
        public void TopologyPortLookup_FindsGeneratedPortsByPersistentId()
        {
            _river.mouthEnd.body = _lake;
            _river.RegenerateConnection(WaterRiverEndKind.Mouth);
            WaterRiverEndConnection end = _river.MouthEnd;
            // Registration normally happens in WaterConnection.OnEnable; the rig stays inactive
            // (no half-built OnEnable), so register through the topology seam directly.
            WaterTopology.Register(end.connection);

            Assert.That(WaterTopology.TryGetPort(end.riverPort.PortId,
                                                 out WaterConnectionPort port,
                                                 out WaterConnection connection), Is.True);
            Assert.That(port, Is.SameAs(end.riverPort));
            Assert.That(connection, Is.SameAs(end.connection));
            Assert.That(WaterTopology.TryGetPort("no-such-port", out _, out _), Is.False);
        }
    }
}
