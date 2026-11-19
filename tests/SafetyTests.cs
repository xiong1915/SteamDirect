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
            Assert(!FilePathSafety.IsInside(@"C:\Temp\SteamDirect", @"C:\Temp\SteamDirect-other\x.tmp"), "Reject sibling prefix.");
            Assert(!FilePathSafety.IsInside(@"C:\Temp\SteamDirect", @"C:\Temp\SteamDirect\..\personal.tmp"), "Reject parent traversal.");
            Assert(!FilePathSafety.IsInside(@"C:\Temp\SteamDirect", @"x.tmp"), "Reject relative cleanup paths.");
            Assert(FilePathSafety.IsInside(@"C:\Temp\SteamDirect", @"C:\Temp\SteamDirect\nested\x.tmp"), "Accept actual descendant.");
            Assert(ProxyAppShutdown.Identify(@"C:\Unknown\chrome.exe", ProxyAppShutdown.YouTubeAppId, @"C:\Local", @"C:\Program Files") == null, "App ID alone cannot trust executable.");
            string root = Path.Combine(Path.GetTempPath(), "SteamDirectSafetyTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                bool blocked = false;
                try { SteamTrust.OpenVerified("steam.exe").Dispose(); }
                catch (InvalidOperationException) { blocked = true; }
                Assert(blocked, "The Steam trust gate requires an absolute path.");
                string unsignedSteam = Path.Combine(root, "steam.exe");
                File.Copy(fixture, unsignedSteam);
                blocked = false;
                try { SteamTrust.OpenVerified(unsignedSteam).Dispose(); }
                catch (InvalidOperationException) { blocked = true; }
                Assert(blocked, "An unsigned executable under the expected name must be rejected.");
                var cleaner = new TempCleaner(root);
                var token = System.Threading.CancellationToken.None;
                string shared = Directory.CreateDirectory(Path.Combine(root, "Temp")).FullName;
                string owned = Directory.CreateDirectory(Path.Combine(shared, "SteamDirect")).FullName;
                string nested = Directory.CreateDirectory(Path.Combine(owned, "cache")).FullName;
                string stale = Path.Combine(owned, "stale.tmp");
                string recent = Path.Combine(owned, "recent.tmp");
                string document = Path.Combine(owned, "personal.txt");
                string privateTemp = Path.Combine(shared, "not-steamdirect.tmp");
                string nestedFile = Path.Combine(nested, "nested.cache");
                foreach (string file in new[] { stale, recent, document, privateTemp, nestedFile })
                    File.WriteAllText(file, "fixture");
                var old = DateTime.UtcNow.AddDays(-10);
                foreach (string file in new[] { stale, document, privateTemp, nestedFile })
                { File.SetCreationTimeUtc(file, old); File.SetLastWriteTimeUtc(file, old); File.SetLastAccessTimeUtc(file, old); }
                var scan = cleaner.Scan(DateTime.UtcNow, token);
                Assert(scan.Scanned == 4 && scan.Items.Count == 2 && scan.Recent == 1, "Scan includes only the owned root, with nested files and retention.");
                Assert(scan.Items.Exists(item => item.Path == stale) && scan.Items.Exists(item => item.Path == nestedFile),
                    "Preview contains the eligible root and nested files.");
                Assert(File.Exists(stale) && File.Exists(nestedFile), "Scanning must leave previewed files intact.");
                File.AppendAllText(nestedFile, "changed after preview");
                scan.Items.Add(new CleanupCandidate { Path = privateTemp });
                var deleted = cleaner.CleanConfirmed(scan, token);
                Assert(deleted.Files == 1 && !File.Exists(stale), "Only the unchanged confirmed owned file was cleaned.");
                Assert(deleted.DeleteSkipped == 2 && File.Exists(privateTemp) && File.Exists(nestedFile), "Changed and out-of-scope files must remain intact.");
                Assert(File.Exists(recent) && File.Exists(document), "Recent files and unrelated extensions must be retained.");
            }
            finally { Directory.Delete(root, true); }
            return 23;
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static int Main() { try { Console.WriteLine("Safety tests: " + Run() + " passed"); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    }
}
