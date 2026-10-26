// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace KimodoForUnity.Tests
{
    /// <summary>Repository directory, migration and installer regression tests.</summary>
    public sealed class KimodoSetupTests
    {
        private string root;
        private KimodoModelLocation model;

        /// <summary>Creates only private temporary fixture files.</summary>
        [SetUp]
        public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "Kimodo-directory-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "arbitrary.txt"), "a local test payload");
            model = new KimodoModelLocation { directory = root };
        }

        /// <summary>Removes only the known fixture file and empty directory.</summary>
        [TearDown]
        public void TearDown()
        {
            if (File.Exists(Path.Combine(root, "arbitrary.txt"))) File.Delete(Path.Combine(root, "arbitrary.txt"));
            if (Directory.Exists(root)) Directory.Delete(root);
        }

        /// <summary>Local directory checks require no per-file user metadata.</summary>
        [Test]
        public void RepositoryDirectoryNeedsNoFileMetadata()
            => Assert.DoesNotThrow(() => KimodoModelFiles.CheckDirectory(model, CancellationToken.None));

        /// <summary>An empty destination is not an existing downloaded repository.</summary>
        [Test]
        public void EmptyRepositoryDirectoryFails()
        {
            File.Delete(Path.Combine(root, "arbitrary.txt"));
            Assert.Throws<InvalidDataException>(() => KimodoModelFiles.CheckDirectory(model, CancellationToken.None));
        }

        /// <summary>A missing local repository must fail the dependency gate.</summary>
        [Test]
        public void MissingRepositoryDirectoryFails()
        {
            model.directory = Path.Combine(root, "missing");
            Assert.Throws<DirectoryNotFoundException>(() => KimodoModelFiles.CheckDirectory(model, CancellationToken.None));
        }

        /// <summary>Relative or missing paths cannot silently depend on the process working directory.</summary>
        [TestCase("")]
        [TestCase("relative")]
        public void RepositoryDirectoryMustBeAbsolute(string path)
        {
            model.directory = path;
            Assert.Throws<InvalidDataException>(() => KimodoModelFiles.CheckDirectory(model, CancellationToken.None));
        }

        /// <summary>Cancellation stops directory inspection.</summary>
        [Test]
        public void DirectoryInspectionCanBeCancelled()
            => Assert.Throws<OperationCanceledException>(() => KimodoModelFiles.CheckDirectory(model, new CancellationToken(true)));

        /// <summary>Removing a selected directory invalidates the previous inspection.</summary>
        [Test]
        public void RemovedDirectoryInvalidatesStamp()
        {
            var config = new KimodoConfiguration { models = new[] { model } };
            var before = KimodoModelFiles.Stamp(config);
            File.Delete(Path.Combine(root, "arbitrary.txt"));
            Directory.Delete(root);
            Assert.AreNotEqual(before, KimodoModelFiles.Stamp(config));
        }

        /// <summary>Existing repository paths survive migration while retired per-file fields are dropped.</summary>
        [Test]
        public void OldRepositoryPreferencesKeepDirectories()
        {
            var config = JsonUtility.FromJson<KimodoConfiguration>(
                "{\"schemaVersion\":3,\"models\":[{\"directory\":\"D:/motion\",\"files\":[{\"path\":\"weights\",\"sha256\":\"old\"}]},{\"directory\":\"D:/encoder\"},{\"directory\":\"D:/adapter\"}]}");
            config.Normalize();
            Assert.AreEqual(6, config.schemaVersion);
            Assert.AreEqual("D:/motion", config.models[0].directory);
            Assert.AreEqual("D:/encoder", config.models[1].directory);
            Assert.AreEqual("D:/adapter", config.models[2].directory);
            Assert.AreEqual(4, config.models.Length);
            StringAssert.DoesNotContain("\"files\"", JsonUtility.ToJson(config));
            StringAssert.DoesNotContain("\"sha256\"", JsonUtility.ToJson(config));
        }

        /// <summary>Schema one preferences migrate without retaining the unused source reference.</summary>
        [Test]
        public void OldPreferencesMigrateToInstallationDirectory()
        {
            var config = JsonUtility.FromJson<KimodoConfiguration>("{\"schemaVersion\":1,\"assetsRoot\":\"D:/Kimodo Assets\",\"pythonPath\":\"D:/Python/python.exe\",\"sourcePath\":\"D:/retired\"}");
            config.Normalize();
            Assert.AreEqual(6, config.schemaVersion);
            Assert.That(config.installRoot.Replace('\\', '/'), Is.EqualTo("D:/Kimodo Assets/Environments/kimodo-py314"));
            Assert.AreEqual(4, config.models.Length);
            StringAssert.DoesNotContain("sourcePath", JsonUtility.ToJson(config));
        }

        /// <summary>Paths containing apostrophes remain one literal PowerShell argument.</summary>
        [Test]
        public void InstallerPathsAreLiteral()
        {
            Assert.AreEqual("'D:\\O''Brien\\$(no-execute)'", KimodoInstaller.PsQuote("D:\\O'Brien\\$(no-execute)"));
            var info = KimodoInstaller.PowerShellCommand("Write-Output 'ok'");
            Assert.IsFalse(info.UseShellExecute);
            Assert.IsTrue(info.CreateNoWindow);
        }

        /// <summary>Downloads preserve literal repository paths and use the current user's login context.</summary>
        [Test]
        public void DownloadCommandUsesLiteralDirectoriesWithoutElevation()
        {
            var config = DownloadConfiguration();
            config.models[0].directory = Path.Combine(root, "O'Brien $(no-execute)");
            var info = KimodoModelDownloader.StartInfo(config);
            Assert.IsFalse(info.UseShellExecute);
            Assert.IsTrue(info.RedirectStandardOutput);
            var encoded = info.Arguments.Substring(info.Arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16);
            var command = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            StringAssert.Contains(KimodoInstaller.PsQuote(config.models[0].directory), command);
            StringAssert.Contains("Download-KimodoModels.ps1", command);
            StringAssert.DoesNotContain("auth login", command);
        }

        /// <summary>Different repositories cannot overwrite the same download destination.</summary>
        [Test]
        public void DownloadsRejectOverlappingDirectories()
        {
            var config = DownloadConfiguration();
            config.models[2].directory = Path.Combine(config.models[0].directory, "nested");
            Assert.Throws<ArgumentException>(() => KimodoModelDownloader.StartInfo(config));
        }

        /// <summary>An adapter directory is not sufficient for the separate full base-model requirement.</summary>
        [Test]
        public void AdapterOnlyDirectoryDoesNotPassBaseModelCheck()
        {
            File.WriteAllText(Path.Combine(root, "config.json"), "{}");
            File.WriteAllText(Path.Combine(root, "adapter_model.safetensors"), "fixture");
            try { Assert.Throws<InvalidDataException>(() => KimodoModelFiles.CheckBaseModel(root)); }
            finally
            {
                File.Delete(Path.Combine(root, "config.json"));
                File.Delete(Path.Combine(root, "adapter_model.safetensors"));
            }
        }

        /// <summary>Schema four paths remain intact when the missing base-model slot is added.</summary>
        [Test]
        public void SchemaFourAddsBaseModelWithoutReplacingExistingDirectories()
        {
            var config = DownloadConfiguration();
            Array.Resize(ref config.models, 3);
            config.schemaVersion = 4;
            config.Normalize();
            Assert.AreEqual(Path.Combine(root, "encoder"), config.models[1].directory);
            Assert.That(config.models[3].directory.Replace('\\', '/'),
                Does.EndWith("/Models/TextEncoders/Ollama-Llama3-8B-Instruct-FP16"));
        }

        /// <summary>Existing manually prepared base weights remain usable when Ollama settings are added.</summary>
        [Test]
        public void SchemaFivePreservesExistingModelPaths()
        {
            var config = DownloadConfiguration();
            config.schemaVersion = 5;
            config.Normalize();
            Assert.AreEqual(6, config.schemaVersion);
            Assert.AreEqual(Path.Combine(root, "base"), config.models[3].directory);
            Assert.AreEqual(Path.Combine(root, "Models", "Ollama"), config.ollamaModelsPath);
        }

        /// <summary>The HF command no longer requests the gated base model.</summary>
        [Test]
        public void HfDownloadExcludesOllamaBaseModel()
        {
            var info = KimodoModelDownloader.StartInfo(DownloadConfiguration());
            var encoded = info.Arguments.Substring(info.Arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16);
            var command = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            StringAssert.DoesNotContain("-BaseDirectory", command);
            StringAssert.DoesNotContain("meta-llama/", command);
        }

        /// <summary>Ollama paths are passed literally and client installation is never part of the command.</summary>
        [Test]
        public void OllamaDownloadUsesExplicitExternalPaths()
        {
            var config = DownloadConfiguration();
            config.ollamaPath = Path.Combine(root, "arbitrary.txt");
            config.pythonPath = config.ollamaPath;
            config.ollamaModelsPath = Path.Combine(root, "O'Brien $(no-execute)");
            Directory.CreateDirectory(config.models[1].directory);
            var encoderConfig = Path.Combine(config.models[1].directory, "config.json");
            File.WriteAllText(encoderConfig, "{}");
            try
            {
                var info = KimodoModelDownloader.OllamaStartInfo(config);
                var encoded = info.Arguments.Substring(info.Arguments.IndexOf("-EncodedCommand ", StringComparison.Ordinal) + 16);
                var command = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
                StringAssert.Contains("Download-KimodoLlama.ps1", command);
                StringAssert.Contains(KimodoInstaller.PsQuote(config.ollamaModelsPath), command);
                StringAssert.Contains(KimodoInstaller.PsQuote(config.models[3].directory), command);
                Assert.IsFalse(info.UseShellExecute);
                config.ollamaModelsPath = config.models[1].directory;
                Assert.Throws<ArgumentException>(() => KimodoModelDownloader.OllamaStartInfo(config));
            }
            finally
            {
                File.Delete(encoderConfig);
                Directory.Delete(config.models[1].directory);
            }
        }

        /// <summary>Administrator startup uses UAC without unsupported pipe redirection.</summary>
        [Test]
        public void AdministratorInstallationUsesShellElevation()
        {
            var info = KimodoInstaller.ElevationCommand("Write-Output 'ok'");
            Assert.IsTrue(info.UseShellExecute);
            Assert.AreEqual("runas", info.Verb);
            Assert.AreEqual(System.Diagnostics.ProcessWindowStyle.Hidden, info.WindowStyle);
            Assert.IsFalse(info.RedirectStandardOutput);
            Assert.IsFalse(info.RedirectStandardError);
            StringAssert.Contains("-EncodedCommand ", info.Arguments);
        }

        private KimodoConfiguration DownloadConfiguration()
            => new KimodoConfiguration
            {
                assetsRoot = root, pythonPath = Path.Combine(root, "python.exe"),
                models = new[]
                {
                    new KimodoModelLocation { directory = Path.Combine(root, "motion") },
                    new KimodoModelLocation { directory = Path.Combine(root, "encoder") },
                    new KimodoModelLocation { directory = Path.Combine(root, "adapter") },
                    new KimodoModelLocation { directory = Path.Combine(root, "base") }
                }
            };
    }
}
