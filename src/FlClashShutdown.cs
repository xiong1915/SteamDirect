using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SteamDirect
{
    internal static class FlClashShutdown
    {
        internal static bool IsAllowed(string processName, string executable, ISet<string> folders)
        {
            if (string.IsNullOrWhiteSpace(executable)) return false;
            if (processName != "FlClash" && processName != "FlClashCore") return false;
            try
            {
                return string.Equals(Path.GetFileName(executable), processName + ".exe", StringComparison.OrdinalIgnoreCase) && folders.Contains(Path.GetDirectoryName(Path.GetFullPath(executable)));
            }
            catch (ArgumentException) { return false; }
        }
        private static void Wait(string name, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                Process[] found = Process.GetProcessesByName(name);
                bool present = found.Length > 0;
                foreach (var process in found) process.Dispose();
                if (!present) return;
                Thread.Sleep(100);
            }
        }
        private static void StopVerified(string name, HashSet<string> folders, Action<string> log)
        {
            Process[] processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        string path = process.MainModule.FileName;
                        if (!IsAllowed(name, path, folders)) throw new InvalidOperationException("无法核对 " + name + " 的安装路径，已停止；未结束未知程序。");
                        process.Kill();
                        if (!process.WaitForExit(3000)) throw new InvalidOperationException(name + " 自动退出超时。");
                        log("已自动结束 " + name + "（PID " + process.Id + "）。");
                    }
                    catch (InvalidOperationException) { if (!process.HasExited) throw; }
                    catch (Win32Exception ex) { throw new InvalidOperationException("无法自动退出 " + name + "：" + ex.Message + "。系统权限可能不足，已停止流程。", ex); }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        internal static void Run(Action<string> log)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] apps = Process.GetProcessesByName("FlClash");
            try
            {
                foreach (var app in apps)
                {
                    try
                    {
                        string path = app.MainModule.FileName;
                        if (!string.Equals(Path.GetFileName(path), "FlClash.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("FlClash 安装路径无法确认。");
                        folders.Add(Path.GetDirectoryName(Path.GetFullPath(path)));
                        app.CloseMainWindow();
                    }
                    catch (InvalidOperationException) { if (!app.HasExited) throw; }
                }
            }
            finally { foreach (var app in apps) app.Dispose(); }
            // Also recover an orphaned core whose executable is next to the installed FlClash GUI.
            Process[] cores = Process.GetProcessesByName("FlClashCore");
            try
            {
                foreach (var core in cores)
                {
                    try
                    {
                        string path = core.MainModule.FileName;
                        string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                        if (File.Exists(Path.Combine(directory, "FlClash.exe"))) folders.Add(directory);
                    }
                    catch (InvalidOperationException) { if (!core.HasExited) throw; }
                }
            }
            finally { foreach (var core in cores) core.Dispose(); }
            Wait("FlClash", 2000);
            StopVerified("FlClash", folders, log);
            Wait("FlClashCore", 500);
            StopVerified("FlClashCore", folders, log);
            log("FlClash 主程序和对应内核自动退出完成；配置与订阅保留。");
        }
    }
}
