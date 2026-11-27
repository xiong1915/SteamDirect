using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace SteamDirect
{
    // One consumer per active burst. There is no resident thread or polling timer.
    internal sealed class OperationLog
    {
        internal const int Capacity = 512;
        private const int BatchSize = 64;
        private sealed class Entry
        {
            internal string Directory, Line;
            internal DateTime Day;
            internal int Retention, SkippedAfter;
        }

        private readonly Queue<Entry> pending = new Queue<Entry>();
        private readonly Action<string> report;
        private readonly Action<string, string> append;
        private Task worker = Task.FromResult(0);
        private bool draining, closed;
        private Entry tail;
        private string cleanedDirectory;
        private DateTime cleanedDay;
        private int cleanedRetention;

        internal OperationLog(Action<string> report, Action<string, string> append = null)
        {
            this.report = report;
            this.append = append ?? ((path, text) => File.AppendAllText(path, text, Encoding.UTF8));
        }

        internal void Enqueue(string directory, int retention, DateTime timestamp, string line)
        {
            lock (pending)
            {
                if (closed) return;
                if (pending.Count == Capacity)
                {
                    if (tail.SkippedAfter < int.MaxValue) tail.SkippedAfter++;
                    return;
                }
                tail = new Entry { Directory = directory, Retention = retention, Day = timestamp.Date, Line = line };
                pending.Enqueue(tail);
                if (draining) return;
                draining = true;
                worker = Task.Run((Action)Drain);
            }
        }

        internal Task WhenIdle() { lock (pending) return worker; }
        internal Task Complete() { lock (pending) { closed = true; return worker; } }

        private void Drain()
        {
            var text = new StringBuilder();
            for (;;)
            {
                Entry head;
                lock (pending)
                {
                    if (pending.Count == 0) { draining = false; tail = null; return; }
                    head = pending.Peek();
                    for (int i = 0; i < BatchSize && pending.Count != 0; i++)
                    {
                        Entry next = pending.Peek();
                        if (next.Directory != head.Directory || next.Day != head.Day || next.Retention != head.Retention) break;
                        pending.Dequeue();
                        text.AppendLine(next.Line);
                        if (next.SkippedAfter != 0) text.Append("日志队列已满，略过 ").Append(next.SkippedAfter).AppendLine(" 条。");
                    }
                }
                try
                {
                    Directory.CreateDirectory(head.Directory);
                    if (head.Directory != cleanedDirectory || head.Day != cleanedDay || head.Retention != cleanedRetention)
                    {
                        cleanedDirectory = head.Directory; cleanedDay = head.Day; cleanedRetention = head.Retention;
                        try { LogStore.Cleanup(head.Directory, head.Retention, head.Day); }
                        catch (Exception ex) { report("过期日志暂未清理：" + ex.Message); }
                    }
                    append(LogStore.PathForDay(head.Directory, head.Day), text.ToString());
                }
                catch (Exception ex) { report("日志写入失败：" + ex.Message); }
                text.Clear();
            }
        }
    }
}
