// WebGL Water - main controller (Unity 6 / URP port)
// Drives the simulation, caustics, sphere physics, mouse interaction and the
// orbiting camera. Port of main.js / renderer.js by Evan Wallace (MIT).
//
// Coordinate convention (identical to the original demo):
//   - water surface at y = 0, pool spans x,z in [-1, 1], floor at y = -1.
//   - light points toward the light source; default normalize(2, 2, -1).
using UnityEngine;
using UnityEngine.Rendering;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace WebGLWater
{
    [DefaultExecutionOrder(-50)]
    public class WaterController : MonoBehaviour
    {
        [Header("Assigned by the scene builder")]
        public ComputeShader simCompute;
        public Shader causticsShader;
        public Mesh waterMesh;            // XY grid plane, [-1,1], shared with the water surface renderers
        public Camera targetCamera;
        public Transform sphere;          // unit-sphere renderer; positioned at the sphere center

        [Header("Look / surfaces")]
        public Texture tiles;             // pool tile albedo sampled by the water reflection (assign your own)
        public Cubemap sky;               // sky cubemap for above-water reflections

        [Header("Simulation")]
        public Vector3 lightDir = new Vector3(2f, 2f, -1f);
        public float sphereRadius = 0.25f;
        public Vector3 sphereStart = new Vector3(-0.4f, -0.75f, 0.2f);
        public int causticResolution = 1024;
        public bool spherePhysics = false;

        [Header("Ripple tuning")]
        [Tooltip("Propagation stiffness. Higher = faster waves. Stable up to ~2.0.")]
        [Range(0.1f, 2.0f)] public float waveSpeed = 2.0f;
        [Tooltip("Velocity damping per step. Lower = ripples die out faster.")]
        [Range(0.90f, 1.0f)] public float damping = 0.995f;
        [Tooltip("Simulation sub-steps per frame. More = faster, smoother propagation.")]
        [Range(1, 8)] public int stepsPerFrame = 2;
        [Tooltip("Height added by a click/drag ripple (deformation intensity).")]
        [Range(0.001f, 0.08f)] public float rippleStrength = 0.01f;
        [Tooltip("Radius of a click/drag ripple in pool space.")]
        [Range(0.005f, 0.2f)] public float rippleRadius = 0.03f;
        [Tooltip("Seed the pool with random ripples on start.")]
        public bool seedRipplesOnStart = true;
        [Tooltip("Keep total water volume constant so the surface doesn't drift up/down.")]
        public bool conserveVolume = true;

        [Header("Caustics")]
        public Shader causticBlurShader;
        [Tooltip("Caustic blur radius in texels. 0 = off (crisp, original look).")]
        [Range(0f, 4f)] public float causticSmoothness = 1.2f;

        [Header("Camera")]
        public OrbitCamera orbit;

        // runtime
        WaterSimulation _water;
        Material _causticMat;
        Material _blurMat;
        RenderTexture _causticRT;
        RenderTexture _causticTmp;
        RenderTexture _heightMip;
        CommandBuffer _cb;

        Vector3 _center, _oldCenter, _velocity;
        readonly Vector3 _gravity = new Vector3(0f, -4f, 0f);
        bool _paused;

        // interaction
        const int MODE_NONE = -1, MODE_ADD_DROPS = 0, MODE_MOVE_SPHERE = 1, MODE_ORBIT = 2;
        int _mode = MODE_NONE;
        Vector3 _prevHit, _planeNormal;
        Vector2 _oldMouse;

        // shader global ids
        static readonly int ID_Water = Shader.PropertyToID("_WaterTex");
        static readonly int ID_Caustic = Shader.PropertyToID("_CausticTex");
        static readonly int ID_Tiles = Shader.PropertyToID("_Tiles");
        static readonly int ID_Sky = Shader.PropertyToID("_Sky");
        static readonly int ID_Light = Shader.PropertyToID("_LightDir");
        static readonly int ID_SphereC = Shader.PropertyToID("_SphereCenter");
        static readonly int ID_SphereR = Shader.PropertyToID("_SphereRadius");

        void OnEnable()
        {
            if (simCompute == null) { Debug.LogError("WaterController: simCompute not assigned."); enabled = false; return; }

            _water = new WaterSimulation(simCompute);

            _causticMat = new Material(causticsShader);
            _causticRT = new RenderTexture(causticResolution, causticResolution, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "CausticTex"
            };
            _causticRT.Create();
            _causticTmp = new RenderTexture(_causticRT.descriptor) { name = "CausticTmp" };
            _causticTmp.Create();
            if (causticBlurShader != null) _blurMat = new Material(causticBlurShader);
            _cb = new CommandBuffer { name = "WebGLWater.Caustics" };

            _heightMip = new RenderTexture(WaterSimulation.Resolution, WaterSimulation.Resolution, 0, RenderTextureFormat.RFloat)
            {
                useMipMap = true,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "HeightMip"
            };
            _heightMip.Create();

            _center = _oldCenter = sphereStart;
            _velocity = Vector3.zero;

            // seed the pool with a few ripples
            if (seedRipplesOnStart)
                for (int i = 0; i < 20; i++)
                    _water.AddDrop(Random.value * 2f - 1f, Random.value * 2f - 1f, 0.03f, (i & 1) == 1 ? 0.01f : -0.01f);

            if (targetCamera != null)
            {
                targetCamera.fieldOfView = 45f;
                targetCamera.nearClipPlane = 0.01f;
                targetCamera.farClipPlane = 100f;
            }

            Shader.SetGlobalVector(ID_Light, lightDir.normalized);
            Shader.SetGlobalFloat(ID_SphereR, sphereRadius);
            if (tiles != null) Shader.SetGlobalTexture(ID_Tiles, tiles);
            if (sky != null) Shader.SetGlobalTexture(ID_Sky, sky);
        }

        void OnDisable()
        {
            _water?.Dispose();
            if (_causticRT != null) _causticRT.Release();
            if (_causticTmp != null) _causticTmp.Release();
            if (_heightMip != null) _heightMip.Release();
            _cb?.Release();
        }

        void Update()
        {
            HandleKeys();
            HandleMouse();

            float dt = Time.deltaTime;
            if (!_paused) Step(dt);

            // publish globals for the surface / pool / sphere shaders
            Shader.SetGlobalTexture(ID_Water, _water.Texture);
            Shader.SetGlobalVector(ID_Light, lightDir.normalized);
            Shader.SetGlobalVector(ID_SphereC, _center);
            Shader.SetGlobalFloat(ID_SphereR, sphereRadius);

            if (sphere != null)
            {
                sphere.position = _center;
                sphere.localScale = Vector3.one * sphereRadius;
            }

            UpdateCaustics();
        }

        void Step(float seconds)
        {
            if (seconds > 1f) return;

            if (_mode == MODE_MOVE_SPHERE)
            {
                _velocity = Vector3.zero;
            }
            else if (spherePhysics)
            {
                float percentUnderWater = Mathf.Clamp01((sphereRadius - _center.y) / (2f * sphereRadius));
                _velocity += _gravity * (seconds - 1.1f * seconds * percentUnderWater);
                _velocity -= _velocity.normalized * (percentUnderWater * seconds * Vector3.Dot(_velocity, _velocity));
                _center += _velocity * seconds;
                if (_center.y < sphereRadius - 1f)
                {
                    _center.y = sphereRadius - 1f;
                    _velocity.y = Mathf.Abs(_velocity.y) * 0.7f;
                }
            }

            _water.MoveSphere(_oldCenter, _center, sphereRadius);
            _oldCenter = _center;

            int steps = Mathf.Max(1, stepsPerFrame);
            for (int i = 0; i < steps; i++)
                _water.StepSimulation(waveSpeed, damping);

            if (conserveVolume)
            {
                Graphics.Blit(_water.Texture, _heightMip); // copy height (R) into the mipped RT
                _heightMip.GenerateMips();                 // top 1x1 mip = mean height
                _water.ConserveVolume(_heightMip);         // subtract the mean
            }

            _water.UpdateNormals();
        }

        void UpdateCaustics()
        {
            _cb.Clear();
            _cb.SetRenderTarget(_causticRT);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(waterMesh, Matrix4x4.identity, _causticMat, 0, 0);

            // optional separable blur to soften the caustics
            if (_blurMat != null && causticSmoothness > 0.001f)
            {
                _cb.SetGlobalVector("_BlurDir", new Vector4(causticSmoothness, 0f, 0f, 0f));
                _cb.Blit(_causticRT, _causticTmp, _blurMat, 0);
                _cb.SetGlobalVector("_BlurDir", new Vector4(0f, causticSmoothness, 0f, 0f));
                _cb.Blit(_causticTmp, _causticRT, _blurMat, 0);
            }

            Graphics.ExecuteCommandBuffer(_cb);
            Shader.SetGlobalTexture(ID_Caustic, _causticRT);
        }

        // ---- camera ---------------------------------------------------------
        Ray PixelRay(Vector2 p)
        {
            return targetCamera.ScreenPointToRay(new Vector3(p.x, p.y, 0f));
        }

        // ---- interaction ----------------------------------------------------
        void HandleMouse()
        {
            Vector2 m = MousePos();

            if (MouseDown())
            {
                _oldMouse = m;
                Ray ray = PixelRay(m);
                Vector3 eye = ray.origin;
                Vector3 d = ray.direction;

                Vector3 pointOnPlane = eye + d * (-eye.y / d.y); // intersect y = 0
                float st = IntersectSphere(eye, d, _center, sphereRadius);

                if (st < 1e6f)
                {
                    _mode = MODE_MOVE_SPHERE;
                    _prevHit = eye + d * st;
                    _planeNormal = -targetCamera.transform.forward;
                }
                else if (Mathf.Abs(pointOnPlane.x) < 1f && Mathf.Abs(pointOnPlane.z) < 1f)
                {
                    _mode = MODE_ADD_DROPS;
                    DuringDrag(m);
                }
                else
                {
                    _mode = MODE_ORBIT;
                }
            }
            else if (MouseHeld())
            {
                DuringDrag(m);
            }
            else if (MouseUp())
            {
                _mode = MODE_NONE;
            }
        }

        void DuringDrag(Vector2 m)
        {
            switch (_mode)
            {
                case MODE_ADD_DROPS:
                {
                    Ray ray = PixelRay(m);
                    Vector3 eye = ray.origin, d = ray.direction;
                    Vector3 p = eye + d * (-eye.y / d.y);
                    _water.AddDrop(p.x, p.z, rippleRadius, rippleStrength);
                    break;
                }
                case MODE_MOVE_SPHERE:
                {
                    Ray ray = PixelRay(m);
                    Vector3 eye = ray.origin, d = ray.direction;
                    float t = -Vector3.Dot(_planeNormal, eye - _prevHit) / Vector3.Dot(_planeNormal, d);
                    Vector3 nextHit = eye + d * t;
                    _center += nextHit - _prevHit;
                    _center.x = Mathf.Clamp(_center.x, sphereRadius - 1f, 1f - sphereRadius);
                    _center.y = Mathf.Clamp(_center.y, sphereRadius - 1f, 10f);
                    _center.z = Mathf.Clamp(_center.z, sphereRadius - 1f, 1f - sphereRadius);
                    _prevHit = nextHit;
                    break;
                }
                case MODE_ORBIT:
                {
                    if (orbit != null) orbit.Rotate(m.x - _oldMouse.x, m.y - _oldMouse.y);
                    break;
                }
            }
            _oldMouse = m;
        }

        static float IntersectSphere(Vector3 origin, Vector3 ray, Vector3 c, float r)
        {
            Vector3 toSphere = origin - c;
            float a = Vector3.Dot(ray, ray);
            float b = 2f * Vector3.Dot(toSphere, ray);
            float cc = Vector3.Dot(toSphere, toSphere) - r * r;
            float disc = b * b - 4f * a * cc;
            if (disc > 0f)
            {
                float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
                if (t > 0f) return t;
            }
            return 1e6f;
        }

        void HandleKeys()
        {
            if (KeySpaceDown()) _paused = !_paused;
            if (KeyGDown()) spherePhysics = !spherePhysics;
            if (KeyLHeld() && targetCamera != null)
                lightDir = -targetCamera.transform.forward;
        }

        // ---- input abstraction (works with new or legacy input) -------------
        static Vector2 MousePos()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
#else
            return Input.mousePosition;
#endif
        }
        static bool MouseDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }
        static bool MouseHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.isPressed;
#else
            return Input.GetMouseButton(0);
#endif
        }
        static bool MouseUp()
        {
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;
#else
            return Input.GetMouseButtonUp(0);
#endif
        }
        static bool KeySpaceDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Space);
#endif
        }
        static bool KeyGDown()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.gKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.G);
#endif
        }
        static bool KeyLHeld()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && Keyboard.current.lKey.isPressed;
#else
            return Input.GetKey(KeyCode.L);
#endif
        }
    }
}
