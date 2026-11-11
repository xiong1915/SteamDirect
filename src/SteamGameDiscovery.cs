using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SteamDirect
{
    internal sealed class SteamGameProcess
    {
        internal string Key;
        internal string Name;
        internal long StartedAt;
    }

    // Steam manifests provide ownership, not whether an executable is an interactive game.
    // The session is observed while one or more executables owned by the same AppID remain alive.
    // Steam tools installed as apps may therefore be recorded. Very short launches between polls can be missed.
    internal sealed class SteamGameDiscovery
    {
        private const uint ProcessSnapshot = 0x00000002;
        private const uint QueryLimitedInformation = 0x1000;
        private const int NoMoreFiles = 18;
        private const long FileTimeUnixOffsetMs = 11644473600000L;
        private const long LibraryRefreshMs = 120000;
        private const long NegativeRefreshMs = 30000;

        private static readonly Regex CurrentLibraryPath = new Regex("\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LegacyLibraryPath = new Regex("^[ \\t]*\"[0-9]+\"[ \\t]+\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex ManifestField = new Regex("^[ \\t]*\"(appid|name|installdir)\"[ \\t]*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex VdfEscape = new Regex("\\\\([\"\\\\])", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> NonGameExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam.exe", "steamwebhelper.exe", "gameoverlayui.exe", "crashpad_handler.exe",
            "UnityCrashHandler64.exe", "UnityCrashHandler32.exe", "CrashReportClient.exe", "CrashReportClient-Win64-Shipping.exe"
        };

        private sealed class GameFolder
        {
            internal string AppId;
            internal string Name;
            internal string Prefix;
        }

        private sealed class CachedProcess
        {
            internal string Executable;
            internal long Creation;
            internal long RetryAt;
            internal bool HasPath;
            internal GameFolder Game;
        }

        private sealed class ObservedGame
        {
            internal string Name;
            internal long EarliestCreation = long.MaxValue;
            internal uint EarliestPid;
            internal readonly Dictionary<uint, long> Processes = new Dictionary<uint, long>();
            internal SteamGameProcess Session;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            internal uint Size, Usage, Id;
            internal IntPtr DefaultHeap;
            internal uint ModuleId, ThreadCount, ParentId;
            internal int BasePriority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle OpenProcess(uint access, bool inheritHandle, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeFileHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        private readonly string fixtureRoot;
        private readonly uint desktopSession;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly StringBuilder imagePath = new StringBuilder(1024);
        private Dictionary<uint, CachedProcess> cached = new Dictionary<uint, CachedProcess>();
        private Dictionary<string, ObservedGame> running = new Dictionary<string, ObservedGame>(StringComparer.Ordinal);
        private List<GameFolder> folders = new List<GameFolder>();
        private long refreshAt;

        internal SteamGameDiscovery(string steamRoot = null)
        {
            fixtureRoot = steamRoot;
            using (var current = Process.GetCurrentProcess()) desktopSession = (uint)current.SessionId;
        }

        internal List<SteamGameProcess> Scan()
        {
            long now = clock.ElapsedMilliseconds;
            // Defer reading library metadata during observed game sessions. The process snapshot is independent of disk.
            if (now >= refreshAt && running.Count == 0)
            {
                folders = ReadLibrary();
                cached.Clear();
                refreshAt = now + LibraryRefreshMs;
            }
            var sessions = new List<SteamGameProcess>();
            if (folders.Count == 0) return sessions;

            var current = new Dictionary<string, ObservedGame>(StringComparer.Ordinal);
            var nextCache = new Dictionary<uint, CachedProcess>(cached.Count);
            using (var snapshot = CreateToolhelp32Snapshot(ProcessSnapshot, 0))
            {
                if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                do
                {
                    if (entry.Id == 0 || string.IsNullOrEmpty(entry.Executable) || NonGameExecutables.Contains(entry.Executable)) continue;
                    CachedProcess prior;
                    cached.TryGetValue(entry.Id, out prior);
                    if (prior != null && !string.Equals(prior.Executable, entry.Executable, StringComparison.OrdinalIgnoreCase)) prior = null;
                    var state = prior != null && prior.Game == null && now < prior.RetryAt ? prior : Resolve(entry, prior, now);
                    nextCache[entry.Id] = state;
                    if (state.Game == null) continue;

                    ObservedGame game;
                    if (!current.TryGetValue(state.Game.AppId, out game))
                    {
                        game = new ObservedGame { Name = state.Game.Name };
                        current.Add(state.Game.AppId, game);
                    }
                    game.Processes[entry.Id] = state.Creation;
                    if (state.Creation < game.EarliestCreation || (state.Creation == game.EarliestCreation && entry.Id < game.EarliestPid))
                    {
                        game.EarliestCreation = state.Creation;
                        game.EarliestPid = entry.Id;
                    }
                } while (Process32Next(snapshot, ref entry));
                int lastError = Marshal.GetLastWin32Error();
                if (lastError != NoMoreFiles) throw new Win32Exception(lastError);
            }

            foreach (var pair in current)
            {
                ObservedGame previous;
                var observed = pair.Value;
                if (running.TryGetValue(pair.Key, out previous) && SharesProcess(previous, observed)) observed.Session = previous.Session;
                else observed.Session = new SteamGameProcess
                {
                    Key = pair.Key + ":" + observed.EarliestPid.ToString(CultureInfo.InvariantCulture) + ":" + observed.EarliestCreation.ToString(CultureInfo.InvariantCulture),
                    Name = observed.Name,
                    StartedAt = observed.EarliestCreation / 10000L - FileTimeUnixOffsetMs
                };
                sessions.Add(observed.Session);
            }
            running = current;
            cached = nextCache;
            return sessions;
        }

        private static bool SharesProcess(ObservedGame previous, ObservedGame current)
        {
            foreach (var pair in current.Processes)
            {
                long created;
                if (previous.Processes.TryGetValue(pair.Key, out created) && created == pair.Value) return true;
            }
            return false;
        }

        private CachedProcess Resolve(ProcessEntry entry, CachedProcess prior, long now)
        {
            var negative = new CachedProcess { Executable = entry.Executable, RetryAt = now + NegativeRefreshMs };
            uint session;
            if (!ProcessIdToSessionId(entry.Id, out session) || session != desktopSession) return negative;
            using (var process = OpenProcess(QueryLimitedInformation, false, entry.Id))
            {
                if (process.IsInvalid) return negative;
                long created, exited, kernel, user;
                // Exit time is undefined for a running process. Toolhelp supplies the observed live process set.
                if (!GetProcessTimes(process, out created, out exited, out kernel, out user) || created / 10000L <= FileTimeUnixOffsetMs) return negative;
                if (prior != null && created == prior.Creation && prior.HasPath)
                {
                    prior.RetryAt = now + NegativeRefreshMs;
                    return prior;
                }
                negative.Creation = created;
                uint size = (uint)imagePath.Capacity;
                imagePath.Length = 0;
                if (!QueryFullProcessImageName(process, 0, imagePath, ref size))
                {
                    if (Marshal.GetLastWin32Error() != 122 || imagePath.Capacity >= 32768) return negative;
                    imagePath.Capacity = 32768;
                    size = (uint)imagePath.Capacity;
                    if (!QueryFullProcessImageName(process, 0, imagePath, ref size)) return negative;
                }
                string path = imagePath.ToString();
                // A reused PID can point at a different image after the Toolhelp snapshot.
                if (!string.Equals(Path.GetFileName(path), entry.Executable, StringComparison.OrdinalIgnoreCase)) return negative;
                negative.HasPath = true;
                foreach (var folder in folders)
                {
                    if (!path.StartsWith(folder.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    negative.Game = folder;
                    break;
                }
                return negative;
            }
        }

        private static string SteamRoot()
        {
            using (var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (steam != null)
                {
                    var path = steam.GetValue("SteamPath") as string;
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                    var executable = steam.GetValue("SteamExe") as string;
                    if (!string.IsNullOrWhiteSpace(executable)) return Path.GetDirectoryName(executable);
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        }

        private List<GameFolder> ReadLibrary()
        {
            var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddLibrary(libraries, fixtureRoot ?? SteamRoot());
            foreach (string root in new List<string>(libraries))
            {
                string list = ReadSmallFile(Path.Combine(root, "steamapps", "libraryfolders.vdf"), 2 * 1024 * 1024);
                if (list == null) continue;
                Match match = CurrentLibraryPath.Match(list);
                if (match.Success)
                {
                    for (; match.Success; match = match.NextMatch()) AddLibrary(libraries, Decode(match.Groups[1].Value));
                }
                else
                {
                    foreach (Match legacy in LegacyLibraryPath.Matches(list)) AddLibrary(libraries, Decode(legacy.Groups[1].Value));
                }
            }
            var games = new List<GameFolder>();
            foreach (string library in libraries)
            {
                string steamApps = Path.Combine(library, "steamapps");
                string common = Path.Combine(steamApps, "common") + Path.DirectorySeparatorChar;
                string[] manifests;
                try { manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string manifest in manifests)
                {
                    string content = ReadSmallFile(manifest, 131072);
                    if (content == null) continue;
                    string appId = null, name = null, installDir = null;
                    foreach (Match match in ManifestField.Matches(content))
                    {
                        string key = match.Groups[1].Value.ToLowerInvariant();
                        string value = Decode(match.Groups[2].Value);
                        if (key == "appid" && appId == null) appId = value;
                        if (key == "name" && name == null) name = value;
                        if (key == "installdir" && installDir == null) installDir = value;
                    }
                    uint number;
                    if (!uint.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number == 0 || number == 431960 || number == 228980 || number == 250820 ||
                        !string.Equals(Path.GetFileName(manifest), "appmanifest_" + appId + ".acf", StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(installDir) || installDir.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || installDir == "." || installDir == "..") continue;
                    games.Add(new GameFolder
                    {
                        AppId = appId,
                        Name = string.IsNullOrWhiteSpace(name) ? "Steam App " + appId : name,
                        Prefix = Path.Combine(common, installDir) + Path.DirectorySeparatorChar
                    });
                }
            }
            return games;
        }

        private static string Decode(string value) { return VdfEscape.Replace(value, "$1"); }

        private static void AddLibrary(HashSet<string> libraries, string path)
        {
            // Only local drive paths. Avoid opening an offline UNC share from the polling worker.
            if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return;
            try
            {
                string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Length == 2) full += Path.DirectorySeparatorChar;
                if (new DriveInfo(Path.GetPathRoot(full)).DriveType != DriveType.Network) libraries.Add(full);
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (NotSupportedException) { }
        }

        private static string ReadSmallFile(string path, int maximum)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > maximum) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        var content = new StringBuilder();
                        var buffer = new char[4096];
                        int read;
                        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (content.Length + read > maximum) return null;
                            content.Append(buffer, 0, read);
                        }
                        return content.ToString();
                    }
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}

