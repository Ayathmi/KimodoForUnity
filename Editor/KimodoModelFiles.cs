// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KimodoForUnity
{
    /// <summary>Local repository directory selected by the user.</summary>
    [Serializable]
    public sealed class KimodoModelLocation
    {
        /// <summary>Local repository directory.</summary>
        public string directory = "";
    }

    /// <summary>Local directory checks and official CLI directory mapping.</summary>
    public static class KimodoModelFiles
    {
        /// <summary>Fixed official model repositories in configuration order.</summary>
        public static readonly string[] Repositories =
        {
            "nvidia/" + KimodoConfiguration.Model,
            "McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp",
            "McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised",
            "meta-llama/Meta-Llama-3-8B-Instruct"
        };

        /// <summary>Checks repository directories without reading or hashing model weights.</summary>
        public static Task<List<KimodoCheckItem>> CheckDirectoriesAsync(KimodoConfiguration config, CancellationToken cancellation)
            => Task.Run(() =>
            {
                var results = new List<KimodoCheckItem>();
                for (var i = 0; i < Repositories.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        CheckDirectory(config.models[i], cancellation);
                        if (i == 3) CheckBaseModel(config.models[i].directory);
                        results.Add(new KimodoCheckItem { name = Repositories[i], passed = true, detail = config.models[i].directory });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        results.Add(new KimodoCheckItem { name = Repositories[i], passed = false, detail = ex.Message });
                    }
                }
                return results;
            }, cancellation);

        /// <summary>Requires a readable repository directory; no per-file integrity checks are performed.</summary>
        public static void CheckDirectory(KimodoModelLocation model, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (model == null || string.IsNullOrWhiteSpace(model.directory) || !Path.IsPathRooted(model.directory))
                throw new InvalidDataException("Select an absolute repository directory.");
            if (!Directory.Exists(model.directory)) throw new DirectoryNotFoundException(model.directory);
            using (var entries = Directory.EnumerateFileSystemEntries(model.directory).GetEnumerator())
            {
                if (!entries.MoveNext()) throw new InvalidDataException("Repository directory is empty.");
            }
        }

        /// <summary>Invalidates inspection when a selected directory changes or disappears.</summary>
        public static string Stamp(KimodoConfiguration config)
        {
            var value = new StringBuilder();
            if (config.models == null) return "";
            foreach (var model in config.models)
            {
                var directory = model == null ? "" : model.directory;
                value.Append(directory).Append('|');
                try
                {
                    var info = new DirectoryInfo(directory);
                    value.Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
                }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                value.Append('\n');
            }
            return value.ToString();
        }

        /// <summary>Rejects an adapter-only directory in place of the required full base model.</summary>
        public static void CheckBaseModel(string directory)
        {
            if (!File.Exists(Path.Combine(directory, "config.json")))
                throw new InvalidDataException("Missing base-model config.json: " + Repositories[3]);
            using (var weights = Directory.EnumerateFiles(directory, "model*.safetensors").GetEnumerator())
                if (!weights.MoveNext())
                    throw new InvalidDataException("Missing base-model weights: " + Repositories[3]);
        }

        /// <summary>Builds a local adapter view with its base-model reference resolved to the selected directory.</summary>
        public static string CreateLayout(KimodoConfiguration config)
        {
            for (var i = 0; i < Repositories.Length; i++) CheckDirectory(config.models[i], CancellationToken.None);
            CheckBaseModel(config.models[3].directory);
            var root = KimodoPaths.Child(config.assetsRoot, "RuntimeModels", Guid.NewGuid().ToString("N"));
            var script = Path.Combine(Path.GetDirectoryName(KimodoInstaller.ScriptPath()), "Prepare-KimodoModels.ps1");
            var command = "& " + KimodoInstaller.PsQuote(script) + " -RuntimeDirectory " + KimodoInstaller.PsQuote(root)
                + " -MotionDirectory " + KimodoInstaller.PsQuote(config.models[0].directory)
                + " -EncoderDirectory " + KimodoInstaller.PsQuote(config.models[1].directory)
                + " -AdapterDirectory " + KimodoInstaller.PsQuote(config.models[2].directory)
                + " -BaseDirectory " + KimodoInstaller.PsQuote(config.models[3].directory)
                + "; exit $LASTEXITCODE";
            var info = KimodoInstaller.PowerShellCommand(command);
            using (var child = new KimodoOwnedProcess(info))
            {
                var end = DateTime.UtcNow.AddSeconds(60);
                while (!child.Finished)
                {
                    if (DateTime.UtcNow > end) throw new TimeoutException("Local model directory mapping timed out.");
                    Thread.Sleep(20);
                }
                if (child.ExitCode != 0) throw new IOException(child.Errors);
            }
            return root;
        }
    }
}
