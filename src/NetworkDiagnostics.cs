using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SteamDirect
{
    internal sealed class NetworkEvidence
    {
        internal bool HasGateway, GatewayReplied, GatewayLoss, SteamRunning, GameRunning, SocketScanOk, ProcessScanOk;
        internal int DnsFailures, DirectOk, DirectFailures, SystemOk, SystemTests, RecoveredHosts, SteamTcp, SteamLoopbackTcp, GameUdp, GameTcp;
        internal string ProxyIssue;
        internal bool ServerResponded;
        internal string Conclusion()
        {
            var lines = new List<string>();
            if (GatewayLoss) lines.Add("本地网络有 ICMP 未回复样本：先检查网线、Wi-Fi 信号和路由器负载；短时样本还需复测。");
            else if (!HasGateway) lines.Add("未发现常规网关：检查网络连接；VPN 或特殊路由也可能没有常规网关。");
            else if (!GatewayReplied) lines.Add("网关未回应 Ping：可能禁用了 ICMP，不能单独判定局域网故障。");
            if (!string.IsNullOrEmpty(ProxyIssue)) lines.Add("发现代理/VPN 配置或程序：可能影响路径，但尚不能证明它导致游戏卡顿。");
            if (DnsFailures > 0) lines.Add("部分 Steam 域名解析失败：优先排查 DNS 或上游网络，再重试。");
            if (RecoveredHosts > 0) lines.Add("有 " + RecoveredHosts + " 项 Steam 服务在系统配置路径下成功、直连失败：更应关注这些域名的直连路径/访问限制，建议复测。不能据此认定游戏卡顿由代理造成。");
            else if (DirectFailures > 0) lines.Add("部分 Steam HTTPS 检测失败：查看下方 DNS、TLS 或超时证据；仅凭超时无法区分运营商、路由、防火墙与服务端问题。");
            if (ProcessScanOk && !SteamRunning) lines.Add("Steam 未运行：打开客户端后复测登录连接。");
            else if (SocketScanOk && SteamTcp == 0) lines.Add("未观察到 Steam 的外部 TCP 已建立连接：这是瞬时快照，不能据此断言离线；Steam 也使用 UDP。");
            if (ProcessScanOk && !GameRunning) lines.Add("CS2 未运行：尚未检测实际游戏进程，请进入游戏/对局后复测。");
            else if (GameRunning && !ServerResponded) lines.Add("尚未验证实际对局 UDP 链路：本地 UDP 端口不代表对局已连通；请在 CS2 遥测中核对延迟、抖动和数据包异常。");
            if (ServerResponded) lines.Add("指定服务器返回 UDP 查询响应：该查询链路可达，但不代表官方匹配/中继链路或持续对局质量正常。");
            if (lines.Count == 0) lines.Add("本次已测项目未发现明确异常；短时检测不能排除间歇性抖动和对局链路问题。");
            return string.Join(Environment.NewLine + Environment.NewLine, lines);
        }
    }

    internal sealed class DiagnosticUpdate
    {
        internal int Index;
        internal int CompletedUnits = -1;
        internal string Title, State, Detail;
        internal DiagnosticUpdate(int index, string title, string state, string detail) { Index = index; Title = title; State = state; Detail = detail; }
    }

    internal sealed class ProcessConnections
    {
        internal bool SteamRunning, GameRunning;
        internal bool Complete = true;
        internal string Error;
        internal int SteamTcp, SteamLoopbackTcp, GameTcp, GameUdp;
        internal readonly List<string> Endpoints = new List<string>();
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
        [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
        internal static ProcessConnections Read()
        {
            var result = new ProcessConnections();
            var owners = new Dictionary<int, bool>();
            Process[] processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        string name = process.ProcessName;
                        if (string.Equals(name, "steam", StringComparison.OrdinalIgnoreCase)) result.SteamRunning = true;
                        bool game = string.Equals(name, "cs2", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "csgo", StringComparison.OrdinalIgnoreCase);
                        if (game) result.GameRunning = true;
                        if (game || string.Equals(name, "steam", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "steamwebhelper", StringComparison.OrdinalIgnoreCase)) owners[process.Id] = game;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            foreach (int family in new[] { 2, 23 })
            {
                foreach (bool udp in new[] { false, true })
                {
                    try { ReadTable(result, owners, family, udp); }
                    catch (Exception ex) { result.Complete = false; result.Error = ex.Message; }
                }
            }
            return result;
        }
        private static void ReadTable(ProcessConnections result, Dictionary<int, bool> owners, int family, bool udp)
        {
            int size = 0;
            uint error = udp ? GetExtendedUdpTable(IntPtr.Zero, ref size, false, family, 1, 0) : GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 5, 0);
            if (error != 0 && error != 122) throw new Win32Exception((int)error, "读取进程连接表失败");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr memory = Marshal.AllocHGlobal(Math.Max(size, 4));
                try
                {
                    int allocated = Math.Max(size, 4);
                    error = udp ? GetExtendedUdpTable(memory, ref size, false, family, 1, 0) : GetExtendedTcpTable(memory, ref size, false, family, 5, 0);
                    if (error == 122) continue;
                    if (error != 0) throw new Win32Exception((int)error, "读取进程连接表失败");
                    int count = Marshal.ReadInt32(memory);
                    int rowSize = udp ? (family == 2 ? 12 : 28) : (family == 2 ? 24 : 56);
                    if (count < 0 || (long)count * rowSize + 4 > allocated) throw new InvalidDataException("连接表长度无效。");
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = IntPtr.Add(memory, 4 + i * rowSize);
                        int pid = Marshal.ReadInt32(row, rowSize - 4);
                        bool game;
                        if (!owners.TryGetValue(pid, out game)) continue;
                        if (udp) { if (game) result.GameUdp++; continue; }
                        if (Marshal.ReadInt32(row, family == 2 ? 0 : 48) != 5) continue;
                        IPAddress remote;
                        int portOffset;
                        if (family == 2) { remote = new IPAddress((long)(uint)Marshal.ReadInt32(row, 12)); portOffset = 16; }
                        else
                        {
                            byte[] bytes = new byte[16]; Marshal.Copy(IntPtr.Add(row, 24), bytes, 0, 16);
                            remote = new IPAddress(bytes, (uint)Marshal.ReadInt32(row, 40)); portOffset = 44;
                        }
                        int port = (Marshal.ReadByte(row, portOffset) << 8) | Marshal.ReadByte(row, portOffset + 1);
                        if (game) result.GameTcp++;
                        else if (IPAddress.IsLoopback(remote)) result.SteamLoopbackTcp++;
                        else result.SteamTcp++;
                        if (result.Endpoints.Count < 8) result.Endpoints.Add((game ? "CS2" : "Steam") + " PID " + pid + " TCP → " + new IPEndPoint(remote, port));
                    }
                    return;
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            throw new InvalidOperationException("连接表持续变化，请重试。");
        }
    }

    internal static class NetworkDiagnostics
    {
        internal static IPEndPoint ParseServer(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim(); IPAddress address; int port = 27015;
            if (!(text.StartsWith("[", StringComparison.Ordinal) && text.Contains("]:")) && IPAddress.TryParse(text, out address)) return new IPEndPoint(address, port);
            int split = text.LastIndexOf(':');
            if (split < 1 || !int.TryParse(text.Substring(split + 1), out port) || port < 1 || port > 65535) throw new ArgumentException("请输入服务器 IP 或 IP:查询端口，例如 1.2.3.4:27015。IPv6 带端口请使用 [地址]:端口。");
            string host = text.Substring(0, split).Trim('[', ']');
            if (!IPAddress.TryParse(host, out address)) throw new ArgumentException("服务器地址必须是有效 IP；可留空。");
            return new IPEndPoint(address, port);
        }
        internal static List<long> PingSamples(IPAddress address, CancellationToken token)
        {
            var samples = new List<long>();
            using (var ping = new Ping())
            {
                for (int i = 0; i < 8; i++)
                {
                    token.ThrowIfCancellationRequested();
                    try { var reply = ping.Send(address, 850); if (reply.Status == IPStatus.Success) samples.Add(reply.RoundtripTime); } catch (PingException) { }
                    if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
                }
            }
            return samples;
        }
        internal static string SummarizePing(IList<long> samples)
        {
            if (samples.Count == 0) return "8 次 ICMP 均无响应，可能禁用 Ping，不能单独判定断网";
            long sum = 0, max = 0, min = long.MaxValue, variation = 0;
            for (int i = 0; i < samples.Count; i++) { sum += samples[i]; min = Math.Min(min, samples[i]); max = Math.Max(max, samples[i]); if (i > 0) variation += Math.Abs(samples[i] - samples[i - 1]); }
            return string.Format("回复 {0}/8，未回复 {1:0}% · 延迟 {2}/{3:0.0}/{4} ms（低/均/高）· 波动 {5:0.0} ms", samples.Count, 100.0 * (8 - samples.Count) / 8, min, sum / (double)samples.Count, max, samples.Count > 1 ? variation / (double)(samples.Count - 1) : 0);
        }
        internal static bool ErrorResponseProvesTargetReachable(bool systemProxy, int status)
        {
            return !systemProxy && status >= 400 && status <= 599 && status != 407;
        }
        private static bool ProbeHttps(string host, bool systemProxy, Action<string> log, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create("https://" + host + (host == "api.steampowered.com" ? "/ISteamWebAPIUtil/GetServerInfo/v1/" : "/"));
            request.Method = "GET"; request.AllowAutoRedirect = false;
            request.Proxy = systemProxy ? WebRequest.GetSystemWebProxy() : null;
            request.Timeout = 4500; request.ReadWriteTimeout = 4500;
            request.UserAgent = "SteamDirect-NetworkCheck/1.7";
            var watch = Stopwatch.StartNew();
            var response = Task.Factory.FromAsync<WebResponse>(request.BeginGetResponse, request.EndGetResponse, null);
            bool consumed = false;
            try
            {
                if (!response.Wait(5000, token)) { log(host + (systemProxy ? " [系统配置]" : " [直连]") + "：HTTPS 超时"); return false; }
                using (var web = (HttpWebResponse)response.Result)
                {
                    consumed = true;
                    int status = (int)web.StatusCode;
                    log(host + (systemProxy ? " [系统配置]" : " [直连]") + "：HTTPS " + status + "，" + watch.ElapsedMilliseconds + " ms");
                    return true; // Any authenticated HTTP response demonstrates HTTPS reachability, not login success.
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var webError = ex.GetBaseException() as WebException;
                if (webError != null && webError.Response != null)
                {
                    using (var web = (HttpWebResponse)webError.Response)
                    {
                        int status = (int)web.StatusCode;
                        bool reachable = ErrorResponseProvesTargetReachable(systemProxy, status);
                        log(host + (systemProxy ? " [系统配置]" : " [直连]") + "：HTTP " + status +
                            (status == 407 ? "（代理要求认证，未确认目标 HTTPS 连通）" : reachable ? "（目标 HTTPS 可达，但服务拒绝/不支持请求；不代表登录可用）" : "（服务或代理拒绝，不能确认目标 HTTPS 连通）"));
                        return reachable;
                    }
                }
                else log(host + (systemProxy ? " [系统配置]" : " [直连]") + "：" + (webError == null ? ex.GetBaseException().Message : webError.Status + " · " + webError.Message));
                return false;
            }
            finally
            {
                request.Abort();
                if (!consumed) response.ContinueWith(task => { if (task.IsFaulted) { var ignored = task.Exception; } else if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose(); }, TaskScheduler.Default);
            }
        }
        internal static bool IsInfoReply(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20 || bytes[0] != 255 || bytes[1] != 255 || bytes[2] != 255 || bytes[3] != 255 || bytes[4] != 0x49) return false;
            int offset = 6;
            for (int i = 0; i < 4; i++)
            {
                int end = Array.IndexOf(bytes, (byte)0, offset);
                if (end < 0) return false;
                offset = end + 1;
            }
            return bytes.Length >= offset + 10 && Array.IndexOf(bytes, (byte)0, offset + 9) >= 0;

        }
        internal static bool ProbeServer(IPEndPoint server, Action<string> log, CancellationToken token)
        {
            byte[] query = new byte[] { 255, 255, 255, 255, 0x54 };
            byte[] text = Encoding.ASCII.GetBytes("Source Engine Query\0");
            var request = new byte[query.Length + text.Length]; Array.Copy(query, request, query.Length); Array.Copy(text, 0, request, query.Length, text.Length);
            using (var udp = new UdpClient(server.AddressFamily))
            {
                udp.Connect(server); udp.Client.ReceiveTimeout = 1800;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    udp.Send(request, request.Length);
                    IPEndPoint remote = server;
                    byte[] reply;
                    try { reply = udp.Receive(ref remote); } catch (SocketException ex) { log("服务器 UDP 查询未收到有效响应：" + ex.SocketErrorCode + "。可能不支持公开查询、使用中继或端口不同，不能认定游戏 UDP 被封锁。"); return false; }
                    if (IsInfoReply(reply)) { log("指定服务器 " + server + " 返回有效 A2S_INFO 响应，UDP 查询链路可达。"); return true; }
                    if (reply.Length == 9 && reply[0] == 255 && reply[1] == 255 && reply[2] == 255 && reply[3] == 255 && reply[4] == 0x41 && attempt == 0)
                    {
                        var challenged = new byte[request.Length + 4]; Array.Copy(request, challenged, request.Length); Array.Copy(reply, 5, challenged, request.Length, 4); request = challenged; continue;
                    }
                    log("收到非标准或分片 UDP 响应，当前无法确认服务器查询状态。"); return false;
                }
            }
            return false;
        }
        internal static NetworkEvidence Run(IPEndPoint server, Action<DiagnosticUpdate> update, Action<string> log, CancellationToken token)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var evidence = new NetworkEvidence();
            token.ThrowIfCancellationRequested();
            update(new DiagnosticUpdate(0, "本地网络", "检测中", "测量活动网关的延迟与 ICMP 回复"));
            var gateways = new HashSet<string>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                token.ThrowIfCancellationRequested();
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var gateway in adapter.GetIPProperties().GatewayAddresses)
                {
                    if (gateway.Address.Equals(IPAddress.Any) || gateway.Address.Equals(IPAddress.IPv6Any) || !gateways.Add(gateway.Address.ToString())) continue;
                    evidence.HasGateway = true;
                    var samples = PingSamples(gateway.Address, token);
                    evidence.GatewayReplied |= samples.Count > 0;
                    evidence.GatewayLoss |= samples.Count > 0 && samples.Count < 8;
                    log("网关 " + gateway.Address + "（" + adapter.Name + "）：" + SummarizePing(samples));
                }
            }
            update(new DiagnosticUpdate(0, "本地网络", evidence.GatewayLoss ? "需关注" : evidence.GatewayReplied ? "有回复" : "待确认", evidence.GatewayLoss ? "出现 ICMP 未回复样本，建议复测局域网" : evidence.GatewayReplied ? "网关采样完成，详细延迟见下方" : "无常规网关或无 Ping 回复，不能单独判定断网"));
            update(new DiagnosticUpdate(-1, "进度", "", "本地网络检查完成") { CompletedUnits = 1 });
            update(new DiagnosticUpdate(1, "代理 / VPN", "检测中", "检查设置、进程、服务和虚拟网卡"));
            evidence.ProxyIssue = ProxySettings.DiagnoseProxyState();
            if (evidence.ProxyIssue != null) log("代理检查：" + evidence.ProxyIssue);
            update(new DiagnosticUpdate(1, "代理 / VPN", evidence.ProxyIssue == null ? "未发现" : "需关注", evidence.ProxyIssue == null ? "常见本机代理检查通过" : "发现配置/进程或检查未通过；不等于已确认卡顿原因"));
            update(new DiagnosticUpdate(-1, "进度", "", "代理状态检查完成") { CompletedUnits = 2 });
            token.ThrowIfCancellationRequested();
            update(new DiagnosticUpdate(3, "Steam 客户端", "检测中", "读取 Steam 与游戏进程的连接快照"));
            try
            {
                var sockets = ProcessConnections.Read(); evidence.SocketScanOk = sockets.Complete; evidence.ProcessScanOk = true;
                if (!sockets.Complete) log("连接表读取不完整：" + sockets.Error);
                evidence.SteamRunning = sockets.SteamRunning; evidence.GameRunning = sockets.GameRunning;
                evidence.SteamTcp = sockets.SteamTcp; evidence.SteamLoopbackTcp = sockets.SteamLoopbackTcp;
                evidence.GameTcp = sockets.GameTcp; evidence.GameUdp = sockets.GameUdp;
                foreach (var line in sockets.Endpoints) log(line);
            }
            catch (Exception ex) { log("进程连接快照未完成：" + ex.Message); }
            update(new DiagnosticUpdate(3, "Steam 客户端", !evidence.SocketScanOk ? "待确认" : !evidence.SteamRunning ? "未运行" : evidence.SteamTcp > 0 ? "有连接" : "待确认", !evidence.SocketScanOk ? "连接表读取失败，不能判断客户端状态" : "外部 TCP " + evidence.SteamTcp + " · 本地 TCP " + evidence.SteamLoopbackTcp + "；快照不等于登录成功"));
            update(new DiagnosticUpdate(4, "CS2 进程", !evidence.SocketScanOk ? "待确认" : !evidence.GameRunning ? "未运行" : "已运行", !evidence.SocketScanOk ? "无法读取游戏连接状态" : !evidence.GameRunning ? "请进入游戏/对局后重新检测" : "本地 UDP 端口 " + evidence.GameUdp + " · TCP " + evidence.GameTcp + "；不代表对局已连通"));
            update(new DiagnosticUpdate(-1, "进度", "", "客户端与游戏连接快照完成") { CompletedUnits = 4 });
            update(new DiagnosticUpdate(2, "Steam 服务", "检测中", "分别测试 DNS、直连 HTTPS 与系统配置路径"));
            int completedUnits = 4;
            foreach (string host in new[] { "store.steampowered.com", "api.steampowered.com", "steamcommunity.com" })
            {
                token.ThrowIfCancellationRequested();
                try
                {
                var dns = Dns.GetHostAddressesAsync(host);
                try
                {
                    if (!dns.Wait(3000, token) || dns.Result.Length == 0) { evidence.DnsFailures++; log(host + "：DNS 查询超时或无地址"); continue; }
                    log(host + "：DNS 返回 " + dns.Result.Length + " 个地址");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { evidence.DnsFailures++; log(host + "：DNS 失败 · " + ex.GetBaseException().Message); continue; }
                finally { dns.ContinueWith(task => { if (task.IsFaulted) { var ignored = task.Exception; } }, TaskScheduler.Default); }
                bool direct = ProbeHttps(host, false, log, token);
                if (direct) evidence.DirectOk++; else evidence.DirectFailures++;
                if (!direct || evidence.ProxyIssue != null) { evidence.SystemTests++; if (ProbeHttps(host, true, log, token)) { evidence.SystemOk++; if (!direct) evidence.RecoveredHosts++; } }
                }
                finally
                {
                    if (!token.IsCancellationRequested) update(new DiagnosticUpdate(-1, "进度", "", host + " 检查结束") { CompletedUnits = ++completedUnits });
                }
            }
            update(new DiagnosticUpdate(2, "Steam 服务", evidence.DirectOk == 3 ? "直连可达" : evidence.SystemOk > 0 ? "路径差异" : "需关注", "直连成功 " + evidence.DirectOk + "/3 · 系统配置成功 " + evidence.SystemOk + "/" + evidence.SystemTests + " · DNS 失败 " + evidence.DnsFailures + "；不等于客户端/对局正常"));
            token.ThrowIfCancellationRequested();
            if (server == null) update(new DiagnosticUpdate(5, "对局服务器", "未测试", "填写 IP:查询端口可检查指定服务器；官方匹配可能使用中继"));
            else
            {
                update(new DiagnosticUpdate(5, "对局服务器", "检测中", "测试指定地址的 ICMP 与 A2S UDP 查询"));
                log("服务器 ICMP：" + SummarizePing(PingSamples(server.Address, token)));
                try { evidence.ServerResponded = ProbeServer(server, log, token); } catch (SocketException ex) { log("服务器 UDP 查询异常：" + ex.Message); }
                update(new DiagnosticUpdate(5, "对局服务器", evidence.ServerResponded ? "查询可达" : "待确认", evidence.ServerResponded ? "已收到服务器 UDP 查询响应；不代表持续对局质量" : "无有效查询响应；可能不支持查询或查询端口不同"));
            }
            update(new DiagnosticUpdate(-1, "进度", "", "全部检测步骤已结束，未测试项请查看卡片") { CompletedUnits = 8 });
            return evidence;
        }
    }
}

