// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KimodoForUnity
{
    /// <summary>Starts only existing interpreters, with offline model loading.</summary>
    public static class KimodoProcess
    {
        /// <summary>Quotes one argument for Windows CommandLineToArgvW rules.</summary>
        public static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0)
                throw new ArgumentException("Invalid process argument.");
            var output = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') output.Append('\\', slashes * 2 + 1);
                else output.Append('\\', slashes);
                output.Append(c);
                slashes = 0;
            }
            output.Append('\\', slashes * 2).Append('"');
            return output.ToString();
        }

        /// <summary>Builds a local-only process environment. This never installs dependencies.</summary>
        public static ProcessStartInfo StartInfo(KimodoConfiguration config, IEnumerable<string> args, string workingDirectory, string modelLayout = null)
        {
            if (!File.Exists(config.RuntimePython))
                throw new FileNotFoundException("Select an existing python.exe.", config.RuntimePython);
            var command = new StringBuilder();
            foreach (var arg in args)
            {
                if (command.Length > 0) command.Append(' ');
                command.Append(Quote(arg));
            }
            var info = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(config.RuntimePython),
                Arguments = command.ToString(),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var env = info.EnvironmentVariables;
            env["PYTHONUTF8"] = "1";
            env["PYTHONIOENCODING"] = "utf-8";
            env["PYTHONUNBUFFERED"] = "1";
            env["PYTHONDONTWRITEBYTECODE"] = "1";
            env["PYTHONNOUSERSITE"] = "1";
            env.Remove("PYTHONPATH");
            env["HF_HUB_OFFLINE"] = "1";
            env["TRANSFORMERS_OFFLINE"] = "1";
            env["HF_HUB_DISABLE_TELEMETRY"] = "1";
            env["GRADIO_ANALYTICS_ENABLED"] = "False";
            env["GRADIO_SERVER_NAME"] = "127.0.0.1";
            env["GRADIO_SERVER_PORT"] = config.port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            env["TEXT_ENCODER_DEVICE"] = config.cpuTextEncoder ? "cpu" : "cuda";
            env["TEXT_ENCODER_MODE"] = "api";
            env["TEXT_ENCODER_URL"] = "http://127.0.0.1:" + env["GRADIO_SERVER_PORT"];
            env["CHECKPOINT_DIR"] = modelLayout ?? Path.Combine(config.assetsRoot, "Models");
            env["TEXT_ENCODERS_DIR"] = Path.Combine(env["CHECKPOINT_DIR"], "TextEncoders");
            env["HF_HOME"] = Path.Combine(config.assetsRoot, "Cache", "HuggingFace");
            env["HF_HUB_CACHE"] = Path.Combine(env["HF_HOME"], "hub");
            env["HUGGINGFACE_CACHE_DIR"] = env["HF_HUB_CACHE"];
            env["TORCH_HOME"] = Path.Combine(config.assetsRoot, "Cache", "Torch");
            env["PIP_CACHE_DIR"] = Path.Combine(config.assetsRoot, "Cache", "Pip");
            env["TEMP"] = Path.Combine(config.assetsRoot, "Temp");
            env["TMP"] = env["TEMP"];
            env["GRADIO_TEMP_DIR"] = Path.Combine(config.assetsRoot, "Temp", "Gradio");
            env["TEXT_ENCODER_TMP_FOLDER"] = Path.Combine(config.assetsRoot, "Temp", "TextEncoder");
            env["LOCAL_CACHE"] = "true";
            env.Remove("HF_TOKEN");
            env.Remove("HUGGING_FACE_HUB_TOKEN");
            return info;
        }

        /// <summary>Converts terminal refreshes to log lines and removes terminal formatting.</summary>
        public static string NormalizeLogLines(string text)
        {
            text = text ?? "";
            if (text.IndexOf('\u001b') >= 0)
            {
                text = Regex.Replace(text, @"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", "");
                text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
            }
            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        /// <summary>Normalizes diagnostic lines and removes credentials and non-BMP symbols.</summary>
        public static string Sanitize(string text)
        {
            text = Regex.Replace(NormalizeLogLines(text), @"hf_[A-Za-z0-9]{10,}", "[redacted]");
            return Regex.Replace(text, @"[\uD800-\uDBFF][\uDC00-\uDFFF]", "");
        }
    }

    /// <summary>Owns a child process and its Windows job, with bounded captured output.</summary>
    public sealed class KimodoOwnedProcess : IDisposable
    {
        private readonly Process process;
        private readonly StringBuilder output = new StringBuilder();
        private readonly StringBuilder errors = new StringBuilder();
        private readonly object sync = new object();
        private IntPtr job;
        private bool disposed;
        private readonly Task outputTask;
        private readonly Task errorTask;

        /// <summary>Starts the process without a visible shell and confines its child tree.</summary>
        public KimodoOwnedProcess(ProcessStartInfo info)
        {
            process = new Process { StartInfo = info };
            if (!process.Start())
                throw new InvalidOperationException("Could not start the interpreter.");
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                var limits = new ExtendedLimits();
                limits.basic.limitFlags = 0x2000;
                if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())
                    || !AssignProcessToJobObject(job, process.Handle))
                    throw new InvalidOperationException("Cannot establish ownership of the child process tree.");
                outputTask = Capture(process.StandardOutput, output);
                errorTask = Capture(process.StandardError, errors);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                if (job != IntPtr.Zero) CloseHandle(job);
                process.Dispose();
                throw;
            }
        }

        /// <summary>Whether the process and output streams have completed.</summary>
        public bool Finished => !disposed && process.HasExited && outputTask.IsCompleted && errorTask.IsCompleted;
        /// <summary>Native exit code, available only after completion.</summary>
        public int ExitCode => process.ExitCode;
        /// <summary>Captured standard output, limited to its most recent 256 KiB.</summary>
        public string Output { get { lock (sync) return KimodoProcess.Sanitize(output.ToString()); } }
        /// <summary>Captured diagnostics, limited to their most recent 256 KiB.</summary>
        public string Errors { get { lock (sync) return KimodoProcess.Sanitize(errors.ToString()); } }
        /// <summary>Owned process identifier.</summary>
        public int Id => process.Id;

        private async Task Capture(StreamReader reader, StringBuilder target)
        {
            var buffer = new char[2048];
            try
            {
                int count;
                while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    lock (sync)
                    {
                        target.Append(buffer, 0, count);
                        if (target.Length > 262144) target.Remove(0, target.Length - 262144);
                    }
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }

        /// <summary>Terminates only the owned process tree.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            process.Dispose();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long processTime, jobTime;
            public uint limitFlags;
            public UIntPtr minimumWorkingSet, maximumWorkingSet;
            public uint activeProcessLimit;
            public UIntPtr affinity;
            public uint priorityClass, schedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong readOperations, writeOperations, otherOperations, readBytes, writeBytes, otherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits basic;
            public IoCounters io;
            public UIntPtr processMemory, jobMemory, peakProcessMemory, peakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr handle, int informationClass, ref ExtendedLimits information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr handle, IntPtr processHandle);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
