using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SteamDirect
{
    internal static class ProxyAppShutdown
    {
        internal const string YouTubeAppId = "Chrome._crx_agimnkijcamfeangaknmldooml";
        private const string OpenAiPublisher = "_2p2nqsd0c76g0";
        private const uint CloseMessage = 0x0010;

        private sealed class AppWindow
        {
            internal IntPtr Handle;
            internal uint ProcessId;
            internal string Name;
        }

        private delegate bool WindowCallback(IntPtr window, IntPtr state);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(WindowCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out PropertyStore store);
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);

        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { internal Guid Format; internal uint Id; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue
        {
            [FieldOffset(0)] internal ushort Type;
            [FieldOffset(8)] internal IntPtr String;
        }
        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface PropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
        }

        internal static string Identify(string path, string appId, string localApps, string programFiles)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string executable = Path.GetFileName(path);
            if (string.Equals(executable, "Antigravity.exe", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(path, Path.Combine(localApps, "Programs", "antigravity", executable), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(path, Path.Combine(programFiles, "Antigravity", executable), StringComparison.OrdinalIgnoreCase))) return "Antigravity";

            if (string.Equals(executable, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            {
                string windowsApps = Path.Combine(programFiles, "WindowsApps") + Path.DirectorySeparatorChar;
                if (path.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase))
                {
                    string package = path.Substring(windowsApps.Length).Split(Path.DirectorySeparatorChar)[0];
                    if ((package.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) || package.StartsWith("OpenAI.ChatGPT-Desktop_", StringComparison.OrdinalIgnoreCase)) &&
                        package.EndsWith(OpenAiPublisher, StringComparison.OrdinalIgnoreCase)) return "ChatGPT";
                }
            }

            if (string.Equals(executable, "chrome.exe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appId, YouTubeAppId, StringComparison.OrdinalIgnoreCase)) return "YouTube";
            return null;
        }

        private static string ReadAppId(IntPtr window)
        {
            Guid interfaceId = typeof(PropertyStore).GUID;
            PropertyStore store;
            if (SHGetPropertyStoreForWindow(window, ref interfaceId, out store) != 0 || store == null) return null;
            try
            {
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                PropertyValue value;
                int result = store.GetValue(ref key, out value);
                try { return result == 0 && value.Type == 31 ? Marshal.PtrToStringUni(value.String) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        private static List<AppWindow> FindWindows()
        {
            var windows = new List<AppWindow>();
            string localApps = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var paths = new Dictionary<uint, string>();
            if (!EnumWindows((window, state) =>
            {

                uint pid;
                if (GetWindowThreadProcessId(window, out pid) == 0) return true;
                string path;
                if (!paths.TryGetValue(pid, out path))
                {
                    path = null;
                    try
                    {
                        using (var process = Process.GetProcessById((int)pid))
                        {
                            string name = process.ProcessName;
                            if (string.Equals(name, "ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Antigravity", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "chrome", StringComparison.OrdinalIgnoreCase)) path = process.MainModule.FileName;
                        }
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                    paths[pid] = path;
                }
                string appId = null;
                if (string.Equals(Path.GetFileName(path), "chrome.exe", StringComparison.OrdinalIgnoreCase))
                {
                    try { appId = ReadAppId(window); }
                    catch (COMException) { }
                }
                string app = Identify(path, appId, localApps, programFiles);
                if (app != null) windows.Add(new AppWindow { Handle = window, ProcessId = pid, Name = app });
                return true;
            }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举需要关闭的应用窗口。");
            return windows;
        }

        internal sealed class AppProcess
        {
            internal int Id;
            internal string Name, Path;
            internal long Started;
        }

        private static List<AppProcess> FindProcesses()
        {
            var result = new List<AppProcess>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string executable in new[] { "ChatGPT", "Antigravity" })
                foreach (var process in Process.GetProcessesByName(executable))
                    using (process)
                    {
                        try
                        {
                            string path = process.MainModule.FileName;
                            string app = Identify(path, null, local, programs);
                            if (app != null) result.Add(new AppProcess { Id = process.Id, Name = app, Path = path, Started = process.StartTime.ToUniversalTime().Ticks });
                            else throw new InvalidOperationException("发现身份未能确认的 " + executable + " 进程，已停止直连流程。");
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { if (!process.HasExited) throw; }
                        catch (Win32Exception ex) { throw new InvalidOperationException("无法检查 " + executable + " 后台进程，已停止直连流程。", ex); }
                    }
            return result;
        }

        private static void EndChatGpt(AppProcess target)
        {
            try
            {
                using (var process = Process.GetProcessById(target.Id))
                {
                    if (process.StartTime.ToUniversalTime().Ticks != target.Started ||
                        !string.Equals(process.MainModule.FileName, target.Path, StringComparison.OrdinalIgnoreCase)) return;
                    process.Kill();
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        internal static void FinishProcesses(Func<List<AppProcess>> find, Action<AppProcess> endChatGpt,
            Action<int> wait, Func<long> elapsed, Action<string> log)
        {
            var ended = new HashSet<string>();
            while (true)
            {
                var remaining = find();
                if (remaining.Count == 0) { log("已确认指定独立应用没有后台进程残留。"); return; }
                if (elapsed() >= 8000)
                {
                    var names = new HashSet<string>();
                    foreach (var process in remaining) names.Add(process.Name);
                    throw new InvalidOperationException("仍在运行：" + string.Join("、", names) + "。已停止流程，不会启动 Steam。请处理保存/退出提示或查看日志后重试。");
                }
                if (elapsed() >= 2000)
                    foreach (var process in remaining)
                        if (process.Name == "ChatGPT" && ended.Add(process.Id + ":" + process.Started))
                        {
                            log("正在完整退出 ChatGPT 后台进程（PID " + process.Id + "）。");
                            endChatGpt(process);
                        }
                wait(250);
            }
        }

        internal static void CheckStopped()
        {
            if (FindProcesses().Count != 0 || FindWindows().Count != 0)
                throw new InvalidOperationException("指定应用又在运行，已阻止启动 Steam。请重新执行直连流程。");
        }

        internal static void Run(Action<string> log)
        {
            var targets = FindWindows();

            foreach (var target in targets)
            {
                if (target.Name == "YouTube")
                {
                    try { if (!string.Equals(ReadAppId(target.Handle), YouTubeAppId, StringComparison.Ordinal)) continue; }
                    catch (COMException) { continue; }
                }
                uint pid;
                if (GetWindowThreadProcessId(target.Handle, out pid) == 0 || pid != target.ProcessId) continue;
                if (PostMessage(target.Handle, CloseMessage, IntPtr.Zero, IntPtr.Zero)) log("已请求正常关闭 " + target.Name + " 窗口。");
                else log("关闭请求未送达 " + target.Name + "，将复查窗口状态。");
            }
            var processWatch = Stopwatch.StartNew();
            FinishProcesses(FindProcesses, EndChatGpt, Thread.Sleep, () => processWatch.ElapsedMilliseconds, log);
            var watch = Stopwatch.StartNew();
            while (true)
            {
                var remaining = FindWindows();
                if (remaining.Count == 0) { log("指定应用窗口已关闭。"); return; }
                if (watch.ElapsedMilliseconds >= 5000)
                {
                    var names = new HashSet<string>();
                    foreach (var window in remaining) names.Add(window.Name);
                    throw new InvalidOperationException("仍有应用窗口未关闭：" + string.Join("、", names) + "。请处理应用的保存或退出提示后重试。");
                }
                Thread.Sleep(250);
            }
        }
    }
}

