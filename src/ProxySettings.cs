using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SteamDirect
{
    internal static class ProxySettings
    {
        // Proxy environment variables commonly honored by Steam/embedded libraries.
        internal static readonly string[] ProxyVariableNames = {
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "FTP_PROXY", "WS_PROXY", "WSS_PROXY"
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct ConnOption { public int Option; public IntPtr Value; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ConnOptionList
        {
            public int Size;
            public IntPtr Connection;
            public int OptionCount;
            public int OptionError;
            public IntPtr Options;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct WinHttpProxyInfo
        {
            public int AccessType;
            public IntPtr Proxy;
            public IntPtr ProxyBypass;
        }
        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
        private static extern bool SetConnOption(IntPtr handle, int option, ref ConnOptionList data, int length);
        [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
        private static extern bool QueryConnOption(IntPtr handle, int option, ref ConnOptionList data, ref int length);
        [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
        private static extern bool NotifyOption(IntPtr handle, int option, IntPtr data, int length);
        [DllImport("winhttp.dll", SetLastError = true)]
        private static extern bool WinHttpGetDefaultProxyConfiguration(out WinHttpProxyInfo info);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr memory);

        internal static void SetWinInetDirect()
        {
            // WinINet supported API; don't edit undocumented DefaultConnectionSettings bytes.
            ApplyConnFlags(1); // PROXY_TYPE_DIRECT; clears manual/PAC/WPAD active flags.
            RefreshConnection();
        }

        private static void RefreshConnection()
        {
            if (!NotifyOption(IntPtr.Zero, 39, IntPtr.Zero, 0) || !NotifyOption(IntPtr.Zero, 37, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 未能刷新系统代理设置。");
            Thread.Sleep(350);
        }

        private static void ApplyConnFlags(int flags)
        {
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr optionPointer = Marshal.AllocHGlobal(itemSize);
            try
            {
                Marshal.StructureToPtr(new ConnOption { Option = 1, Value = new IntPtr(flags) }, optionPointer, false);
                ConnOptionList options = new ConnOptionList {
                    Size = Marshal.SizeOf(typeof(ConnOptionList)), Connection = IntPtr.Zero,
                    OptionCount = 1, OptionError = 0, Options = optionPointer
                };
                if (!SetConnOption(IntPtr.Zero, 75, ref options, options.Size))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法修改当前用户 Windows 系统代理（可能被组织策略管理）。");
            }
            finally { Marshal.FreeHGlobal(optionPointer); }
        }

        internal static ProxySnapshot CaptureConnection()
        {
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr memory = Marshal.AllocHGlobal(itemSize * 4);
            int[] options = { 10, 2, 3, 4 };
            for (int i = 0; i < 4; i++) Marshal.StructureToPtr(new ConnOption { Option = options[i], Value = IntPtr.Zero }, IntPtr.Add(memory, i * itemSize), false);
            try
            {
                var list = new ConnOptionList { Size = Marshal.SizeOf(typeof(ConnOptionList)), OptionCount = 4, Options = memory };
                int length = list.Size;
                if (!QueryConnOption(IntPtr.Zero, 75, ref list, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法完整备份系统代理；未执行切换。");
                var result = new ProxySnapshot();
                for (int i = 0; i < 4; i++)
                {
                    var option = (ConnOption)Marshal.PtrToStructure(IntPtr.Add(memory, i * itemSize), typeof(ConnOption));
                    if (i == 0) { result.Flags = (int)option.Value.ToInt64(); continue; }
                    string value = option.Value == IntPtr.Zero ? "" : Marshal.PtrToStringUni(option.Value);
                    if (i == 1) result.Server = value;
                    if (i == 2) result.Bypass = value;
                    if (i == 3) result.AutoConfigUrl = value;
                }
                return result;
            }
            finally
            {
                for (int i = 1; i < 4; i++)
                {
                    var option = (ConnOption)Marshal.PtrToStructure(IntPtr.Add(memory, i * itemSize), typeof(ConnOption));
                    if (option.Value != IntPtr.Zero) GlobalFree(option.Value);
                }
                Marshal.FreeHGlobal(memory);
            }
        }

        internal static void RestoreConnection(ProxySnapshot snapshot)
        {
            snapshot.Validate();
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr memory = Marshal.AllocHGlobal(itemSize * 4);
            IntPtr[] values = new IntPtr[3];
            try
            {
                string[] strings = { snapshot.Server, snapshot.Bypass, snapshot.AutoConfigUrl };
                Marshal.StructureToPtr(new ConnOption { Option = 1, Value = new IntPtr(snapshot.Flags) }, memory, false);
                for (int i = 0; i < 3; i++)
                {
                    values[i] = Marshal.StringToHGlobalUni(strings[i]);
                    Marshal.StructureToPtr(new ConnOption { Option = i + 2, Value = values[i] }, IntPtr.Add(memory, (i + 1) * itemSize), false);
                }
                var list = new ConnOptionList { Size = Marshal.SizeOf(typeof(ConnOptionList)), OptionCount = 4, Options = memory };
                if (!SetConnOption(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复系统代理，备份已保留。");
                RefreshConnection();
            }
            finally
            {
                foreach (IntPtr value in values) if (value != IntPtr.Zero) Marshal.FreeHGlobal(value);
                Marshal.FreeHGlobal(memory);
            }
        }

        private static int ReadConnFlags()
        {
            int itemSize = Marshal.SizeOf(typeof(ConnOption));
            IntPtr optionPointer = Marshal.AllocHGlobal(itemSize);
            try
            {
                Marshal.StructureToPtr(new ConnOption { Option = 10, Value = IntPtr.Zero }, optionPointer, false);
                ConnOptionList options = new ConnOptionList {
                    Size = Marshal.SizeOf(typeof(ConnOptionList)), Connection = IntPtr.Zero,
                    OptionCount = 1, OptionError = 0, Options = optionPointer
                };
                int length = options.Size;
                if (!QueryConnOption(IntPtr.Zero, 75, ref options, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取系统代理有效状态。");
                ConnOption result = (ConnOption)Marshal.PtrToStructure(optionPointer, typeof(ConnOption));
                return unchecked((int)result.Value.ToInt64());
            }
            finally { Marshal.FreeHGlobal(optionPointer); }
        }

        internal static string ObserveHistoryMode(bool tunnel, bool route, bool knownProxy)
        {
            if (tunnel) return "代理";
            try
            {
                int flags = ReadConnFlags();
                if ((flags & 6) != 0) return "代理";
                foreach (string variable in ProxyVariableNames)
                {
                    foreach (var level in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable, level))) return "代理";
                }
                WinHttpProxyInfo info;
                if (!WinHttpGetDefaultProxyConfiguration(out info)) return "未知";
                try
                {
                    if (info.Proxy != IntPtr.Zero && !string.IsNullOrWhiteSpace(Marshal.PtrToStringUni(info.Proxy))) return "代理";
                    return route && flags == 1 && !knownProxy && info.AccessType == 1 ? "直连" : "未知";
                }
                finally
                {
                    if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
                    if (info.ProxyBypass != IntPtr.Zero) GlobalFree(info.ProxyBypass);
                }
            }
            catch (Exception) { return "未知"; }
        }

        private static void CheckWinHttp()
        {
            WinHttpProxyInfo info;
            if (!WinHttpGetDefaultProxyConfiguration(out info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 WinHTTP 代理配置。");
            try
            {
                if (info.AccessType != 1 || info.Proxy != IntPtr.Zero)
                    throw new InvalidOperationException("检测到 WinHTTP 系统级代理仍生效。未修改系统级配置；请使用管理员终端核实 netsh winhttp show proxy 后手动处理。");
            }
            finally
            {
                if (info.Proxy != IntPtr.Zero) GlobalFree(info.Proxy);
                if (info.ProxyBypass != IntPtr.Zero) GlobalFree(info.ProxyBypass);
            }
            // Newer Windows versions also have advanced WinHTTP proxy settings.
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), "winhttp show advproxy") {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string result = ReadProcessOutput(p, 3500);
                    if (p.ExitCode != 0) throw new InvalidOperationException("高级代理查询未成功，无法确认直连；已停止检查。");
                    ValidateAdvancedProxy(result);
                }
            }
            catch (Win32Exception ex) { throw new InvalidOperationException("无法运行高级代理检查，不能确认直连状态。", ex); }
        }

        internal static void ValidateAdvancedProxy(string result)
        {
            Match proxy = Regex.Match(result, "\"Proxy\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            Match pac = Regex.Match(result, "\"AutoconfigUrl\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            Match proxyEnabled = Regex.Match(result, "\"ProxyIsEnabled\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            Match pacEnabled = Regex.Match(result, "\"AutoConfigIsEnabled\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            Match auto = Regex.Match(result, "\"AutoDetect\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            bool activeProxy = proxyEnabled.Success ? string.Equals(proxyEnabled.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) :
                proxy.Success && !string.IsNullOrWhiteSpace(proxy.Groups[1].Value);
            bool activePac = pacEnabled.Success ? string.Equals(pacEnabled.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) :
                pac.Success && !string.IsNullOrWhiteSpace(pac.Groups[1].Value);
            bool activeAuto = auto.Success && string.Equals(auto.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            if (activeProxy || activePac || activeAuto)
                throw new InvalidOperationException("WinHTTP 高级代理仍启用（代理/PAC/自动检测）。请检查其他代理程序或系统策略。");
            bool complete = (proxyEnabled.Success && pacEnabled.Success && auto.Success) || (proxy.Success && pac.Success && auto.Success);
            if (!complete &&
                !Regex.IsMatch(result, "Direct access|直接访问|直连|直接存取", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("无法识别 WinHTTP 高级代理状态，请先核实配置。");
        }

        internal static string ReadProcessOutput(Process process, int timeoutMs)
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            var watch = Stopwatch.StartNew();
            if (!process.WaitForExit(timeoutMs) ||
                !Task.WaitAll(new Task[] { output, error }, Math.Max(0, timeoutMs - (int)watch.ElapsedMilliseconds)))
            {
                try { process.Kill(); } catch { }
                throw new System.TimeoutException("WinHTTP 高级代理查询超时，已停止查询。");
            }
            return output.Result + error.Result;
        }

        internal static void CheckPrerequisites()
        {
            // Proxy absence is verified after FlClash exits and user settings are cleared.
            foreach (string name in ProxyVariableNames)
            {
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)))
                    throw new InvalidOperationException("机器级环境变量 " + name + " 设置了代理；未修改网络，请先核实。");
                string user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(user) && !IsFlClashLocalProxy(user))
                    throw new InvalidOperationException("用户环境变量 " + name + " 指向其他代理；未修改网络，请先核实。");
            }
        }
        private static bool IsFlClashLocalProxy(string value)
        {
            // Never remove remote/corporate proxy values silently.
            return Regex.IsMatch(value.Trim(), @"^(?:(?:http|https|socks|socks5)://)?(?:127\.0\.0\.1|localhost|\[::1\]):\d+/?$", RegexOptions.IgnoreCase);
        }

        internal static void CleanLocalEnvironmentProxy(Action<string> log)
        {
            foreach (string name in ProxyVariableNames)
            {
                string machine = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
                if (!string.IsNullOrWhiteSpace(machine))
                    throw new InvalidOperationException("机器级环境变量 " + name + " 设置了代理。未覆盖其他配置，请先人工核实清除。");
                string user = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
                if (!string.IsNullOrWhiteSpace(user))
                {
                    if (!IsFlClashLocalProxy(user))
                        throw new InvalidOperationException("用户环境变量 " + name + " 仍指向其他代理。请检查该变量的配置。");
                    Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
                    log("已清除用户级本地代理环境变量：" + name);
                }
                // This process may itself have inherited stale environment variables.
                Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
            }
        }

        private static void CheckEnvironment()
        {
            foreach (string name in ProxyVariableNames)
            {
                foreach (EnvironmentVariableTarget level in new[] {
                    EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                {
                    string current = Environment.GetEnvironmentVariable(name, level);
                    if (!string.IsNullOrWhiteSpace(current))
                        throw new InvalidOperationException("检测到 " + level + " 环境变量 " + name + " 仍指向代理，已停止。");
                }
            }
        }

        private static void CheckTunnels()
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                string text = adapter.Name + " " + adapter.Description;
                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                    Regex.IsMatch(text, @"(?i)Wintun|WireGuard|Tailscale|OpenVPN|Clash|Mihomo|ZeroTier|SoftEther|TAP-Windows|NordLynx"))
                    throw new InvalidOperationException("发现活跃 VPN/TUN 网卡：" + adapter.Name + "。未自动禁用网卡，请先关闭对应 VPN/TUN，再重试。");
            }
        }

        internal static string DiagnoseProxyState()
        {
            var issues = new List<string>();
            Action[] checks = {
                () => { int flags = ReadConnFlags(); if (flags != 1) throw new InvalidOperationException("Windows 系统代理/PAC/自动检测仍启用（标志 " + flags + "）"); },
                CheckWinHttp, CheckEnvironment, CheckTunnels, CheckProxyProcesses, CheckProxyPorts
            };
            foreach (var check in checks)
            {
                try { check(); }
                catch (Exception ex) { issues.Add(ex.Message); }
            }
            return issues.Count == 0 ? null : string.Join("；", issues);
        }

        internal static void CheckDirect()
        {
            int flags = ReadConnFlags();
            if (flags != 1)
                throw new InvalidOperationException("Windows WinINet 当前未处于纯直连模式（标志：" + flags + "）。");
            CheckWinHttp();
            CheckEnvironment();
            CheckTunnels();
            CheckProxyProcesses();
            CheckProxyPorts();
        }

        private static readonly HashSet<string> ProxyComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FlClash", "FlClashCore", "clash", "clashmeta", "clashpremium", "clashwin64", "ClashforWindows",
            "clashverge", "clashvergeservice", "vergemihomo", "vergemihomoalpha", "mihomo", "mihomoparty", "mihomoservice",
            "singbox", "v2rayN", "v2ray", "xray", "Shadowsocks", "ShadowsocksR", "sslocal", "ssrlocal",
            "trojan", "hysteria", "hysteria2", "naive", "naiveproxy", "nekoray", "nekobox",
            "Proxifier", "ProxyCap", "tun2socks", "OpenVPN", "openvpnserv", "openvpnserv2", "OpenVPNService",
            "WireGuard", "Tailscale", "tailscaled", "ZeroTierOne", "vpnclient", "vpnclientx64"
        };

        internal static bool IsProxyComponent(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return ProxyComponents.Contains(name.Replace("-", "").Replace("_", "").Replace(" ", ""));
        }

        internal static void CheckProxyProcesses()
        {
            var found = new List<string>();
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (IsProxyComponent(name)) found.Add(name + " (PID " + process.Id + ")");
                    }
                    catch (InvalidOperationException) { } // Process exited during the snapshot.
                }
            }
            finally { foreach (Process process in processes) process.Dispose(); }
            if (found.Count > 0)
                throw new InvalidOperationException("仍有代理/VPN 程序或内核运行：" + string.Join("、", found) + "。请正常退出后重试。");
            CheckProxyServices();
        }

        private static void CheckProxyServices()
        {
            var found = new List<string>();
            ServiceController[] services = ServiceController.GetServices();
            try
            {
                foreach (ServiceController service in services)
                {
                    string name = service.ServiceName;
                    if (!IsProxyComponent(name) && !IsProxyComponent(service.DisplayName) &&
                        !name.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase)) continue;
                    if (service.Status != ServiceControllerStatus.Stopped)
                        found.Add(service.DisplayName + " [" + name + "]");
                }
            }
            finally { foreach (ServiceController service in services) service.Dispose(); }
            if (found.Count > 0)
                throw new InvalidOperationException("代理/VPN 后台服务尚未停止：" + string.Join("、", found) + "。请通过对应软件退出服务模式。");
        }

        private static void CheckProxyPorts()
        {
            // A still-listening Clash core can remain after the Flutter GUI exits.
            // FlClash standard HTTP/mixed ports; do not kill whichever program owns the port.
            foreach (System.Net.IPEndPoint ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (ep.Port == 7890 || ep.Port == 7891)
                    throw new InvalidOperationException("本地端口 " + ep.Port + " 仍有程序监听，可能是代理程序。请检查 FlClash/其他代理程序后重试。");
            }
        }
    }
}

namespace SteamDirect
{

}
