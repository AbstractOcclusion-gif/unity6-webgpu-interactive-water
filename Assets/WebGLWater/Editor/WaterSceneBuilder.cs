// WebGL Water - one-click scene builder (Unity 6 / URP port)
// Menu: Tools > WebGL Water > Build Scene
//
// Generates the grid + sphere meshes, a procedural sky cubemap (and a fallback
// tile texture), the materials, and wires up the camera + WaterController.
// The analytic pool (walls/floor rendered by PoolWall.shader, which shows the
// caustics) is OPTIONAL - leave it off if you've built your own pool.
using System.IO;
using UnityEditor;
using UnityEngine;
using WebGLWater;

namespace WebGLWater.EditorTools
{
    public static class WaterSceneBuilder
    {
        const string Root = "Assets/WebGLWater";
        const string Gen = "Assets/WebGLWater/Generated";
        const int GridDetail = 200;

        [MenuItem("Tools/WebGL Water/Build Scene (with analytic pool)")]
        static void BuildWithPool() => Build(true);

        [MenuItem("Tools/WebGL Water/Build Scene (water only - keep my pool)")]
        static void BuildWaterOnly() => Build(false);

        static void Build(bool buildAnalyticPool)
        {
            if (!AssetDatabase.IsValidFolder(Gen))
                AssetDatabase.CreateFolder(Root, "Generated");

            // ---- shaders ----
            var sfWater  = Shader.Find("WebGLWater/WaterSurface");
            var sfPool   = Shader.Find("WebGLWater/PoolWall");
            var sfSphere = Shader.Find("WebGLWater/WaterSphere");
            var sfCaust  = Shader.Find("WebGLWater/Caustics");
            var sfBlur   = Shader.Find("WebGLWater/CausticBlur");
            var compute  = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "/Shaders/WaterSim.compute");

            if (sfWater == null || sfSphere == null || sfCaust == null || compute == null)
            {
                EditorUtility.DisplayDialog("WebGL Water",
                    "Could not find the shaders / compute shader. Make sure the WebGLWater/Shaders folder imported without errors, then try again.",
                    "OK");
                return;
            }

            // ---- meshes ----
            var gridMesh   = SaveAsset(BuildGrid(GridDetail),     Gen + "/WaterGrid.asset");
            var sphereMesh = SaveAsset(BuildUnitSphere(32, 24),   Gen + "/UnitSphere.asset");

            // ---- textures ----
            var sky   = SaveCubemap(BuildSky(128), Gen + "/SkyCubemap.cubemap");
            var tiles = LoadOrBuildTiles(Gen + "/Tiles.png");

            // ---- materials ----
            var matAbove = SaveMaterial(MakeMat(sfWater, m => { m.SetFloat("_Underwater", 0f); m.SetFloat("_Cull", 1f); }),
                                        Gen + "/WaterAbove.mat");
            var matUnder = SaveMaterial(MakeMat(sfWater, m => { m.SetFloat("_Underwater", 1f); m.SetFloat("_Cull", 2f); }),
                                        Gen + "/WaterUnder.mat");
            var matSphere = SaveMaterial(MakeMat(sfSphere, m => m.SetFloat("_Cull", 2f)), Gen + "/Sphere.mat");
            Material matPool = null;
            if (buildAnalyticPool && sfPool != null)
                matPool = SaveMaterial(MakeMat(sfPool, m => m.SetFloat("_Cull", 2f)), Gen + "/Pool.mat");

            // ---- scene objects ----
            var root = new GameObject("WebGL Water");

            var above = CreateRenderer("Water (above)", gridMesh, matAbove, root.transform);
            var under = CreateRenderer("Water (under)", gridMesh, matUnder, root.transform);

            var sphereGO = CreateRenderer("Sphere", sphereMesh, matSphere, root.transform);

            GameObject poolGO = null;
            if (matPool != null)
            {
                poolGO = CreateRenderer("Analytic Pool", SaveAsset(BuildPool(), Gen + "/Pool.asset"), matPool, root.transform);
            }

