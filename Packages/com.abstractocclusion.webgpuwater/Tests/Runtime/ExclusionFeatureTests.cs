using NUnit.Framework;
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class ExclusionFeatureTests
    {
        readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            WaterExclusionVolume.ResetStaticState();
            _objects.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in _objects)
                if (gameObject != null) Object.DestroyImmediate(gameObject);
            WaterExclusionVolume.ResetStaticState();
        }

        [Test]
        public void ContainsPoint_UsesTheVolumeShapeTransformAndScale()
        {
            WaterExclusionVolume box = CreateVolume("Box", WaterExclusionVolume.Shape.Box,
                                                    new Vector3(3f, 0f, 0f), new Vector3(2f, 4f, 6f));
            WaterExclusionVolume sphere = CreateVolume("Sphere", WaterExclusionVolume.Shape.Sphere,
                                                       new Vector3(-3f, 0f, 0f), new Vector3(2f, 4f, 6f));

            Assert.That(WaterExclusionVolume.ContainsPoint(box.transform.position + Vector3.forward * 2.9f), Is.True);
            Assert.That(WaterExclusionVolume.ContainsPoint(box.transform.position + Vector3.forward * 3.1f), Is.False);
            Assert.That(WaterExclusionVolume.ContainsPoint(sphere.transform.position + Vector3.up * 1.9f), Is.True);
            Assert.That(WaterExclusionVolume.ContainsPoint(sphere.transform.position + Vector3.up * 2.1f), Is.False);
        }

        [Test]
        public void WriteVolumeUniforms_SelectsTheNearestSupportedVolumes()
        {
            CreateVolume("Far", WaterExclusionVolume.Shape.Box, new Vector3(10f, 0f, 0f), Vector3.one);
            CreateVolume("One", WaterExclusionVolume.Shape.Box, new Vector3(1f, 0f, 0f), Vector3.one);
            CreateVolume("Two", WaterExclusionVolume.Shape.Sphere, new Vector3(2f, 0f, 0f), Vector3.one);
            CreateVolume("Three", WaterExclusionVolume.Shape.Box, new Vector3(3f, 0f, 0f), Vector3.one);
            CreateVolume("Four", WaterExclusionVolume.Shape.Box, new Vector3(4f, 0f, 0f), Vector3.one);

            var matrices = new Matrix4x4[WaterExclusionVolume.MaxVolumes];
            var shapes = new Vector4[WaterExclusionVolume.MaxVolumes];
            int count = WaterExclusionVolume.WriteVolumeUniforms(matrices, shapes, null, null, Vector3.zero);

            Assert.That(count, Is.EqualTo(WaterExclusionVolume.MaxVolumes));
            Assert.That(shapes[0].x, Is.EqualTo((float)WaterExclusionVolume.Shape.Box));
            Assert.That(shapes[1].x, Is.EqualTo((float)WaterExclusionVolume.Shape.Sphere));
            Assert.That(matrices[0].inverse.MultiplyPoint3x4(Vector3.zero).x, Is.EqualTo(1f).Within(0.0001f));
            Assert.That(matrices[3].inverse.MultiplyPoint3x4(Vector3.zero).x, Is.EqualTo(4f).Within(0.0001f));
        }

        [Test]
        public void MeshVolume_UsesItsProxyForAnalyticUniformsAndCountsOnlyAssignedMeshes()
        {
            WaterExclusionVolume volume = CreateVolume("Mesh", WaterExclusionVolume.Shape.Mesh,
                                                        Vector3.zero, Vector3.one);
            volume.meshProxy = WaterExclusionVolume.Shape.Sphere;
            volume.castsSunShadow = false;

            Assert.That(volume.ShapeUniform, Is.EqualTo(new Vector4(1f, 1f, 1f, 0f)));
            Assert.That(WaterExclusionVolume.MeshVolumeCount, Is.Zero);

            volume.carveMesh = WaterMeshBuilder.BuildUnitCube();
            Assert.That(WaterExclusionVolume.MeshVolumeCount, Is.EqualTo(1));
            Object.DestroyImmediate(volume.carveMesh);
            volume.carveMesh = null;
        }

        [Test]
        public void WriteVolumeUniforms_RejectsBuffersThatCannotMatchShaderArraySize()
        {
            var validMatrices = new Matrix4x4[WaterExclusionVolume.MaxVolumes];
            var validShapes = new Vector4[WaterExclusionVolume.MaxVolumes];
            var shortMatrices = new Matrix4x4[WaterExclusionVolume.MaxVolumes - 1];

            Assert.That(
                () => WaterExclusionVolume.WriteVolumeUniforms(shortMatrices, validShapes, null, null, Vector3.zero),
                Throws.InstanceOf<System.ArgumentException>());
            Assert.That(
                () => WaterExclusionVolume.WriteVolumeUniforms(validMatrices, null, null, null, Vector3.zero),
                Throws.InstanceOf<System.ArgumentException>());
        }

        WaterExclusionVolume CreateVolume(string objectName, WaterExclusionVolume.Shape shape,
                                           Vector3 position, Vector3 size)
        {
            var gameObject = new GameObject(objectName);
            gameObject.transform.position = position;
            WaterExclusionVolume volume = gameObject.AddComponent<WaterExclusionVolume>();
            volume.shape = shape;
            volume.size = size;
            _objects.Add(gameObject);
            return volume;
        }
    }
}
