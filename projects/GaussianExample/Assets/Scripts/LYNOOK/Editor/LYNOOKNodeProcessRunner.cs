#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Lynook.DualScreen.Editor
{
    public static class LYNOOKNodeProcessRunner
    {
        internal static string ResolveSceneQueueScript()
        {
            // GaussianExample 工程位于 <repo>/projects/GaussianExample，Tools 在 <repo>/Tools。
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return null;
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "Tools", "scene-queue-json.mjs")),
                Path.GetFullPath(Path.Combine(projectRoot, "Tools", "scene-queue-json.mjs")),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        internal static string LocateNode()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate = Path.Combine(dir, "node");
                if (File.Exists(candidate)) return candidate;
            }
            // macOS 常见位置兜底
            foreach (var fallback in new[] { "/usr/local/bin/node", "/opt/homebrew/bin/node", "/usr/bin/node" })
                if (File.Exists(fallback)) return fallback;
            throw new InvalidOperationException("未找到 node 可执行文件，请确认 PATH 或安装 Node.js。");
        }

        // ---- 批量录制辅助 ----

        /// <summary>运行一个 Tools 下的 node 脚本，解析 stdout 的 JSON。</summary>
        internal static T RunNodeScript<T>(string scriptName, string arguments) where T : class
        {
            string scriptPath = LYNOOKNodeProcessRunner.ResolveToolScript(scriptName);
            if (string.IsNullOrEmpty(scriptPath))
                throw new InvalidOperationException("找不到脚本 Tools/" + scriptName);
            var start = new ProcessStartInfo
            {
                FileName = LYNOOKNodeProcessRunner.LocateNode(),
                Arguments = $"\"{scriptPath}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("无法启动 node 进程。");
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"脚本 {scriptName} 失败（exit {process.ExitCode}）：{stderr.Trim()}");
                int brace = stdout.IndexOf('{');
                if (brace < 0) throw new InvalidOperationException($"{scriptName} 输出不是 JSON：{stdout.Trim()}");
                string json = stdout.Substring(brace);
                var result = JsonUtility.FromJson<T>(json);
                if (result == null) throw new InvalidOperationException($"{scriptName} JSON 解析失败。");
                return result;
            }
        }

        /// <summary>
        /// 异步运行 node 脚本，不阻塞 Unity 主线程。完成后通过 callback 在主线程回调。
        /// callback 参数：(result, error) —— 成功时 error 为 null，失败时 result 为 null。
        /// </summary>
        internal static void RunNodeScriptAsync<T>(string scriptName, string arguments, Action<T, string> callback) where T : class
        {
            string scriptPath = LYNOOKNodeProcessRunner.ResolveToolScript(scriptName);
            if (string.IsNullOrEmpty(scriptPath))
            {
                callback(null, "找不到脚本 Tools/" + scriptName);
                return;
            }
            var start = new ProcessStartInfo
            {
                FileName = LYNOOKNodeProcessRunner.LocateNode(),
                Arguments = $"\"{scriptPath}\" {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var stdoutBuf = new System.Text.StringBuilder();
            var stderrBuf = new System.Text.StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) stdoutBuf.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderrBuf.AppendLine(e.Data); };
            process.Exited += (sender, e) =>
            {
                try
                {
                    string stdout = stdoutBuf.ToString();
                    string stderr = stderrBuf.ToString();
                    if (process.ExitCode != 0)
                    {
                        string err = $"脚本 {scriptName} 失败（exit {process.ExitCode}）：{stderr.Trim()}";
                        EditorApplication.delayCall += () => callback(null, err);
                        return;
                    }
                    int brace = stdout.IndexOf('{');
                    if (brace < 0)
                    {
                        EditorApplication.delayCall += () => callback(null, $"{scriptName} 输出不是 JSON：{stdout.Trim()}");
                        return;
                    }
                    string json = stdout.Substring(brace);
                    var result = JsonUtility.FromJson<T>(json);
                    if (result == null)
                    {
                        EditorApplication.delayCall += () => callback(null, $"{scriptName} JSON 解析失败。");
                        return;
                    }
                    EditorApplication.delayCall += () => callback(result, null);
                }
                finally { process.Dispose(); }
            };
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                callback(null, "启动 node 进程失败：" + ex.Message);
            }
        }

        internal static string ResolveToolScript(string name)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return null;
            string[] candidates =
            {
                Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "Tools", name)),
                Path.GetFullPath(Path.Combine(projectRoot, "Tools", name)),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

    }
}
#endif
