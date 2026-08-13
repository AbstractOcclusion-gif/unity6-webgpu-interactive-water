// WebGpuWater - editor-only resolution of this package's own root folder.
//
// WHY: every editor path into the package used to be written one of two ways - a hardcoded
// "Packages/com.abstractocclusion.webgpuwater/..." literal, or its own private call to
// PackageInfo.FindForAssembly. Both break on the Asset Store delivery path: a .unitypackage
// import lands the package under Assets/, where the Packages/ mount does not exist and
// FindForAssembly returns null (WaterWaveConstantsValidator depends on exactly that null to stay
// author-only - it must NOT use this type). Before this existed, the Water Wizard aborted on its
// first click with a dialog naming a path that could not exist.
//
// Resolution is three-tiered and is never worse than the behaviour it replaced:
//   1. PackageInfo - correct for embedded, registry and tarball installs.
//   2. This file's own script asset - the Asset Store case, where the package sits under Assets/.
//   3. The historical UPM literal, with one warning, so a delivery shape we have not seen degrades
//      to exactly what shipped before instead of throwing.
using System.IO;
using UnityEditor;
using UnityEngine;
// Alias: plain 'PackageInfo' is ambiguous inside UnityEditor code (legacy
// UnityEditor.PackageInfo also exists) - CS0104 without this.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterPackagePaths
    {
        // Tier 2 anchors on THIS file: <root>/Editor/WaterPackagePaths.cs -> <root>. The containing
        // folder is matched too, so a consumer's own same-named script cannot resolve the root to
        // their folder.
        const string AnchorScriptName = "WaterPackagePaths";
        const string AnchorFilter = AnchorScriptName + " t:MonoScript";
        const string EditorFolderName = "Editor";
        const string ScriptExtension = ".cs";

        // Tier 3: what every path in the package was hardcoded to before this type existed.
        const string LegacyUpmRoot = "Packages/com.abstractocclusion.webgpuwater";

        static string _assetRoot;
        static string _physicalRoot;
        static bool _warnedUnresolved;

        /// <summary>Project-relative package root ("Packages/&lt;id&gt;" or "Assets/&lt;folder&gt;"),
        /// for AssetDatabase calls. Never null.</summary>
        internal static string AssetRoot => _assetRoot ??= ResolveAssetRoot();

        /// <summary>Absolute filesystem package root, for File/Directory work on content Unity does
        /// not import (Samples~, Editor/WebGLTemplates~). Never null.</summary>
        internal static string PhysicalRoot => _physicalRoot ??= ResolvePhysicalRoot();

        /// <summary>Project-relative path to packaged content, for AssetDatabase.</summary>
        internal static string Asset(string packageRelativePath) => AssetRoot + "/" + packageRelativePath;

        /// <summary>Absolute path to packaged content, for File/Directory.</summary>
        internal static string Physical(string packageRelativePath) =>
            Path.Combine(PhysicalRoot, packageRelativePath);

        /// <summary>Installed package version, or null where there is no manifest to read it from
        /// (the Asset Store path). Callers must handle the null - it is not an error.</summary>
        internal static string Version => Package()?.version;

        static PackageInfo Package() => PackageInfo.FindForAssembly(typeof(WaterPackagePaths).Assembly);

        static string ResolveAssetRoot()
        {
            PackageInfo package = Package();
            if (package != null) return package.assetPath;

            string anchorFolder = FindAnchorEditorFolder();
            if (anchorFolder != null) return ParentFolder(anchorFolder);

            WarnUnresolvedOnce();
            return LegacyUpmRoot;
        }

        static string ResolvePhysicalRoot()
        {
            // resolvedPath is the only correct answer for a registry or tarball install, where the
            // package lives in the global cache rather than under the project.
            PackageInfo package = Package();
            if (package != null) return package.resolvedPath;

            // Both remaining tiers yield a project-relative path, and the project root is Unity's
            // working directory.
            return Path.GetFullPath(AssetRoot);
        }

        // Asset path of the Editor folder holding this script, or null when it cannot be found.
        static string FindAnchorEditorFolder()
        {
            foreach (string guid in AssetDatabase.FindAssets(AnchorFilter))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(ScriptExtension, System.StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileNameWithoutExtension(path) != AnchorScriptName) continue;

                string folder = ParentFolder(path);
                if (Path.GetFileName(folder) == EditorFolderName) return folder;
            }
            return null;
        }

        static string ParentFolder(string path) => Path.GetDirectoryName(path).Replace('\\', '/');

        static void WarnUnresolvedOnce()
        {
            if (_warnedUnresolved) return;
            _warnedUnresolved = true;
            Debug.LogWarning(WaterBuildKit.LogPrefix +
                             $"could not resolve the package folder; falling back to '{LegacyUpmRoot}'. " +
                             "Packaged shaders and textures will not load if the package was " +
                             "installed anywhere else.");
        }
    }
}
