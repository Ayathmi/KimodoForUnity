// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace KimodoForUnity
{
    /// <summary>One bounded read-only dependency observation.</summary>
    [Serializable]
    public sealed class KimodoCheckItem
    {
        /// <summary>Stable dependency label.</summary>
        public string name;
        /// <summary>Whether this observation passed.</summary>
        public bool passed;
        /// <summary>Diagnostic details, without credentials.</summary>
        public string detail;
    }

    /// <summary>Results from the import-only Python probe.</summary>
    [Serializable]
    public sealed class KimodoPythonProbe
    {
        /// <summary>Python version string.</summary>
        public string version;
        /// <summary>Whether the interpreter meets the language baseline.</summary>
        public bool pythonSupported;
        /// <summary>CUDA availability reported by PyTorch.</summary>
        public bool cuda;
        /// <summary>Detected device name.</summary>
        public string gpu;
        /// <summary>Module import observations.</summary>
        public KimodoCheckItem[] modules;
    }

    /// <summary>Inspection of existing dependencies, never an installer.</summary>
    public static class KimodoDependencyCheck
    {
        /// <summary>Stable folders expected by the official text encoder wrapper.</summary>
        public static readonly string[] TextRepositories =
        {
            "McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp",
            "McGill-NLP/LLM2Vec-Meta-Llama-3-8B-Instruct-mntp-supervised"
        };

        /// <summary>Local import introspection only; no model construction or network client.</summary>
        public const string ProbeCode =
            "import sys,json,importlib\n" +
            "r={'version':sys.version.split()[0],'pythonSupported':sys.version_info>=(3,10),'cuda':False,'gpu':'','modules':[]}\n" +
            "for name in ['torch','kimodo','transformers','motion_correction._motion_correction','bvhio','glm','gradio','gradio_client','peft']:\n" +
            " try:\n" +
            "  m=importlib.import_module(name); r['modules'].append({'name':name,'passed':True,'detail':str(getattr(m,'__version__','import OK'))})\n" +
            "  if name=='torch':\n" +
            "   r['cuda']=bool(m.cuda.is_available()); r['gpu']=m.cuda.get_device_name(0) if r['cuda'] else ''\n" +
            " except Exception as e: r['modules'].append({'name':name,'passed':False,'detail':type(e).__name__+': '+str(e)})\n" +
            "print('KIMODO_PROBE_JSON:'+json.dumps(r))\n";

        /// <summary>Runs the explicit user-requested probe with a timeout and cancellation.</summary>
        public static async Task<string> ProbeAsync(KimodoConfiguration config, CancellationToken cancellation)
        {
            return await Task.Run(async () =>
            {
                var info = KimodoProcess.StartInfo(config, new[] { "-B", "-c", ProbeCode }, Path.GetDirectoryName(config.RuntimePython));
                using (var child = new KimodoOwnedProcess(info))
                {
                    var started = DateTime.UtcNow;
                    while (!child.Finished)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (DateTime.UtcNow - started > TimeSpan.FromSeconds(90))
                            throw new TimeoutException("Dependency imports exceeded 90 seconds. No installation was attempted.");
                        await Task.Delay(100, cancellation).ConfigureAwait(false);
                    }
                    if (child.ExitCode != 0)
                        throw new InvalidOperationException(child.Errors + "\n" + child.Output);
                    var marker = child.Output.LastIndexOf("KIMODO_PROBE_JSON:", StringComparison.Ordinal);
                    if (marker < 0)
                        throw new InvalidDataException("Interpreter returned no dependency report.\n" + child.Errors);
                    return child.Output.Substring(marker + "KIMODO_PROBE_JSON:".Length).Trim();
                }
            }, cancellation).ConfigureAwait(false);
        }

        /// <summary>Tests the complete basic dependency report; model loading remains a separate test.</summary>
        public static bool CanGenerate(KimodoPythonProbe probe, List<KimodoCheckItem> files)
        {
            if (probe == null || !probe.pythonSupported || !probe.cuda || probe.modules == null || probe.modules.Length != 9
                || files == null || files.Count != KimodoModelFiles.Repositories.Length)
                return false;
            foreach (var module in probe.modules)
                if (!module.passed) return false;
            foreach (var file in files)
                if (!file.passed) return false;
            return true;
        }

    }
}
