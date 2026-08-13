// WebGpuWater - the two plain data types the build kit threads through its generators.
// Split out of WaterBuildKit.cs so that file is the kit and nothing else; these are shared
// STATE, not build steps, and every partial of the kit takes one or the other as a parameter.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    // The water shaders + compute, loaded and validated once (see WaterBuildKit.TryLoadShaders).
    internal struct ShaderSet
    {
        public Shader Water, Pool, Caustics, Obstacle;
        public ComputeShader Compute;
    }

    // Package defaults plus the project-owned folders created for one water body.
    internal sealed class BuildContext
    {
        public ShaderSet Shaders;
        public Mesh Grid;
        public Mesh PoolMesh;
        public Cubemap Sky;
        public Texture2D Tiles;
        public WaterQuality Quality;
        public Camera Camera;
        public OrbitCamera Orbit;
        public Light Sun;
        public Material MatAbove, MatUnder, MatPool;
        public string WaterFolder;
        public string MaterialsFolder;
    }
}
