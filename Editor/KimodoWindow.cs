// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Generation, history and transactional animation import.</summary>
    public sealed class KimodoWindow : EditorWindow
    {
        private KimodoConfiguration config;
        private string prompt = "A person walks forward at a steady pace.";
        private int duration = 5;
        private int frameRate = 30;
        private int steps = 100;
        private int seed = 42;
        private int tab;
        private string error;
        private string bvhPath = "";
        private string loadedPath;
        private string loadedHash;
        private KimodoBvh motion;
        private KimodoPreview preview;
        private GameObject target;
        private int frame;
        private float yaw;
        private bool playing;
        private bool inPlace = true;
        private bool loop;
        private double lastFrame;
        private Vector2 scroll;
        private Vector2 logScroll;
        private string previousLog;
        private bool scrollLogToEnd;
        private GUIStyle logStyle;

        /// <summary>Opens the main tool without starting services.</summary>
        [MenuItem("Tools/Kimodo for Unity/Motion Studio", priority = 1)]
        public static void Open()
        {
            var window = GetWindow<KimodoWindow>("Kimodo for Unity");
            window.minSize = new Vector2(700, 620);
            window.Show();
        }

        private string T(string zh, string en) => config.chinese ? zh : en;

        private void OnEnable()
        {
            config = KimodoConfiguration.Load();
            if (!string.IsNullOrEmpty(config.assetsRoot)) KimodoSession.LoadHistory(config);
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            preview?.Dispose();
            preview = null;
        }

        private void OnFocus()
        {
            // Saved Wizard paths become visible without discarding the prompt or import preview.
            config = KimodoConfiguration.Load();
        }

        private void Tick()
        {
            if (playing && motion != null && EditorApplication.timeSinceStartup - lastFrame >= motion.FrameTime)
            {
                frame = (frame + 1) % motion.Frames;
                lastFrame = EditorApplication.timeSinceStartup;
            }
            Repaint();
        }

        private void OnGUI()
        {
            if (config == null) config = KimodoConfiguration.Load();
            KimodoEditorLanguage.DrawSelector(config);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Kimodo for Unity " + KimodoConfiguration.Version, EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(T("设置向导", "Setup Wizard"), GUILayout.Width(120))) KimodoSetupWizard.Open();
            if (GUILayout.Button(T("重读设置", "Reload settings"), GUILayout.Width(120))) config = KimodoConfiguration.Load();
            EditorGUILayout.EndHorizontal();
            tab = GUILayout.Toolbar(tab, config.chinese ? new[] { "生成", "预览与导入", "任务历史", "关于" } : new[] { "Generate", "Preview & import", "History", "About" });
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (tab == 0) DrawGeneration();
            if (tab == 1) DrawImport();
            if (tab == 2) DrawHistory();
            if (tab == 3) DrawAbout();
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.EndScrollView();
        }

        private void DrawGeneration()
        {
            KimodoModelPresets.DrawSelector(config);
            GUILayout.Label(KimodoConfiguration.Model + " | " + (config.cpuTextEncoder
                ? T("CPU 文本编码备用模式", "CPU text encoder fallback") : T("CUDA 文本编码器", "CUDA text encoder")));
            GUILayout.Label(T("动作描述，建议英文", "Motion prompt, English recommended"));
            prompt = EditorGUILayout.TextArea(prompt, GUILayout.MinHeight(75));
            frameRate = Mathf.Clamp(EditorGUILayout.IntField(T("帧速率", "Frame rate"), frameRate), 1, 60);
            duration = Mathf.Clamp(EditorGUILayout.IntField(T("时长，秒（1–10）", "Duration, seconds (1–10)"), duration), 1, 10);
            seed = EditorGUILayout.IntField(T("随机种子", "Seed"), seed);
            steps = EditorGUILayout.IntField(T("去噪步数", "Diffusion steps"), steps);
            using (new EditorGUI.DisabledScope(!KimodoSession.IsInspected(config)))
                if (GUILayout.Button(T("加入生成队列", "Queue generation"), GUILayout.Height(32)))
                    Try(() => KimodoSession.Enqueue(config, prompt, duration, seed, steps, frameRate));
            if (!KimodoSession.IsInspected(config))
                EditorGUILayout.HelpBox(T("当前配置尚未通过本次编辑器会话的依赖检测。", "These settings have not passed dependency checks in this Editor session."), MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(T("停止任务与编码器", "Stop tasks and encoder"))) Try(KimodoSession.StopEncoder);
            var log = KimodoSession.Status + "\n" + KimodoSession.LiveOutput;
            if (!string.IsNullOrEmpty(error)) log += "\n" + error;
            if (GUILayout.Button(T("复制日志", "Copy log"))) EditorGUIUtility.systemCopyBuffer = log;
            using (new EditorGUI.DisabledScope(!File.Exists(KimodoSession.LogFile)))
                if (GUILayout.Button(T("打开日志文件", "Open log file")))
                    EditorUtility.OpenWithDefaultApp(KimodoSession.LogFile);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(KimodoSession.Status, EditorStyles.wordWrappedLabel);
            GUILayout.Label(T("排队数量：", "Queued: ") + KimodoSession.Queued);
            DrawLog(log);
        }

        private void DrawLog(string log)
        {
            if (logStyle == null) logStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            if (!string.Equals(previousLog, log, StringComparison.Ordinal))
            {
                previousLog = log;
                scrollLogToEnd = true;
            }
            var text = log.Length > 16000 ? log.Substring(log.Length - 16000) : log;
            var viewport = GUILayoutUtility.GetRect(0, 220, GUILayout.ExpandWidth(true));
            var width = Mathf.Max(1, viewport.width - GUI.skin.verticalScrollbar.fixedWidth - 4);
            var height = Mathf.Max(viewport.height, logStyle.CalcHeight(new GUIContent(text), width));
            if (Event.current.type == EventType.Repaint && scrollLogToEnd)
            {
                logScroll.y = Mathf.Max(0, height - viewport.height);
                scrollLogToEnd = false;
            }
            logScroll = GUI.BeginScrollView(viewport, logScroll, new Rect(0, 0, width, height), false, true);
            GUI.TextArea(new Rect(0, 0, width, height), text, logStyle);
            GUI.EndScrollView();
        }

        private void DrawImport()
        {
            EditorGUILayout.BeginHorizontal();
            bvhPath = EditorGUILayout.TextField("BVH", bvhPath);
            if (GUILayout.Button("...", GUILayout.Width(32)))
            {
                var selected = EditorUtility.OpenFilePanel("SOMA standard T-pose BVH", config.assetsRoot, "bvh");
                if (!string.IsNullOrEmpty(selected)) bvhPath = selected;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(T("填入刚才生成的动画BVH文件路径", "Enter the BVH file path of the animation you just generated."), EditorStyles.wordWrappedLabel);
            var newTarget = (GameObject)EditorGUILayout.ObjectField(T("目标 Humanoid，可选", "Target Humanoid, optional"), target, typeof(GameObject), false);
            if (newTarget != target)
            {
                target = newTarget;
                if (motion != null) Try(LoadPreview);
            }
            if (GUILayout.Button(T("加载并验证 BVH", "Load and validate BVH")))
                Try(() => LoadMotion(bvhPath, target));
            if (motion == null) return;
            EditorGUILayout.LabelField(motion.Frames + " frames | " + (1f / motion.FrameTime).ToString("F2") + " fps | " + motion.Duration.ToString("F3") + " s");
            frame = EditorGUILayout.IntSlider(T("帧", "Frame"), frame, 0, motion.Frames - 1);
            yaw = EditorGUILayout.Slider(T("预览角度", "View angle"), yaw, -180, 180);
            playing = EditorGUILayout.Toggle(T("播放", "Play"), playing);
            var rect = GUILayoutUtility.GetRect(300, 330, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint && preview != null)
                try { preview.Draw(rect, frame, yaw); }
                catch (Exception ex) { error = ex.Message; playing = false; }
            inPlace = EditorGUILayout.Toggle(T("原地，保留垂直起伏与朝向", "In-place, retain vertical motion and orientation"), inPlace);
            loop = EditorGUILayout.Toggle(T("循环，首尾需另行检查", "Loop, inspect the seam separately"), loop);
            if (GUILayout.Button(T("烘焙并另存 Humanoid AnimationClip", "Bake and save Humanoid AnimationClip"), GUILayout.Height(32)))
                Try(ExportClip);
        }

        private void LoadPreview()
        {
            preview?.Dispose();
            preview = new KimodoPreview();
            preview.Load(motion, target);
        }

        /// <summary>Validates an external motion and opens its isolated preview.</summary>
        public void LoadMotion(string path, GameObject targetPrefab = null)
        {
            var fullPath = Path.GetFullPath(path);
            var before = Hash(fullPath);
            var data = KimodoBvh.Read(fullPath);
            if (before != Hash(fullPath)) throw new IOException("BVH changed during loading. Load it again.");
            motion = data;
            bvhPath = loadedPath = fullPath;
            loadedHash = before;
            target = targetPrefab;
            frame = 0;
            LoadPreview();
            tab = 1;
        }

        private static string Hash(string path)
        {
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream));
        }

        private void ExportClip()
        {
            if (Path.GetFullPath(bvhPath) != loadedPath || Hash(loadedPath) != loadedHash)
                throw new IOException("BVH changed since preview. Load and review it again before importing.");
            var path = EditorUtility.SaveFilePanelInProject("Save Humanoid animation", Path.GetFileNameWithoutExtension(bvhPath), "anim", "Choose a game asset directory outside KimodoForUnity.");
            if (string.IsNullOrEmpty(path)) return;
            if (path.IndexOf("/KimodoForUnity/", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new ArgumentException("Save generated animation outside the plugin source directory.");
            if (File.Exists(path) || AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new IOException("Choose a new file name; existing assets are preserved.");
            if (Hash(loadedPath) != loadedHash)
                throw new IOException("BVH changed while choosing a destination. Load it again.");
            var clip = KimodoHumanoid.Bake(motion, inPlace, loop);
            try
            {
                clip.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();
                EditorGUIUtility.PingObject(clip);
                Selection.activeObject = clip;
            }
            catch
            {
                if (!AssetDatabase.Contains(clip)) DestroyImmediate(clip);
                throw;
            }
        }

        private void DrawHistory()
        {
            if (GUILayout.Button(T("读取本地历史", "Load local history"))) Try(() => KimodoSession.LoadHistory(config));
            foreach (var job in KimodoSession.History)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                GUILayout.Label(job.id + " | " + job.state);
                EditorGUILayout.LabelField(job.prompt ?? "", EditorStyles.wordWrappedLabel);
                EditorGUILayout.LabelField(job.detail ?? "", EditorStyles.wordWrappedLabel);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(T("复用参数", "Reuse parameters")))
                {
                    Try(() =>
                    {
                        KimodoModelPresets.Set(config, job.modelPreset);
                        prompt = job.prompt; duration = Mathf.Clamp(Mathf.RoundToInt(job.duration), 1, 10);
                        frameRate = job.requestedFps > 0 ? job.requestedFps : 30;
                        seed = job.seed; steps = job.steps; tab = 0;
                    });
                }
                if (GUILayout.Button(T("打开目录", "Open folder")))
                    Try(() => EditorUtility.RevealInFinder(KimodoSession.JobFolder(config, job.id)));
                using (new EditorGUI.DisabledScope(job.state != "Ready"))
                    if (GUILayout.Button(T("预览", "Preview")))
                        Try(() =>
                        {
                            LoadMotion(Path.Combine(KimodoSession.JobFolder(config, job.id), "output", "motion.bvh"), target);
                        });
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawAbout()
        {
            EditorGUILayout.LabelField(T(
                "Kimodo for Unity 基于 Kimodo，使用 Apache 2.0 授权协议。",
                "Kimodo for Unity is based on Kimodo and licensed under Apache 2.0."),
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Built with Meta Llama 3. NVIDIA Kimodo, model weights and LLM2Vec/Llama retain their own licenses. These dependencies and game assets are not included in this plugin.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField(T("由Github：@Ayathmi制作", "Created by GitHub: @Ayathmi"), EditorStyles.wordWrappedLabel);
        }

        private void Try(Action action)
        {
            error = null;
            try { action(); }
            catch (Exception ex) { error = ex.GetBaseException().Message; }
        }
    }
}
