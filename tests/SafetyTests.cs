using System;
using System.Collections.Generic;
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
                    ms => elapsed += ms, () => elapsed, s => { });
                throw new Exception("Expected exit timeout.");
            }
            catch (InvalidOperationException) { }
            Assert(elapsed == 8000, "Leave unsaved ChatGPT running and stop workflow on graceful-exit timeout.");
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Apps\FlClash" };
            Assert(!FlClashShutdown.IsAllowed("FlClash", @"C:\Apps\FlClash2\FlClash.exe", folders), "Reject sibling prefix.");
            Assert(!FlClashShutdown.IsAllowed("Other", @"C:\Apps\FlClash\Other.exe", folders), "Reject unrelated process.");
            Assert(ProxyAppShutdown.Identify(@"C:\Unknown\ChatGPT.exe", null, @"C:\Users\u\AppData\Local", @"C:\Program Files") == null, "Reject same-name app outside trusted root.");
            Assert(ProxyAppShutdown.SameIdentity(42, 100, @"C:\Apps\x.exe", 42, 100, @"c:\apps\x.exe"), "Identity match accepts path casing.");
            Assert(!ProxyAppShutdown.SameIdentity(42, 100, @"C:\Apps\x.exe", 42, 101, @"C:\Apps\x.exe"), "Reject PID reuse.");
            Assert(!ProxyAppShutdown.SameIdentity(42, 100, @"C:\Apps\x.exe", 42, 100, @"C:\Apps\y.exe"), "Reject replaced executable path.");
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
                bool refused = false;
                try { TempCleaner.Scan(new[] { root }, true, DateTime.UtcNow, System.Threading.CancellationToken.None); }
                catch (InvalidOperationException) { refused = true; }
                Assert(refused && File.Exists(stale), "Scan cannot silently delete files.");
                var deleted = TempCleaner.CleanConfirmed(scan, System.Threading.CancellationToken.None);
                Assert(deleted.Files == 0 && File.Exists(stale) && File.Exists(document), "Even confirmed candidates outside owned roots cannot be deleted.");
            }
            finally { Directory.Delete(root, true); }
            return 15;
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static int Main() { try { Console.WriteLine("Safety tests: " + Run() + " passed"); return 0; } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; } }
    }
}
