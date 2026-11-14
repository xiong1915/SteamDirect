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
                @"D:\FlClash",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FlClash"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "FlClash")
            };
            // Never infer trust from the running executable itself or a same-named neighboring file.
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var targets = Snapshot(folders);
                if (targets.Count == 0) break;
                if (watch.ElapsedMilliseconds >= 5000) throw new InvalidOperationException("FlClash 或内核未结束，已停止后续步骤。");
                foreach (var target in targets)
                {
                    ProxyAppShutdown.KillVerified(new ProxyAppShutdown.AppProcess { Id = target.Id, Started = target.Started, Path = target.Path, Name = target.Name },
                        path => IsAllowed(target.Name, path, folders));
                    log("已结束 " + target.Name + "（PID " + target.Id + "）。");
                }
                Thread.Sleep(100);
            }
            log("已确认 FlClash 主程序和内核均已退出；配置与订阅保留。");
        }
    }
}
