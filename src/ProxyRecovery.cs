using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace SteamDirect
{
    internal sealed class ProxySnapshot
    {
        internal const int ManualProxy = 2;
        private static readonly byte[] protectedHeader = Encoding.ASCII.GetBytes("SteamDirect.ProxyBackup.DPAPI.v2\n");
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
            path = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            if (!FilePathSafety.NoReparseAncestors(directory)) throw new IOException("代理备份目录包含重解析路径。");
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
            bytes = ProtectedData.Protect(bytes, protectedHeader, DataProtectionScope.CurrentUser);
            string stage = path + ".new-" + Guid.NewGuid().ToString("N");
            bool created = false;
            try
            {
                using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { created = true; output.Write(protectedHeader, 0, protectedHeader.Length); output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                if (!SameAs(Load(stage))) throw new InvalidDataException("代理备份写入核验失败。");
                File.Move(stage, path);
                created = false;
            }
            finally
            {
                if (created) { try { File.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
        private static void Add(XmlDocument document, XmlElement root, string name, string value)
        { var node = document.CreateElement(name); node.InnerText = value; root.AppendChild(node); }
        internal static ProxySnapshot Load(string path)
        {
            byte[] bytes = ReadBounded(path);
            if (!IsProtected(bytes))
            {
                if (IsLegacyDocument(ReadDocument(bytes)))
                    throw new InvalidDataException("旧版明文代理备份需核验并确认迁移；原备份已保留，请使用恢复按钮查看迁移提示。");
                throw new InvalidDataException("代理备份格式无效。");
            }
            byte[] encrypted = new byte[bytes.Length - protectedHeader.Length];
            Buffer.BlockCopy(bytes, protectedHeader.Length, encrypted, 0, encrypted.Length);
            try { bytes = ProtectedData.Unprotect(encrypted, protectedHeader, DataProtectionScope.CurrentUser); }
            catch (CryptographicException ex)
            { throw new InvalidDataException("代理备份完整性核验失败，或不属于当前 Windows 用户；未修改代理，备份已保留。", ex); }
            return ReadSnapshot(ReadDocument(bytes));
        }
        internal static bool IsLegacy(string path)
        {
            byte[] bytes = ReadBounded(path);
            return !IsProtected(bytes) && IsLegacyDocument(ReadDocument(bytes));
        }
        // Call only after the user explicitly accepts the unverifiable legacy contents.
        // DPAPI protects future reads; it cannot establish the authenticity of an old plaintext file.
        internal static void MigrateLegacy(string path)
        {
            byte[] original = ReadBounded(path);
            if (IsProtected(original)) throw new InvalidOperationException("此代理备份已受保护，无需迁移。");
            var snapshot = ReadSnapshot(ReadDocument(original));
            string stage = path + ".protected-" + Guid.NewGuid().ToString("N");
            try
            {
                snapshot.SaveNew(stage);
                string legacy = path + ".legacy";
                File.Copy(path, legacy, false); // Never replace an existing legacy recovery point.
                if (!SameBytes(original, ReadBounded(legacy)) || !SameBytes(original, ReadBounded(path)))
                    throw new IOException("迁移期间原备份发生变化，已停止；所有恢复文件均已保留。");
                File.Replace(stage, path, null);
                if (!snapshot.SameAs(Load(path))) throw new InvalidDataException("迁移后核验失败，旧版备份保留在 .legacy 文件中。");
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
        private static bool IsProtected(byte[] bytes)
        {
            if (bytes.Length <= protectedHeader.Length) return false;
            for (int i = 0; i < protectedHeader.Length; i++) if (bytes[i] != protectedHeader[i]) return false;
            return true;
        }
        private static byte[] ReadBounded(string path)
        {
            if (!FilePathSafety.NoReparseAncestors(path)) throw new IOException("代理备份路径不存在、不可访问或包含重解析点。");
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!FilePathSafety.IsSameFinalPath(input.SafeFileHandle, Path.GetFullPath(path)))
                    throw new IOException("代理备份的实际路径与预期不符。");
                if (input.Length > 1048576) throw new InvalidDataException("代理备份过大，已拒绝读取。");
                using (var memory = new MemoryStream()) { input.CopyTo(memory); return memory.ToArray(); }
            }
        }
        private static XmlDocument ReadDocument(byte[] bytes)
        {
            var document = new XmlDocument { XmlResolver = null };
            using (var memory = new MemoryStream(bytes))
            using (var reader = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 })) document.Load(reader);
            return document;
        }
        private static bool IsLegacyDocument(XmlDocument document)
        { var root = document.DocumentElement; return root != null && root.Name == "ProxyBackup" && root.GetAttribute("Version") == "1"; }
        private static ProxySnapshot ReadSnapshot(XmlDocument document)
        {
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
            if (!baseline.SameAs(access.Capture())) RestoreVerified(access, baseline);
            if (created && File.Exists(path) && baseline.SameAs(ProxySnapshot.Load(path))) File.Delete(path);
            completed = true;
        }
        private static void RestoreVerified(IProxyAccess access, ProxySnapshot snapshot)
        {
            access.Restore(snapshot);
            if (!snapshot.SameAs(access.Capture()))
                throw new InvalidOperationException("恢复后核验未通过，备份已保留，请勿删除。");
        }

    }
}
