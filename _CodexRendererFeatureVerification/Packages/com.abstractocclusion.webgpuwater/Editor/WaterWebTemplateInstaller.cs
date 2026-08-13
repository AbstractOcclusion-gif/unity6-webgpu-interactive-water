// WebGpuWater - editor utility: install the WebGPU gate web template into the PROJECT.
//
// Why an installer and not just a template in the package: Unity only discovers WebGL
// templates under Assets/WebGLTemplates/, and the PROJECT: template setting cannot point
// into Packages/ - a UPM package can NOT contribute a web template directly. Without the
// gate, a WebGPU build ships on Unity's default template and hard-crashes in browsers
// without WebGPU (older mobiles especially) instead of showing the friendly support
// message. So the template travels inside the package as raw content (a ~ folder the
// importer ignores) and this one-click menu item copies it into the user's project and
// selects it in Player Settings.
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static class WaterWebTemplateInstaller
    {
        const string MenuPath = WaterBuildKit.MenuRoot + "Install WebGPU Web Template";
        const string TemplateName = "WebGPUGate";
        // Package-side source: shipped with the package, invisible to the asset importer.
        const string PackageTemplateFolder = "Editor/WebGLTemplates~/" + TemplateName;
        // Unity's fixed discovery root for project-owned web templates.
        const string ProjectTemplatesRoot = "Assets/WebGLTemplates";
        // PlayerSettings.WebGL.template value for a template living in Assets.
        const string TemplateSetting = "PROJECT:" + TemplateName;

        [MenuItem(MenuPath)]
        static void Install()
        {
            string source = WaterPackagePaths.Physical(PackageTemplateFolder);
            if (!Directory.Exists(source))
            {
                Debug.LogError($"WebGpuWater: template source missing at '{source}'; " +
                               "reinstall the package.");
                return;
            }

            string destination = Path.Combine(ProjectTemplatesRoot, TemplateName);
            CopyDirectory(source, destination);
            AssetDatabase.Refresh();
            PlayerSettings.WebGL.template = TemplateSetting;
            Debug.Log($"WebGpuWater: installed the {TemplateName} web template to " +
                      $"'{destination}' and selected it in Player Settings. WebGPU builds " +
                      "now show a browser-support message instead of crashing where " +
                      "WebGPU is unavailable.");
        }

        // Recursive raw copy (overwrite): the template is plain web content, so the file
        // APIs handle it and the single Refresh above imports the result.
        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string folder in Directory.GetDirectories(source))
                CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
        }
    }
}