namespace SteamDirect
{
    internal sealed class NetworkWindow : Form
    {
        private readonly TextBox target = new TextBox();
        private readonly TextBox details = new TextBox();
        private readonly TextBox conclusion = new TextBox();
        private readonly Button start = new RoundedButton();
        private readonly Label progress = new Label();
        private readonly ProgressBar progressBar = new ProgressBar();
        private readonly Label[] states = new Label[6];
        private readonly Label[] notes = new Label[6];
        private CancellationTokenSource cancellation;
        private readonly Action<string> log;
        internal NetworkWindow(Action<string> saveLog)
        {
            log = saveLog;
            SuspendLayout();
            Text = "CS2 × Steam 网络诊断";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10F);
            BackColor = AppTheme.Page;
            ForeColor = AppTheme.Ink;
            ClientSize = new Size(950, 740);
            MinimumSize = new Size(900, 730);
            StartPosition = FormStartPosition.CenterParent;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 22, 24, 22), ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 68, 118, 228 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 53));

            var header = Column(2);
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var title = new Label { Text = "网络诊断", Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = AppTheme.Ink, Font = new Font(Font.FontFamily, 22F) };
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(new Label { Text = "CS2 · Steam    查看连接状态与网络路径", Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = AppTheme.Muted }, 0, 1);
            root.Controls.Add(header, 0, 0);

            root.Controls.Add(BuildControls(), 0, 1);
            root.Controls.Add(BuildStatusCards(), 0, 2);

            conclusion.Text = "开始检测后，这里会显示网络诊断结果和处理建议。";
            root.Controls.Add(BuildReadout("诊断结论与建议", conclusion, AppTheme.Ink, new Padding(0, 0, 0, 12)), 0, 3);
            root.Controls.Add(BuildReadout("检测明细", details, AppTheme.Muted, Padding.Empty), 0, 4);

            Controls.Add(root);
            Shown += (sender, args) =>
            {
                conclusion.Select(0, 0);
                details.Select(0, 0);
                start.Select();
            };
            FormClosing += (sender, args) => { if (cancellation != null) cancellation.Cancel(); };
            AppTheme.Apply(this);
            ResumeLayout(true);
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            if (!TopLevel) MinimumSize = Size.Empty;
        }

        private Label Heading(string text, float size)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ForeColor = AppTheme.Ink,
                Font = new Font(Font.FontFamily, size, FontStyle.Bold)
            };
        }

        private static TableLayoutPanel Column(int rows)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return layout;
        }

        private RoundedPanel BuildControls()
        {
            var panel = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 10, 16, 10), Margin = new Padding(0, 0, 0, 12) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
            foreach (int height in new[] { 22, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var targetLabel = new Label { Text = "对局服务器（可选）    默认查询端口 27015", Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font(Font.FontFamily, 9F), ForeColor = AppTheme.Muted };
            layout.Controls.Add(targetLabel, 0, 0);
            layout.SetColumnSpan(targetLabel, 2);

            var targetFrame = new RoundedPanel { Dock = DockStyle.Fill, BackColor = AppTheme.Page, Padding = new Padding(12, 7, 12, 7), Margin = new Padding(0, 0, 12, 0) };
            target.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            target.BorderStyle = BorderStyle.None;
            target.BackColor = AppTheme.Page;
            target.Margin = Padding.Empty;
            target.AccessibleName = "对局服务器 IP 和查询端口（可选）";
            target.AccessibleDescription = "留空时检测本地网络和 Steam 服务。官方匹配可能使用中继。";
            target.TabIndex = 0;
            var targetLayout = Column(1);
            targetLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            targetLayout.Controls.Add(target, 0, 0);
            targetFrame.Controls.Add(targetLayout);
            layout.Controls.Add(targetFrame, 0, 1);

            start.Text = "开始检测";
            start.Dock = DockStyle.Fill;
            start.Margin = Padding.Empty;
            start.TabIndex = 1;
            start.Font = new Font(Font.FontFamily, 10F, FontStyle.Bold);
            AppTheme.ActionButton(start, true);
            start.Click += async (sender, args) => { if (cancellation != null) cancellation.Cancel(); else await RunAsync(); };
            layout.Controls.Add(start, 1, 1);

            progress.Text = "等待开始检测";
            progress.Dock = DockStyle.Fill;
            progress.Margin = Padding.Empty;
            progress.TextAlign = ContentAlignment.MiddleLeft;
            progress.ForeColor = AppTheme.Muted;
            progress.Font = new Font(Font.FontFamily, 9F);
            progress.AutoEllipsis = true;
            var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 4, 0, 0) };
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
            progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
            progressRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            progressRow.Controls.Add(progress, 0, 0);
            progressBar.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            progressBar.Height = 5;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Maximum = 100;
            progressBar.TabStop = false;
            progressBar.Margin = Padding.Empty;
            progressBar.AccessibleName = "网络检测进度";
            progressRow.Controls.Add(progressBar, 1, 0);
            layout.Controls.Add(progressRow, 0, 2);
            layout.SetColumnSpan(progressRow, 2);
            panel.Controls.Add(layout);
            return panel;
        }

        private TableLayoutPanel BuildStatusCards()
        {
            var section = Column(2);
            section.Margin = new Padding(0, 0, 0, 12);
            section.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            section.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            section.Controls.Add(Heading("连接状态", 10F), 0, 0);
            var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
            for (int i = 0; i < 3; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            string[] titles = { "本地网络", "代理 / VPN", "Steam 服务", "Steam 客户端", "CS2 进程", "对局服务器" };
            for (int i = 0; i < titles.Length; i++)
            {
                var card = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(16, 10, 16, 10), Margin = new Padding(0, 0, i % 3 == 2 ? 0 : 12, i < 3 ? 10 : 0) };
                var inner = Column(3);
                inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
                inner.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
                inner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                inner.Controls.Add(new Label { Text = titles[i], Dock = DockStyle.Fill, Margin = Padding.Empty, Font = new Font(Font.FontFamily, 9F), ForeColor = AppTheme.Muted }, 0, 0);
                states[i] = Heading("待检测", 12F);
                states[i].AccessibleName = titles[i] + "状态";
                notes[i] = new Label { Text = i == 5 ? "选填服务器地址。官方匹配可能使用中继。" : "等待检测", Dock = DockStyle.Fill, Margin = Padding.Empty, AutoEllipsis = true, Font = new Font(Font.FontFamily, 9F), ForeColor = AppTheme.Muted };
                inner.Controls.Add(states[i], 0, 1);
                inner.Controls.Add(notes[i], 0, 2);
                card.Controls.Add(inner);
                cards.Controls.Add(card, i % 3, i / 3);
            }
            section.Controls.Add(cards, 0, 1);
            return section;
        }

        private RoundedPanel BuildReadout(string title, TextBox box, Color foreground, Padding margin)
        {
            var frame = new RoundedPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18, 12, 18, 12), Margin = margin };
            var layout = Column(2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(Heading(title, 10F), 0, 0);
            box.Dock = DockStyle.Fill;
            box.Margin = Padding.Empty;
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Vertical;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Color.White;
            box.ForeColor = foreground;
            box.AccessibleName = title;
            layout.Controls.Add(box, 0, 1);
            frame.Controls.Add(layout);
            return frame;
        }
        private void Append(string text)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(Append), text); } catch (InvalidOperationException) { } return; }
            details.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine);
        }
        internal void Apply(DiagnosticUpdate item)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<DiagnosticUpdate>(Apply), item); } catch (InvalidOperationException) { } return; }
            if (item.CompletedUnits >= 0)
            {
                progressBar.Value = Math.Max(0, Math.Min(100, item.CompletedUnits * 100 / 8));
                progress.Text = "检查进度 " + progressBar.Value + "% · " + item.CompletedUnits + "/8 · " + item.Detail;
                return;
            }
            states[item.Index].Text = item.State;
            bool warn = item.State == "需关注" || item.State == "路径差异";
            bool good = item.State == "有回复" || item.State == "未发现" || item.State == "直连可达" || item.State == "有连接" || item.State == "查询可达";
            AppTheme.SetForeground(states[item.Index], warn ? Color.FromArgb(184, 102, 24) : good ? Color.FromArgb(24, 137, 110) : Color.FromArgb(72, 88, 222));
            notes[item.Index].Text = item.Detail;
            if (item.State == "检测中") progress.Text = "正在检测 · " + item.Title;
        }
        private async Task RunAsync()
        {
            IPEndPoint server;
            try { server = NetworkDiagnostics.ParseServer(target.Text); } catch (ArgumentException ex) { NoticeDialog.Show(this, ex.Message, "服务器地址"); return; }
            progressBar.Value = 0;
            target.Enabled = false; start.Text = "取消检测"; details.Clear(); conclusion.Text = "正在收集证据，请等待各项检查完成。";
            for (int i = 0; i < 6; i++) { states[i].Text = "待检测"; AppTheme.SetForeground(states[i], Color.FromArgb(28, 39, 60)); notes[i].Text = "等待检测"; }
            cancellation = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();
            try
            {
                var evidence = await Task.Run(() => NetworkDiagnostics.Run(server, Apply, Append, cancellation.Token));
                if (!IsDisposed)
                {
                    conclusion.Text = evidence.Conclusion(); progress.Text = "检测完成 · 耗时 " + watch.Elapsed.TotalSeconds.ToString("0.0") + " 秒 · 明细如下";
                    log("网络诊断：" + evidence.Conclusion().Replace(Environment.NewLine, " "));
                }
            }
            catch (OperationCanceledException)
            {
                if (!IsDisposed) { conclusion.Text = "检测已取消。部分项目尚未完成，不生成最终故障结论。"; progress.Text = "检测已取消"; MarkIncomplete("已取消"); }
            }
            catch (Exception ex)
            {
                if (!IsDisposed) { conclusion.Text = "检测未完成：" + ex.GetBaseException().Message + "。请重试；本次无法给出完整结论。"; progress.Text = "检测未完成"; MarkIncomplete("未完成"); }
            }
            finally
            {
                cancellation.Dispose(); cancellation = null;
                if (!IsDisposed) { start.Text = "重新检测"; target.Enabled = true; }
            }
        }
        private void MarkIncomplete(string text)
        {
            for (int i = 0; i < 6; i++) if (states[i].Text == "检测中" || states[i].Text == "待检测") { states[i].Text = text; notes[i].Text = "该项未完成，不能判断结果"; }
        }
    }
}






