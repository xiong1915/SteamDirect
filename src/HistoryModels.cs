using System;
using System.Collections.Generic;
using System.Globalization;

namespace SteamDirect
{
    internal sealed class HistorySession
    {
        internal string Id, Game, Mode, Target, Report;
        internal long Start, End;
        internal bool Interrupted;
        internal bool HasAnomalies;
    }

    internal sealed class HistorySample
    {
        internal string SessionId, Mode, Gateway;
        internal long At;
        internal int GatewayMs, ExternalMs, Dns;
        internal bool AdapterUp;
    }

    internal sealed class HistoryEvent
    {
        internal string Id, SessionId, Kind;
        internal long Start, End;
    }

    internal sealed class HistoryTracker
    {
        private sealed class State
        {
            internal long Candidate, Recovery;
            internal int Bad, Good;
            internal HistoryEvent Active;
        }

        private readonly string session;
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();
        private readonly Queue<bool> attempts = new Queue<bool>();
        private string previousMode;
        private int losses, baselineCount;
        private double baseline;

        internal HistoryTracker(string sessionId) { session = sessionId; }

        internal void Observe(HistorySample sample, List<HistoryEvent> changes)
        {
            if (previousMode != sample.Mode)
            {
                attempts.Clear();
                losses = 0;
                baseline = 0;
                baselineCount = 0;
                previousMode = sample.Mode;
            }

            bool? local = !sample.AdapterUp ? (bool?)null : sample.GatewayMs == -1 && sample.ExternalMs == -1 ? (bool?)true :
                sample.GatewayMs >= 0 || sample.ExternalMs >= 0 ? (bool?)false : null;
            bool? external = sample.GatewayMs >= 0 && sample.ExternalMs == -1 ? (bool?)true : sample.ExternalMs >= 0 ? (bool?)false : null;
            bool? dns = sample.Dns >= 0 ? (bool?)(sample.Dns == 0) : null;
            bool? proxy = sample.Mode == "代理" && (external == true || dns == true) ? (bool?)true :
                sample.Mode != "代理" && sample.Mode != "未知" || sample.ExternalMs >= 0 && sample.Dns == 1 ? (bool?)false : null;

            bool? spike = null, loss = null;
            if (sample.ExternalMs >= -1)
            {
                bool missed = sample.ExternalMs == -1;
                attempts.Enqueue(missed);
                if (missed) losses++;
                if (attempts.Count > 12 && attempts.Dequeue()) losses--;
                if (attempts.Count >= 8) loss = losses >= 3;
                if (!missed)
                {
                    bool high = baselineCount >= 4 && sample.ExternalMs >= Math.Max(baseline + 80, baseline * 2.5);
                    spike = baselineCount >= 4 ? (bool?)high : null;
                    if (!high)
                    {
                        baseline = baselineCount == 0 ? sample.ExternalMs : baseline * 0.85 + sample.ExternalMs * 0.15;
                        if (baselineCount < 4) baselineCount++;
                    }
                }
            }

            ObserveState("网卡不可用", !sample.AdapterUp, sample.At, changes);
            ObserveState("本地链路探测异常", local, sample.At, changes);
            ObserveState("外部探测异常", external, sample.At, changes);
            ObserveState("DNS 异常", dns, sample.At, changes);
            ObserveState("代理模式下的异常", proxy, sample.At, changes);
            ObserveState("延迟突增", spike, sample.At, changes);
            ObserveState("探测丢包异常", loss, sample.At, changes);
        }

        internal void ObserveAdapter(bool available, long at, List<HistoryEvent> changes)
        {
            ObserveState("网卡不可用", !available, at, changes, 1);
        }

        private void ObserveState(string kind, bool? abnormal, long at, List<HistoryEvent> changes, int required = 2)
        {
            if (!abnormal.HasValue) return;
            State state;
            if (!states.TryGetValue(kind, out state)) states[kind] = state = new State();
            if (abnormal.Value)
            {
                state.Good = 0;
                if (state.Active != null) return;
                if (state.Bad++ == 0) state.Candidate = at;
                if (state.Bad < required) return;
                state.Active = new HistoryEvent { Id = Guid.NewGuid().ToString("N"), SessionId = session, Kind = kind, Start = state.Candidate };
                changes.Add(state.Active);
            }
            else
            {
                state.Bad = 0;
                if (state.Active == null) return;
                if (state.Good++ == 0) state.Recovery = at;
                if (state.Good < required) return;
                changes.Add(new HistoryEvent { Id = state.Active.Id, SessionId = session, Kind = kind, Start = state.Active.Start, End = state.Recovery });
                state.Active = null;
            }
        }
    }

    internal sealed class HistoryMetrics
    {
        internal int Attempts, Lost, JitterCount, P95;
        internal double Average, Jitter;
        internal double LossRate { get { return Attempts == 0 ? 0 : 100.0 * Lost / Attempts; } }
        internal string Describe()
        {
            int replies = Attempts - Lost;
            if (Attempts == 0) return "没有外部 ICMP 样本";
            string loss = string.Format(CultureInfo.InvariantCulture, "探测超时 {0}/{1}（{2:F1}%）", Lost, Attempts, LossRate);
            if (replies == 0) return loss + "，延迟与抖动不可用";
            return string.Format(CultureInfo.InvariantCulture, "{0}，平均 {1:F1} ms，P95 {2} ms，抖动 {3}",
                loss, Average, P95, JitterCount == 0 ? "样本不足" : Jitter.ToString("F1", CultureInfo.InvariantCulture) + " ms");
        }
    }

    internal static class HistoryStatistics
    {
        internal static HistoryMetrics Calculate(IEnumerable<HistorySample> samples)
        {
            var result = new HistoryMetrics();
            var values = new List<int>();
            long sum = 0, movement = 0;
            HistorySample last = null;
            foreach (var sample in samples)
            {
                if (sample.ExternalMs >= -1) result.Attempts++;
                if (sample.ExternalMs == -1) result.Lost++;
                if (sample.ExternalMs >= 0)
                {
                    sum += sample.ExternalMs;
                    values.Add(sample.ExternalMs);
                    if (last != null && last.ExternalMs >= 0 && last.Mode == sample.Mode && sample.At > last.At && sample.At - last.At <= 25000)
                    {
                        movement += Math.Abs((long)sample.ExternalMs - last.ExternalMs);
                        result.JitterCount++;
                    }
                }
                last = sample;
            }
            if (values.Count > 0)
            {
                values.Sort();
                result.Average = (double)sum / values.Count;
                result.P95 = values[(int)Math.Ceiling(values.Count * .95) - 1];
            }
            if (result.JitterCount > 0) result.Jitter = (double)movement / result.JitterCount;
            return result;
        }
    }
}
