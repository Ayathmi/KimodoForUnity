// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity.Tests
{
    /// <summary>Regression checks for readable terminal output and interface-only language changes.</summary>
    public sealed class KimodoInterfaceTests
    {
        /// <summary>Each carriage-return progress update becomes a distinct log line.</summary>
        [TestCase("Loading\r0%\r50%\r100%\n", "Loading\n0%\n50%\n100%\n")]
        [TestCase("first\r\nsecond\nthird\r", "first\nsecond\nthird\n")]
        [TestCase("Traceback\r\n  File C:\\folder\\file.py\r\n", "Traceback\n  File C:\\folder\\file.py\n")]
        public void ProgressAndTracebacksUseDistinctLines(string source, string expected)
            => Assert.AreEqual(expected, KimodoProcess.Sanitize(source));

        /// <summary>Color, cursor movement and hyperlink wrappers do not pollute editor text.</summary>
        [Test]
        public void TerminalFormattingDoesNotMergeProgressUpdates()
        {
            var source = "\u001b[32m20%\u001b[0m\r\u001b[2K40%\u001b[A\r"
                + "\u001b]8;;https://example.com\u0007done\u001b]8;;\u001b\\\n";
            Assert.AreEqual("20%\n40%\ndone\n", KimodoProcess.Sanitize(source));
        }

        /// <summary>Formatting removal happens before token redaction, and repeated sanitization is stable.</summary>
        [Test]
        public void NormalizedOutputStillRedactsCredentials()
        {
            var source = "hf_\u001b[31mABCDEFGHIJKLM\u001b[0m\rfinished\n";
            var output = KimodoProcess.Sanitize(source);
            Assert.AreEqual("[redacted]\nfinished\n", output);
            Assert.AreEqual(output, KimodoProcess.Sanitize(output));
            Assert.AreEqual("", KimodoProcess.Sanitize(null));
        }

        /// <summary>Real redirected stderr preserves CRLF across reads and separates progress refreshes.</summary>
        [Test]
        public void CapturedProcessProgressUsesSeparateLines()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Windows process capture test.");
            var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var command = "[Console]::Error.Write('10%' + [char]13); [Console]::Error.Flush(); "
                + "Start-Sleep -Milliseconds 100; [Console]::Error.Write('' + [char]10 + '50%' + [char]13 + '100%' + [char]10); "
                + "[Console]::Out.WriteLine('complete')";
            using (var process = new KimodoOwnedProcess(new ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-NoProfile -NonInteractive -Command " + KimodoProcess.Quote(command),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }))
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => process.Finished, 10000));
                Assert.AreEqual(0, process.ExitCode);
                Assert.AreEqual("10%\n50%\n100%\n", process.Errors);
                Assert.AreEqual("complete\n", process.Output);
            }
        }

        /// <summary>A language choice is shared without changing workspace paths or requiring a configured environment.</summary>
        [Test]
        public void LanguagePreferenceWorksAcrossWindowsWithoutWorkspace()
        {
            var existed = EditorPrefs.HasKey(KimodoEditorLanguage.PreferenceKey);
            var previous = EditorPrefs.GetBool(KimodoEditorLanguage.PreferenceKey);
            try
            {
                EditorPrefs.DeleteKey(KimodoEditorLanguage.PreferenceKey);
                var first = new KimodoConfiguration { chinese = false };
                var second = new KimodoConfiguration { chinese = true };
                Assert.IsFalse(KimodoEditorLanguage.Get(first));
                KimodoEditorLanguage.Set(first, false);
                Assert.IsFalse(KimodoEditorLanguage.Get(second));
                KimodoEditorLanguage.Set(second, true);
                Assert.IsTrue(KimodoEditorLanguage.Get(first));
                Assert.AreEqual("", first.assetsRoot);
                Assert.AreEqual("", first.installRoot);
            }
            finally
            {
                if (existed) EditorPrefs.SetBool(KimodoEditorLanguage.PreferenceKey, previous);
                else EditorPrefs.DeleteKey(KimodoEditorLanguage.PreferenceKey);
            }
        }

        /// <summary>Switching UI language preserves the dependency gate while inference changes still invalidate it.</summary>
        [Test]
        public void InspectionSignatureIgnoresOnlyLanguage()
        {
            var signature = typeof(KimodoSession).GetMethod("Signature", BindingFlags.Static | BindingFlags.NonPublic);
            var config = new KimodoConfiguration();
            var initial = (string)signature.Invoke(null, new object[] { config });
            config.chinese = false;
            Assert.AreEqual(initial, signature.Invoke(null, new object[] { config }));
            Assert.IsFalse(config.chinese);
            config.port++;
            Assert.AreNotEqual(initial, signature.Invoke(null, new object[] { config }));
        }
    }
}
