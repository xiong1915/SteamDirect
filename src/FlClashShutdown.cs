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
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable)) return false;
            if (processName != "FlClash" && processName != "FlClashCore") return false;
            try
            {
                return string.Equals(Path.GetFileName(executable), processName + ".exe", StringComparison.OrdinalIgnoreCase) &&
                    folders.Contains(Path.GetDirectoryName(Path.GetFullPath(executable)));
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        private sealed class Target
        {
            internal int Id;
            internal long Started;
            internal string Path, Name;
        }

        private static List<Target> Snapshot(HashSet<string> folders)
        {
            var result = new List<Target>();
            foreach (string name in new[] { "FlClash", "FlClashCore" })
                foreach (var process in Process.GetProcessesByName(name))
                    using (process)
                    {
                        try
                        {
                            string path = process.MainModule.FileName;
                            if (!IsAllowed(name, path, folders) || !TempCleaner.NoReparseAncestors(path))
                                throw new InvalidOperationException("无法确认 " + name + " 的可信安装位置。请手动退出自定义目录中的 FlClash 和内核后重试；未关闭未知程序。");
                            result.Add(new Target { Id = process.Id, Started = process.StartTime.ToUniversalTime().Ticks, Path = path, Name = name });
                        }
                        catch (ArgumentException) { }
                        catch (Win32Exception ex) { throw new InvalidOperationException("无法确认 FlClash 进程身份，请手动退出后重试。", ex); }
                    }
            return result;
        }

        internal static void Run(Action<string> log)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlClash"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FlClash")
            };
            // Never infer trust from the running executable itself or a same-named neighboring file.
            var targets = Snapshot(folders);
            foreach (var target in targets)
            {
                if (target.Name != "FlClash") continue;
                try
                {
                    using (var process = Process.GetProcessById(target.Id))
                    {
                        if (!ProxyAppShutdown.SameIdentity(target.Id, target.Started, target.Path, process.Id,
                            process.StartTime.ToUniversalTime().Ticks, process.MainModule.FileName))
                            throw new InvalidOperationException("FlClash 进程身份已变化，已停止关闭请求。");
                        if (process.CloseMainWindow()) log("已请求 FlClash 正常关闭；不会强制结束程序或内核。");
                    }
                }
                catch (ArgumentException) { }
                catch (Win32Exception ex) { throw new InvalidOperationException("无法请求 FlClash 正常退出，请手动退出后重试。", ex); }
            }
            var watch = Stopwatch.StartNew();
            while (Snapshot(folders).Count != 0)
            {
                if (watch.ElapsedMilliseconds >= 5000)
                    throw new InvalidOperationException("FlClash 或内核仍在运行。关闭窗口可能只是最小化到托盘，请通过 FlClash 菜单选择退出后重试；未强制终止进程。");
                Thread.Sleep(100);
            }
            log("已确认 FlClash 主程序和内核均已退出；配置与订阅保留。");
        }
    }
}
