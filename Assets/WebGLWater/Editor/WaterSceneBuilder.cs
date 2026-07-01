// WebGL Water - one-click scene builder (Unity 6 / URP port)
// Menu: Tools > WebGL Water > Build Scene
//
// Generates the grid mesh, a procedural sky cubemap (and a fallback
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

        // Shader names (keep in sync with the Shader "..." declarations in Shaders/).
        const string ShaderWaterSurface = "WebGLWater/WaterSurface";
        const string ShaderPoolWall = "WebGLWater/PoolWall";
        const string ShaderCaustics = "WebGLWater/Caustics";
        const string ShaderObstacle = "WebGLWater/ObstacleDepth";
        const string ShaderReceiver = "WebGLWater/WaterReceiver";
        const string ShaderGodRays = "WebGLWater/GodRays";
        const string ShaderSpritesDefault = "Sprites/Default";

        // Material property names (keep in sync with the shader Properties blocks).
        const string PropUnderwater = "_Underwater";
        const string PropCull = "_Cull";
        const string PropBaseColor = "_BaseColor";

        [MenuItem("Tools/WebGL Water/Build Scene (with analytic pool)")]
        static void BuildWithPool() => Build(true);

        [MenuItem("Tools/WebGL Water/Build Scene (water only - keep my pool)")]
        static void BuildWaterOnly() => Build(false);

        static void Build(bool buildAnalyticPool)
        {
            if (!AssetDatabase.IsValidFolder(Gen))
                AssetDatabase.CreateFolder(Root, "Generated");

            // ---- shaders ----
            var sfWater    = Shader.Find(ShaderWaterSurface);
            var sfPool     = Shader.Find(ShaderPoolWall);
            var sfCaust    = Shader.Find(ShaderCaustics);
            var sfObstacle = Shader.Find(ShaderObstacle);
            var sfReceiver = Shader.Find(ShaderReceiver);
            var compute    = AssetDatabase.LoadAssetAtPath<ComputeShader>(Root + "/Shaders/WaterSim.compute");

            if (sfWater == null || sfCaust == null || compute == null)
            {
                EditorUtility.DisplayDialog("WebGL Water",
                    "Could not find the shaders / compute shader. Make sure the WebGLWater/Shaders folder imported without errors, then try again.",
                    "OK");
                return;
            }

            // Optional shaders degrade gracefully but should say so, not fail silently.
            if (sfObstacle == null) Debug.LogWarning($"[WebGL Water] Shader '{ShaderObstacle}' not found; object->water displacement will be disabled.");
            if (sfReceiver == null) Debug.LogWarning($"[WebGL Water] Shader '{ShaderReceiver}' not found; the demo crate will use its default material.");
            if (buildAnalyticPool && sfPool == null) Debug.LogWarning($"[WebGL Water] Shader '{ShaderPoolWall}' not found; the analytic pool will be skipped.");

            // ---- meshes ----
            var gridMesh   = SaveAsset(BuildGrid(GridDetail),     Gen + "/WaterGrid.asset");

            // ---- textures ----
            var sky   = SaveCubemap(BuildSky(128), Gen + "/SkyCubemap.cubemap");
            var tiles = LoadOrBuildTiles(Gen + "/Tiles.png");

            // ---- materials ----
            // _Cull maps to UnityEngine.Rendering.CullMode: the above-water pass culls front
            // faces, the underwater pass and the pool interior cull back faces.
            float cullFront = (float)UnityEngine.Rendering.CullMode.Front;
            float cullBack = (float)UnityEngine.Rendering.CullMode.Back;
            var matAbove = SaveMaterial(MakeMat(sfWater, m => { m.SetFloat(PropUnderwater, 0f); m.SetFloat(PropCull, cullFront); }),
                                        Gen + "/WaterAbove.mat");
            var matUnder = SaveMaterial(MakeMat(sfWater, m => { m.SetFloat(PropUnderwater, 1f); m.SetFloat(PropCull, cullBack); }),
                                        Gen + "/WaterUnder.mat");

            Material matPool = null;
            if (buildAnalyticPool && sfPool != null)
                matPool = SaveMaterial(MakeMat(sfPool, m => m.SetFloat(PropCull, cullBack)), Gen + "/Pool.mat");

            // ---- scene objects ----
            var root = new GameObject("WebGL Water");

            var above = CreateRenderer("Water (above)", gridMesh, matAbove, root.transform);
            var under = CreateRenderer("Water (under)", gridMesh, matUnder, root.transform);

            GameObject poolGO = null;
            if (matPool != null)
            {
                poolGO = CreateRenderer("Analytic Pool", SaveAsset(BuildPool(), Gen + "/Pool.asset"), matPool, root.transform);
                poolGO.GetComponent<MeshRenderer>().receiveShadows = true; // catch object shadows
            }

            // ---- demo interaction: a floor to catch objects + a falling crate ----
            // (Phase 3a shows the water reacting; Phase 3b buoyancy will make it float.)
            var floor = new GameObject("Pool Floor Collider");
            floor.transform.SetParent(root.transform);
            floor.transform.position = new Vector3(0f, -1.05f, 0f);
            floor.AddComponent<BoxCollider>().size = new Vector3(2f, 0.1f, 2f);

            var crate = GameObject.CreatePrimitive(PrimitiveType.Cube); // brings a BoxCollider
            crate.name = "Floating Crate (demo)";
            crate.transform.SetParent(root.transform);
            crate.transform.position = new Vector3(0.15f, 0.7f, -0.1f);
            crate.transform.localScale = Vector3.one * 0.3f;
            if (sfReceiver != null)
            {
                var crateMat = new Material(sfReceiver);
                crateMat.SetColor(PropBaseColor, new Color(0.82f, 0.52f, 0.30f));
                crate.GetComponent<MeshRenderer>().sharedMaterial = crateMat;
            }
            var rb = crate.AddComponent<Rigidbody>();
            rb.mass = 0.4f;
            crate.AddComponent<WaterInteractable>();  // object -> water (displacement)
            crate.AddComponent<WaterBuoyancy>();       // water -> object (floats)
            crate.AddComponent<WaterSplash>();         // droplet burst on impact
            crate.AddComponent<WaterMembership>();      // lit by the lake it is actually in

            // ---- underwater god-ray volume (caustic-masked light shafts) ----
            var sfGodRays = Shader.Find(ShaderGodRays);
            GameObject godGO = null;
            if (sfGodRays != null)
            {
                // Pool-space box ([-1,0] in y, [-1,1] in x,z) with an IDENTITY transform;
                // the GodRays shader places it via the volume frame, like the surface/pool.
                godGO = CreateRenderer("God Rays", SaveAsset(BuildGodRayBox(), Gen + "/GodRayBox.asset"),
                                       new Material(sfGodRays), root.transform);
                var gmr = godGO.GetComponent<MeshRenderer>();
                gmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                gmr.receiveShadows = false;
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

            // planar reflection of the real scene across the water plane (y = 0).
            // Tickable per-material via "Use Planar Reflection"; harmless if unused.
            var planar = cam.GetComponent<PlanarReflection>();
            if (planar == null) planar = cam.gameObject.AddComponent<PlanarReflection>();
            planar.sourceCamera = cam;
            planar.waterHeight = 0f;
            // Off until a body opts into Planar mode (its OnEnable turns it on + tracks its
            // plane). Bodies default to SSR, so nothing samples the planar texture otherwise.
            planar.enableReflection = false;

            // Single directional light: drives the analytic water + caustics (via the
            // _LightDir global the controller publishes) AND casts real URP shadows.
            var sunGO = new GameObject("Sun");
            sunGO.transform.SetParent(root.transform);
            var sun = sunGO.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = 1.2f;
            // lightDir is "toward the light"; the light itself travels the opposite way.
            sun.transform.rotation = Quaternion.LookRotation(-new Vector3(2f, 2f, -1f).normalized);

            // Shared, fully editable splash particles (used by object impacts + mouse).
            // Select "Splash Particles" to tune the system or swap the droplet texture.
            var splashGO = new GameObject("Splash Particles");
            splashGO.transform.SetParent(root.transform);
            var splashPS = splashGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureForDrift(splashPS);
            var splashPSR = splashGO.GetComponent<ParticleSystemRenderer>();
            var sfSprite = Shader.Find(ShaderSpritesDefault);
            if (sfSprite != null)
            {
                var dm = new Material(sfSprite) { mainTexture = LoadOrBuildDroplet(Gen + "/Droplet.png") };
                dm.color = new Color(0.92f, 0.97f, 1f, 1f);
                splashPSR.sharedMaterial = dm;
            }
            splashPSR.renderMode = ParticleSystemRenderMode.Billboard;
            var splashEmitter = splashGO.AddComponent<WaterSplashEmitter>();
            splashEmitter.particles = splashPS;

            // Crown splash: a separate system that plays the splash flipbook once per
            // impact. Vertical billboard + base pivot so the crown stands on the water.
            var crownGO = new GameObject("Splash Crown");
            crownGO.transform.SetParent(root.transform);
            var crownPS = crownGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureCrown(crownPS, 8, 8);
            var crownPSR = crownGO.GetComponent<ParticleSystemRenderer>();
            crownPSR.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            crownPSR.pivot = new Vector3(0f, 0.5f, 0f);
            var crownSheet = LoadFlipbook(Gen + "/SplashFlipbook_8x8.png", TextureWrapMode.Clamp, false);
            if (sfSprite != null && crownSheet != null)
            {
                var cm = new Material(sfSprite) { mainTexture = crownSheet };
                cm.color = new Color(0.95f, 0.98f, 1f, 1f);
                crownPSR.sharedMaterial = cm;
            }
            splashEmitter.crownParticles = crownPS;

            // controller
            var ctrlGO = new GameObject("Water Controller");
            ctrlGO.transform.SetParent(root.transform);
            var ctrl = ctrlGO.AddComponent<WaterController>();
            ctrl.simCompute = compute;
            ctrl.causticsShader = sfCaust;
            ctrl.obstacleShader = sfObstacle;
            ctrl.waterMesh = gridMesh;
            ctrl.targetCamera = cam;
            ctrl.sun = sun;
            ctrl.orbit = orbit;
            ctrl.splashEmitter = splashEmitter;
            ctrl.tiles = tiles;
            ctrl.sky = sky;
            ctrl.quality = LoadOrCreateWaterQuality(Gen + "/WaterQuality.asset");

            // Multi-instance: this body drives its own renderers via a property block.
            ctrl.surfaceAbove = above.GetComponent<Renderer>();
            ctrl.surfaceUnder = under.GetComponent<Renderer>();
            if (poolGO != null) ctrl.poolRenderer = poolGO.GetComponent<Renderer>();
            if (godGO != null) ctrl.godRayRenderer = godGO.GetComponent<Renderer>();
            ctrl.isPrimary = true;

            Selection.activeObject = root;
            EditorUtility.SetDirty(ctrl);
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            AssetDatabase.SaveAssets();

            Debug.Log("[WebGL Water] Scene built. Press Play.  " +
                      (buildAnalyticPool ? "Analytic pool included." : "No pool created - using your own.") +
                      "  Assign your pool tile texture to the Water Controller's 'Tiles' field for matching reflections.");
        }

        // Adds a SECOND (non-primary) water body next to the primary, sharing the sun,
        // camera, compute and shaders. The new body renders through its own
        // MaterialPropertyBlock, so it must look independent - proof the de-globalisation
        // works. Move its "Frame" child to reposition; edit its extent for a different size.
        [MenuItem("Tools/WebGL Water/Add Water Body (secondary)")]
        static void AddSecondaryBody()
        {
            var all = Object.FindObjectsByType<WaterController>(FindObjectsSortMode.None);
            if (all == null || all.Length == 0)
            {
                Debug.LogError("[WebGL Water] Build the scene first (no WaterController found).");
                return;
            }
            WaterController primary = System.Array.Find(all, c => c.isPrimary) ?? all[0];

            var bodyRoot = new GameObject("Water Body (secondary)");

            // The frame IS the controller's transform. Offset it so the bodies sit side by side.
            var frameGO = new GameObject("Frame (WaterController)");
            frameGO.transform.SetParent(bodyRoot.transform);
            float offsetX = 2f * primary.volumeExtent.x + 1f;
            frameGO.transform.position = primary.transform.position + new Vector3(offsetX, 0f, 0f);

            var ctrl = frameGO.AddComponent<WaterController>();
            ctrl.simCompute = primary.simCompute;
            ctrl.causticsShader = primary.causticsShader;
            ctrl.obstacleShader = primary.obstacleShader;
            ctrl.waterMesh = primary.waterMesh;
            ctrl.targetCamera = primary.targetCamera;
            ctrl.sun = primary.sun;
            ctrl.tiles = primary.tiles;
            ctrl.sky = primary.sky;
            ctrl.quality = primary.quality; // share the scene's quality tier
            ctrl.volumeExtent = primary.volumeExtent;
            ctrl.isPrimary = false; // only ONE body mirrors to globals

            // Renderers live at world identity; the volume frame places them in the shader.
            var rendGO = new GameObject("Renderers");
            rendGO.transform.SetParent(bodyRoot.transform);
            ctrl.surfaceAbove = CloneBodyRenderer(primary.surfaceAbove, rendGO.transform, "Water (above)");
            ctrl.surfaceUnder = CloneBodyRenderer(primary.surfaceUnder, rendGO.transform, "Water (under)");
            ctrl.poolRenderer = CloneBodyRenderer(primary.poolRenderer, rendGO.transform, "Analytic Pool");
            ctrl.godRayRenderer = CloneBodyRenderer(primary.godRayRenderer, rendGO.transform, "God Rays");

            Selection.activeObject = bodyRoot;
            EditorUtility.SetDirty(ctrl);
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            Debug.Log("[WebGL Water] Secondary water body added. Move its 'Frame' child to reposition; " +
                      "edit that Water Controller's Volume Extent for a different size/shape.");
        }

        // Copy a body renderer (same mesh + material + world transform, so its object->world
        // maps to the same pool space as the source); per-body data arrives via the MPB.
        static Renderer CloneBodyRenderer(Renderer src, Transform parent, string name)
        {
            if (src == null) return null;
            var go = new GameObject(name);
            go.transform.SetParent(parent);
            go.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
            go.transform.localScale = src.transform.lossyScale;

            var srcFilter = src.GetComponent<MeshFilter>();
            if (srcFilter != null) go.AddComponent<MeshFilter>().sharedMesh = srcFilter.sharedMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = src.sharedMaterial;
            mr.shadowCastingMode = src.shadowCastingMode;
            mr.receiveShadows = src.receiveShadows;
            return mr;
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
            // Geometry is displaced into the XZ plane AND placed by the volume frame in
            // the shader, so use large explicit bounds to avoid wrong frustum culling
            // when the volume is scaled or offset.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
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
            // Placed by the volume frame in the shader; large bounds avoid wrong culling.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            return mesh;
        }

        // Closed box in POOL space: y in [-1,0], x,z in [-1,1]. Outward-wound (like a
        // primitive cube) so the GodRays pass's Cull Front renders the back faces. The
        // shader places it via the volume frame, so the bounds are made large to avoid
        // wrong frustum culling when the volume is scaled or offset.
        static Mesh BuildGodRayBox()
        {
            const float lo = -1f, hi = 0f;
            var v = new System.Collections.Generic.List<Vector3>();
            var t = new System.Collections.Generic.List<int>();

            void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
            {
                int i = v.Count;
                v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
                t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }

            Quad(new Vector3(-1, hi, -1), new Vector3(-1, hi, 1), new Vector3(1, hi, 1), new Vector3(1, hi, -1));   // +y
            Quad(new Vector3(-1, lo, -1), new Vector3(1, lo, -1), new Vector3(1, lo, 1), new Vector3(-1, lo, 1));   // -y
            Quad(new Vector3(-1, lo, -1), new Vector3(-1, hi, -1), new Vector3(1, hi, -1), new Vector3(1, lo, -1)); // -z
            Quad(new Vector3(1, lo, 1), new Vector3(1, hi, 1), new Vector3(-1, hi, 1), new Vector3(-1, lo, 1));     // +z
            Quad(new Vector3(-1, lo, 1), new Vector3(-1, hi, 1), new Vector3(-1, hi, -1), new Vector3(-1, lo, -1)); // -x
            Quad(new Vector3(1, lo, -1), new Vector3(1, hi, -1), new Vector3(1, hi, 1), new Vector3(1, lo, 1));     // +x

            var mesh = new Mesh { name = "GodRayBox" };
            mesh.SetVertices(v);
            mesh.SetTriangles(t, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
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

        // Soft round droplet sprite for the splash particles (swap for your own).
        static Texture2D LoadOrBuildDroplet(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int s = 64;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f;
                    float dy = (y + 0.5f) / s * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    a *= a; // soft round falloff
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path);
            var imp = (TextureImporter)AssetImporter.GetAtPath(path);
            imp.alphaIsTransparency = true;
            imp.wrapMode = TextureWrapMode.Clamp;
            imp.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // Load a pre-generated flipbook sheet from the Generated folder and apply the
        // import settings the foam shader / particle sheet expect. Returns null (and
        // leaves the feature off) if the PNG hasn't been generated.
        static Texture2D LoadFlipbook(string path, TextureWrapMode wrap, bool mipmaps)
        {
            if (!File.Exists(path)) return null;
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter imp)
            {
                imp.textureType = TextureImporterType.Default;
                imp.alphaIsTransparency = true;
                imp.wrapMode = wrap;
                imp.mipmapEnabled = mipmaps;
                imp.SaveAndReimport();
            }
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

        // Reuse the existing quality asset if present (so hand-tuned tiers survive a rebuild),
        // otherwise create one with the default tiers.
        static WaterQuality LoadOrCreateWaterQuality(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<WaterQuality>(path);
            if (existing != null) return existing;
            var q = ScriptableObject.CreateInstance<WaterQuality>();
            AssetDatabase.CreateAsset(q, path);
            return q;
        }
    }
}
