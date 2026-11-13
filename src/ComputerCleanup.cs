using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace SteamDirect
{
    internal sealed class TempCleanupResult
    {
        internal long Bytes;
        internal int Files, Scanned, Candidates, Recent, Protected, ScanSkipped, DeleteSkipped;
        internal bool Limited;
        internal readonly List<CleanupCandidate> Items = new List<CleanupCandidate>();

        internal string SizeText
        {
            get
            {
                if (Bytes < 1024) return Bytes + " B";
                if (Bytes < 1048576) return (Bytes / 1024.0).ToString("0.0") + " KiB";
                return (Bytes / 1048576.0).ToString("0.0") + " MiB";
            }
        }

        internal string Describe()
        {
            string message;
            if (Files > 0) message = "已清理 " + Files + " 个过期临时文件，共 " + SizeText;
            else if (Candidates > 0) message = "找到 " + Candidates + " 个过期临时文件，均未能删除";
            else message = "未找到符合条件的过期临时文件";

            if (Limited) message += "（本次扫描已达上限）";
            int skipped = ScanSkipped + DeleteSkipped + Protected;
            if (skipped > 0) message += "，跳过 " + skipped + " 项";
            return message + "。";
        }

        internal string Details()
        {
            return "已检查 " + Scanned + " 个文件。符合清理规则 " + Candidates + " 个，7 天内有创建、修改或访问 " + Recent +
                " 个，受保护 " + Protected + " 个，扫描跳过 " + ScanSkipped + " 项，删除失败或文件已变化 " + DeleteSkipped + " 个。";
        }
    }
    internal sealed class CleanupCandidate
    {
        internal string Root, Path;
        internal long Length;
        internal uint Volume, IndexHigh, IndexLow;
        internal DateTime Created, Accessed, Written;
    }
    internal static class ComputerStatus
    {
        [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
        {
            internal uint Length, Load;
            internal ulong Total, Available, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, Extended;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
        internal static uint MemoryPercent()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(ref status)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return status.Load;
        }
    }
    internal static class TempCleaner
    {
        private static int cleaning;
        [StructLayout(LayoutKind.Sequential)] private struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Disposition { internal byte Delete; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref Disposition information, uint size);
        internal static string[] Roots { get { return new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "SteamDirect") }; } }
        internal static bool IsInside(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(root) || !Path.IsPathRooted(path)) return false;
            try { return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
        internal static bool NoReparseAncestors(string path)
        {
            try
            {
                string current = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                    current = Path.GetDirectoryName(current);
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (ArgumentException) { return false; }
        }
        private static bool EligibleName(string path)
        {
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".tmp", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".temp", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".cache", StringComparison.OrdinalIgnoreCase);
        }
        private static DateTime FileTime(System.Runtime.InteropServices.ComTypes.FILETIME time)
        { return DateTime.FromFileTimeUtc(((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime); }
        private static bool DeleteEligible(CleanupCandidate candidate, DateTime cutoff, out long bytes)
        {
            bytes = 0;
            string root = candidate.Root, path = candidate.Path;
            if (Array.FindIndex(Roots, r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase)) < 0 ||
                !IsInside(root, path) || !EligibleName(path) || !NoReparseAncestors(path)) return false;
            // Request deletion rights and exclusive ownership, but do not mark for deletion until all checks pass.
            using (var handle = CreateFile(path, 0x10080, 0, IntPtr.Zero, 3, 0x200000, IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                FileInformation info;
                if (!GetFileInformationByHandle(handle, out info) || info.Links != 1 || (info.Attributes & (0x400 | 0x10 | 0x1 | 0x1000)) != 0) return false;
                long lengthOnDisk = ((long)info.SizeHigh << 32) | info.SizeLow;
                if (candidate.Volume != info.Volume || candidate.IndexHigh != info.IndexHigh || candidate.IndexLow != info.IndexLow ||
                    candidate.Length != lengthOnDisk || candidate.Created != FileTime(info.Created) ||
                    candidate.Written != FileTime(info.Written) || candidate.Accessed != FileTime(info.Accessed)) return false;
                if (FileTime(info.Created) >= cutoff || FileTime(info.Accessed) >= cutoff || FileTime(info.Written) >= cutoff) return false;
                var final = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
                if (length == 0 || length >= final.Capacity) return false;
                string resolved = final.ToString();
                if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved.Substring(4);
                string boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!resolved.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !string.Equals(resolved, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !EligibleName(resolved)) return false;
                var disposition = new Disposition { Delete = 1 };
                if (!SetFileInformationByHandle(handle, 4, ref disposition, 1)) return false;
                bytes = ((long)info.SizeHigh << 32) | info.SizeLow;
                return true;
            }
        }
        internal static TempCleanupResult Scan(string[] roots, bool delete, DateTime now, CancellationToken token)
        {
            if (delete) throw new InvalidOperationException("必须先扫描并确认具体文件清单，再执行清理。");
            var result = new TempCleanupResult(); var watch = Stopwatch.StartNew(); int inspected = 0;
            DateTime cutoff = now.ToUniversalTime().AddDays(-7);
            foreach (string root in roots)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(root)) continue;
                var pending = new Stack<string>(); pending.Push(root);
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (watch.ElapsedMilliseconds > 10000 || inspected > 15000) { result.Limited = true; return result; }
                    string directory = pending.Pop();
                    try
                    {
                        if (!NoReparseAncestors(directory)) { result.ScanSkipped++; continue; }
                        foreach (string child in Directory.EnumerateDirectories(directory)) { inspected++; if (watch.ElapsedMilliseconds > 10000 || inspected > 15000) { result.Limited = true; return result; } pending.Push(child); }
                        foreach (string file in Directory.EnumerateFiles(directory))
                        {
                            inspected++; token.ThrowIfCancellationRequested();
                            if (watch.ElapsedMilliseconds > 10000 || inspected > 15000) { result.Limited = true; return result; }
                            result.Scanned++;
                            if (!EligibleName(file)) continue;
                            try
                            {
                                var info = new FileInfo(file);
                                if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.Offline)) != 0)
                                {
                                    result.Protected++;
                                    continue;
                                }
                                if (info.LastWriteTimeUtc >= cutoff || info.CreationTimeUtc >= cutoff || info.LastAccessTimeUtc >= cutoff)
                                {
                                    result.Recent++;
                                    continue;
                                }
                                // Pin the preview to a file identity, not only a pathname and timestamps.
                                using (var snapshot = CreateFile(file, 0x80, 7, IntPtr.Zero, 3, 0x200000, IntPtr.Zero))
                                {
                                    FileInformation identity;
                                    if (snapshot.IsInvalid || !GetFileInformationByHandle(snapshot, out identity) || identity.Links != 1 ||
                                        (identity.Attributes & (0x400 | 0x10 | 0x1 | 0x1000)) != 0) { result.Protected++; continue; }
                                    if (FileTime(identity.Created) >= cutoff || FileTime(identity.Accessed) >= cutoff || FileTime(identity.Written) >= cutoff)
                                    { result.Recent++; continue; }
                                    long length = ((long)identity.SizeHigh << 32) | identity.SizeLow;
                                    result.Candidates++;
                                    result.Items.Add(new CleanupCandidate { Root = root, Path = file, Length = length,
                                        Volume = identity.Volume, IndexHigh = identity.IndexHigh, IndexLow = identity.IndexLow,
                                        Created = FileTime(identity.Created), Accessed = FileTime(identity.Accessed), Written = FileTime(identity.Written) });
                                    result.Bytes += length; result.Files++;
                                }
                            }
                            catch (IOException) { result.ScanSkipped++; }
                            catch (UnauthorizedAccessException) { result.ScanSkipped++; }
                        }
                    }
                    catch (IOException) { result.ScanSkipped++; }
                    catch (UnauthorizedAccessException) { result.ScanSkipped++; }
                }
            }
            return result;
        }
        internal static TempCleanupResult CleanConfirmed(TempCleanupResult preview, CancellationToken token)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            if (Interlocked.CompareExchange(ref cleaning, 1, 0) != 0) throw new InvalidOperationException("已有临时文件清理正在进行。");
            try
            {
                var result = new TempCleanupResult { Scanned = preview.Scanned, Candidates = preview.Items.Count, Limited = preview.Limited };
                foreach (var item in preview.Items)
                {
                    token.ThrowIfCancellationRequested();
                    long bytes;
                    if (DeleteEligible(item, DateTime.UtcNow.AddDays(-7), out bytes)) { result.Files++; result.Bytes += bytes; }
                    else result.DeleteSkipped++;
                }
                return result;
            }
            finally { Interlocked.Exchange(ref cleaning, 0); }
        }
    }
    internal sealed class CleanupPanel : UserControl
    {
        private readonly Label memory = new Label(), files = new Label(), note = new Label();
        private readonly Button clean = new RoundedButton();
        private readonly ToolTip details = new ToolTip();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Action<string> log;
        private bool busy, allowed = true;
        private int scanVersion;
        internal CleanupPanel(Action<string> saveLog)
        {
            log = saveLog; Dock = DockStyle.Fill; Margin = new Padding(0, UiStyle.Gap, 0, 0);
            var frame = UiStyle.Card(true);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.Space24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.ButtonHeight));
            var title = new Label { Text = "系统资源", Dock = DockStyle.Fill, Margin = Padding.Empty, ForeColor = AppTheme.Ink, Font = UiStyle.SectionTitle };
            layout.Controls.Add(title, 0, 0); layout.SetColumnSpan(title, 2);
            memory.Text = "内存占用：读取中"; files.Text = "过期临时文件：扫描中";
            memory.Dock = files.Dock = DockStyle.Fill;
            memory.Margin = files.Margin = Padding.Empty;
            memory.Font = files.Font = UiStyle.SectionTitle;
            layout.Controls.Add(memory, 0, 1); layout.Controls.Add(files, 1, 1);
            note.Text = "仅扫描 SteamDirect 自身临时目录；清理前须确认，内存占用仅供参考。";
            note.Dock = DockStyle.Fill; note.Margin = Padding.Empty; note.AutoEllipsis = true; note.Font = UiStyle.Caption; note.ForeColor = AppTheme.Muted;
            layout.Controls.Add(note, 0, 2); layout.SetColumnSpan(note, 2);
            clean.Text = "扫描过期临时文件"; clean.Dock = DockStyle.Fill;
            clean.Margin = Padding.Empty;
            AppTheme.ActionButton(clean, false);
            clean.Click += async (sender, args) => await CleanAsync(); layout.Controls.Add(clean, 0, 3); layout.SetColumnSpan(clean, 2);
            frame.Controls.Add(layout); Controls.Add(frame);
            Load += async (sender, args) => { UpdateMemory(); await RefreshEstimateAsync(); };
            timer.Interval = 5000; timer.Tick += (sender, args) => UpdateMemory(); timer.Start();
        }
        internal void AllowCleanup(bool value) { allowed = value; clean.Enabled = allowed && !busy; }
        private void UpdateMemory()
        {
            try { memory.Text = "内存占用：" + ComputerStatus.MemoryPercent() + "%"; }
            catch { memory.Text = "内存占用：不可用"; }
        }
        internal async Task RefreshEstimateAsync()
        {
            try
            {
                int version = ++scanVersion;
                var token = lifetime.Token;
                var result = await Task.Run(() => TempCleaner.Scan(TempCleaner.Roots, false, DateTime.UtcNow, token));
                if (!IsDisposed && version == scanVersion)
                {
                    files.Text = result.Files == 0 ? "过期临时文件：未找到" :
                        "可尝试清理：" + result.Files + " 个 · " + result.SizeText;
                    if (result.Limited) files.Text += "（部分扫描）";
                    details.SetToolTip(files, result.Details());
                }
            }
            catch (OperationCanceledException) { }
            catch { if (!IsDisposed) files.Text = "临时文件：扫描未完成"; }
        }
        private async Task CleanAsync()
        {
            if (busy || !allowed) return;
            busy = true; clean.Enabled = false; clean.Text = "正在扫描…";
            try
            {
                var token = lifetime.Token;
                var preview = await Task.Run(() => TempCleaner.Scan(TempCleaner.Roots, false, DateTime.UtcNow, token));
                if (IsDisposed || !allowed) return;
                files.Text = "扫描结果：" + preview.Files + " 个 · " + preview.SizeText;
                if (preview.Files == 0) { note.Text = "未找到符合条件的 SteamDirect 临时文件。"; return; }
                var listing = new StringBuilder();
                foreach (var item in preview.Items) listing.AppendLine(item.Path + "  (" + item.Length + " B)");
                using (var dialog = new Form { Text = "确认手动清理", Size = new Size(640, 440), AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new SizeF(96F, 96F), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false })
                {
                    var description = new Label { Dock = DockStyle.Top, Height = 90, Padding = new Padding(12), Text =
                        "将永久删除以下 " + preview.Files + " 个 SteamDirect 临时文件（" + preview.SizeText + "）。" +
                        "仅包含超过 7 天且未改变的 .tmp / .temp / .cache 文件。请核对清单；删除后不能撤销。" };
                    var list = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Text = listing.ToString() };
                    var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                    var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
                    var confirm = new Button { Text = "确认删除", DialogResult = DialogResult.OK };
                    buttons.Controls.Add(cancel); buttons.Controls.Add(confirm);
                    dialog.Controls.Add(list); dialog.Controls.Add(description); dialog.Controls.Add(buttons); dialog.CancelButton = cancel;
                    AppTheme.Apply(dialog);
                    if (dialog.ShowDialog(this) != DialogResult.OK || IsDisposed || !allowed) return;
                }
                clean.Text = "正在清理已确认文件…";
                var result = await Task.Run(() => TempCleaner.CleanConfirmed(preview, token));
                if (!IsDisposed)
                {
                    note.Text = result.Describe();
                    details.SetToolTip(note, result.Details());
                    log(result.Describe() + result.Details());
                    await RefreshEstimateAsync();
                    UpdateMemory();
                }
            }
            catch (Exception ex) { if (!IsDisposed) { note.Text = "清理未完成，部分文件已跳过。"; log("临时文件清理未完成：" + ex.Message); } }
            finally { busy = false; if (!IsDisposed) { clean.Enabled = allowed; clean.Text = "扫描过期临时文件"; } }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); details.Dispose(); lifetime.Cancel(); lifetime.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