            // camera - reuse the scene's main camera if there is one (avoids
            // two cameras rendering on top of each other).
            var cam = Camera.main;
            if (cam == null)
            {
                var camGO = new GameObject("Water Camera");
                cam = camGO.AddComponent<Camera>();
                camGO.tag = "MainCamera";
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView = 45f;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;

            var orbit = cam.GetComponent<OrbitCamera>();
            if (orbit == null) orbit = cam.gameObject.AddComponent<OrbitCamera>();
            orbit.pivot = new Vector3(0f, -0.5f, 0f);
            orbit.pitch = -25f;
            orbit.yaw = -200.5f;
            orbit.distance = 4f;

            // controller
            var ctrlGO = new GameObject("Water Controller");
            ctrlGO.transform.SetParent(root.transform);
            var ctrl = ctrlGO.AddComponent<WaterController>();
            ctrl.simCompute = compute;
            ctrl.causticsShader = sfCaust;
            ctrl.causticBlurShader = sfBlur;
            ctrl.waterMesh = gridMesh;
            ctrl.targetCamera = cam;
            ctrl.orbit = orbit;
            ctrl.sphere = sphereGO.transform;
            ctrl.tiles = tiles;
            ctrl.sky = sky;

            Selection.activeObject = root;
            EditorUtility.SetDirty(ctrl);
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();

            Debug.Log("[WebGL Water] Scene built. Press Play.  " +
                      (buildAnalyticPool ? "Analytic pool included." : "No pool created - using your own.") +
                      "  Assign your pool tile texture to the Water Controller's 'Tiles' field for matching reflections.");
        }

