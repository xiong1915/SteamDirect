using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
namespace SteamDirect
{
    internal static class SafetyTests
    {
        internal static int Run()
        {
            long elapsed = 0;
            try
            {
                ProxyAppShutdown.FinishProcesses(
                    () => new List<ProxyAppShutdown.AppProcess> { new ProxyAppShutdown.AppProcess { Id = 42, Name = "ChatGPT", Path = @"C:\Unknown\ChatGPT.exe", Started = 1 } },
                    ms => elapsed += ms, () => elapsed, s => { }, process => { });
                throw new Exception("Expected exit timeout.");
            }
            catch (InvalidOperationException) { }
            Assert(elapsed == 8000, "A process surviving direct termination must stop the workflow after the verification timeout.");
            elapsed = 0; int terminated = 0;
            var pending = new List<ProxyAppShutdown.AppProcess> { new ProxyAppShutdown.AppProcess { Id = 99, Name = "ChatGPT", Path = @"C:\known\ChatGPT.exe", Started = 1 } };
            ProxyAppShutdown.FinishProcesses(() => pending, ms => elapsed += ms, () => elapsed, text => { }, process => { terminated++; pending.Clear(); });
            Assert(terminated == 1 && elapsed == 250, "ChatGPT must be terminated on the first pass and checked again.");
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\FlClash" };
            Assert(!FlClashShutdown.IsAllowed("FlClash", @"C:\Apps\FlClash2\FlClash.exe", folders), "Reject sibling prefix.");
            Assert(!FlClashShutdown.IsAllowed("Other", @"C:\Apps\FlClash\Other.exe", folders), "Reject unrelated process.");
            Assert(ProxyAppShutdown.Identify(@"C:\Unknown\ChatGPT.exe", null, @"C:\Users\u\AppData\Local", @"C:\Program Files") == null, "Reject same-name app outside trusted root.");
            Assert(ProxyAppShutdown.SameIdentity(42, 100, @"C:\Apps\x.exe", 42, 100, @"c:\apps\x.exe"), "Identity match accepts path casing.");
            Assert(!ProxyAppShutdown.SameIdentity(42, 100, @"C:\Apps\x.exe", 42, 101, @"C:\Apps\x.exe"), "Reject PID reuse.");
            Assert(!ProxyAppShutdown.SameIdentity(42, 100, @"C:\Apps\x.exe", 42, 100, @"C:\Apps\y.exe"), "Reject replaced executable path.");
            string fixture = typeof(CoreTests).Assembly.Location;
            using (var child = Process.Start(new ProcessStartInfo(fixture, "--process-shutdown-fixture")
            {
                UseShellExecute = false, CreateNoWindow = true
            }))
            {
                try
                {
                    var target = new ProxyAppShutdown.AppProcess { Id = child.Id, Path = fixture,
                        Started = child.StartTime.ToUniversalTime().Ticks + 1 };
                    bool rejected = false;
                    try { ProxyAppShutdown.KillVerified(target, path => path == fixture); }
                    catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected && !child.HasExited, "A changed process identity must remain alive.");
                    target.Started--;
                    ProxyAppShutdown.KillVerified(target, path => string.Equals(path, fixture, StringComparison.OrdinalIgnoreCase));
                    Assert(child.WaitForExit(3000), "The verified fixture must exit without window messages or confirmation.");
                    ProxyAppShutdown.KillVerified(target, path => false);
                }
                finally { if (!child.HasExited) child.Kill(); }
            }
            Assert(!TempCleaner.IsInside(@"C:\Temp\SteamDirect", @"C:\Temp\SteamDirect-other\x.tmp"), "Reject sibling prefix.");
            Assert(!TempCleaner.IsInside(@"C:\Temp\SteamDirect", @"C:\Temp\SteamDirect\..\personal.tmp"), "Reject parent traversal.");
            Assert(!TempCleaner.IsInside(@"C:\Temp\SteamDirect", @"x.tmp"), "Reject relative cleanup paths.");
            Assert(TempCleaner.IsInside(@"C:\Temp\SteamDirect", @"C:\Temp\SteamDirect\nested\x.tmp"), "Accept actual descendant.");
            Assert(ProxyAppShutdown.Identify(@"C:\Unknown\chrome.exe", ProxyAppShutdown.YouTubeAppId, @"C:\Local", @"C:\Program Files") == null, "App ID alone cannot trust executable.");
            string root = Path.Combine(Path.GetTempPath(), "SteamDirectSafetyTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string stale = Path.Combine(root, "stale.tmp"), recent = Path.Combine(root, "recent.tmp"), document = Path.Combine(root, "personal.txt");
                File.WriteAllText(stale, "temporary"); File.WriteAllText(recent, "active"); File.WriteAllText(document, "retain");
                var old = DateTime.UtcNow.AddDays(-10);
                foreach (string file in new[] { stale, document })
                { File.SetCreationTimeUtc(file, old); File.SetLastWriteTimeUtc(file, old); File.SetLastAccessTimeUtc(file, old); }
                var scan = TempCleaner.Scan(new[] { root }, false, DateTime.UtcNow, System.Threading.CancellationToken.None);
                Assert(scan.Items.Count == 1 && scan.Items[0].Path == stale, "Only stale allowed extensions appear in preview.");
                var nested = Directory.CreateDirectory(Path.Combine(root, "app-cache"));
                var nestedFile = Path.Combine(nested.FullName, "nested.tmp");
                File.WriteAllText(nestedFile, "retain");
                File.SetCreationTimeUtc(nestedFile, old);
                File.SetLastWriteTimeUtc(nestedFile, old);
                File.SetLastAccessTimeUtc(nestedFile, old);
                var scoped = TempCleaner.Scan(new[] { root }, false, DateTime.UtcNow, System.Threading.CancellationToken.None, topLevelOnly: true);
                Assert(scoped.Items.Count == 1 && scoped.Items[0].Path == stale && scoped.Recent == 1, "Top-level scan must skip app folders and preserve recent files.");
                bool refused = false;
                try { TempCleaner.Scan(new[] { root }, true, DateTime.UtcNow, System.Threading.CancellationToken.None); }
                catch (InvalidOperationException) { refused = true; }
                Assert(refused && File.Exists(stale), "Scan cannot silently delete files.");
                var deleted = TempCleaner.CleanConfirmed(scan, System.Threading.CancellationToken.None);
                Assert(deleted.Files == 0 && File.Exists(stale) && File.Exists(document), "Even confirmed candidates outside owned roots cannot be deleted.");
            }
            finally { Directory.Delete(root, true); }
            return 19;
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static int Main() { try { Console.WriteLine("Safety tests: " + Run() + " passed"); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    }
}
