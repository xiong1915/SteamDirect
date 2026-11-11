using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace SteamDirect
{
    internal sealed class ProxySnapshot
    {
        internal const int ManualProxy = 2;
        internal int Flags;
        internal string Server = "", Bypass = "", AutoConfigUrl = "";
        internal readonly Dictionary<string, string> UserEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> ProcessEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal void Validate()
        {
            if (Flags < 1 || (Flags & ~15) != 0) throw new InvalidDataException("备份中的代理标志无效。");
            if (Server == null || Bypass == null || AutoConfigUrl == null) throw new InvalidDataException("备份缺少代理字段。");
            foreach (var map in new[] { UserEnvironment, ProcessEnvironment })
            {
                if (map.Count != ProxySettings.ProxyVariableNames.Length) throw new InvalidDataException("备份中的环境变量不完整。");
                foreach (string name in ProxySettings.ProxyVariableNames) if (!map.ContainsKey(name)) throw new InvalidDataException("备份缺少环境变量字段。");
            }
        }
        internal bool SameAs(ProxySnapshot other)
        {
            if (other == null || Flags != other.Flags || Server != other.Server || Bypass != other.Bypass || AutoConfigUrl != other.AutoConfigUrl) return false;
            foreach (string name in ProxySettings.ProxyVariableNames)
                if (UserEnvironment[name] != other.UserEnvironment[name] || ProcessEnvironment[name] != other.ProcessEnvironment[name]) return false;
            return true;
        }
        internal void SaveNew(string path)
        {
            Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var document = new XmlDocument();
            var root = document.CreateElement("ProxyBackup"); root.SetAttribute("Version", "1"); root.SetAttribute("Created", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)); document.AppendChild(root);
            Add(document, root, "Flags", Flags.ToString(CultureInfo.InvariantCulture));
            Add(document, root, "Server", Server); Add(document, root, "Bypass", Bypass); Add(document, root, "AutoConfigUrl", AutoConfigUrl);
            foreach (var scope in new[] { "User", "Process" })
                foreach (string name in ProxySettings.ProxyVariableNames)
                {
                    var node = document.CreateElement("Variable"); node.SetAttribute("Scope", scope); node.SetAttribute("Name", name);
                    string value = (scope == "User" ? UserEnvironment : ProcessEnvironment)[name];
                    node.SetAttribute("Present", value == null ? "false" : "true"); node.InnerText = value ?? ""; root.AppendChild(node);
                }
            // Serialize and validate before creating the persistent recovery file.
            byte[] bytes;
            using (var memory = new MemoryStream()) { document.Save(memory); bytes = memory.ToArray(); }
            bool created = false;
            try
            {
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { created = true; output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                Load(path);
            }
            catch
            {
                if (created) { try { File.Delete(path); } catch { } }
                throw;
            }
        }
        private static void Add(XmlDocument document, XmlElement root, string name, string value)
        { var node = document.CreateElement(name); node.InnerText = value; root.AppendChild(node); }
        internal static ProxySnapshot Load(string path)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 })) document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.Name != "ProxyBackup" || root.GetAttribute("Version") != "1") throw new InvalidDataException("代理备份格式无效。");
            var result = new ProxySnapshot();
            int flags;
            if (!int.TryParse(ReadOne(root, "Flags"), NumberStyles.Integer, CultureInfo.InvariantCulture, out flags)) throw new InvalidDataException("代理备份标志无效。");
            result.Flags = flags; result.Server = ReadOne(root, "Server"); result.Bypass = ReadOne(root, "Bypass"); result.AutoConfigUrl = ReadOne(root, "AutoConfigUrl");
            foreach (XmlNode node in root.SelectNodes("Variable"))
            {
                var item = (XmlElement)node; string scope = item.GetAttribute("Scope"), name = item.GetAttribute("Name");
                if (scope != "User" && scope != "Process") throw new InvalidDataException("备份环境变量范围无效。");
                bool allowed = false; foreach (string known in ProxySettings.ProxyVariableNames) if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase)) allowed = true;
                if (!allowed) throw new InvalidDataException("备份包含非代理环境变量。");
                bool present;
                if (!bool.TryParse(item.GetAttribute("Present"), out present)) throw new InvalidDataException("备份环境变量状态无效。");
                var map = scope == "User" ? result.UserEnvironment : result.ProcessEnvironment;
                if (map.ContainsKey(name)) throw new InvalidDataException("备份包含重复环境变量。");
                map.Add(name, present ? item.InnerText : null);
            }
            result.Validate(); return result;
        }
        private static string ReadOne(XmlElement root, string name)
        { var nodes = root.SelectNodes(name); if (nodes.Count != 1) throw new InvalidDataException("代理备份字段缺失或重复。"); return nodes[0].InnerText; }
    }

    internal interface IProxyAccess
    {
        ProxySnapshot Capture();
        void Restore(ProxySnapshot snapshot);
    }
    internal sealed class WindowsProxyAccess : IProxyAccess
    {
        public ProxySnapshot Capture()
        {
            var snapshot = ProxySettings.CaptureConnection();
            foreach (string name in ProxySettings.ProxyVariableNames)
            {
                snapshot.UserEnvironment.Add(name, Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
                snapshot.ProcessEnvironment.Add(name, Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process));
            }
            snapshot.Validate(); return snapshot;
        }
        public void Restore(ProxySnapshot snapshot)
        {
            snapshot.Validate();
            ProxySettings.RestoreConnection(snapshot);
            foreach (string name in ProxySettings.ProxyVariableNames)
            {
                Environment.SetEnvironmentVariable(name, snapshot.UserEnvironment[name], EnvironmentVariableTarget.User);
                Environment.SetEnvironmentVariable(name, snapshot.ProcessEnvironment[name], EnvironmentVariableTarget.Process);
            }
            if (!snapshot.SameAs(Capture())) throw new InvalidOperationException("恢复后核验未通过，备份已保留，请勿删除。");
        }
    }

    internal sealed class ProxySession
    {
        internal static readonly string BackupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamDirect", "proxy-backup.xml");
        private readonly string path;
        private readonly IProxyAccess access;
        private readonly ProxySnapshot baseline;
        private readonly bool created;
        private bool completed;
        internal ProxySession(string backupPath, IProxyAccess proxyAccess)
        {
            path = backupPath; access = proxyAccess;
            if (File.Exists(path)) ProxySnapshot.Load(path); // Never overwrite an earlier original recovery point.
            baseline = access.Capture(); baseline.Validate();
            if (!File.Exists(path)) { baseline.SaveNew(path); created = true; }
        }
        internal static void VerifyStable(Action check, Action pause)
        {
            for (int i = 0; i < 3; i++) { check(); if (i < 2) pause(); }
        }
        internal static void VerifyAndLaunch(Action verify, Action launch)
        {
            verify();
            launch();
        }
        internal void Complete() { completed = true; }
        internal void Rollback()
        {
            if (completed) return;
            if (!baseline.SameAs(access.Capture())) access.Restore(baseline);
            if (created) File.Delete(path);
            completed = true;
        }
        internal static void RestoreOriginal(string path, IProxyAccess access)
        {
            var snapshot = ProxySnapshot.Load(path); // Full validation before changing anything.
            CheckLocalManualProxy(snapshot);
            access.Restore(snapshot);
            File.Delete(path); // Delete only after restoration and verification succeed.
        }

        private static void CheckLocalManualProxy(ProxySnapshot snapshot)
        {
            if ((snapshot.Flags & ProxySnapshot.ManualProxy) == 0) return;

            var checkedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in snapshot.Server.Split(new[] { ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = entry.Substring(entry.IndexOf('=') + 1);
                Uri server;
                if (!Uri.TryCreate(value.IndexOf("://", StringComparison.Ordinal) >= 0 ? value : "http://" + value,
                    UriKind.Absolute, out server) || server.Port <= 0) continue;

                string host = server.DnsSafeHost;
                IPAddress address = null;
                bool localhost = string.Equals(host.TrimEnd('.'), "localhost", StringComparison.OrdinalIgnoreCase);
                if (!localhost && (!IPAddress.TryParse(host, out address) || !IPAddress.IsLoopback(address))) continue;
                if (!checkedEndpoints.Add(server.Authority)) continue;

                bool ready = localhost
                    ? CanConnect(IPAddress.Loopback, server.Port) || CanConnect(IPAddress.IPv6Loopback, server.Port)
                    : CanConnect(address, server.Port);
                if (!ready)
                    throw new InvalidOperationException("本地代理端口 " + server.Authority +
                        " 尚未就绪，未写回代理设置。请先打开原代理软件，等它就绪后再点击恢复。");
            }
        }

        private static bool CanConnect(IPAddress address, int port)
        {
            try
            {
                using (var client = new TcpClient(address.AddressFamily))
                {
                    var pending = client.BeginConnect(address, port, null, null);
                    using (var completed = pending.AsyncWaitHandle)
                    {
                        if (!completed.WaitOne(500)) return false;
                        client.EndConnect(pending);
                        return true;
                    }
                }
            }
            catch (SocketException) { return false; }
        }
    }
}
