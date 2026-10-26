// SPDX-License-Identifier: Apache-2.0
using UnityEditor;

namespace KimodoForUnity
{
    /// <summary>Shares a persistent interface-language preference without rewriting environment settings.</summary>
    public static class KimodoEditorLanguage
    {
        /// <summary>Editor-local preference, independent of the external workspace.</summary>
        public const string PreferenceKey = "KimodoForUnity.InterfaceChinese";
        private static readonly string[] Options = { "中文", "English" };

        /// <summary>Uses the existing workspace language until the user explicitly chooses one.</summary>
        public static bool Get(KimodoConfiguration config)
            => EditorPrefs.GetBool(PreferenceKey, config.chinese);

        /// <summary>Updates only the language and the shared editor preference.</summary>
        public static void Set(KimodoConfiguration config, bool chinese)
        {
            config.chinese = chinese;
            EditorPrefs.SetBool(PreferenceKey, chinese);
        }

        /// <summary>Draws the same language selector in Motion Studio and Setup Wizard.</summary>
        public static void DrawSelector(KimodoConfiguration config)
        {
            config.chinese = Get(config);
            var selected = EditorGUILayout.Popup("Language / 语言", config.chinese ? 0 : 1, Options) == 0;
            if (selected != config.chinese) Set(config, selected);
        }
    }
}
