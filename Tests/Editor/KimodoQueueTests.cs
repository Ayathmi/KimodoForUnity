// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace KimodoForUnity.Tests
{
    /// <summary>Exercises task-boundary cleanup with owned stand-in processes and persisted results.</summary>
    public sealed class KimodoQueueTests
    {
        private readonly Dictionary<FieldInfo, object> originalFields = new Dictionary<FieldInfo, object>();
        private readonly List<KimodoOwnedProcess> processes = new List<KimodoOwnedProcess>();
        private string root;
        private KimodoConfiguration config;

        /// <summary>Uses an isolated external workspace and never replaces live user work.</summary>
        [SetUp]
        public void SetUp()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Windows process ownership test.");
            if (KimodoSession.EncoderRunning || KimodoSession.Queued != 0 || Get("active") != null || Get("generation") != null)
                Assert.Ignore("A user generation session is active.");
            foreach (var field in typeof(KimodoSession).GetFields(BindingFlags.Static | BindingFlags.NonPublic))
                if (!field.IsInitOnly) originalFields.Add(field, field.GetValue(null));
            root = Path.Combine(Path.GetTempPath(), "KimodoQueueTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            config = new KimodoConfiguration { assetsRoot = root, idleMinutes = 0 };
            Set("inspectedConfig", config);
            Set("encoderLogFile", Path.Combine(root, "encoder.log"));
            Set("retainedOutput", "");
            Set("lastLogFile", "");
        }

        /// <summary>Stops only test-owned processes and restores the original editor session.</summary>
        [TearDown]
        public void TearDown()
        {
            foreach (var process in processes) process.Dispose();
            processes.Clear();
            if (originalFields.Count > 0)
            {
                Queue.Clear();
                foreach (var pair in originalFields) pair.Key.SetValue(null, pair.Value);
                originalFields.Clear();
            }
            if (root != null && Directory.Exists(root)) Directory.Delete(root, true);
        }

        /// <summary>Success and failure both release the last encoder while retaining task evidence.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void LastFinishedTaskReleasesOwnedProcessesAndPreservesResult(bool success)
        {
            var encoder = StartProcess(true);
            var unrelated = StartProcess(true);
            Set("encoder", encoder);
            var job = PrepareCompletedTask(success);
            using (var encoderHandle = Process.GetProcessById(encoder.Id))
            using (var unrelatedHandle = Process.GetProcessById(unrelated.Id))
            {
                Invoke("FinishTask");
                Assert.IsTrue(encoderHandle.WaitForExit(5000), "Owned encoder must actually terminate.");
                Assert.IsFalse(unrelatedHandle.HasExited, "Other processes must remain alive.");
            }
            Assert.IsFalse(KimodoSession.EncoderRunning);
            Assert.IsNull(Get("generation"));
            Assert.IsNull(Get("active"));
            Assert.AreEqual(0, KimodoSession.Queued);
            Assert.AreEqual(success ? "Ready" : "Failed", job.state);
            StringAssert.StartsWith(job.state + ":", KimodoSession.Status);
            StringAssert.Contains("Queue empty", KimodoSession.Status);
            StringAssert.Contains("Using device: cuda", KimodoSession.LiveOutput);
            Assert.AreEqual(Path.Combine(KimodoSession.JobFolder(config, job.id), "stderr.log"), KimodoSession.LogFile);
            Assert.IsTrue(File.Exists(Path.Combine(root, "encoder.log")));
            var persisted = JsonUtility.FromJson<KimodoJob>(File.ReadAllText(Path.Combine(KimodoSession.JobFolder(config, job.id), "record.json")));
            Assert.AreEqual(job.state, persisted.state);
        }

        /// <summary>A remaining request keeps the encoder and its pending state after either outcome.</summary>
        [TestCase(true)]
        [TestCase(false)]
        public void FinishedTaskWithPendingWorkKeepsEncoder(bool success)
        {
            var encoder = StartProcess(true);
            Set("encoder", encoder);
            PrepareCompletedTask(success);
            var pending = new KimodoJob { id = "20261026-120001-bbbbbbbb", state = "Queued" };
            Queue.Enqueue(pending);
            Invoke("FinishTask");
            Assert.IsTrue(KimodoSession.EncoderRunning);
            Assert.AreSame(encoder, Get("encoder"));
            Assert.AreEqual(1, KimodoSession.Queued);
            Assert.AreSame(pending, Queue.Peek());
            Assert.AreEqual("Queued", pending.state);
            Assert.IsNull(Get("generation"));
            Assert.IsNull(Get("active"));
            StringAssert.DoesNotContain("Queue empty", KimodoSession.Status);
        }

        /// <summary>An empty pending queue never stops a still-running task during polling.</summary>
        [Test]
        public void PollingRunningTaskDoesNotReleaseEmptyQueue()
        {
            var encoder = StartProcess(true);
            var generation = StartProcess(true);
            Set("encoder", encoder);
            Set("generation", generation);
            Set("active", new KimodoJob { state = "Running" });
            Set("nextTick", DateTime.MinValue);
            Invoke("Tick");
            Assert.AreSame(generation, Get("generation"));
            Assert.AreSame(encoder, Get("encoder"));
            Assert.AreEqual("Running", ((KimodoJob)Get("active")).state);
        }

        /// <summary>Starting the encoder without finishing a task does not trigger queue-drain cleanup.</summary>
        [Test]
        public void PollingIdleEncoderDoesNotRunTaskBoundaryCleanup()
        {
            var encoder = StartProcess(true);
            Set("encoder", encoder);
            Set("nextTick", DateTime.MinValue);
            Invoke("Tick");
            Assert.AreSame(encoder, Get("encoder"));
            Assert.IsTrue(KimodoSession.EncoderRunning);
        }

        /// <summary>Creates lightweight output fixtures, without invoking a model or claiming inference coverage.</summary>
        private KimodoJob PrepareCompletedTask(bool success)
        {
            var job = new KimodoJob { id = "20261026-120000-aaaaaaaa", state = "Running" };
            var folder = KimodoSession.JobFolder(config, job.id);
            Directory.CreateDirectory(Path.Combine(folder, "output"));
            if (success)
            {
                File.WriteAllText(Path.Combine(folder, "output", "motion.bvh"), KimodoTestFixture.Motion());
                File.WriteAllBytes(Path.Combine(folder, "output", "motion.npz"), new byte[] { 1 });
            }
            var generation = StartProcess(false);
            Assert.IsTrue(SpinWait.SpinUntil(() => generation.Finished, 10000), "Stand-in generation must finish.");
            Set("generation", generation);
            Set("active", job);
            Set("activeStarted", DateTime.UtcNow.AddSeconds(-1));
            return job;
        }

        /// <summary>Starts harmless Windows stand-ins inside the production process-ownership wrapper.</summary>
        private KimodoOwnedProcess StartProcess(bool longRunning)
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var process = new KimodoOwnedProcess(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "-NoProfile -NonInteractive -Command " + KimodoProcess.Quote(longRunning
                    ? "Start-Sleep -Seconds 60" : "Write-Output 'Using device: cuda'; Start-Sleep -Milliseconds 200"),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            });
            processes.Add(process);
            return process;
        }

        private static Queue<KimodoJob> Queue => (Queue<KimodoJob>)Get("queue");
        private static FieldInfo Field(string name) => typeof(KimodoSession).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
        private static object Get(string name) => Field(name).GetValue(null);
        private static void Set(string name, object value) => Field(name).SetValue(null, value);
        private static void Invoke(string name) => typeof(KimodoSession).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    }
}
