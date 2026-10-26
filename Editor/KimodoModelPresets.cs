// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using UnityEditor;

namespace KimodoForUnity
{
    /// <summary>Stable serialized identities for the supported encoder combinations.</summary>
    public enum KimodoModelPreset { Fp16 = 0, Int8 = 1, Nf4 = 2 }

    /// <summary>Shared selection and explicitly supported merged encoder repositories.</summary>
    public static class KimodoModelPresets
    {
        /// <summary>Pinned repository containing both merged adapters and INT8 weights.</summary>
        public const string Int8Repository = "voxta/Llama-3-8B-LLM2Vec-ARDY-INT8";
        /// <summary>Pinned repository containing both merged adapters and NF4 weights.</summary>
        public const string Nf4Repository = "voxta/Llama-3-8B-LLM2Vec-ARDY-NF4";
        /// <summary>Inspected immutable INT8 repository revision.</summary>
        public const string Int8Revision = "96c7eb1cc9100cd3925662d1093926c00b64a698";
        /// <summary>Inspected immutable NF4 repository revision.</summary>
        public const string Nf4Revision = "f746923e9e7edb3b6fc54aae7dfd8cecb0f52a8f";

        /// <summary>Whether the selection requires a merged bitsandbytes encoder.</summary>
        public static bool IsQuantized(KimodoConfiguration config) => config.modelPreset != KimodoModelPreset.Fp16;
        /// <summary>Repository appropriate to the selected encoder format.</summary>
        public static string Repository(KimodoConfiguration config)
            => config.modelPreset == KimodoModelPreset.Int8 ? Int8Repository : Nf4Repository;
        /// <summary>Local directory appropriate to the selected encoder format.</summary>
        public static string Directory(KimodoConfiguration config)
            => config.modelPreset == KimodoModelPreset.Int8 ? config.int8Directory : config.nf4Directory;
        /// <summary>Workspace-specific editor preference avoids stale selections in another open window.</summary>
        public static string PreferenceKey(KimodoConfiguration config)
            => "KimodoForUnity.ModelPreset." + (string.IsNullOrWhiteSpace(config.assetsRoot) ? "unconfigured"
                : Path.GetFullPath(config.assetsRoot).TrimEnd('\\', '/').ToUpperInvariant());

        /// <summary>Applies the shared selection without overwriting unsaved path edits.</summary>
        public static void Synchronize(KimodoConfiguration config)
        {
            var selected = EditorPrefs.GetInt(PreferenceKey(config), (int)config.modelPreset);
            if (selected >= 0 && selected <= 2) config.modelPreset = (KimodoModelPreset)selected;
        }

        /// <summary>Changes the shared selection and invalidates the previous inference inspection.</summary>
        public static void Set(KimodoConfiguration config, KimodoModelPreset preset)
        {
            if (!Enum.IsDefined(typeof(KimodoModelPreset), preset)) throw new ArgumentOutOfRangeException(nameof(preset));
            if (KimodoSession.EncoderRunning) throw new InvalidOperationException("Stop generation before changing model combinations.");
            config.modelPreset = preset;
            EditorPrefs.SetInt(PreferenceKey(config), (int)preset);
            KimodoSession.InvalidateInspection();
        }

        /// <summary>Draws the synchronized combination selector in either editor window.</summary>
        public static void DrawSelector(KimodoConfiguration config)
        {
            Synchronize(config);
            using (new EditorGUI.DisabledScope(KimodoSession.EncoderRunning))
            {
                var selected = (KimodoModelPreset)EditorGUILayout.Popup(config.chinese ? "模型组合" : "Model combination", (int)config.modelPreset,
                    config.chinese ? new[] { "原版 FP16", "通用 INT8 量化", "通用 NF4 量化" }
                        : new[] { "Original FP16", "General INT8", "General NF4" });
                if (selected != config.modelPreset) Set(config, selected);
            }
        }
    }
}
