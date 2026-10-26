// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace KimodoForUnity.Tests
{
    /// <summary>Runs focused tests and retains the actual Unity NUnit XML.</summary>
    public static class KimodoTestExport
    {
        private static TestRunnerApi api;
        private static Recorder recorder;

        /// <summary>Starts a focused asynchronous test run with external evidence output.</summary>
        public static void Run(string outputPath)
        {
            if (api != null) throw new InvalidOperationException("A Kimodo test export is already running.");
            api = ScriptableObject.CreateInstance<TestRunnerApi>();
            recorder = new Recorder(outputPath);
            api.RegisterCallbacks(recorder);
            api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode, assemblyNames = new[] { "KimodoForUnity.Editor.Tests" } }));
        }

        private sealed class Recorder : ICallbacks
        {
            private readonly string path;
            public Recorder(string outputPath) { path = outputPath; }
            public void RunStarted(ITestAdaptor testsToRun) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }
            public void RunFinished(ITestResultAdaptor result)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    TestRunnerApi.SaveResultToFile(result, path);
                }
                finally
                {
                    api.UnregisterCallbacks(this);
                    UnityEngine.Object.DestroyImmediate(api);
                    api = null;
                    recorder = null;
                }
            }
        }
    }
}
