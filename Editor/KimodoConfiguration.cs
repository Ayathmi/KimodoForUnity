// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Local preferences, never included in the distributed plugin.</summary>
    [Serializable]
    public sealed class KimodoConfiguration
    {
        /// <summary>Configuration schema.</summary>
        public int schemaVersion = 6;
        /// <summary>External asset root selected by the user.</summary>
        public string assetsRoot = "";
        /// <summary>Existing user-managed interpreter.</summary>
        public string pythonPath = "";
        /// <summary>Dedicated Python environment installation directory.</summary>
        public string installRoot = "";
        /// <summary>User-selected Ninja executable used for native builds.</summary>
        public string ninjaPath = "";
        /// <summary>User-selected MinGW-w64 C compiler.</summary>
        public string gccPath = "";
        /// <summary>User-selected MinGW-w64 C++ compiler.</summary>
        public string gxxPath = "";
        /// <summary>User-installed Ollama executable; the plugin never installs the client.</summary>
        public string ollamaPath = "";
        /// <summary>Dedicated Ollama model storage used by an isolated download server.</summary>
        public string ollamaModelsPath = "";
        /// <summary>User-selected local model repository directories.</summary>
        public KimodoModelLocation[] models = CreateModels();
        /// <summary>Explicit opt-in fallback; CUDA remains the default.</summary>
        public bool cpuTextEncoder;
        /// <summary>Loopback service port.</summary>
        public int port = 9550;
        /// <summary>Editor interface language.</summary>
        public bool chinese = true;
        /// <summary>Idle service timeout in minutes; zero disables the timer, not task-boundary release.</summary>
        public int idleMinutes = 10;
        /// <summary>Selected external root preference key.</summary>
        public const string RootPreference = "KimodoForUnity.ExternalRoot";
        /// <summary>Fixed supported motion model.</summary>
        public const string Model = "Kimodo-SOMA-RP-v1.2";
        /// <summary>Plugin release version.</summary>
        public const string Version = "0.2.0";

        /// <summary>Loads settings without creating folders or starting processes.</summary>
        public static KimodoConfiguration Load()
        {
            var root = EditorPrefs.GetString(RootPreference, "");
            if (string.IsNullOrEmpty(root))
                return new KimodoConfiguration();
            try
            {
                KimodoPaths.ValidateRoot(root);
                var path = Path.Combine(root, "Config", "unity-settings.json");
                if (!File.Exists(path))
                    return new KimodoConfiguration { assetsRoot = root };
                if (new FileInfo(path).Length > 65536)
                    throw new InvalidDataException("Settings exceed 64 KiB.");
                var result = JsonUtility.FromJson<KimodoConfiguration>(File.ReadAllText(path));
                if (result == null || result.schemaVersion < 1 || result.schemaVersion > 6)
                    throw new InvalidDataException("Unsupported settings schema.");
                result.assetsRoot = root;
                result.Normalize();
                return result;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Kimodo settings: " + ex.Message);
                return new KimodoConfiguration { assetsRoot = root };
            }
        }

        /// <summary>Saves only explicitly edited local preferences.</summary>
        public void Save()
        {
            KimodoPaths.ValidateRoot(assetsRoot);
            Normalize();
            KimodoPaths.ValidateRoot(installRoot);
            if (port < 1024 || port > 65535)
                throw new ArgumentException("Service port must be 1024-65535.");
            if (idleMinutes < 0 || idleMinutes > 240)
                throw new ArgumentException("Idle release must be 0-240 minutes.");
            assetsRoot = Path.GetFullPath(assetsRoot);
            KimodoFiles.WriteJson(Path.Combine(assetsRoot, "Config", "unity-settings.json"), this);
            EditorPrefs.SetString(RootPreference, assetsRoot);
        }

        /// <summary>Resolves the installed interpreter, with the selected base as a pre-install fallback.</summary>
        public string RuntimePython => File.Exists(InstalledPython) ? InstalledPython : pythonPath;
        /// <summary>Interpreter created inside the selected installation directory.</summary>
        public string InstalledPython => Path.Combine(installRoot ?? "", "Scripts", "python.exe");
        /// <summary>Migrates local preferences without selecting tools or changing downloaded files.</summary>
        public void Normalize()
        {
            schemaVersion = 6;
            ninjaPath = ninjaPath ?? "";
            gccPath = gccPath ?? "";
            gxxPath = gxxPath ?? "";
            if (string.IsNullOrEmpty(ollamaPath)) ollamaPath = KimodoModelDownloader.FindOllama();
            if (string.IsNullOrEmpty(ollamaModelsPath) && !string.IsNullOrEmpty(assetsRoot))
                ollamaModelsPath = Path.Combine(assetsRoot, "Models", "Ollama");
            if (string.IsNullOrEmpty(installRoot) && !string.IsNullOrEmpty(assetsRoot))
                installRoot = Path.Combine(assetsRoot, "Environments", "kimodo-py314");
            if (models == null) models = CreateModels();
            else if (models.Length != KimodoModelFiles.Repositories.Length)
                Array.Resize(ref models, KimodoModelFiles.Repositories.Length);
            for (var i = 0; i < models.Length; i++)
            {
                if (models[i] == null) models[i] = new KimodoModelLocation();
                if (string.IsNullOrEmpty(models[i].directory) && !string.IsNullOrEmpty(assetsRoot))
                    models[i].directory = i == 0 ? Path.Combine(assetsRoot, "Models", Model)
                        : Path.Combine(assetsRoot, "Models", "TextEncoders", i == 3 ? "Ollama-Llama3-8B-Instruct-FP16" : KimodoModelFiles.Repositories[i]);
            }
        }

        private static KimodoModelLocation[] CreateModels()
            => new[] { new KimodoModelLocation(), new KimodoModelLocation(), new KimodoModelLocation(), new KimodoModelLocation() };
        /// <summary>Returns a private snapshot for asynchronous operations.</summary>
        public KimodoConfiguration Copy() => JsonUtility.FromJson<KimodoConfiguration>(JsonUtility.ToJson(this));
    }

    /// <summary>Canonical path checks shared by GUI, processes and distribution.</summary>
    public static class KimodoPaths
    {
        /// <summary>Tests a complete directory boundary, not a string prefix.</summary>
        public static bool Within(string path, string parent)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Rejects project roots, drive roots and reparse-point ancestors.</summary>
        public static void ValidateRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
                throw new ArgumentException("Choose an absolute external assets directory.");
            var full = Path.GetFullPath(root);
            if (full.TrimEnd('\\', '/').Equals(Path.GetPathRoot(full).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A drive root is not an assets directory.");
            var project = Directory.GetParent(Application.dataPath).FullName;
            if (Within(full, project) || Within(project, full))
                throw new ArgumentException("Assets storage must be separate from the Unity project.");
            for (var item = new DirectoryInfo(full); item != null; item = item.Parent)
            {
                if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Use a direct path without junctions or symbolic links.");
            }
            var name = new DirectoryInfo(full).Name.Replace(" ", "");
            if (name.Equals("KimodoForUnity", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The plugin source directory cannot store environments or models.");
        }

        /// <summary>Validates output containment including existing link ancestors.</summary>
        public static string Child(string root, params string[] parts)
        {
            var value = root;
            foreach (var part in parts)
                value = Path.Combine(value, part);
            value = Path.GetFullPath(value);
            if (!Within(value, root))
                throw new ArgumentException("Path leaves its allowed directory.");
            for (var item = new DirectoryInfo(Path.GetDirectoryName(value)); item != null && Within(item.FullName, root); item = item.Parent)
                if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Output path contains a junction or symbolic link.");
            return value;
        }
    }

    /// <summary>Bounded, versioned JSON and atomic UTF-8 file output.</summary>
    public static class KimodoFiles
    {
        /// <summary>Writes a DTO through a same-directory temporary file.</summary>
        public static void WriteJson<T>(string path, T value)
        {
            WriteText(path, JsonUtility.ToJson(value, true));
        }

        /// <summary>Atomically publishes a UTF-8 text file.</summary>
        public static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
    }
}
