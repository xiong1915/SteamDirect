using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SteamDirect
{
    internal static class CoreTests
    {
        private static int assertions, failures;
        private static void Need(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        private static void Throws<T>(Action action, string message) where T : Exception
        {
            assertions++;
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException(message);
        }
        private static void Check(string name, Action test)
        {
            try { test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException()); }
        }
        [STAThread]
        private static int Main()
        {
            Check("proxy backup and fault injection", ProxyRecovery);
            Check("SQLite transactions, recovery and Unicode", Storage);
            Check("statistics and incident tracking", Statistics);
            Check("strict diagnostic input", Parsing);
            Check("CSV formula and quote safety", Csv);
            Check("settings corruption and atomic save", Settings);
            Check("Steam manifest boundaries", Discovery);
            Check("monitor concurrent shutdown", MonitorLifetime);
            foreach (var type in typeof(CoreTests).Assembly.GetTypes())
            {
                if (type == typeof(CoreTests) || !type.Name.EndsWith("Tests", StringComparison.Ordinal)) continue;
                var run = type.GetMethod("Run", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (run != null) Check(type.Name, () => { object result = run.Invoke(null, null); if (result is int) assertions += (int)result; });
            }
            Console.WriteLine("Assertions: " + assertions + "; failing groups: " + failures);
            return failures == 0 ? 0 : 1;
        }
        private sealed class FakeProxy : IProxyAccess
        {
            internal ProxySnapshot Current;
            internal bool IgnoreRestore;
            internal int Failures;
            public ProxySnapshot Capture() { return Copy(Current); }
            public void Restore(ProxySnapshot value)
            {
                if (IgnoreRestore) return;
                Current = Copy(value);
                if (Failures > 0) { Failures--; Current.Server = "partial"; throw new IOException("injected partial write"); }
            }
        }
        private static ProxySnapshot Snapshot(string value)
        {
            var result = new ProxySnapshot { Flags = 1, Server = value };
            foreach (string name in ProxySettings.ProxyVariableNames)
            { result.UserEnvironment.Add(name, null); result.ProcessEnvironment.Add(name, "original"); }
            return result;
        }
        private static ProxySnapshot Copy(ProxySnapshot source)
        {
            var result = new ProxySnapshot { Flags = source.Flags, Server = source.Server, Bypass = source.Bypass, AutoConfigUrl = source.AutoConfigUrl };
            foreach (string name in ProxySettings.ProxyVariableNames)
            { result.UserEnvironment.Add(name, source.UserEnvironment[name]); result.ProcessEnvironment.Add(name, source.ProcessEnvironment[name]); }
            return result;
        }
        private static void Temporary(Action<string> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SteamDirect-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(directory); } finally { Directory.Delete(directory, true); }
        }
        private static void ProxyRecovery()
        {
            Temporary(directory =>
            {
                string path = Path.Combine(directory, "backup.xml");
                var original = Snapshot("original"); original.SaveNew(path);
                Need(original.SameAs(ProxySnapshot.Load(path)), "backup lost absent environment variables");
                byte[] protectedBytes = File.ReadAllBytes(path);
                Need(!Encoding.UTF8.GetString(protectedBytes).Contains("original") && !ProxySnapshot.IsLegacy(path), "new backup persisted plaintext settings");
                byte[] tampered = (byte[])protectedBytes.Clone(); tampered[tampered.Length - 1] ^= 1; File.WriteAllBytes(path, tampered);
                Throws<InvalidDataException>(() => ProxySnapshot.Load(path), "modified DPAPI backup accepted");
                Need(File.ReadAllBytes(path).SequenceEqual(tampered), "tampered backup silently deleted");
                File.WriteAllBytes(path, protectedBytes);
                int headerLength = Encoding.ASCII.GetByteCount("SteamDirect.ProxyBackup.DPAPI.v2\n");
                byte[] encrypted = protectedBytes.Skip(headerLength).ToArray();
                byte[] legacyBytes = ProtectedData.Unprotect(encrypted, Encoding.ASCII.GetBytes("SteamDirect.ProxyBackup.DPAPI.v2\n"), DataProtectionScope.CurrentUser);
                string legacyPath = Path.Combine(directory, "old.xml"); File.WriteAllBytes(legacyPath, legacyBytes);
                Need(ProxySnapshot.IsLegacy(legacyPath), "legacy recovery point not identified");
                Throws<InvalidDataException>(() => ProxySnapshot.Load(legacyPath), "unprotected downgrade automatically accepted");
                Need(File.ReadAllBytes(legacyPath).SequenceEqual(legacyBytes), "legacy backup changed without migration consent");
                ProxySnapshot.MigrateLegacy(legacyPath);
                Need(original.SameAs(ProxySnapshot.Load(legacyPath)) && !ProxySnapshot.IsLegacy(legacyPath) &&
                    File.ReadAllBytes(legacyPath + ".legacy").SequenceEqual(legacyBytes), "explicit legacy migration lost recovery contents");
                File.WriteAllBytes(legacyPath, legacyBytes);
                Throws<IOException>(() => ProxySnapshot.MigrateLegacy(legacyPath), "migration overwrote previous .legacy recovery point");
                Need(File.ReadAllBytes(legacyPath).SequenceEqual(legacyBytes) && File.ReadAllBytes(legacyPath + ".legacy").SequenceEqual(legacyBytes), "migration collision damaged existing files");
                Throws<IOException>(() => Snapshot("new").SaveNew(path), "old recovery point overwritten");
                var access = new FakeProxy { Current = Snapshot("current"), IgnoreRestore = true };
                Throws<InvalidOperationException>(() => ProxySession.RestoreOriginal(path, access), "unverified restore accepted");
                Need(File.Exists(path), "unverified restoration deleted backup");
                access.IgnoreRestore = false; access.Failures = 1;
                Throws<IOException>(() => ProxySession.RestoreOriginal(path, access), "partial restoration did not fail");
                Need(access.Current.Server == "current" && File.Exists(path), "partial restoration failed to roll back or retain backup");
                ProxySession.RestoreOriginal(path, access);
                Need(original.SameAs(access.Current) && !File.Exists(path), "successful restoration did not finish");
                var session = new ProxySession(path, access);
                access.Current = Snapshot("changed"); access.IgnoreRestore = true;
                Throws<InvalidOperationException>(session.Rollback, "unverified workflow rollback accepted");
                Need(File.Exists(path), "failed workflow rollback deleted backup");
                access.IgnoreRestore = false; session.Rollback();
                Need(!File.Exists(path) && original.SameAs(access.Current), "workflow rollback failed");
                original.SaveNew(path); access.Current = Snapshot("another"); access.Failures = 2;
                Throws<AggregateException>(() => ProxySession.RestoreOriginal(path, access), "double restoration/rollback failure not surfaced");
                Need(File.Exists(path) && original.SameAs(ProxySnapshot.Load(path)), "double failure damaged original recovery point");
                access.Current = Snapshot("current");
                var olderRecovery = new ProxySession(path, access); access.Current = Snapshot("changed"); olderRecovery.Rollback();
                Need(File.Exists(path) && original.SameAs(ProxySnapshot.Load(path)) && access.Current.Server == "current", "existing original recovery point lost on workflow rollback");
                int checks = 0, pauses = 0;
                ProxySession.VerifyStable(() => checks++, () => pauses++);
                Need(checks == 3 && pauses == 2, "stability check count changed");
                bool launched = false;
                Throws<IOException>(() => ProxySession.VerifyAndLaunch(() => { throw new IOException("injected verification failure"); }, () => launched = true), "launch verification failure ignored");
                Need(!launched, "launch ran before verification succeeded");
                File.WriteAllText(path, "<!DOCTYPE ProxyBackup [<!ENTITY x SYSTEM 'file:///invalid'>]><ProxyBackup Version='1'>&x;</ProxyBackup>");
                Throws<System.Xml.XmlException>(() => ProxySnapshot.Load(path), "DTD accepted in recovery file");
            });
        }
        private static void Storage()
        {
            Temporary(directory =>
            {
                var store = new HistoryStore(Path.Combine(directory, "测试.sqlite3")); store.Initialize(); store.Initialize();
                var session = new HistorySession { Id = "s'1", Game = "中文 =游戏", Mode = "直连", Target = "example.invalid", Start = 10 };
                var sample = new HistorySample { SessionId = session.Id, At = 20, Mode = "直连", ExternalMs = -1, GatewayMs = 2, Dns = -1, AdapterUp = true };
                var issue = new HistoryEvent { Id = "e'1", SessionId = session.Id, Kind = "未恢复", Start = 20 };
                store.WriteBatch(new[] { session }, new[] { sample }, new[] { issue }); store.RecoverInterrupted();
                var recovered = store.ListSessions()[0];
                Need(recovered.Game == session.Game && recovered.Interrupted && recovered.End == 20, "Unicode/recovery failed");
                Need(store.ReadEvents(session.Id)[0].End == 0, "unobserved event recovery fabricated");
                recovered.Report = "保留报告"; store.WriteBatch(new[] { recovered }, null, null); recovered.Report = null;
                store.WriteBatch(new[] { recovered }, null, null);
                Need(store.ListSessions()[0].Report == "保留报告" && store.ReadSamples(session.Id).Count == 1, "upsert lost data");
                Throws<InvalidOperationException>(() => store.WriteBatch(new[] { new HistorySession { Id = "second", Game = "Other", Mode = "未知", Start = 22 } },
                    new[] { new HistorySample { SessionId = "orphan", At = 22, Mode = "未知" } }, null), "orphan accepted");
                Need(store.ListSessions().Count == 1, "failed batch was not atomic");
                Need(store.ListSessions(200, 0, "' OR 1=1 --").Count == 0, "filter was not bound");
                Throws<ArgumentOutOfRangeException>(() => store.ReadSamplePage(session.Id, -1), "negative page accepted");
                var reopened = new HistoryStore(Path.Combine(directory, "测试.sqlite3"));
                Need(reopened.ReadSamples(session.Id)[0].At == 20, "reopen lost WAL data");
                string damaged = Path.Combine(directory, "damaged.sqlite3"); byte[] data = System.Text.Encoding.UTF8.GetBytes("not a database; preserve this file"); File.WriteAllBytes(damaged, data);
                Throws<InvalidOperationException>(() => new HistoryStore(damaged).Initialize(), "damaged database silently accepted");
                Need(File.ReadAllBytes(damaged).SequenceEqual(data), "damaged database was overwritten");
            });
        }
        private static void Statistics()
        {
            var metrics = HistoryStatistics.Calculate(new[] {
                new HistorySample { At = 1, ExternalMs = 10 }, new HistorySample { At = 2, ExternalMs = -1 },
                new HistorySample { At = 3, ExternalMs = -2 }, new HistorySample { At = 4, ExternalMs = 30 } });
            Need(metrics.Attempts == 3 && metrics.Lost == 1 && metrics.Average == 20 && metrics.P95 == 30 && metrics.JitterCount == 0, "unknowns/loss/statistics incorrect");
            metrics = HistoryStatistics.Calculate(new[] { new HistorySample { At = 20000, ExternalMs = 10 }, new HistorySample { At = 10000, ExternalMs = 40 } });
            Need(metrics.JitterCount == 0, "reverse timestamps connected for jitter");
            var tracker = new HistoryTracker("test"); var events = new List<HistoryEvent>();
            for (int i = 0; i < 2; i++) tracker.Observe(new HistorySample { At = 1000 + i * 5000, Mode = "直连", GatewayMs = 2, ExternalMs = -1, Dns = 1, AdapterUp = true }, events);
            Need(events.Count == 1 && events[0].Start == 1000 && events[0].End == 0, "incident debounce failed");
            for (int i = 0; i < 2; i++) tracker.Observe(new HistorySample { At = 11000 + i * 5000, Mode = "直连", GatewayMs = 2, ExternalMs = 20, Dns = -1, AdapterUp = true }, events);
            Need(events.Count == 2 && events[1].End == 11000, "observed recovery time incorrect");
            Need(HistoryMonitor.Interval(.1) == 5 && HistoryMonitor.Interval(.75) == 10 && HistoryMonitor.Interval(.9) == 20, "adaptive interval regression");
            using (var monitor = new HistoryMonitor(Path.Combine(Path.GetTempPath(), "unused.sqlite3"), () => new List<SteamGameProcess>())) { monitor.Dispose(); monitor.Start(); }
        }
        private static void Parsing()
        {
            Need(NetworkDiagnostics.ParseServer(" ") == null, "empty optional target rejected");
            Need(NetworkDiagnostics.ParseServer("1.2.3.4").Port == 27015, "default port incorrect");
            Need(NetworkDiagnostics.ParseServer("[2001:db8::1]:65535").Address.Equals(IPAddress.Parse("2001:db8::1")), "IPv6 parse failed");
            foreach (string invalid in new[] { "example.com:80", "1.2.3.4:0", "1.2.3.4:65536", "[127.0.0.1]]:27015", "[::1]:+80" })
                Throws<ArgumentException>(() => NetworkDiagnostics.ParseServer(invalid), "malformed server accepted: " + invalid);
            Need(!NetworkDiagnostics.IsInfoReply(new byte[6]) && !NetworkDiagnostics.IsInfoReply(null), "truncated A2S reply accepted");
            Need(NetworkDiagnostics.ErrorResponseProvesTargetReachable(false, 403) && !NetworkDiagnostics.ErrorResponseProvesTargetReachable(false, 407) &&
                !NetworkDiagnostics.ErrorResponseProvesTargetReachable(true, 403), "proxy error mistaken for target reachability");
        }
        private static void Csv()
        {
            var method = typeof(HistoryWindow).GetMethod("CsvRow", BindingFlags.NonPublic | BindingFlags.Static);
            using (var writer = new StringWriter())
            {
                method.Invoke(null, new object[] { writer, new[] { "=CMD()", "  +123", "@SUM(1)", "\tvalue", "line\n\"quoted\"", null } });
                string value = writer.ToString();
                Need(value.StartsWith("\"'=CMD()\",\"'  +123\",\"'@SUM(1)\",\"'\tvalue\","), "formula/tab prefix not neutralized");
                Need(value.Contains("\"line\n\"\"quoted\"\"\",\"\""), "quote/newline escaping incorrect");
            }
        }
        private static void Settings()
        {
            Temporary(directory =>
            {
                string path = Path.Combine(directory, "settings.ini");
                Need(UserPreferences.Load(path).RetentionDays == 7, "missing settings lack defaults");
                File.WriteAllText(path, "RetentionDays=-3\nThemeColor=invalid\nTrayEnabled=broken\nLogDirectory=relative\nReduceMotion=broken");
                var value = UserPreferences.Load(path);
                Need(value.RetentionDays == 7 && value.TrayEnabled && !value.ReduceMotion && Path.IsPathRooted(value.LogDirectory), "malformed settings corrupted defaults");
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Need(UserPreferences.Load(path).RetentionDays == 7, "temporarily unreadable settings crash startup");
                UserPreferences.Save(path, false, 30, true, "#CC0000", directory, true);
                value = UserPreferences.Load(path);
                Need(!value.TrayEnabled && value.RetentionDays == 30 && value.NightMode && value.ThemeColor == "#CC0000" && value.LogDirectory == directory && value.ReduceMotion, "settings round trip failed");
                string before = File.ReadAllText(path);
                Throws<ArgumentOutOfRangeException>(() => UserPreferences.Save(path, true, 0), "invalid retention saved");
                Need(File.ReadAllText(path) == before && Directory.GetFiles(directory, "*.new-*").Length == 0, "failed save damaged existing preferences");
                string fake = Path.Combine(directory, "steam.exe"); File.WriteAllText(fake, "not an executable");
                Throws<InvalidOperationException>(() => { using (SteamTrust.OpenVerified(fake)) { } }, "unsigned executable trusted");
            });
        }
        private static void Discovery()
        {
            Temporary(directory =>
            {
                string apps = Path.Combine(directory, "steamapps"); Directory.CreateDirectory(apps);
                File.WriteAllText(Path.Combine(apps, "appmanifest_123.acf"), "\"appid\" \"123\"\n\"name\" \"测试游戏\"\n\"installdir\" \"Game\"");
                File.WriteAllText(Path.Combine(apps, "appmanifest_124.acf"), "\"appid\" \"124\"\n\"installdir\" \"..\"");
                File.WriteAllText(Path.Combine(apps, "appmanifest_125.acf"), "\"appid\" \"126\"\n\"installdir\" \"Other\"");
                File.WriteAllText(Path.Combine(apps, "appmanifest_431960.acf"), "\"appid\" \"431960\"\n\"installdir\" \"Wallpaper\"");
                var scanner = new SteamGameDiscovery(directory);
                var read = typeof(SteamGameDiscovery).GetMethod("ReadLibrary", BindingFlags.NonPublic | BindingFlags.Instance);
                var games = (System.Collections.ICollection)read.Invoke(scanner, null);
                Need(games.Count == 1, "manifest traversal, mismatched AppID or excluded tool accepted");
            });
        }
        private static void MonitorLifetime()
        {
            // No games are returned: these workers never perform ping, DNS or proxy writes.
            Temporary(directory =>
            {
                for (int i = 0; i < 80; i++)
                {
                    var monitor = new HistoryMonitor(Path.Combine(directory, i + ".sqlite3"), () => new List<SteamGameProcess>());
                    Task.WaitAll(Task.Run(() => monitor.Start()), Task.Run(() => monitor.Dispose()));
                    var worker = (Task)typeof(HistoryMonitor).GetField("worker", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(monitor);
                    if (worker != null) worker.Wait();
                    Need(!monitor.IsPlaying, "disposed monitor retained playing status");
                }
            });
        }
    }
}
