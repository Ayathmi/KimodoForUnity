// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Environment installation, repository download and dependency setup GUI.</summary>
    public sealed class KimodoSetupWizard : EditorWindow
    {
        private KimodoConfiguration config;
        private KimodoConfiguration snapshot;
        private Vector2 scroll;
        private int step;
        private string error;
        private string report;
        private string directoryStamp;
        private Task<string> probeTask;
        private Task<List<KimodoCheckItem>> directoryTask;
        private CancellationTokenSource cancellation;
        private List<KimodoCheckItem> files;
        private KimodoElevatedInstallation installation;
        private KimodoOwnedProcess download;
        private bool downloadingOllama;
        private string downloadLog;
        private Vector2 downloadLogScroll;
        private bool scrollDownloadToEnd;
        private string installLog;
        private Vector2 installLogScroll;
        private bool scrollLogToEnd;
        private GUIStyle installLogStyle;
        private bool Busy => probeTask != null || directoryTask != null || installation != null || download != null;

        /// <summary>Opens the setup window without starting installation or downloading models.</summary>
        [MenuItem("Tools/Kimodo for Unity/Setup Wizard", priority = 0)]
        public static void Open()
        {
            var window = GetWindow<KimodoSetupWizard>("Kimodo Setup");
            window.minSize = new Vector2(660, 530);
            window.Show();
        }

        private void OnEnable()
        {
            config = KimodoConfiguration.Load();
            config.Normalize();
            EditorApplication.update += Tick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            cancellation?.Cancel();
            cancellation?.Dispose();
            cancellation = null;
            probeTask = null;
            directoryTask = null;
            installation?.Dispose();
            installation = null;
            download?.Dispose();
            download = null;
        }

        private string T(string zh, string en) => config.chinese ? zh : en;

        private void Tick()
        {
            if (installation != null)
            {
                var previousLog = installLog;
                installLog = installation.Output + "\n" + installation.Errors;
                if (installation.Finished)
                {
                    installLog = installation.Output + "\n" + installation.Errors;
                    if (installation.ExitCode != 0) error = T("安装失败，请查看日志。", "Installation failed. See the log.");
                    else installLog += "\n" + T("安装完成", "Installation completed");
                    installation.Dispose();
                    installation = null;
                }
                if (!string.Equals(previousLog, installLog, StringComparison.Ordinal))
                    scrollLogToEnd = true;
                Repaint();
            }
            if (download != null)
            {
                var previousLog = downloadLog;
                downloadLog = download.Output + "\n" + download.Errors;
                if (download.Finished)
                {
                    downloadLog = download.Output + "\n" + download.Errors;
                    if (!downloadingOllama && download.ExitCode == 2)
                    {
                        error = KimodoModelDownloader.LoginRequired;
                        downloadLog = null;
                    }
                    else if (download.ExitCode != 0)
                        error = T("拉取失败，请查看日志。", "Download failed. See the log.");
                    download.Dispose();
                    download = null;
                }
                if (!string.Equals(previousLog, downloadLog, StringComparison.Ordinal))
                    scrollDownloadToEnd = true;
                Repaint();
            }
            if (directoryTask == null || !directoryTask.IsCompleted || probeTask == null || !probeTask.IsCompleted) return;
            try
            {
                files = directoryTask.GetAwaiter().GetResult();
                if (directoryStamp != KimodoModelFiles.Stamp(snapshot))
                    throw new IOException(T("目录已更改，请重新检测。", "Directories changed. Check again."));
                var probe = JsonUtility.FromJson<KimodoPythonProbe>(probeTask.GetAwaiter().GetResult());
                var lines = new List<string> { "Python: " + probe.version, "CUDA: " + probe.cuda + "  " + probe.gpu };
                foreach (var module in probe.modules)
                    lines.Add((module.passed ? "PASS " : "MISSING ") + module.name + ": " + module.detail);
                report = string.Join("\n", lines);
                KimodoSession.SetInspection(snapshot, probe, files);
            }
            catch (OperationCanceledException) { report = T("已取消", "Cancelled"); }
            catch (Exception ex) { error = ex.GetBaseException().Message; }
            finally
            {
                probeTask = null;
                directoryTask = null;
                cancellation?.Dispose();
                cancellation = null;
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (config == null) config = KimodoConfiguration.Load();
            using (new EditorGUI.DisabledScope(Busy))
            {
                KimodoEditorLanguage.DrawSelector(config);
                GUILayout.Label("Kimodo for Unity", EditorStyles.boldLabel);
                step = GUILayout.Toolbar(step, config.chinese
                    ? new[] { "1 路径", "2 环境", "3 授权与模型", "4 GPU", "5 检测" }
                    : new[] { "1 Paths", "2 Environment", "3 Access & models", "4 GPU", "5 Check" });
            }
            scroll = EditorGUILayout.BeginScrollView(scroll);
            using (new EditorGUI.DisabledScope(Busy))
            {
                switch (step)
                {
                    case 0: DrawPaths(); break;
                    case 1: DrawEnvironment(); break;
                    case 2: DrawModels(); break;
                    case 3: DrawGpu(); break;
                    case 4: DrawChecks(); break;
                }
            }
            if (Busy && GUILayout.Button(T("取消", "Cancel")))
            {
                cancellation?.Cancel();
                installation?.Dispose();
                installation = null;
                download?.Dispose();
                download = null;
                report = T("已取消", "Cancelled");
            }
            var displayedFiles = step == 4 ? files : null;
            if (displayedFiles != null)
                foreach (var item in displayedFiles)
                    EditorGUILayout.HelpBox(item.name + "\n" + item.detail, item.passed ? MessageType.Info : MessageType.Warning);
            if (step == 1 && !string.IsNullOrEmpty(installLog))
                DrawProcessLog(installLog, ref installLogScroll, ref scrollLogToEnd);
            if (step == 2 && !string.IsNullOrEmpty(downloadLog))
                DrawProcessLog(downloadLog, ref downloadLogScroll, ref scrollDownloadToEnd);
            if (step == 4 && !string.IsNullOrEmpty(report)) EditorGUILayout.TextArea(report, GUILayout.MinHeight(60));
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.EndScrollView();
            using (new EditorGUI.DisabledScope(Busy))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(T("上一步", "Back"))) step = Math.Max(0, step - 1);
                if (GUILayout.Button(T("保存", "Save"))) Try(() => config.Save());
                if (GUILayout.Button(T("下一步", "Next"))) step = Math.Min(4, step + 1);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawPaths()
        {
            config.assetsRoot = PathField(T("Kimodo工作区目录", "Kimodo workspace directory"), config.assetsRoot, true);
            config.installRoot = PathField(T("Kimodo安装目录", "Kimodo installation directory"), config.installRoot, true);
            config.pythonPath = PathField("Python executable", config.pythonPath, false);
        }

        private void DrawProcessLog(string log, ref Vector2 logScroll, ref bool scrollToEnd)
        {
            if (installLogStyle == null)
                installLogStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            var text = log.Length > 16000 ? log.Substring(log.Length - 16000) : log;
            var viewport = GUILayoutUtility.GetRect(0, 200, GUILayout.ExpandWidth(true));
            var width = Mathf.Max(1, viewport.width - GUI.skin.verticalScrollbar.fixedWidth - 4);
            var height = Mathf.Max(viewport.height, installLogStyle.CalcHeight(new GUIContent(text), width));
            if (Event.current.type == EventType.Repaint && scrollToEnd)
            {
                logScroll.y = Mathf.Max(0, height - viewport.height);
                scrollToEnd = false;
            }
            logScroll = GUI.BeginScrollView(viewport, logScroll, new Rect(0, 0, width, height), false, true);
            GUI.TextArea(new Rect(0, 0, width, height), text, installLogStyle);
            GUI.EndScrollView();
        }

        private void DrawEnvironment()
        {
            config.ninjaPath = PathField("Ninja executable", config.ninjaPath, false);
            config.gccPath = PathField("GCC executable", config.gccPath, false);
            config.gxxPath = PathField("G++ executable", config.gxxPath, false);
            if (GUILayout.Button(T("一键安装", "Install dependencies")))
                Try(() =>
                {
                    if (KimodoSession.EncoderRunning) throw new InvalidOperationException(T("请先停止生成任务。", "Stop generation first."));
                    config.Save();
                    KimodoSession.InvalidateInspection();
                    installLog = report = null;
                    installLogScroll = Vector2.zero;
                    scrollLogToEnd = true;
                    installation = KimodoInstaller.Start(config.Copy());
                });
            if (GUILayout.Button(T("安装量化依赖", "Install quantization dependencies")))
                Try(() =>
                {
                    if (KimodoSession.EncoderRunning) throw new InvalidOperationException(T("请先停止生成任务。", "Stop generation first."));
                    config.Save();
                    KimodoSession.InvalidateInspection();
                    installLog = report = null;
                    scrollLogToEnd = true;
                    installation = KimodoInstaller.Start(config.Copy(), true);
                });
            Text(T("如果您希望手动安装，可以在终端中执行如下命令：",
                "If you prefer manual installation, run the following commands in your terminal:"));
            var commands = KimodoInstaller.ManualCommands(config);
            var commandStyle = new GUIStyle(EditorStyles.textArea) { wordWrap = true };
            var commandHeight = Mathf.Max(190, commandStyle.CalcHeight(new GUIContent(commands), Mathf.Max(300, position.width - 35)));
            EditorGUILayout.TextArea(commands, commandStyle, GUILayout.Height(commandHeight));
            if (GUILayout.Button(T("复制命令", "Copy commands"))) EditorGUIUtility.systemCopyBuffer = commands;
            Link(T("官方Kimodo安装说明", "Official Kimodo installation"), "https://research.nvidia.com/labs/sil/projects/kimodo/docs/getting_started/installation.html");
            Link("PyTorch CUDA Wheels", "https://pytorch.org/get-started/locally/");
            Link("pybind11 Python 3.14 support", "https://pybind11.readthedocs.io/en/stable/changelog.html");
        }

        private void DrawModels()
        {
            KimodoModelPresets.DrawSelector(config);
            Text(T("您需要同意以下授权协议，方可使用对应的工具：",
                "Agree to the following licenses before using the corresponding tools:"));
            Link("Kimodo Apache 2.0", "https://github.com/nv-tlabs/kimodo/blob/main/LICENSE");
            Link("NVIDIA Open Model License", "https://huggingface.co/nvidia/" + KimodoConfiguration.Model + "/blob/main/LICENSE");
            Link("Meta Llama 3 Community License", "https://huggingface.co/meta-llama/Meta-Llama-3-8B-Instruct/blob/main/LICENSE");
            Link("LLM2Vec MIT", "https://github.com/McGill-NLP/llm2vec/blob/main/LICENSE");
            Text("请先自行登录hugging face cli");
            var quantized = KimodoModelPresets.IsQuantized(config);
            for (var i = 0; i < (quantized ? 1 : 3); i++)
            {
                EditorGUILayout.Space();
                Link(KimodoModelFiles.Repositories[i], "https://huggingface.co/" + KimodoModelFiles.Repositories[i] + "/tree/main");
                config.models[i].directory = PathField(T("仓库目录", "Repository directory"), config.models[i].directory, true);
            }
            if (quantized)
            {
                EditorGUILayout.Space();
                Link(KimodoModelPresets.Repository(config), "https://huggingface.co/" + KimodoModelPresets.Repository(config) + "/tree/main");
                var directory = PathField(T("仓库目录", "Repository directory"), KimodoModelPresets.Directory(config), true);
                if (config.modelPreset == KimodoModelPreset.Int8) config.int8Directory = directory;
                else config.nf4Directory = directory;
            }
            if (GUILayout.Button(T("一键拉取仓库", "Download repositories")))
                Try(() => BeginDownload(false));
            if (quantized) return;
            EditorGUILayout.Space();
            Text(T("Meta Llama 3需要从ollama下载，您需要在这里下载安装ollama：",
                "Download Meta Llama 3 through Ollama. Download and install Ollama here:"));
            Link("Ollama", "https://ollama.com/download");
            Link("Llama 3 8B", "https://ollama.com/library/llama3:8b");
            Link("llama3:8b-instruct-fp16", "https://ollama.com/library/llama3:8b-instruct-fp16");
            config.ollamaPath = PathField(T("Ollama 可执行文件", "Ollama executable"), config.ollamaPath, false);
            config.ollamaModelsPath = PathField(T("Ollama 模型目录", "Ollama model directory"), config.ollamaModelsPath, true);
            config.models[3].directory = PathField(T("转换后模型目录", "Converted model directory"), config.models[3].directory, true);
            if (GUILayout.Button(T("拉取并转换 Llama 3 FP16", "Download and convert Llama 3 FP16")))
                Try(() => BeginDownload(true));
        }

        private void BeginDownload(bool ollama)
        {
            if (KimodoSession.EncoderRunning) throw new InvalidOperationException(T("请先停止生成任务。", "Stop generation first."));
            config.Save();
            KimodoSession.InvalidateInspection();
            files = null;
            report = downloadLog = null;
            downloadLogScroll = Vector2.zero;
            scrollDownloadToEnd = true;
            downloadingOllama = ollama;
            download = ollama ? KimodoModelDownloader.StartOllama(config.Copy()) : KimodoModelDownloader.Start(config.Copy());
        }
        private void DrawGpu()
        {
            Link(T("NVIDIA 系统内存回退", "NVIDIA system-memory fallback"), "https://nvidia.custhelp.com/app/answers/detail/a_id/5490");
            config.cpuTextEncoder = EditorGUILayout.ToggleLeft(T("CPU 文本编码备用模式", "CPU text encoder fallback"), config.cpuTextEncoder);
            config.port = EditorGUILayout.IntField(T("本机服务端口", "Loopback service port"), config.port);
            config.idleMinutes = EditorGUILayout.IntField(T("空闲释放分钟数", "Idle release minutes"), config.idleMinutes);
        }

        private void DrawChecks()
        {
            if (GUILayout.Button(T("检测现有依赖", "Check dependencies"))) Try(BeginCheck);
            if (GUILayout.Button(T("打开生成窗口", "Open generation"))) KimodoWindow.Open();
        }

        private void BeginCheck()
        {
            config.Normalize();
            config.Save();
            snapshot = config.Copy();
            files = null;
            report = null;
            KimodoSession.InvalidateInspection();
            cancellation = new CancellationTokenSource();
            directoryStamp = KimodoModelFiles.Stamp(snapshot);
            directoryTask = KimodoModelFiles.CheckDirectoriesAsync(snapshot, cancellation.Token);
            probeTask = KimodoDependencyCheck.ProbeAsync(snapshot, cancellation.Token);
        }

        private string PathField(string label, string value, bool directory)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value);
            if (GUILayout.Button("...", GUILayout.Width(32)))
            {
                var selected = directory ? EditorUtility.OpenFolderPanel(label, value, "")
                    : EditorUtility.OpenFilePanel(label, string.IsNullOrEmpty(value) ? "" : Path.GetDirectoryName(value), "exe");
                if (!string.IsNullOrEmpty(selected)) value = selected;
            }
            EditorGUILayout.EndHorizontal();
            return value;
        }

        private void Link(string label, string url)
        {
            var rect = GUILayoutUtility.GetRect(new GUIContent(label), EditorStyles.linkLabel);
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (GUI.Button(rect, label, EditorStyles.linkLabel)) Application.OpenURL(url);
        }

        private void Text(string text) => EditorGUILayout.LabelField(text, EditorStyles.wordWrappedLabel);

        private void Try(Action action)
        {
            error = null;
            try { action(); }
            catch (Exception ex) { error = ex.GetBaseException().Message; }
        }
    }
}
