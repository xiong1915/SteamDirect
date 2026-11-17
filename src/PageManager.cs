using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamDirect
{
    internal interface IManagedPage
    {
        void EnterPage();
        void LeavePage();
    }

    internal sealed class PageHost : Panel
    {
        internal Control ActivePage;
        internal System.Drawing.Rectangle PageBounds { get { var r=DisplayRectangle;int width=Math.Min(r.Width,UiStyle.Scale(this,1160));return new System.Drawing.Rectangle(r.X+(r.Width-width)/2,r.Y,width,r.Height); } }
        internal PageHost() { Dock = DockStyle.Fill; DoubleBuffered = true; }
        protected override void OnPaintBackground(PaintEventArgs args) { if (!Wallpaper.Paint(this, args.Graphics)) base.OnPaintBackground(args); }
        protected override void OnLayout(LayoutEventArgs args)
        {
            base.OnLayout(args);
            if (ActivePage != null && !ActivePage.IsDisposed && ActivePage.Bounds != PageBounds) ActivePage.Bounds = PageBounds;
        }
    }

    internal sealed class RedrawBatch : IDisposable
    {
        private readonly Control control;
        private IntPtr handle;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr region, uint flags);
        internal RedrawBatch(Control control)
        {
            this.control = control;
            // Never re-enable redraw on an originally hidden HWND: WM_SETREDRAW would show it.
            if (control.Visible && control.IsHandleCreated) { handle = control.Handle; SendMessage(handle, 0x000B, IntPtr.Zero, IntPtr.Zero); }
        }
        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            IntPtr saved = handle; handle = IntPtr.Zero;
            if (control.IsDisposed || !control.IsHandleCreated || control.Handle != saved) return;
            SendMessage(saved, 0x000B, new IntPtr(1), IntPtr.Zero);
            // Queue one repaint; do not synchronously paint every child during navigation/resize.
            RedrawWindow(saved, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0400);
        }
    }

    // UI-thread owned. The old page stays alive and covers preparation of the next page.
    internal sealed class PageManager : IDisposable
    {
        private readonly Control host, overview;
        private readonly Dictionary<string, Form> pages = new Dictionary<string, Form>();
        private Control current;
        private string currentName = "概览";
        private bool switching, disposed;
        internal bool DiagnosticsEnabled;
        internal string LastMeasurement { get; private set; }
        internal Form ActivePage { get { return current as Form; } }
        internal int CachedCount { get { return pages.Count; } }

        internal PageManager(Control host, Control overview)
        {
            this.host = host; this.overview = overview; current = overview;
            var surface = host as PageHost;
            if (surface != null) { overview.Dock = DockStyle.None; surface.ActivePage = overview; overview.Bounds = surface.PageBounds; }
        }
        private System.Drawing.Rectangle PageArea { get { var surface=host as PageHost;return surface==null?host.DisplayRectangle:surface.PageBounds; } }
        internal bool Navigate(string name, Func<Form> create)
        {
            if (disposed || switching || name == currentName) return false;
            switching = true;
            bool diagnostic = DiagnosticsEnabled || WindowEnvironment.OffscreenTests;
            var total = diagnostic ? Stopwatch.StartNew() : null;
            var stage = diagnostic ? Stopwatch.StartNew() : null;
            double construct = 0, handles = 0, layout = 0, paint = 0;
            int createdHandles = 0; bool fresh = false, committed = false;
            Control next = overview;
            using (var trace = new NavigationTrace(diagnostic))
            try
            {
                if (name != "概览")
                {
                    Form page;
                    if (!pages.TryGetValue(name, out page))
                    {
                        page = create(); next = page; fresh = true;
                        if (diagnostic) { construct = stage.Elapsed.TotalMilliseconds; stage.Restart(); }
                        page.TopLevel = false; page.FormBorderStyle = FormBorderStyle.None;
                        page.MinimumSize = System.Drawing.Size.Empty; page.Dock = DockStyle.None; page.Tag = name;
                        page.SuspendLayout(); host.SuspendLayout();
                        try
                        {
                            host.Controls.Add(page); host.Controls.SetChildIndex(page, host.Controls.Count - 1);
                            page.Bounds = PageArea;
                            // Native handles are initialized once, without showing an empty page.
                            if (!page.IsHandleCreated) { var handle = page.Handle; createdHandles++; }
                        }
                        finally { page.ResumeLayout(false); host.ResumeLayout(false); }
                        pages.Add(name, page);
                        if (diagnostic) { handles = stage.Elapsed.TotalMilliseconds; stage.Restart(); }
                    }
                    next = page;
                }
                if (next.Bounds != PageArea) next.Bounds = PageArea;
                if (!(host is PageHost)) next.Dock = DockStyle.Fill;
                trace.Observe(next);
                // Hidden cached pages still synchronize configuration/data before presentation.
                var incoming = next as IManagedPage;
                if (fresh) next.PerformLayout();
                if (incoming != null) incoming.EnterPage();
                if (diagnostic) { layout = stage.Elapsed.TotalMilliseconds; stage.Restart(); }
                // Batch visibility and Z-order changes. No message pumping, sleeps or global compositing.
                using (new RedrawBatch(host))
                {
                host.SuspendLayout();
                try
                {
                    next.Show(); next.BringToFront();
                    var surface = host as PageHost; if (surface != null) surface.ActivePage = next;
                    var outgoing = current as IManagedPage;
                    if (outgoing != null) outgoing.LeavePage();
                    if (current != next) { current.Hide(); current.Dock = DockStyle.None; UiStyle.ReleaseHiddenRendering(current); }
                    current = next; currentName = name; committed = true;
                }
                finally { host.ResumeLayout(false); }
                }
                if (diagnostic)
                {
                    paint = stage.Elapsed.TotalMilliseconds;
                    LastMeasurement = "page=" + name + " fresh=" + fresh + " total_ms=" + total.Elapsed.TotalMilliseconds.ToString("F3") +
                        " construct_ms=" + construct.ToString("F3") + " native_init_ms=" + handles.ToString("F3") +
                        " prepare_layout_ms=" + layout.ToString("F3") + " commit_paint_dispatch_ms=" + paint.ToString("F3") +
                        " handles_created=" + createdHandles + " " + trace.Summary;
                    if (DiagnosticsEnabled) WindowDiagnostics.Record(LastMeasurement);
                }
                return true;
            }
            finally
            {
                if (!committed && fresh && next != overview) { pages.Remove(name); next.Dispose(); }
                switching = false;
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var page in pages.Values) page.Dispose();
            pages.Clear(); current = null;
        }
    }

    // Bounded, opt-in observation. Paint dispatch is not a DWM/full-frame timestamp.
    internal sealed class NavigationTrace : IDisposable
    {
        private readonly bool enabled;
        private readonly List<Probe> probes = new List<Probe>();
        private readonly List<Control> controls = new List<Control>();
        [ThreadStatic] internal static NavigationTrace Current;
        private readonly NavigationTrace previous;
        private int layouts, paints, timedLayouts, layoutDepth; private long ticks, layoutTicks, layoutStart;
        internal NavigationTrace(bool enabled) { this.enabled = enabled; previous = Current; if (enabled) Current = this; }
        internal void BeginLayout() { timedLayouts++; if (layoutDepth++ == 0) layoutStart = Stopwatch.GetTimestamp(); }
        internal void EndLayout() { if (--layoutDepth == 0) layoutTicks += Stopwatch.GetTimestamp() - layoutStart; }
        internal void Observe(Control root)
        {
            if (!enabled) return;
            controls.Add(root); root.Layout += OnLayout;
            if (root.IsHandleCreated) probes.Add(new Probe(root.Handle, this));
            foreach (Control child in root.Controls) Observe(child);
        }
        private void OnLayout(object sender, LayoutEventArgs e) { layouts++; }
        internal string Summary { get { return "observed_layouts=" + layouts + " timed_form_grid_layouts=" + timedLayouts + " form_grid_layout_ms=" + (layoutTicks * 1000.0 / Stopwatch.Frequency).ToString("F3") + " native_paints=" + paints + " paint_handler_ms=" + (ticks * 1000.0 / Stopwatch.Frequency).ToString("F3") + " full_visible_frame=BLOCKED"; } }
        private sealed class Probe : NativeWindow
        {
            private readonly NavigationTrace trace;
            internal Probe(IntPtr handle, NavigationTrace trace) { this.trace = trace; AssignHandle(handle); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg != 0x000F) { base.WndProc(ref message); return; }
                long start = Stopwatch.GetTimestamp();
                try { base.WndProc(ref message); } finally { trace.paints++; trace.ticks += Stopwatch.GetTimestamp() - start; }
            }
        }
        public void Dispose() { foreach (var control in controls) control.Layout -= OnLayout; foreach (var probe in probes) probe.ReleaseHandle(); Current = previous; }
    }
}
