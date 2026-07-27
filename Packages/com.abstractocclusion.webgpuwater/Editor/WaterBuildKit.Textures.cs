// WebGpuWater build kit - procedurally generated and package-provisioned textures: the sky
// cubemap, the tile pattern, and the foam/droplet sheets copied out of the package on demand.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // ---------------------------------------------------------------- textures
        internal static Cubemap BuildSky(int size)
        {
            // WITH a mip chain: the water surface samples this cube at a roughness-driven mip
            // (texCUBElod in WaterSurface.shader), so a rough/distant surface reflects a BLURRED
            // sky. Without mips that lod sample silently clamps to mip 0 and the blur is dead.
            // Apply() below regenerates the mips from the authored mip 0 (its default).
            var cube = new Cubemap(size, TextureFormat.RGB24, true);
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

        // Procedural sky palette + gradient curvature (pow eases the blend toward the horizon).
        static readonly Color SkyHorizonColor = new Color(0.78f, 0.86f, 0.96f);
        static readonly Color SkyZenithColor = new Color(0.26f, 0.47f, 0.86f);
        static readonly Color SkyGroundColor = new Color(0.30f, 0.30f, 0.33f);
        const float SkyZenithCurve = 0.6f;
        const float SkyGroundCurve = 0.5f;

        static Color SkyColor(Vector3 dir)
        {
            if (dir.y >= 0f) return Color.Lerp(SkyHorizonColor, SkyZenithColor, Mathf.Pow(dir.y, SkyZenithCurve));
            return Color.Lerp(SkyHorizonColor, SkyGroundColor, Mathf.Pow(-dir.y, SkyGroundCurve));
        }

        internal static Texture2D LoadOrBuildTiles(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            const int TextureSize = 256;
            const int TileCellSize = 32;         // pixels per tile
            const int GroutWidthPixels = 2;
            const float NoiseFloor = 0.85f;      // brightness variation: floor + amplitude * Perlin
            const float NoiseAmplitude = 0.15f;
            const float NoiseFrequency = 0.08f;
            Color tileColor = new Color(0.55f, 0.75f, 0.85f);
            Color groutColor = new Color(0.30f, 0.45f, 0.55f);

            var tex = new Texture2D(TextureSize, TextureSize, TextureFormat.RGB24, true);
            for (int y = 0; y < TextureSize; y++)
                for (int x = 0; x < TextureSize; x++)
                {
                    bool grout = (x % TileCellSize < GroutWidthPixels) || (y % TileCellSize < GroutWidthPixels);
                    float n = NoiseFloor + NoiseAmplitude * Mathf.PerlinNoise(x * NoiseFrequency, y * NoiseFrequency);
                    tex.SetPixel(x, y, grout ? groutColor : tileColor * n);
                }
            tex.Apply();
            return SavePngAsset(path, tex, imp =>
            {
                imp.wrapMode = TextureWrapMode.Repeat;
                imp.mipmapEnabled = true;
            });
        }

        // Packed droplet sprite (KWS channel layout, consumed by SplashParticles' packed path):
        // R = mass (round falloff), G = shine (tight hot core, cubed in the shader),
        // B = dissolve noise (lifetime burn threshold), A = thickness (soft-fade band).
        static Texture2D LoadOrBuildDroplet(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            // 128: the 64px original went soft on big hero droplets (velocity-stretched
            // sprites magnify it further). All maths below are sprite-space, so the bump is
            // resolution-independent. Delete Generated/DropletPacked.png to regenerate.
            const int s = 128;
            const float ShineFalloffPower = 6f;   // hot core confined near the centre
            const float NoiseFrequency = 9f;      // dissolve-noise feature size across the sprite
            const float NoiseFloor = 0.15f;       // keeps every texel erodable (never sticks at 0)
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, true);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = (x + 0.5f) / s * 2f - 1f;
                    float dy = (y + 0.5f) / s * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    float mass = a * a;
                    float shine = Mathf.Pow(a, ShineFalloffPower);
                    float noise = NoiseFloor + (1f - NoiseFloor)
                                * Mathf.PerlinNoise(x / (float)s * NoiseFrequency,
                                                    y / (float)s * NoiseFrequency);
                    tex.SetPixel(x, y, new Color(mass, shine, noise, mass));
                }
            tex.Apply();
            return SavePngAsset(path, tex, imp =>
            {
                imp.sRGBTexture = false;          // channel-packed DATA, not color
                imp.alphaIsTransparency = false;  // A is thickness, not coverage
                imp.wrapMode = TextureWrapMode.Clamp;
            });
        }

        // 'linear' is for data textures (e.g. the raw-RGB foam normal map): sRGB sampling
        // would distort the decoded vectors.
        static Texture2D LoadFlipbook(string path, TextureWrapMode wrap, bool mipmaps, bool linear = false)
        {
            if (!File.Exists(path)) return null;
            AssetDatabase.ImportAsset(path);
            if (AssetImporter.GetAtPath(path) is TextureImporter imp)
            {
                imp.textureType = TextureImporterType.Default;
                imp.sRGBTexture = !linear;
                imp.alphaIsTransparency = !linear;
                imp.wrapMode = wrap;
                imp.mipmapEnabled = mipmaps;
                imp.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // The crown sheets are authored 8x8 art assets (not procedurally buildable like the
        // droplet), and they live only in the package's Samples~ folder, which Unity does not
        // import. So a project that never imported the demos has nothing for LoadFlipbook to
        // load and the crown renders untextured. Provision each once by copying the packaged
        // source into Generated, then load it with the crown's import settings (packed DATA
        // sheet -> linear, clamped, no mips).
        static Texture2D LoadOrProvisionPackagedSheet(string genPath, string packageRelativePath)
        {
            if (!File.Exists(genPath))
            {
                string packagedSheet = PackagedSheetPath(packageRelativePath);
                if (packagedSheet == null || !File.Exists(packagedSheet))
                {
                    Debug.LogWarning($"WebGpuWater: crown sheet not found in the package " +
                                     $"('{packageRelativePath}'); the splash crown will miss this sheet.");
                    return null;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(genPath));
                File.Copy(packagedSheet, genPath, overwrite: false);
                AssetDatabase.ImportAsset(genPath);
            }

            return LoadFlipbook(genPath, TextureWrapMode.Clamp, mipmaps: false, linear: true);
        }

        // Physical path of a packaged sheet inside the package's Samples~ folder. resolvedPath
        // differs between embedded and registry/tarball installs, so it is resolved via the
        // package system rather than assumed to sit under the project's Packages folder.
        static string PackagedSheetPath(string packageRelativePath)
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(WaterBuildKit).Assembly);
            return package == null ? null : Path.Combine(package.resolvedPath, packageRelativePath);
        }

    }
}