        // ---------------------------------------------------------------- meshes
        static GameObject CreateRenderer(string name, Mesh mesh, Material mat, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        // XY-plane grid in [-1,1], z = 0 (matches the original lightgl plane mesh).
        static Mesh BuildGrid(int detail)
        {
            int n = detail + 1;
            var verts = new Vector3[n * n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    verts[i * n + j] = new Vector3(i / (float)detail * 2f - 1f, j / (float)detail * 2f - 1f, 0f);

            var tris = new int[detail * detail * 6];
            int t = 0;
            for (int i = 0; i < detail; i++)
                for (int j = 0; j < detail; j++)
                {
                    int a = i * n + j;
                    int b = (i + 1) * n + j;
                    int c = i * n + (j + 1);
                    int d = (i + 1) * n + (j + 1);
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }

            var mesh = new Mesh { name = "WaterGrid", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.triangles = tris;
            // Real geometry is displaced into the XZ plane in the shader, so use
            // explicit bounds covering the pool volume to avoid wrong frustum culling.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(3f, 3f, 3f));
            return mesh;
        }

        static Mesh BuildUnitSphere(int longs, int lats)
        {
            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            for (int y = 0; y <= lats; y++)
            {
                float v = y / (float)lats;
                float theta = v * Mathf.PI;
                for (int x = 0; x <= longs; x++)
                {
                    float u = x / (float)longs;
                    float phi = u * Mathf.PI * 2f;
                    verts.Add(new Vector3(
                        Mathf.Sin(theta) * Mathf.Cos(phi),
                        Mathf.Cos(theta),
                        Mathf.Sin(theta) * Mathf.Sin(phi)));
                }
            }
            int stride = longs + 1;
            for (int y = 0; y < lats; y++)
                for (int x = 0; x < longs; x++)
                {
                    int a = y * stride + x;
                    int b = a + stride;
                    tris.Add(a); tris.Add(a + 1); tris.Add(b);
                    tris.Add(b); tris.Add(a + 1); tris.Add(b + 1);
                }
            var mesh = new Mesh { name = "UnitSphere" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        // Open-top box: floor at y=-1, walls up to y=2/12, spanning x,z in [-1,1].
        // Faces point inward (visible from inside the pool). World coordinates.
        static Mesh BuildPool()
        {
            const float top = 2f / 12f;
            const float lo = -1f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            // floor (normal up)
            Quad(new Vector3(-1, lo, -1), new Vector3(-1, lo, 1), new Vector3(1, lo, 1), new Vector3(1, lo, -1));
            // walls, wound so their fronts face inward
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, top, -1), new Vector3(-1, top, -1)); // -z
            Quad(new Vector3(1, lo, 1), new Vector3(-1, lo, 1), new Vector3(-1, top, 1), new Vector3(1, top, 1));     // +z
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, lo, -1), new Vector3(-1, top, -1), new Vector3(-1, top, 1)); // -x
            Quad(new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(1, top, 1), new Vector3(1, top, -1));     // +x

            var mesh = new Mesh { name = "Pool" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        // ---------------------------------------------------------------- textures
        static Cubemap BuildSky(int size)
        {
            var cube = new Cubemap(size, TextureFormat.RGB24, false);
            CubemapFace[] faces = {
                CubemapFace.PositiveX, CubemapFace.NegativeX,
                CubemapFace.PositiveY, CubemapFace.NegativeY,
                CubemapFace.PositiveZ, CubemapFace.NegativeZ
            };
            foreach (var face in faces)
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float u = (x + 0.5f) / size * 2f - 1f;
                        float w = (y + 0.5f) / size * 2f - 1f;
                        Vector3 dir = FaceDir(face, u, w).normalized;
                        cube.SetPixel(face, x, y, SkyColor(dir));
                    }
            cube.Apply();
            return cube;
        }

        static Vector3 FaceDir(CubemapFace f, float u, float v)
        {
            switch (f)
            {
                case CubemapFace.PositiveX: return new Vector3(1, -v, -u);
                case CubemapFace.NegativeX: return new Vector3(-1, -v, u);
                case CubemapFace.PositiveY: return new Vector3(u, 1, v);
                case CubemapFace.NegativeY: return new Vector3(u, -1, -v);
                case CubemapFace.PositiveZ: return new Vector3(u, -v, 1);
                default: return new Vector3(-u, -v, -1);
            }
        }

        static Color SkyColor(Vector3 dir)
        {
            Color horizon = new Color(0.78f, 0.86f, 0.96f);
            Color zenith = new Color(0.26f, 0.47f, 0.86f);
            Color ground = new Color(0.30f, 0.30f, 0.33f);
            if (dir.y >= 0f) return Color.Lerp(horizon, zenith, Mathf.Pow(dir.y, 0.6f));
            return Color.Lerp(horizon, ground, Mathf.Pow(-dir.y, 0.5f));
        }

        static Texture2D LoadOrBuildTiles(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int s = 256;
            var tex = new Texture2D(s, s, TextureFormat.RGB24, true);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    // simple tiled look with grout lines
                    int cell = 32;
                    bool grout = (x % cell < 2) || (y % cell < 2);
                    float n = 0.85f + 0.15f * Mathf.PerlinNoise(x * 0.08f, y * 0.08f);
                    Color baseCol = new Color(0.55f, 0.75f, 0.85f) * n;
                    tex.SetPixel(x, y, grout ? new Color(0.30f, 0.45f, 0.55f) : baseCol);
                }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.wrapMode = TextureWrapMode.Repeat;
            imp.mipmapEnabled = true;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ---------------------------------------------------------------- helpers
        static Material MakeMat(Shader s, System.Action<Material> cfg)
        {
            var m = new Material(s);
            cfg?.Invoke(m);
            return m;
        }

        static Mesh SaveAsset(Mesh m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); return existing; }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material SaveMaterial(Material m, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) { EditorUtility.CopySerialized(m, existing); return existing; }
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Cubemap SaveCubemap(Cubemap c, string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            if (existing != null) { EditorUtility.CopySerialized(c, existing); return existing; }
            AssetDatabase.CreateAsset(c, path);
            return c;
        }
    }
}
