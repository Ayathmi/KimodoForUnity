// SPDX-License-Identifier: Apache-2.0
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>Explicit one-click pip installation into a dedicated environment.</summary>
    public static class KimodoInstaller
    {
        /// <summary>Quotes a literal PowerShell string.</summary>
        public static string PsQuote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

        /// <summary>Locates the bundled installer independently of the host project.</summary>
        public static string ScriptPath()
        {
            var assemblies = Directory.GetFiles(Application.dataPath, "KimodoForUnity.Editor.asmdef", SearchOption.AllDirectories);
            if (assemblies.Length != 1) throw new IOException("Expected one Kimodo for Unity installation.");
            return Path.Combine(Directory.GetParent(Path.GetDirectoryName(assemblies[0])).FullName, "Scripts", "Install-Kimodo.ps1");
        }

        /// <summary>Starts an explicit installation; opening the Wizard never invokes this method.</summary>
        public static KimodoElevatedInstallation Start(KimodoConfiguration config, bool quantizationOnly = false)
        {
            config.Normalize();
            KimodoPaths.ValidateRoot(config.assetsRoot);
            KimodoPaths.ValidateRoot(config.installRoot);
            if (!File.Exists(quantizationOnly ? config.InstalledPython : config.pythonPath))
                throw new FileNotFoundException("Python executable was not found.");
            if (!quantizationOnly)
            {
                ValidateBuildTool(config.ninjaPath, "ninja.exe");
                ValidateBuildTool(config.gccPath, "gcc.exe");
                ValidateBuildTool(config.gxxPath, "g++.exe");
            }
            var logDirectory = KimodoPaths.Child(config.assetsRoot, "Logs", "Install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(logDirectory);
            var command = "& " + PsQuote(Path.Combine(Path.GetDirectoryName(ScriptPath()), "Run-KimodoElevated.ps1"))
                + " -PythonExecutable " + PsQuote(quantizationOnly ? config.InstalledPython : config.pythonPath)
                + " -WorkspaceDirectory " + PsQuote(config.assetsRoot) + " -InstallDirectory " + PsQuote(config.installRoot)
                + BuildToolArguments(config) + " -LogDirectory " + PsQuote(logDirectory);
            if (quantizationOnly) command += " -QuantizationOnly";
            using (var editor = Process.GetCurrentProcess())
                command += " -ParentProcessId " + editor.Id + " -ParentStartTicks "
                    + editor.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            try { return new KimodoElevatedInstallation(ElevationCommand(command), logDirectory); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new OperationCanceledException(config.chinese ? "已取消管理员授权。" : "Administrator authorization was cancelled.");
            }
        }

        private static void ValidateBuildTool(string path, string name)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || !File.Exists(path)
                || !Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select the full path to " + name + ".");
        }

        private static string BuildToolArguments(KimodoConfiguration config)
            => " -NinjaExecutable " + PsQuote(config.ninjaPath) + " -GccExecutable " + PsQuote(config.gccPath)
                + " -GxxExecutable " + PsQuote(config.gxxPath);

        /// <summary>Constructs a hidden shell process with literal arguments and captured output.</summary>
        public static ProcessStartInfo PowerShellCommand(string command)
            => new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(
                        "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n" + command)),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };

        /// <summary>Requests Windows UAC elevation; output is returned through installation log files.</summary>
        public static ProcessStartInfo ElevationCommand(string command)
        {
            var shell = PowerShellCommand(command);
            return new ProcessStartInfo
            {
                FileName = shell.FileName, Arguments = shell.Arguments,
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
        }

        /// <summary>Provides the equivalent manual pip sequence, without running it.</summary>
        public static string ManualCommands(KimodoConfiguration config)
        {
            var python = PsQuote(config.InstalledPython);
            return "$ErrorActionPreference = 'Stop'\n"
                + "$env:PATH = " + PsQuote(Path.Combine(config.installRoot, "Scripts") + ";") + " + $env:PATH\n"
                + ". " + PsQuote(Path.Combine(Path.GetDirectoryName(ScriptPath()), "Initialize-KimodoBuild.ps1"))
                + BuildToolArguments(config) + "\n"
                + "& " + PsQuote(config.pythonPath) + " -m venv " + PsQuote(config.installRoot) + "\n"
                + "$env:PIP_CACHE_DIR = " + PsQuote(Path.Combine(config.assetsRoot, "Cache", "Pip")) + "\n"
                + "$env:TEMP = " + PsQuote(Path.Combine(config.assetsRoot, "Temp")) + "\n"
                + "$env:TMP = $env:TEMP\n"
                + "New-Item -ItemType Directory -Force -Path $env:TEMP,$env:PIP_CACHE_DIR | Out-Null\n"
                + "& " + python + " -m pip install --upgrade pip setuptools wheel 'pybind11>=3,<4' 'cmake>=3.24,<4'\n"
                + "& " + python + " -m pip install torch==2.10.0+cu128 --index-url https://download.pytorch.org/whl/cu128\n"
                + "$env:CMAKE_PREFIX_PATH = (& " + python + " -m pybind11 --cmakedir)\n"
                + "& " + python + " -m pip install --no-build-isolation 'kimodo @ https://github.com/nv-tlabs/kimodo/archive/e1c219c99b4b94ba48be1fb6a1cbc64ecee4c954.zip'\n"
                + "if ($LASTEXITCODE -ne 0) { throw 'Kimodo installation failed.' }\n"
                + "& " + python + " -m pip install gguf==0.19.0\n"
                + "& " + python + " -m pip install bitsandbytes==0.50.2\n"
                + "Copy-KimodoBuildRuntime -PythonExecutable " + python + " -GxxExecutable " + PsQuote(config.gxxPath) + "\n"
                + "& " + python + " -m pip check\n"
                + "& " + python + " -c 'import kimodo,torch,motion_correction._motion_correction; assert torch.version.cuda'";
        }
    }
}
