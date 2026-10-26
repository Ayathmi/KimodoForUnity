// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Exports only this independent Editor plugin, with Unity metadata.</summary>
    public static class KimodoDistribution
    {
        /// <summary>Exports a local package to the configured external release directory.</summary>
        [MenuItem("Tools/Kimodo for Unity/Export unitypackage", priority = 30)]
        public static void ExportMenu()
        {
            try
            {
                var path = Export(KimodoConfiguration.Load());
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception ex) { EditorUtility.DisplayDialog("Kimodo for Unity", ex.Message, "OK"); }
        }

        /// <summary>Writes an explicitly scoped package without automatic dependency collection.</summary>
        public static string Export(KimodoConfiguration config)
        {
            KimodoPaths.ValidateRoot(config.assetsRoot);
            var definitions = Directory.GetFiles(Application.dataPath, "KimodoForUnity.Editor.asmdef", SearchOption.AllDirectories);
            if (definitions.Length != 1) throw new InvalidOperationException("Expected exactly one KimodoForUnity.Editor assembly.");
            var root = Directory.GetParent(Path.GetDirectoryName(definitions[0])).FullName;
            var relative = "Assets" + root.Substring(Application.dataPath.Length).Replace('\\', '/');
            var output = KimodoPaths.Child(config.assetsRoot, "Releases",
                "KimodoForUnity-" + KimodoConfiguration.Version + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".unitypackage");
            if (File.Exists(output)) throw new IOException("A package with this name already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            AssetDatabase.ExportPackage(relative, output, ExportPackageOptions.Recurse);
            return output;
        }
    }
}
