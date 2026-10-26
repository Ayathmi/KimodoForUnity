// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Persisted generation request and lifecycle record.</summary>
    [Serializable]
    public sealed class KimodoJob
    {
        /// <summary>Schema version.</summary>
        public int schemaVersion = 2;
        /// <summary>Unique task identifier.</summary>
        public string id;
        /// <summary>UTC creation time.</summary>
        public string createdUtc;
        /// <summary>Natural-language input.</summary>
        public string prompt;
        /// <summary>Requested duration in seconds.</summary>
        public float duration;
        /// <summary>Requested BVH and NPZ sampling rate, independent of the checkpoint's native rate.</summary>
        public int requestedFps = 30;
        /// <summary>Encoder combination used by this task.</summary>
        public KimodoModelPreset modelPreset;
        /// <summary>Recorded seed.</summary>
        public int seed;
        /// <summary>Denoising steps.</summary>
        public int steps;
        /// <summary>Fixed checkpoint identity.</summary>
        public string model = KimodoConfiguration.Model;
        /// <summary>Explicit encoder device.</summary>
        public string textDevice;
        /// <summary>Queued, Running, Ready, Failed, Cancelled or Interrupted.</summary>
        public string state;
        /// <summary>Reason or result summary.</summary>
        public string detail;
        /// <summary>Elapsed generation seconds.</summary>
        public double elapsedSeconds;
        /// <summary>Output SHA-256.</summary>
        public string bvhSha256;
        /// <summary>Actual output sample count.</summary>
        public int frames;
        /// <summary>Actual output sampling rate.</summary>
        public float fps;
        /// <summary>Selected environment path for provenance.</summary>
        public string pythonPath;
        /// <summary>Integration version used for this request.</summary>
        public string pluginVersion = KimodoConfiguration.Version;
        /// <summary>Import probe observations from the inspected environment.</summary>
        public string dependencyReport;
    }

    /// <summary>Only the official meta input fields, from one validated request.</summary>
    [Serializable]
    public sealed class KimodoOfficialInput
    {
        /// <summary>Single prompt.</summary>
        public string text;
        /// <summary>Duration in seconds.</summary>
        public float duration;
        /// <summary>Always one sample per queued task.</summary>
        public int num_samples = 1;
        /// <summary>Denoising step count.</summary>
        public int diffusion_steps;
        /// <summary>Actual seed.</summary>
        public int seed;
    }

    /// <summary>Editor-owned serial task queue and isolated encoder service.</summary>
    [InitializeOnLoad]
    public static class KimodoSession
    {
        private static KimodoConfiguration inspectedConfig;
        private static string inspectedSignature;
        private static bool inspectedReady;
        private static string inspectedReport;
        private static KimodoOwnedProcess encoder;
        private static string modelLayout;
        private static KimodoOwnedProcess generation;
        private static readonly Queue<KimodoJob> queue = new Queue<KimodoJob>();
        private static KimodoJob active;
        private static DateTime encoderStarted;
        private static DateTime activeStarted;
        private static DateTime lastUse;
        private static DateTime nextTick;
        private static string status = "Dependencies have not been checked.";
        private static string retainedOutput = "";
        private static string encoderLogFile;
        private static string lastLogFile;
        private static readonly List<KimodoJob> history = new List<KimodoJob>();

        static KimodoSession()
        {
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
        }

        /// <summary>Current service or task status.</summary>
        public static string Status => status;
        /// <summary>Recent tasks, including the current session.</summary>
        public static IReadOnlyList<KimodoJob> History => history;
        /// <summary>Whether this session owns a live encoder.</summary>
        public static bool EncoderRunning => encoder != null;
        /// <summary>Recent process output.</summary>
        public static string LiveOutput => generation != null ? generation.Output + "\n" + generation.Errors
            : encoder != null && string.IsNullOrEmpty(retainedOutput) ? encoder.Output + "\n" + encoder.Errors : retainedOutput;
        /// <summary>Most recently saved process log.</summary>
        public static string LogFile => lastLogFile;
        /// <summary>Number of queued requests.</summary>
        public static int Queued => queue.Count;

        /// <summary>Accepts only a completed explicit inspection.</summary>
        public static void SetInspection(KimodoConfiguration config, KimodoPythonProbe probe, List<KimodoCheckItem> files)
        {
            if (encoder != null && Signature(config) != inspectedSignature)
                throw new InvalidOperationException("Stop the current service before changing its configuration.");
            inspectedConfig = config.Copy();
            inspectedSignature = Signature(config);
            inspectedReady = KimodoDependencyCheck.CanGenerate(probe, files, config);
            inspectedReport = JsonUtility.ToJson(probe);
            status = inspectedReady ? "Basic offline checks passed. Real inference is not yet verified."
                : "Dependencies are incomplete.";
            LoadHistory(config);
        }

        /// <summary>Checks the selected preferences match a successful inspection.</summary>
        public static bool IsInspected(KimodoConfiguration config)
            => inspectedReady && inspectedSignature == Signature(config);

        /// <summary>Invalidates earlier checks before installation, repository download or directory inspection.</summary>
        public static void InvalidateInspection() => inspectedReady = false;

        /// <summary>Starts the existing official service. No downloads or installations occur.</summary>
        public static void StartEncoder(KimodoConfiguration config)
        {
            EnsureReady(config);
            if (encoder != null) return;
            foreach (var listener in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (listener.Port == config.port)
                    throw new InvalidOperationException("The chosen port is already in use. Select another port; existing services will not be replaced.");
            PrepareRuntimeFolders(config);
            modelLayout = KimodoModelFiles.CreateLayout(config);
            retainedOutput = "";
            encoderLogFile = KimodoPaths.Child(config.assetsRoot, "Logs", "encoder-" + Guid.NewGuid().ToString("N") + ".log");
            var info = KimodoProcess.StartInfo(config, new[] { "-B", "-c", KimodoRuntimeBridge.EncoderCode }, config.assetsRoot, modelLayout);
            encoder = new KimodoOwnedProcess(info);
            encoderStarted = lastUse = DateTime.UtcNow;
            status = "Loading the local text encoder on " + (config.cpuTextEncoder ? "CPU (explicit fallback)" : "CUDA") + ".";
        }

        /// <summary>Stops only the owned service and cancels its pending work.</summary>
        public static void StopEncoder()
        {
            CancelAll("Cancelled");
            encoder?.Dispose();
            encoder = null;
            status = "Owned encoder stopped.";
        }

        /// <summary>Validates and persists one task before adding it to the queue.</summary>
        public static void Enqueue(KimodoConfiguration config, string prompt, float duration, int seed, int steps, int frameRate = 30)
        {
            EnsureReady(config);
            ValidateRequest(prompt, duration, steps, frameRate);
            StartEncoder(config);
            var job = new KimodoJob
            {
                id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                createdUtc = DateTime.UtcNow.ToString("O"), prompt = prompt.Trim(), duration = duration,
                seed = seed, steps = steps, textDevice = config.cpuTextEncoder ? "cpu" : "cuda",
                requestedFps = frameRate, modelPreset = config.modelPreset,
                state = "Queued", pythonPath = config.RuntimePython, dependencyReport = inspectedReport
            };
            var folder = JobFolder(config, job.id);
            Directory.CreateDirectory(Path.Combine(folder, "input"));
            Directory.CreateDirectory(Path.Combine(folder, "output"));
            KimodoFiles.WriteJson(Path.Combine(folder, "input", "meta.json"), new KimodoOfficialInput
            {
                text = job.prompt, duration = duration, diffusion_steps = steps, seed = seed
            });
            Save(job);
            history.Insert(0, job);
            queue.Enqueue(job);
        }

        /// <summary>Pure request validation independent of any Python environment.</summary>
        public static void ValidateRequest(string prompt, float duration, int steps, int frameRate = 30)
        {
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 2048 || prompt.IndexOf('\0') >= 0)
                throw new ArgumentException("Prompt must contain 1-2048 characters.");
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration < 1f || duration != Math.Truncate(duration))
                throw new ArgumentException("Duration must be a positive whole number of seconds.");
            if (frameRate < 1 || frameRate > 60) throw new ArgumentException("Frame rate must be 1-60 fps.");
            if (duration > 10f) throw new ArgumentException("The current single-prompt model supports at most 10 seconds.");
            if (steps < 1 || steps > 1000) throw new ArgumentException("Steps must be 1-1000.");
        }

        /// <summary>Cancels active and queued jobs without terminating unrelated Python processes.</summary>
        public static void CancelAll(string terminalState = "Cancelled")
        {
            var hadGeneration = generation != null;
            if (active != null)
            {
                try
                {
                    CaptureLogs();
                    active.state = terminalState;
                    active.detail = "Owned task stopped. No output was imported.";
                    Save(active);
                }
                catch (Exception ex) { Debug.LogWarning("Kimodo task record: " + ex.Message); }
                finally
                {
                    generation?.Dispose();
                    generation = null;
                    active = null;
                }
            }
            while (queue.Count > 0)
            {
                var job = queue.Dequeue();
                job.state = terminalState;
                if (terminalState == "Failed") job.detail = status;
                try { Save(job); }
                catch (Exception ex) { Debug.LogWarning("Kimodo queued record: " + ex.Message); }
            }
            // An encoder request may still be running; closing its owned job prevents overlap.
            ReleaseEncoder(hadGeneration);
            status = terminalState + ".";
        }

        /// <summary>Retains diagnostics and closes the owned encoder tree, releasing its CUDA context.</summary>
        private static void ReleaseEncoder(bool preserveGenerationOutput)
        {
            if (encoder != null)
                try
                {
                    var output = encoder.Output + "\n" + encoder.Errors;
                    if (!string.IsNullOrEmpty(encoderLogFile)) KimodoFiles.WriteText(encoderLogFile, output);
                    if (!preserveGenerationOutput) { retainedOutput = output; lastLogFile = encoderLogFile; }
                }
                catch (Exception ex) { Debug.LogWarning("Kimodo encoder log: " + ex.Message); }
            encoder?.Dispose();
            encoder = null;
        }

        /// <summary>Resolves a validated task folder below external Jobs.</summary>
        public static string JobFolder(KimodoConfiguration config, string id)
        {
            if (string.IsNullOrEmpty(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$"))
                throw new ArgumentException("Invalid task ID.");
            return Path.GetDirectoryName(KimodoPaths.Child(config.assetsRoot, "Jobs", id, "record.json"));
        }

        /// <summary>Reads bounded history records. In-flight records from an older session become interrupted.</summary>
        public static void LoadHistory(KimodoConfiguration config)
        {
            if (active != null || queue.Count > 0) return;
            history.Clear();
            var root = Path.Combine(config.assetsRoot, "Jobs");
            if (!Directory.Exists(root)) return;
            var folders = Directory.GetDirectories(root);
            Array.Sort(folders, StringComparer.Ordinal);
            for (var i = folders.Length - 1; i >= 0 && history.Count < 200; i--)
            {
                try
                {
                    var id = Path.GetFileName(folders[i]);
                    var folder = JobFolder(config, id);
                    var file = Path.Combine(folder, "record.json");
                    if (!File.Exists(file) || new FileInfo(file).Length > 65536) continue;
                    var job = JsonUtility.FromJson<KimodoJob>(File.ReadAllText(file));
                    if (job == null || job.schemaVersion < 1 || job.schemaVersion > 2 || job.id != id) continue;
                    if (job.schemaVersion == 1) { job.requestedFps = 30; job.modelPreset = KimodoModelPreset.Fp16; }
                    if (job.state == "Running" || job.state == "Queued")
                    {
                        job.state = "Interrupted";
                        job.detail = "Previous Editor session ended. Retry explicitly after checking dependencies.";
                        KimodoFiles.WriteJson(file, job);
                    }
                    history.Add(job);
                }
                catch (Exception ex) { Debug.LogWarning("Kimodo history: " + ex.Message); }
            }
        }

        private static void Tick()
        {
            if (DateTime.UtcNow < nextTick) return;
            nextTick = DateTime.UtcNow.AddMilliseconds(200);
            try
            {
                if (encoder != null && encoder.Finished)
                {
                    status = "Text encoder exited (code " + encoder.ExitCode + ").";
                    var failure = status;
                    CancelAll("Failed");
                    status = failure;
                    return;
                }
                if (generation != null && generation.Finished) FinishTask();
                if (generation == null && queue.Count > 0 && encoder != null)
                {
                    var listening = false;
                    foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                        if (endpoint.Port == inspectedConfig.port && (IPAddress.IsLoopback(endpoint.Address))) listening = true;
                    if (listening) BeginTask();
                    else if (DateTime.UtcNow - encoderStarted > TimeSpan.FromMinutes(15))
                        throw new TimeoutException("Encoder did not become ready within 15 minutes. Check local models and logs.");
                }
                if (encoder != null && generation == null && queue.Count == 0 && inspectedConfig.idleMinutes > 0
                    && DateTime.UtcNow - lastUse > TimeSpan.FromMinutes(inspectedConfig.idleMinutes))
                    StopEncoder();
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                CancelAll("Failed");
                status = message;
            }
        }

        private static void BeginTask()
        {
            active = queue.Dequeue();
            var folder = JobFolder(inspectedConfig, active.id);
            var info = KimodoProcess.StartInfo(inspectedConfig, new[]
            {
                "-B", "-c", KimodoRuntimeBridge.GenerationCode,
                Path.Combine(folder, "output", "motion"), active.requestedFps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ((int)active.duration).ToString(System.Globalization.CultureInfo.InvariantCulture), "--model", active.model,
                "--input_folder", Path.Combine(folder, "input"), "--output", Path.Combine(folder, "output", "motion-native"),
                "--bvh", "--bvh_standard_tpose"
            }, folder, modelLayout);
            active.state = "Running";
            Save(active);
            activeStarted = lastUse = DateTime.UtcNow;
            generation = new KimodoOwnedProcess(info);
            status = "Generating " + active.id + ".";
        }

        private static void FinishTask()
        {
            CaptureLogs();
            active.elapsedSeconds = (DateTime.UtcNow - activeStarted).TotalSeconds;
            var bvh = Path.Combine(JobFolder(inspectedConfig, active.id), "output", "motion.bvh");
            var npz = Path.ChangeExtension(bvh, ".npz");
            try
            {
                if (generation.ExitCode != 0) throw new InvalidOperationException("Generation failed. See stdout.log and stderr.log.");
                if (!generation.Output.Contains("Using device: cuda"))
                    throw new InvalidOperationException("Actual CUDA motion execution was not confirmed.");
                if (!File.Exists(npz) || new FileInfo(npz).Length == 0) throw new InvalidDataException("Missing NPZ result.");
                var motion = KimodoBvh.Read(bvh);
                active.frames = motion.Frames;
                active.fps = 1f / motion.FrameTime;
                if (Math.Abs(active.fps - active.requestedFps) > 0.01f || Math.Abs(motion.Duration - active.duration) > 0.001f)
                    throw new InvalidDataException("Output timing does not match the requested frame rate and duration.");
                using (var hash = SHA256.Create())
                using (var stream = File.OpenRead(bvh))
                    active.bvhSha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                active.state = "Ready";
                active.detail = "Validated BVH and NPZ. Ready to preview; nothing imported automatically.";
            }
            catch (Exception ex)
            {
                active.state = "Failed";
                active.detail = ex.Message;
            }
            Save(active);
            status = active.state + ": " + active.detail;
            generation.Dispose();
            generation = null;
            active = null;
            lastUse = DateTime.UtcNow;
            // Check only at a task boundary, after its generation process and CUDA context are disposed.
            if (queue.Count == 0)
            {
                ReleaseEncoder(true);
                queue.Clear();
                status += " Queue empty. Owned generation and encoder processes stopped.";
            }
        }

        private static void CaptureLogs()
        {
            if (active == null || generation == null) return;
            var folder = JobFolder(inspectedConfig, active.id);
            KimodoFiles.WriteText(Path.Combine(folder, "stdout.log"), generation.Output);
            KimodoFiles.WriteText(Path.Combine(folder, "stderr.log"), generation.Errors);
            retainedOutput = generation.Output + "\n" + generation.Errors;
            lastLogFile = Path.Combine(folder, "stderr.log");
        }

        private static void Save(KimodoJob job)
            => KimodoFiles.WriteJson(Path.Combine(JobFolder(inspectedConfig, job.id), "record.json"), job);

        /// <summary>Tracks inference settings while ignoring interface-only language changes.</summary>
        private static string Signature(KimodoConfiguration config)
        {
            var snapshot = config.Copy();
            snapshot.chinese = true;
            return JsonUtility.ToJson(snapshot) + "\n" + config.RuntimePython + "\n" + KimodoModelFiles.Stamp(config);
        }

        private static void EnsureReady(KimodoConfiguration config)
        {
            KimodoPaths.ValidateRoot(config.assetsRoot);
            if (!IsInspected(config))
                throw new InvalidOperationException("Check dependencies for these settings first.");
        }

        private static void PrepareRuntimeFolders(KimodoConfiguration config)
        {
            foreach (var folder in new[] { "Temp", "Temp/TextEncoder", "Temp/Gradio", "Jobs" })
                Directory.CreateDirectory(KimodoPaths.Child(config.assetsRoot, folder, "placeholder").Replace(Path.DirectorySeparatorChar + "placeholder", ""));
        }

        private static void Shutdown()
        {
            try { CancelAll("Interrupted"); }
            catch (Exception ex) { Debug.LogWarning("Kimodo shutdown: " + ex.Message); }
        }
    }
}
