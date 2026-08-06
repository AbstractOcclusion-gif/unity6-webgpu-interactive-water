using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater.Tests
{
    public sealed class ClipmapFeatureTests
    {
        const int GridResolution = 8;
        const int HoleHalfCells = 1;
        const int ExpectedVertexCount = 81;
        const int ExpectedTriangleIndexCount = 360;

        [Test]
        public void BuildAnnulusTemplate_ProducesAnUpFacingUInt32GridWithCentralHole()
        {
            Mesh mesh = LargeWaterClipmap.BuildAnnulusTemplate(GridResolution, HoleHalfCells);
            try
            {
                Assert.That(mesh.vertexCount, Is.EqualTo(ExpectedVertexCount));
                Assert.That(mesh.triangles.Length, Is.EqualTo(ExpectedTriangleIndexCount));
                Assert.That(mesh.indexFormat, Is.EqualTo(IndexFormat.UInt32));

                foreach (Vector3 vertex in mesh.vertices)
                    Assert.That(vertex.y, Is.EqualTo(0f));
                foreach (Vector3 normal in mesh.normals)
                    Assert.That(normal, Is.EqualTo(Vector3.up));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [TestCase(6, 1)]
        [TestCase(9, 1)]
        [TestCase(8, -1)]
        [TestCase(8, 4)]
        public void BuildAnnulusTemplate_RejectsInvalidTopology(int resolution, int holeHalfCells)
        {
            Assert.That(() => LargeWaterClipmap.BuildAnnulusTemplate(resolution, holeHalfCells),
                        Throws.InstanceOf<System.ArgumentException>());
        }

        [Test]
        public void BuildChunkShellBox_UsesTheExpectedPoolSpaceBounds()
        {
            Mesh mesh = WaterMeshBuilder.BuildChunkShellBox();
            try
            {
                Assert.That(mesh.vertexCount, Is.EqualTo(8));
                Assert.That(mesh.triangles.Length, Is.EqualTo(36));
                foreach (Vector3 vertex in mesh.vertices)
                {
                    Assert.That(Mathf.Abs(vertex.x), Is.EqualTo(1f));
                    Assert.That(Mathf.Abs(vertex.y), Is.EqualTo(1f));
                    Assert.That(Mathf.Abs(vertex.z), Is.EqualTo(1f));
                }
                Assert.That(mesh.bounds.size.x, Is.EqualTo(WaterMeshBuilder.HugeBoundsSize));
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
