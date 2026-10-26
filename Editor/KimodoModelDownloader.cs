// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.IO;

namespace KimodoForUnity
{
    /// <summary>Downloads the selected repositories through the user's existing HF CLI login.</summary>
    public static class KimodoModelDownloader
    {
        /// <summary>Exact prompt shown when the download script reports no HF login.</summary>
        public const string LoginRequired = "请先登录hugging face cli";

        private static void ValidateDirectories(KimodoConfiguration config)
        {
            KimodoPaths.ValidateRoot(config.assetsRoot);
            if (config.models == null || config.models.Length != KimodoModelFiles.Repositories.Length)
                throw new ArgumentException("Select all repository directories.");
            var directories = KimodoModelPresets.IsQuantized(config)
                ? new[] { config.models[0].directory, KimodoModelPresets.Directory(config) }
                : Array.ConvertAll(config.models, model => model.directory);
            for (var i = 0; i < directories.Length; i++)
            {
                KimodoPaths.ValidateRoot(directories[i]);
                for (var j = 0; j < i; j++)
                    if (KimodoPaths.Within(directories[i], directories[j])
                        || KimodoPaths.Within(directories[j], directories[i]))
                        throw new ArgumentException("Select separate repository directories.");
            }
        }

        /// <summary>Creates the HF download command without reading credentials or starting authentication.</summary>
        public static ProcessStartInfo StartInfo(KimodoConfiguration config)
        {
            ValidateDirectories(config);
            var script = Path.Combine(Path.GetDirectoryName(KimodoInstaller.ScriptPath()), "Download-KimodoModels.ps1");
            var command = "& " + KimodoInstaller.PsQuote(script)
                + " -PythonExecutable " + KimodoInstaller.PsQuote(config.RuntimePython)
                + " -WorkspaceDirectory " + KimodoInstaller.PsQuote(config.assetsRoot)
                + " -MotionDirectory " + KimodoInstaller.PsQuote(config.models[0].directory)
                + " -EncoderDirectory " + KimodoInstaller.PsQuote(config.models[1].directory)
                + " -AdapterDirectory " + KimodoInstaller.PsQuote(config.models[2].directory)
                + " -ModelPreset " + KimodoInstaller.PsQuote(config.modelPreset.ToString())
                + " -QuantizedDirectory " + KimodoInstaller.PsQuote(KimodoModelPresets.Directory(config))
                + "; exit $LASTEXITCODE";
            return KimodoInstaller.PowerShellCommand(command);
        }

        /// <summary>Starts only after the user explicitly clicks the repository download button.</summary>
        public static KimodoOwnedProcess Start(KimodoConfiguration config) => new KimodoOwnedProcess(StartInfo(config));

        /// <summary>Finds an already installed Ollama client without modifying user settings.</summary>
        public static string FindOllama()
        {
            var standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
            if (File.Exists(standard)) return standard;
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var path = Path.Combine(folder.Trim().Trim('"'), "ollama.exe");
                    if (Path.IsPathRooted(path) && File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
            return "";
        }

        /// <summary>Creates an isolated Ollama download and conversion command without elevating Unity.</summary>
        public static ProcessStartInfo OllamaStartInfo(KimodoConfiguration config)
        {
            if (KimodoModelPresets.IsQuantized(config)) throw new InvalidOperationException("Ollama conversion is only used by the original FP16 combination.");
            ValidateDirectories(config);
            KimodoPaths.ValidateRoot(config.ollamaModelsPath);
            if (!Path.IsPathRooted(config.ollamaPath ?? "") || !File.Exists(config.ollamaPath))
                throw new FileNotFoundException("Select the installed ollama.exe.", config.ollamaPath);
            if (!File.Exists(config.RuntimePython)) throw new FileNotFoundException("Python executable was not found.", config.RuntimePython);
            if (!File.Exists(Path.Combine(config.models[1].directory, "config.json")))
                throw new FileNotFoundException("Download the MNTP repository first.");
            foreach (var model in config.models)
                if (KimodoPaths.Within(config.ollamaModelsPath, model.directory) || KimodoPaths.Within(model.directory, config.ollamaModelsPath))
                    throw new ArgumentException("Select separate Ollama storage and model directories.");
            var script = Path.Combine(Path.GetDirectoryName(KimodoInstaller.ScriptPath()), "Download-KimodoLlama.ps1");
            var command = "& " + KimodoInstaller.PsQuote(script)
                + " -OllamaExecutable " + KimodoInstaller.PsQuote(config.ollamaPath)
                + " -PythonExecutable " + KimodoInstaller.PsQuote(config.RuntimePython)
                + " -WorkspaceDirectory " + KimodoInstaller.PsQuote(config.assetsRoot)
                + " -OllamaModelsDirectory " + KimodoInstaller.PsQuote(config.ollamaModelsPath)
                + " -EncoderDirectory " + KimodoInstaller.PsQuote(config.models[1].directory)
                + " -BaseDirectory " + KimodoInstaller.PsQuote(config.models[3].directory)
                + "; exit $LASTEXITCODE";
            return KimodoInstaller.PowerShellCommand(command);
        }

        /// <summary>Starts only the explicitly requested Ollama download and conversion.</summary>
        public static KimodoOwnedProcess StartOllama(KimodoConfiguration config) => new KimodoOwnedProcess(OllamaStartInfo(config));
    }
}
