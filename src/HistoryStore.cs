using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamDirect
{
    internal sealed class HistoryStore
    {
        private readonly string path;

        internal HistoryStore(string path)
        {
            this.path = Path.GetFullPath(path);
        }

        internal void Initialize()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var db = new Connection(path, true))
            {
                db.Execute("PRAGMA journal_mode=WAL");
                db.Execute(@"
CREATE TABLE IF NOT EXISTS history_sessions (
    id TEXT PRIMARY KEY NOT NULL,
    game TEXT NOT NULL,
    mode TEXT NOT NULL,
    target TEXT,
    start_ms INTEGER NOT NULL,
    end_ms INTEGER NOT NULL,
    interrupted INTEGER NOT NULL,
    report TEXT
);
CREATE INDEX IF NOT EXISTS history_sessions_start ON history_sessions(start_ms DESC);
CREATE TABLE IF NOT EXISTS history_samples (
    session_id TEXT NOT NULL REFERENCES history_sessions(id) ON DELETE CASCADE,
    at_ms INTEGER NOT NULL,
    mode TEXT NOT NULL,
    gateway TEXT,
    gateway_ms INTEGER NOT NULL,
    external_ms INTEGER NOT NULL,
    adapter_up INTEGER NOT NULL,
    dns INTEGER NOT NULL,
    PRIMARY KEY (session_id, at_ms)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS history_events (
    id TEXT PRIMARY KEY NOT NULL,
    session_id TEXT NOT NULL REFERENCES history_sessions(id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    start_ms INTEGER NOT NULL,
    end_ms INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS history_events_session_start ON history_events(session_id, start_ms);");
            }
        }

        internal void RecoverInterrupted()
        {
            using (var db = new Connection(path, true))
                db.Execute(@"UPDATE history_sessions
SET end_ms = MAX(start_ms, COALESCE((SELECT MAX(at_ms) FROM history_samples WHERE session_id = history_sessions.id), start_ms)),
    interrupted = 1
WHERE end_ms = 0");
        }

        internal void WriteBatch(IList<HistorySession> sessions, IList<HistorySample> samples, IList<HistoryEvent> events)
        {
            if ((sessions == null || sessions.Count == 0) && (samples == null || samples.Count == 0) && (events == null || events.Count == 0)) return;
            using (var db = new Connection(path, true))
            {
                db.Execute("BEGIN IMMEDIATE");
                try
                {
                    WriteSessions(db, sessions);
                    WriteSamples(db, samples);
                    WriteEvents(db, events);
                    db.Execute("COMMIT");
                }
                catch
                {
                    try { db.Execute("ROLLBACK"); }
                    catch (InvalidOperationException) { }
                    throw;
                }
            }
        }

        private static void WriteSessions(Connection db, IList<HistorySession> sessions)
        {
            if (sessions == null || sessions.Count == 0) return;
            using (var statement = db.Prepare(@"
INSERT INTO history_sessions(id,game,mode,target,start_ms,end_ms,interrupted,report) VALUES (?,?,?,?,?,?,?,?)
ON CONFLICT(id) DO UPDATE SET game=excluded.game, mode=excluded.mode, target=excluded.target,
    start_ms=excluded.start_ms, end_ms=excluded.end_ms, interrupted=excluded.interrupted,
    report=COALESCE(excluded.report,history_sessions.report)"))
            {
                foreach (var session in sessions)
                {
                    statement.Text(1, session.Id);
                    statement.Text(2, session.Game);
                    statement.Text(3, session.Mode);
                    statement.Text(4, session.Target);
                    statement.Number(5, session.Start);
                    statement.Number(6, session.End);
                    statement.Number(7, session.Interrupted ? 1 : 0);
                    statement.Text(8, session.Report);
                    statement.Write();
                }
            }
        }

        private static void WriteSamples(Connection db, IList<HistorySample> samples)
        {
            if (samples == null || samples.Count == 0) return;
            using (var statement = db.Prepare(@"
INSERT INTO history_samples(session_id,at_ms,mode,gateway,gateway_ms,external_ms,adapter_up,dns) VALUES (?,?,?,?,?,?,?,?)
ON CONFLICT(session_id,at_ms) DO UPDATE SET mode=excluded.mode, gateway=excluded.gateway,
    gateway_ms=excluded.gateway_ms, external_ms=excluded.external_ms, adapter_up=excluded.adapter_up, dns=excluded.dns"))
            {
                foreach (var sample in samples)
                {
                    statement.Text(1, sample.SessionId);
                    statement.Number(2, sample.At);
                    statement.Text(3, sample.Mode);
                    statement.Text(4, sample.Gateway);
                    statement.Number(5, sample.GatewayMs);
                    statement.Number(6, sample.ExternalMs);
                    statement.Number(7, sample.AdapterUp ? 1 : 0);
                    statement.Number(8, sample.Dns);
                    statement.Write();
                }
            }
        }

        private static void WriteEvents(Connection db, IList<HistoryEvent> events)
        {
            if (events == null || events.Count == 0) return;
            using (var statement = db.Prepare(@"
INSERT INTO history_events(id,session_id,kind,start_ms,end_ms) VALUES (?,?,?,?,?)
ON CONFLICT(id) DO UPDATE SET session_id=excluded.session_id, kind=excluded.kind,
    start_ms=excluded.start_ms, end_ms=excluded.end_ms"))
            {
                foreach (var issue in events)
                {
                    statement.Text(1, issue.Id);
                    statement.Text(2, issue.SessionId);
                    statement.Text(3, issue.Kind);
                    statement.Number(4, issue.Start);
                    statement.Number(5, issue.End);
                    statement.Write();
                }
            }
        }

        internal List<HistorySession> ListSessions(int limit = 200, int offset = 0, string game = null, string mode = null)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException("limit");
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");
            var sessions = new List<HistorySession>();
            using (var db = new Connection(path, false))
            using (var statement = db.Prepare(@"SELECT id,game,mode,target,start_ms,end_ms,interrupted,report
FROM history_sessions WHERE INSTR(LOWER(game), LOWER(?)) > 0 AND mode = COALESCE(?, mode)
ORDER BY start_ms DESC, id DESC LIMIT ? OFFSET ?"))
            {
                statement.Text(1, game ?? "");
                statement.Text(2, mode);
                statement.Number(3, limit);
                statement.Number(4, offset);
                while (statement.Read())
                {
                    sessions.Add(new HistorySession {
                        Id = statement.Text(0), Game = statement.Text(1), Mode = statement.Text(2), Target = statement.Text(3),
                        Start = statement.Number(4), End = statement.Number(5), Interrupted = statement.Number(6) != 0,
                        Report = statement.Text(7)
                    });
                }
            }
            return sessions;
        }

        internal List<HistorySession> ListComparisonSessions(HistorySession selected, string mode, int limit)
        {
            if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException("limit");
            var sessions = new List<HistorySession>();
            using (var db = new Connection(path, false))
            using (var statement = db.Prepare(@"SELECT id,game,mode,target,start_ms,end_ms,interrupted,report
FROM history_sessions WHERE game = ? COLLATE NOCASE AND target = ? COLLATE NOCASE
AND (mode = ? OR mode = '混合') AND interrupted = 0
AND end_ms > 0 AND end_ms <= ? AND id <> ?
ORDER BY start_ms DESC, id DESC LIMIT ?"))
            {
                statement.Text(1, selected.Game);
                statement.Text(2, selected.Target);
                statement.Text(3, mode);
                statement.Number(4, selected.Start);
                statement.Text(5, selected.Id);
                statement.Number(6, limit);
                while (statement.Read())
                    sessions.Add(new HistorySession {
                        Id = statement.Text(0), Game = statement.Text(1), Mode = statement.Text(2), Target = statement.Text(3),
                        Start = statement.Number(4), End = statement.Number(5), Interrupted = statement.Number(6) != 0,
                        Report = statement.Text(7)
                    });
            }
            return sessions;
        }

        internal List<HistorySample> ReadSamples(string sessionId)
        {
            return ReadSamplePage(sessionId, 0, int.MaxValue);
        }

        internal List<HistorySample> ReadSamplePage(string sessionId, int offset, int limit = 200)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");
            if (limit < 1) throw new ArgumentOutOfRangeException("limit");
            var samples = new List<HistorySample>();
            using (var db = new Connection(path, false))
            using (var statement = db.Prepare(@"SELECT at_ms,mode,gateway,gateway_ms,external_ms,adapter_up,dns
FROM history_samples WHERE session_id = ? ORDER BY at_ms LIMIT ? OFFSET ?"))
            {
                statement.Text(1, sessionId);
                statement.Number(2, limit);
                statement.Number(3, offset);
                while (statement.Read())
                {
                    samples.Add(new HistorySample {
                        SessionId = sessionId, At = statement.Number(0), Mode = statement.Text(1), Gateway = statement.Text(2),
                        GatewayMs = (int)statement.Number(3), ExternalMs = (int)statement.Number(4),
                        AdapterUp = statement.Number(5) != 0, Dns = (int)statement.Number(6)
                    });
                }
            }
            return samples;
        }

        internal List<HistoryEvent> ReadEvents(string sessionId)
        {
            var events = new List<HistoryEvent>();
            using (var db = new Connection(path, false))
            using (var statement = db.Prepare(@"SELECT id,kind,start_ms,end_ms
FROM history_events WHERE session_id = ? ORDER BY start_ms, id"))
            {
                statement.Text(1, sessionId);
                while (statement.Read())
                {
                    events.Add(new HistoryEvent {
                        SessionId = sessionId, Id = statement.Text(0), Kind = statement.Text(1),
                        Start = statement.Number(2), End = statement.Number(3)
                    });
                }
            }
            return events;
        }

        private sealed class Connection : IDisposable
        {
            private IntPtr handle;

            internal Connection(string path, bool writable)
            {
                int status = Native.sqlite3_open_v2(Utf8(path), out handle, writable ? 0x06 : 0x01, IntPtr.Zero);
                try
                {
                    Check(status);
                    Check(Native.sqlite3_busy_timeout(handle, 2500));
                    if (writable)
                    {
                        Execute("PRAGMA foreign_keys=ON");
                        Execute("PRAGMA synchronous=NORMAL");
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal void Check(int status)
            {
                if (status == 0) return;
                string detail = handle == IntPtr.Zero ? "未能打开数据库" : ReadUtf8(Native.sqlite3_errmsg(handle), -1);
                throw new InvalidOperationException("SQLite " + status + "：" + detail);
            }

            internal void Execute(string sql)
            {
                Check(Native.sqlite3_exec(handle, Utf8(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            }

            internal Statement Prepare(string sql)
            {
                IntPtr statement;
                Check(Native.sqlite3_prepare_v2(handle, Utf8(sql), -1, out statement, IntPtr.Zero));
                return new Statement(this, statement);
            }

            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                Native.sqlite3_close_v2(handle);
                handle = IntPtr.Zero;
            }
        }

        private sealed class Statement : IDisposable
        {
            private readonly Connection connection;
            private IntPtr handle;

            internal Statement(Connection connection, IntPtr handle)
            {
                this.connection = connection;
                this.handle = handle;
            }

            internal void Text(int index, string value)
            {
                if (value == null) connection.Check(Native.sqlite3_bind_null(handle, index));
                else
                {
                    byte[] bytes = Utf8(value);
                    connection.Check(Native.sqlite3_bind_text(handle, index, bytes, bytes.Length - 1, new IntPtr(-1)));
                }
            }

            internal void Number(int index, long value)
            {
                connection.Check(Native.sqlite3_bind_int64(handle, index, value));
            }

            internal string Text(int column)
            {
                IntPtr value = Native.sqlite3_column_text(handle, column);
                return ReadUtf8(value, Native.sqlite3_column_bytes(handle, column));
            }

            internal long Number(int column)
            {
                return Native.sqlite3_column_int64(handle, column);
            }

            internal bool Read()
            {
                int status = Native.sqlite3_step(handle);
                if (status == 100) return true;
                if (status == 101) return false;
                connection.Check(status);
                return false;
            }

            internal void Write()
            {
                int status = Native.sqlite3_step(handle);
                if (status != 101) connection.Check(status);
                connection.Check(Native.sqlite3_reset(handle));
            }

            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                Native.sqlite3_finalize(handle);
                handle = IntPtr.Zero;
            }
        }

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text + '\0');
        }

        private static string ReadUtf8(IntPtr value, int size)
        {
            if (value == IntPtr.Zero) return null;
            if (size < 0)
            {
                size = 0;
                while (Marshal.ReadByte(value, size) != 0) size++;
            }
            if (size == 0) return string.Empty;
            byte[] bytes = new byte[size];
            Marshal.Copy(value, bytes, 0, size);
            return Encoding.UTF8.GetString(bytes);
        }

        private static class Native
        {
            private const string Library = "winsqlite3.dll";
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr state, IntPtr error);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int size, out IntPtr statement, IntPtr tail);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int size, IntPtr destructor);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_null(IntPtr statement, int index);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_step(IntPtr statement);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_reset(IntPtr statement);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_column_bytes(IntPtr statement, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern long sqlite3_column_int64(IntPtr statement, int column);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr sqlite3_errmsg(IntPtr db);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_finalize(IntPtr statement);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int sqlite3_close_v2(IntPtr db);
        }
    }
}
