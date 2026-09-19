// WebGpuWater - editor side of the third-party scene-fog hook (Runtime/Shaders/WaterThirdPartyFog.hlsl).
//
// The hook header carries ONE wizard-managed block. This class is the only writer of that block
// and reads the state back by parsing it - the file IS the setting, nothing else is serialised.
// The Enviro maths is ported into the package (WaterEnviro3Fog.hlsl), so the block holds a bare
// #define; detection and the upstream-drift check below exist for the UI only.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterThirdPartyFogSetup
    {
        internal const string HookAssetPath = "Runtime/Shaders/WaterThirdPartyFog.hlsl";
        const string BlockBegin = "// ---- BEGIN WIZARD-MANAGED BLOCK";
        const string BlockEnd = "// ---- END WIZARD-MANAGED BLOCK ----";
        const string DisabledLine = "// (no third-party fog enabled)";
        const string Enviro3Define = "WATER_ENVIRO3_FOG";

        // Upstream files the port was written against (Enviro 3.3.2b). MD5 is a change detector
        // here, not a security boundary.
        const string Enviro3ProbeFilter = "FogInclude";
        static readonly (string file, string md5)[] Enviro3Pinned =
        {
            ("FogInclude.cginc", "f5cf66b81ff2d1e6803cbdec42ec2c2c"),
            ("SkyInclude.cginc", "70c9454d4a33ba83b3b9dd5cf3cb6ad4"),
        };

        internal static bool HookExists => File.Exists(HookPhysicalPath);
        static string HookPhysicalPath => WaterPackagePaths.Physical(HookAssetPath);

        /// <summary>Folder (asset path) holding Enviro 3's shader includes, or null when not installed.</summary>
        internal static string FindEnviro3IncludeFolder()
        {
            foreach (string guid in AssetDatabase.FindAssets(Enviro3ProbeFilter))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/" + Enviro3Pinned[0].file, StringComparison.Ordinal)) continue;
                return Path.GetDirectoryName(path)?.Replace('\\', '/');
            }
            return null;
        }

        /// <summary>Names of the pinned upstream files whose content differs from the port's reference; empty when in sync.</summary>
        internal static string[] Enviro3DriftedFiles(string includeFolder)
        {
            if (includeFolder == null) return Array.Empty<string>();
            var drifted = new System.Collections.Generic.List<string>();
            foreach (var (file, md5) in Enviro3Pinned)
            {
                string path = includeFolder + "/" + file;
                if (!File.Exists(path) || !string.Equals(Md5(path), md5, StringComparison.OrdinalIgnoreCase))
                    drifted.Add(file);
            }
            return drifted.ToArray();
        }

        internal static bool ReadEnviro3Enabled()
        {
            if (!HookExists) return false;
            string block = ReadBlock(File.ReadAllText(HookPhysicalPath));
            return block != null && block.Contains("#define " + Enviro3Define);
        }

        /// <summary>Rewrites the managed block and reimports the hook. Returns false with a reason on failure.</summary>
        internal static bool SetEnviro3(bool enabled, out string error)
        {
            error = null;
            if (!HookExists) { error = "Hook header not found: " + HookAssetPath; return false; }
            if (enabled && FindEnviro3IncludeFolder() == null)
            {
                error = "Enviro 3 not found in this project (looked for " + Enviro3Pinned[0].file + ").";
                return false;
            }

            string text = File.ReadAllText(HookPhysicalPath);
            int begin = text.IndexOf(BlockBegin, StringComparison.Ordinal);
            int beginLineEnd = begin >= 0 ? text.IndexOf('\n', begin) : -1;
            int end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
            if (begin < 0 || beginLineEnd < 0 || end < beginLineEnd)
            {
                error = "Managed block markers missing in " + HookAssetPath + " - restore the file from the package.";
                return false;
            }
            string body = enabled ? "#define " + Enviro3Define + " 1" : DisabledLine;
            string rewritten = text.Substring(0, beginLineEnd + 1) + body + "\n" + text.Substring(end);
            if (rewritten == text) return true;

            File.WriteAllText(HookPhysicalPath, rewritten);
            AssetDatabase.ImportAsset(WaterPackagePaths.Asset(HookAssetPath), ImportAssetOptions.ForceUpdate);
            Debug.Log("[WebGpuWater] Third-party fog hook " + (enabled ? "enabled (Enviro 3)" : "disabled") +
                      " - water shaders recompiling.");
            return true;
        }

        static string ReadBlock(string text)
        {
            int begin = text.IndexOf(BlockBegin, StringComparison.Ordinal);
            int end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
            return begin >= 0 && end > begin ? text.Substring(begin, end - begin) : null;
        }

        static string Md5(string path)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            byte[] hash = md5.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
