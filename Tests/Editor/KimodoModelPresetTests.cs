// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity.Tests
{
    /// <summary>Format, timing and synchronization gates exercised without downloading model weights.</summary>
    public sealed class KimodoModelPresetTests
    {
        /// <summary>Both frame-rate boundaries accept positive integral durations.</summary>
        [TestCase(1, 1)]
        [TestCase(60, 10)]
        [TestCase(24, 5)]
        public void ValidOutputTiming(int fps, int seconds)
            => Assert.DoesNotThrow(() => KimodoSession.ValidateRequest("A person walks.", seconds, 100, fps));

        /// <summary>Invalid frame rates cannot reach a generation process.</summary>
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(61)]
        public void InvalidOutputRate(int fps)
            => Assert.Throws<ArgumentException>(() => KimodoSession.ValidateRequest("A person walks.", 5, 100, fps));

        /// <summary>Zero and fractional durations are rejected independently of the GUI.</summary>
        [TestCase(0f)]
        [TestCase(0.5f)]
        [TestCase(5.5f)]
        [TestCase(float.PositiveInfinity)]
        public void InvalidWholeSeconds(float seconds)
            => Assert.Throws<ArgumentException>(() => KimodoSession.ValidateRequest("A person walks.", seconds, 100, 30));

        /// <summary>Original repository paths survive addition of separate quantized repositories.</summary>
        [Test]
        public void MigrationKeepsOriginalModelsAndDefaultsToFp16()
        {
            var config = JsonUtility.FromJson<KimodoConfiguration>("{\"schemaVersion\":6,\"assetsRoot\":\"D:/KimodoMigration\",\"models\":[{\"directory\":\"D:/existing-motion\"},{\"directory\":\"D:/existing-mntp\"},{\"directory\":\"D:/existing-supervised\"},{\"directory\":\"D:/existing-llama\"}]}");
            config.Normalize();
            Assert.AreEqual(KimodoModelPreset.Fp16, config.modelPreset);
            Assert.AreEqual("D:/existing-llama", config.models[3].directory);
            Assert.AreNotEqual(config.int8Directory, config.nf4Directory);
            Assert.AreEqual(7, config.schemaVersion);
        }

        /// <summary>Two windows see the same selection without overwriting unsaved path fields.</summary>
        [Test]
        public void CombinationSynchronizesWithinItsWorkspace()
        {
            if (KimodoSession.EncoderRunning) Assert.Ignore("A user generation is active.");
            var first = new KimodoConfiguration { assetsRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
            var second = first.Copy();
            second.nf4Directory = "D:/unsaved-encoder";
            var key = KimodoModelPresets.PreferenceKey(first);
            try
            {
                KimodoModelPresets.Set(first, KimodoModelPreset.Nf4);
                KimodoModelPresets.Synchronize(second);
                Assert.AreEqual(KimodoModelPreset.Nf4, second.modelPreset);
                Assert.AreEqual("D:/unsaved-encoder", second.nf4Directory);
                KimodoModelPresets.Set(second, KimodoModelPreset.Int8);
                KimodoModelPresets.Synchronize(first);
                Assert.AreEqual(KimodoModelPreset.Int8, first.modelPreset);
                var other = new KimodoConfiguration { assetsRoot = first.assetsRoot + "-other" };
                KimodoModelPresets.Synchronize(other);
                Assert.AreEqual(KimodoModelPreset.Fp16, other.modelPreset);
            }
            finally { EditorPrefs.DeleteKey(key); }
        }

        /// <summary>A different 4-bit format cannot masquerade as the NF4 preset.</summary>
        [TestCase("nf4", true)]
        [TestCase("fp4", false)]
        [TestCase("nvfp4", false)]
        public void LocalEncoderFormatMatchesPreset(string format, bool valid)
        {
            var folder = Path.Combine(Path.GetTempPath(), "KimodoFormat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var names = new[] { "config.json", "model.safetensors", "tokenizer.json", "tokenizer_config.json", "llm2vec_config.json" };
            try
            {
                foreach (var name in names) File.WriteAllText(Path.Combine(folder, name), "{}");
                File.WriteAllText(Path.Combine(folder, "config.json"), "{\"model_type\":\"llama\",\"hidden_size\":4096,\"quantization_config\":{\"quant_method\":\"bitsandbytes\",\"load_in_4bit\":true,\"bnb_4bit_quant_type\":\"" + format + "\"}}");
                var config = new KimodoConfiguration { modelPreset = KimodoModelPreset.Nf4, nf4Directory = folder };
                if (valid) Assert.DoesNotThrow(() => KimodoModelFiles.CheckQuantizedEncoder(config, CancellationToken.None));
                else Assert.Throws<InvalidDataException>(() => KimodoModelFiles.CheckQuantizedEncoder(config, CancellationToken.None));
                config.modelPreset = KimodoModelPreset.Int8;
                config.int8Directory = folder;
                Assert.Throws<InvalidDataException>(() => KimodoModelFiles.CheckQuantizedEncoder(config, CancellationToken.None));
            }
            finally
            {
                foreach (var name in names) File.Delete(Path.Combine(folder, name));
                Directory.Delete(folder);
            }
        }

        /// <summary>Quantized combinations require the additional modules and a supported CUDA device.</summary>
        [Test]
        public void QuantizedDependencyGateRejectsMissingBackendAndCpuFallback()
        {
            var config = new KimodoConfiguration { modelPreset = KimodoModelPreset.Int8 };
            var probe = new KimodoPythonProbe { pythonSupported = true, cuda = true, computeMajor = 8, computeMinor = 9, modules = new KimodoCheckItem[11] };
            for (var i = 0; i < probe.modules.Length; i++) probe.modules[i] = new KimodoCheckItem { passed = true };
            var files = new List<KimodoCheckItem> { new KimodoCheckItem { passed = true }, new KimodoCheckItem { passed = true } };
            Assert.IsTrue(KimodoDependencyCheck.CanGenerate(probe, files, config));
            config.cpuTextEncoder = true;
            Assert.IsFalse(KimodoDependencyCheck.CanGenerate(probe, files, config));
            config.cpuTextEncoder = false;
            probe.modules[9].passed = false;
            Assert.IsFalse(KimodoDependencyCheck.CanGenerate(probe, files, config));
            probe.modules[9].passed = true;
            probe.computeMajor = 6;
            probe.computeMinor = 1;
            Assert.IsFalse(KimodoDependencyCheck.CanGenerate(probe, files, config));
        }
    }
}
