// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace KimodoForUnity
{
    /// <summary>Observes an elevated installer without requiring an elevated Unity Editor.</summary>
    public sealed class KimodoElevatedInstallation : IDisposable
    {
        private readonly Process process;
        private readonly string directory;
        private bool disposed;

        /// <summary>Starts the UAC request and preserves user-readable log files.</summary>
        public KimodoElevatedInstallation(ProcessStartInfo info, string logDirectory)
        {
            directory = logDirectory;
            foreach (var name in new[] { "stdout.log", "stderr.log", "host.log" })
                File.WriteAllText(Path.Combine(directory, name), "", new UTF8Encoding(false));
            process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("Could not start the administrator installation.");
        }

        /// <summary>Whether the elevated host has exited.</summary>
        public bool Finished => !disposed && process.HasExited;
        /// <summary>Returns the installer result, or failure if the host exited before recording it.</summary>
        public int ExitCode
        {
            get
            {
                int code;
                return int.TryParse(ReadTail("exit-code.txt"), out code) ? code : -1;
            }
        }
        /// <summary>Recent installation output for the existing scrolling log panel.</summary>
        public string Output => ReadTail("stdout.log");
        /// <summary>Recent installer and elevated-host diagnostics.</summary>
        public string Errors => ReadTail("stderr.log") + "\n" + ReadTail("host.log");

        private string ReadTail(string name)
        {
            try
            {
                using (var stream = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(Math.Max(0, stream.Length - 262144), SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                        return KimodoProcess.Sanitize(reader.ReadToEnd());
                }
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        /// <summary>Asks the elevated host to close its own job and stop the complete installation tree.</summary>
        public void Dispose()
        {
            if (disposed) return;
            try
            {
                if (!process.HasExited)
                    File.WriteAllText(Path.Combine(directory, "cancel"), "", new UTF8Encoding(false));
            }
            finally
            {
                disposed = true;
                process.Dispose();
            }
        }
    }
}
