using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SteamDirect
{
    internal sealed class HistoryMonitor : IDisposable
    {
        private sealed class ActiveGame
        {
            internal HistorySession Session;
            internal HistoryTracker Tracker;
            internal long MissingAt;
            internal bool Sampled;
        }

        internal static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamDirect", "network-history.sqlite3");
        private readonly Func<List<SteamGameProcess>> scan;
        private readonly object lifecycle = new object();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Dictionary<string, ActiveGame> active = new Dictionary<string, ActiveGame>();
        private readonly Dictionary<string, HistorySession> pendingSessions = new Dictionary<string, HistorySession>();
        private readonly List<HistorySample> pendingSamples = new List<HistorySample>();
        private readonly List<HistoryEvent> pendingEvents = new List<HistoryEvent>();
        private readonly Dictionary<string, long> interruptedEnds = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Queue<HistorySession> reports = new Queue<HistorySession>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private long nextFlush, nextSample;
        private Task worker;
        private volatile bool playing;
        private volatile string error;
        private int disposed, started;
        private long pings, dns, batches;
        internal HistoryStore Store { get; private set; }
        internal bool IsPlaying { get { return playing; } }
        internal string Error { get { return error; } }
        internal long PingCount { get { return Interlocked.Read(ref pings); } }
        internal long DnsCount { get { return Interlocked.Read(ref dns); } }
        internal long BatchCount { get { return Interlocked.Read(ref batches); } }

        internal HistoryMonitor(string path, Func<List<SteamGameProcess>> discover = null)
        {
            Store = new HistoryStore(path);
            scan = discover ?? new SteamGameDiscovery().Scan;
        }

        internal void Start()
        {
            lock (lifecycle)
            {
                if (started != 0 || disposed != 0) return;
                started = 1;
                worker = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        internal static int Interval(double load) { return load >= .85 ? 20 : load >= .70 ? 10 : 5; }
        private static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        private void Work()
        {
            var token = stop.Token;
            bool initialized = false;
            try
            {
                using (var network = new HistoryNetwork())
                {
                    long retryAt = 0;
                    while (!token.IsCancellationRequested)
                    {
                        long cycle = clock.ElapsedMilliseconds;
                        try
                        {
                            if (!initialized && clock.ElapsedMilliseconds >= retryAt)
                            {
                                retryAt = clock.ElapsedMilliseconds + 60000;
                                Store.Initialize();
                                Store.RecoverInterrupted();
                                foreach (var item in Store.ListSessions())
                                {
                                    if (item.End > 0 && string.IsNullOrEmpty(item.Report)) reports.Enqueue(item);
                                    if (item.Interrupted && !interruptedEnds.ContainsKey(item.Game)) interruptedEnds[item.Game] = item.End;
                                }
                                initialized = true;
                            }
                            if (initialized)
                            {
                                bool changed = UpdateGames(scan(), Now());
                                foreach (var change in network.DrainLinkChanges())
                                    foreach (var game in active.Values)
                                        if (change.At >= game.Session.Start && (game.MissingAt == 0 || change.At <= game.MissingAt))
                                            game.Tracker.ObserveAdapter(change.Available, change.At, pendingEvents);
                                long tick = clock.ElapsedMilliseconds;
                                if (changed || tick >= nextFlush) Flush();
                                if (playing)
                                {
                                    double load = network.Load();
                                    if (cycle >= nextSample)
                                    {
                                        nextSample = cycle + Interval(load) * 1000L;
                                        try { Capture(network.Sample(Now(), token)); }
                                        finally
                                        {
                                            Interlocked.Exchange(ref pings, network.PingCount);
                                            Interlocked.Exchange(ref dns, network.DnsCount);
                                        }
                                    }
                                }
                                else nextSample = 0;

                                if (!playing && reports.Count > 0) WriteReport(reports.Peek());
                                error = null;
                            }
                        }
                        catch (OperationCanceledException) { if (token.IsCancellationRequested) break; error = "采样取消"; }
                        catch (Exception ex) { error = ex.Message; }
                        if (token.WaitHandle.WaitOne((int)Math.Max(1, 5000 - (clock.ElapsedMilliseconds - cycle)))) break;
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                if (initialized)
                {
                    foreach (var game in active.Values)
                    {
                        game.Session.Interrupted = true;
                        game.Session.End = Math.Max(game.Session.Start, Now());
                        pendingSessions[game.Session.Id] = game.Session;
                    }
                    try { Flush(); } catch (Exception ex) { error = ex.Message; }
                }
                playing = false;
            }
        }

        private bool UpdateGames(List<SteamGameProcess> games, long now)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool startedGame = false;
            foreach (var game in games)
            {
                if (!seen.Add(game.Key)) continue;
                ActiveGame item;
                if (active.TryGetValue(game.Key, out item)) { item.MissingAt = 0; continue; }
                if (pendingSessions.Count >= 256) continue;
                long start = game.StartedAt > 0 ? Math.Min(game.StartedAt, now) : now;
                long interruptedEnd;
                if (interruptedEnds.TryGetValue(game.Name, out interruptedEnd) && start <= interruptedEnd) start = now;
                var session = new HistorySession
                {
                    Id = Guid.NewGuid().ToString("N"), Game = game.Name, Target = HistoryNetwork.Target,
                    Start = start, Mode = "未知"
                };
                active.Add(game.Key, new ActiveGame { Session = session, Tracker = new HistoryTracker(session.Id) });
                pendingSessions[session.Id] = session;
                startedGame = true;
                if (active.Count == 1) { nextSample = 0; nextFlush = clock.ElapsedMilliseconds + 30000; }
            }
            var ended = new List<string>();
            foreach (var item in active)
            {
                if (seen.Contains(item.Key)) continue;
                if (item.Value.MissingAt == 0) { item.Value.MissingAt = now; continue; }
                var session = item.Value.Session;
                session.End = Math.Max(session.Start, item.Value.MissingAt);
                pendingSessions[session.Id] = session;
                if (reports.Count < 200) reports.Enqueue(session);
                ended.Add(item.Key);
            }
            foreach (string key in ended) active.Remove(key);
            playing = active.Count > 0 || seen.Count > 0;
            return startedGame || ended.Count > 0;
        }

        private void Capture(HistorySample probe)
        {
            foreach (var game in active.Values)
            {
                if (game.MissingAt > 0) continue;
                var session = game.Session;
                if (session.Mode != probe.Mode && session.Mode != "混合")
                {
                    session.Mode = game.Sampled ? "混合" : probe.Mode;
                    pendingSessions[session.Id] = session;
                }
                game.Sampled = true;
                var sample = new HistorySample
                {
                    SessionId = session.Id, At = probe.At, Mode = probe.Mode, Gateway = probe.Gateway,
                    GatewayMs = probe.GatewayMs, ExternalMs = probe.ExternalMs, Dns = probe.Dns, AdapterUp = probe.AdapterUp
                };
                pendingSamples.Add(sample);
                game.Tracker.Observe(sample, pendingEvents);
            }
            // Bound memory if local storage remains inaccessible.
            if (pendingSamples.Count > 2000) pendingSamples.RemoveRange(0, pendingSamples.Count - 2000);
            if (pendingEvents.Count > 1000) pendingEvents.RemoveRange(0, pendingEvents.Count - 1000);
        }

        private void Flush()
        {
            nextFlush = clock.ElapsedMilliseconds + 30000;
            if (pendingSessions.Count == 0 && pendingSamples.Count == 0 && pendingEvents.Count == 0) return;
            Store.WriteBatch(new List<HistorySession>(pendingSessions.Values), pendingSamples, pendingEvents);
            Interlocked.Increment(ref batches);
            pendingSessions.Clear();
            pendingSamples.Clear();
            pendingEvents.Clear();
        }

        private void WriteReport(HistorySession session)
        {
            // A retry must persist the pending final sample and session end before reporting.
            if (pendingSessions.Count > 0 || pendingSamples.Count > 0 || pendingEvents.Count > 0) return;
            var metrics = HistoryStatistics.Calculate(Store.ReadSamples(session.Id));
            var incidents = Store.ReadEvents(session.Id);
            int recovered = 0;
            long totalRecovery = 0;
            foreach (var incident in incidents)
            {
                if (incident.End <= 0) continue;
                recovered++;
                totalRecovery += Math.Max(0, incident.End - incident.Start);
            }
            string summary = "外部目标 " + session.Target + "：" + metrics.Describe() +
                "。异常 " + incidents.Count + " 次，观察到恢复 " + recovered + " 次" +
                (recovered == 0 ? "" : "，平均恢复 " + (totalRecovery / (1000.0 * recovered)).ToString("F1") + " 秒") + "。";
            if (session.Mode != "直连" && session.Mode != "代理") summary += "当前会话模式混合或未知，不能按单一模式对比。";
            else
            {
                HistorySession previous = null;
                foreach (var item in Store.ListSessions(200, 0, session.Game, session.Mode))
                {
                    if (item.Id != session.Id && item.End > 0 && item.End <= session.Start &&
                        !item.Interrupted && item.Game == session.Game && item.Mode == session.Mode && item.Target == session.Target)
                    { previous = item; break; }
                }
                if (previous == null || metrics.Attempts < 12) summary += "同游戏、同模式、同目标的历史样本不足。";
                else
                {
                    var earlier = HistoryStatistics.Calculate(Store.ReadSamples(previous.Id));
                    summary += earlier.Attempts < 12 ? "同模式历史样本不足。" : "最近同模式会话：" + earlier.Describe() + "。";
                }
            }
            session.Report = summary + " 指标来自 ICMP 探测。";
            Store.WriteBatch(new[] { session }, new HistorySample[0], new HistoryEvent[0]);
            Interlocked.Increment(ref batches);
            reports.Dequeue();
        }

        public void Dispose()
        {
            Task running;
            lock (lifecycle)
            {
                if (disposed != 0) return;
                disposed = 1;
                stop.Cancel();
                running = worker;
            }
            if (running == null) { stop.Dispose(); return; }
            if (running.Wait(6000)) stop.Dispose();
            else running.ContinueWith(t => stop.Dispose(), TaskScheduler.Default);
        }
    }
}

