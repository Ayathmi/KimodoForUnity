// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace KimodoForUnity
{
    /// <summary>Standalone elevated process host, also compiled by Windows PowerShell without Unity references.</summary>
    public static class KimodoInstallHost
    {
        /// <summary>Runs a gated child in a kill-on-close job, forwarding logs and monitoring cancellation.</summary>
        public static int Run(string shell, string arguments, string directory, int parentId, long parentStartTicks)
        {
            var job = IntPtr.Zero;
            Process child = null;
            try
            {
                using (var parent = Process.GetProcessById(parentId))
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != parentStartTicks || parent.HasExited)
                        return 1223;
                    if (File.Exists(Path.Combine(directory, "cancel"))) return 1223;
                    job = CreateJobObject(IntPtr.Zero, null);
                    var limits = new ExtendedLimits();
                    limits.basic.limitFlags = 0x2000;
                    if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits,
                        (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                        throw new InvalidOperationException("Cannot create the installation process job.");
                    child = Process.Start(new ProcessStartInfo
                    {
                        FileName = shell, Arguments = arguments, WorkingDirectory = directory,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    });
                    if (child == null) throw new InvalidOperationException("Cannot start the installation process.");
                    if (!AssignProcessToJobObject(job, child.Handle))
                        throw new InvalidOperationException("Cannot own the installation process tree.");
                    using (var output = new FileStream(Path.Combine(directory, "stdout.log"), FileMode.Create,
                        FileAccess.Write, FileShare.ReadWrite))
                    using (var errors = new FileStream(Path.Combine(directory, "stderr.log"), FileMode.Create,
                        FileAccess.Write, FileShare.ReadWrite))
                    {
                        var outputTask = Capture(child.StandardOutput.BaseStream, output);
                        var errorTask = Capture(child.StandardError.BaseStream, errors);
                        File.WriteAllText(Path.Combine(directory, "ready"), "");
                        var cancelled = false;
                        while (!child.WaitForExit(200))
                        {
                            if (outputTask.IsFaulted || errorTask.IsFaulted)
                                throw new IOException("Cannot write the installation log files.");
                            if (!parent.HasExited && !File.Exists(Path.Combine(directory, "cancel"))) continue;
                            cancelled = true;
                            break;
                        }
                        CloseHandle(job);
                        job = IntPtr.Zero;
                        child.WaitForExit();
                        Task.WaitAll(outputTask, errorTask);
                        return cancelled ? 1223 : child.ExitCode;
                    }
                }
            }
            finally
            {
                if (job != IntPtr.Zero) CloseHandle(job);
                if (child != null)
                {
                    try { if (!child.HasExited) child.Kill(); } catch (InvalidOperationException) { }
                    child.Dispose();
                }
            }
        }

        private static async Task Capture(Stream input, Stream output)
        {
            var buffer = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
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
