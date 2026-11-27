using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SteamDirect
{
    // A single UI-thread scheduler. No timer wakeups remain after the last transition.
    internal static class Motion
    {
        private static readonly List<Value> active = new List<Value>();
        private static readonly System.Threading.Timer timer = new System.Threading.Timer(QueueFrame);
        private static readonly List<Value> frame = new List<Value>();
        private static FrameWindow frameWindow;
        private static int posted;
        private static System.Threading.EventWaitHandle frameSignal;
        private static System.Threading.RegisteredWaitHandle frameWait;
        private static double framePeriod, nextFrame;
        private static IntPtr displayMonitor, animationWindow;
        private static bool? systemMotionEnabled;
        internal static int RefreshRate { get; private set; }

        internal static double FramePeriodTicks(int refreshHz)
        {
            return Stopwatch.Frequency / (double)(refreshHz >= 30 && refreshHz <= 500 ? refreshHz : 60);
        }

        internal static double NextDeadline(double previous, double period, long now)
        {
            // Count the frame just delivered even if the kernel woke slightly early.
            // After a busy UI frame skip missed deadlines without resetting the phase.
            return previous + Math.Max(1, Math.Floor((now - previous) / period) + 1) * period;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)]
        private static extern Microsoft.Win32.SafeHandles.SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        private static extern bool SetWaitableTimer(Microsoft.Win32.SafeHandles.SafeWaitHandle timer,
            ref long due, int period, IntPtr completion, IntPtr argument, bool resume);
        [DllImport("kernel32.dll")]
        private static extern bool CancelWaitableTimer(Microsoft.Win32.SafeHandles.SafeWaitHandle timer);
        private static bool ticking, shuttingDown;

        // A posted message carries no delegate, argument array or ExecutionContext per frame.
        // All sampling, invalidation and destruction still run on the owning UI thread.
        private sealed class FrameWindow : NativeWindow
        {
            private const int FrameMessage = 0x8001;
            internal FrameWindow()
            {
                CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
            }
            [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
            internal bool Post() { return PostMessage(Handle, FrameMessage, IntPtr.Zero, IntPtr.Zero); }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == FrameMessage) DrawFrame();
                else base.WndProc(ref message);
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        // DEVMODEW is a 220-byte native union. Only dmSize and dmDisplayFrequency are needed.
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        private struct DisplayMode
        {
            [FieldOffset(68)] internal ushort Size;
            [FieldOffset(184)] internal uint Frequency;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);

        private static void UpdateDisplay(IntPtr window)
        {
            IntPtr monitor = MonitorFromWindow(window, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor == displayMonitor && framePeriod > 0) return;
            displayMonitor = monitor;
            var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
            RefreshRate = EnumDisplaySettings(Screen.FromHandle(window).DeviceName, -1, ref mode) &&
                mode.Frequency >= 30 && mode.Frequency <= 500 ? (int)mode.Frequency : 60;
            framePeriod = FramePeriodTicks(RefreshRate);
        }

        internal static void DisplayMessage(IntPtr window, int message)
        {
            if (message == 0x001A) { systemMotionEnabled = null; return; } // WM_SETTINGCHANGE
            if (message == 0x007E) displayMonitor = IntPtr.Zero; // WM_DISPLAYCHANGE
            if (ticking && animationWindow == window) UpdateDisplay(window);
        }

        private static void Start(Control owner)
        {
            if (shuttingDown) return;
            animationWindow = (owner.TopLevelControl ?? owner).Handle;
            UpdateDisplay(animationWindow);
            if (ticking) return;
            if (frameWindow == null) frameWindow = new FrameWindow();
            nextFrame = Stopwatch.GetTimestamp();
            ticking = true;
            if (frameSignal == null)
            {
                var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x00100002);
                if (handle.IsInvalid) handle.Dispose();
                else
                {
                    frameSignal = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset);
                    frameSignal.SafeWaitHandle = handle;
                    frameWait = System.Threading.ThreadPool.RegisterWaitForSingleObject(frameSignal,
                        (state, timeout) => QueueFrame(state), null, System.Threading.Timeout.Infinite, false);
                }
            }
            ScheduleFrame();
        }

        private static void ScheduleFrame()
        {
            long now = Stopwatch.GetTimestamp();
            nextFrame = NextDeadline(nextFrame, framePeriod, now);

            double delayMs = (nextFrame - now) * 1000.0 / Stopwatch.Frequency;
            long due = -Math.Max(1L, (long)Math.Round(delayMs * 10000));
            if (frameWait == null || !SetWaitableTimer(frameSignal.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                timer.Change(Math.Max(1, (int)Math.Ceiling(delayMs)), System.Threading.Timeout.Infinite);
        }

        private static void QueueFrame(object state)
        {
            if (System.Threading.Interlocked.Exchange(ref posted, 1) != 0) return;
            if (!frameWindow.Post()) System.Threading.Interlocked.Exchange(ref posted, 0);
        }

        private static void DrawFrame()
        {
            try
            {
                if (!ticking) return;
                long now = Stopwatch.GetTimestamp();
                bool allowed = Allowed;
                frame.AddRange(active);
                foreach (var value in frame) value.Step(now, allowed);
            }
            finally
            {
                frame.Clear();
                System.Threading.Interlocked.Exchange(ref posted, 0);
                StopIfIdle();
                if (ticking) ScheduleFrame();
            }
        }

        private static void StopIfIdle()
        {
            if (!ticking || active.Count != 0) return;
            ticking = false;
            animationWindow = IntPtr.Zero;
            if (frameWait != null) CancelWaitableTimer(frameSignal.SafeWaitHandle);
            timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        private static bool CanAnimate(Control owner)
        {
            var window = owner.TopLevelControl as Form;
            var desktop = window as DesktopForm;
            return owner.IsHandleCreated && owner.Visible && !owner.IsDisposed &&
                (window == null || window.WindowState != FormWindowState.Minimized) &&
                (desktop == null || !desktop.IsLiveResizing);
        }
        private static bool reduced;
        internal static Func<bool> IsGaming;

        internal static bool Reduced
        {
            get { return reduced; }
            set { reduced = value; if (value) FinishAll(); }
        }
        internal static int ActiveCount { get { return active.Count; } }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint parameter, out int value, uint flags);

        private static bool SystemMotion
        {
            get
            {
                int enabled;
                if (!systemMotionEnabled.HasValue)
                    systemMotionEnabled = !SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled != 0;
                return systemMotionEnabled.Value;
            }
        }
        internal static bool Allowed
        {
            get { return !shuttingDown && !reduced && SystemMotion && (IsGaming == null || !IsGaming()); }
        }

        static Motion()
        {
            Application.ApplicationExit += (sender, args) =>
            {
                shuttingDown = true;
                FinishAll();
                timer.Dispose();
                if (frameWait != null) frameWait.Unregister(null);
                if (frameSignal != null) frameSignal.Dispose();
                if (frameWindow != null) frameWindow.DestroyHandle();
            };
        }

        private static void FinishAll()
        {
            foreach (var value in active.ToArray()) value.Snap();
            StopIfIdle();
        }

        internal static Color Blend(Color from, Color to, float fraction)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            return Color.FromArgb((int)(from.R + (to.R - from.R) * fraction),
                (int)(from.G + (to.G - from.G) * fraction), (int)(from.B + (to.B - from.B) * fraction));
        }

        // Closed-form solution for a critically damped spring with unit mass.
        internal static double SampleSpring(double from, double target, double initialVelocity,
            double frequency, double seconds, out double velocity)
        {
            double displacement = from - target;
            double slope = initialVelocity + frequency * displacement;
            double decay = Math.Exp(-frequency * seconds);
            velocity = (initialVelocity - frequency * slope * seconds) * decay;
            return target + (displacement + slope * seconds) * decay;
        }

        internal sealed class Value : IDisposable
        {
            private readonly Control owner;
            private readonly Action changed;
            private float current, from, target;
            private long started;
            private int duration;
            private double springFrequency, initialVelocity;
            private bool disposed, scheduled, glide;
            internal float Current { get { return current; } }

            internal Value(Control owner, float initial, Action changed = null)
            {
                if (owner == null) throw new ArgumentNullException("owner");
                this.owner = owner;
                this.changed = changed;
                current = target = initial;
                owner.Disposed += OwnerDisposed;
                owner.VisibleChanged += Visibility;
                owner.HandleDestroyed += Visibility;
            }

            internal void To(float value, int milliseconds)
            {
                Retarget(value, milliseconds, false);
            }

            internal void SpringTo(float value, int settleMilliseconds)
            {
                Retarget(value, settleMilliseconds, true);
            }

            internal void GlideTo(float value, int milliseconds)
            {
                Retarget(value, milliseconds, false, true);
            }

            private void Retarget(float value, int milliseconds, bool spring, bool finiteGlide = false)
            {
                if (disposed) return;
                if (target == value && (current == value || scheduled)) return;
                long now = Stopwatch.GetTimestamp();
                double velocity = 0;
                if (duration > 0 && scheduled)
                    current = Interpolate(now, out velocity);
                target = value;
                if (milliseconds <= 0 || !Allowed || !CanAnimate(owner))
                {
                    Snap();
                    return;
                }
                if (Math.Abs(current - target) < .001F && (!spring || Math.Abs(velocity) < .025))
                {
                    Snap();
                    return;
                }
                from = current;
                glide = finiteGlide;
                duration = glide ? Math.Max(1, (int)Math.Round(milliseconds * Math.Abs(target - from))) : milliseconds;
                springFrequency = spring ? 7200.0 / milliseconds : 0;
                initialVelocity = spring ? velocity : 0;
                started = now;
                Schedule();
            }

            internal void SetPulse(bool enabled)
            {
                if (disposed) return;
                if (!enabled || !Allowed || !CanAnimate(owner))
                {
                    Unschedule();
                    return;
                }
                duration = 0;
                Schedule();
            }

            private void Schedule()
            {
                if (!scheduled) { active.Add(this); scheduled = true; }
                Start(owner);
            }

            private void Unschedule()
            {
                if (!scheduled) return;
                scheduled = false;
                active.Remove(this);
                StopIfIdle();
            }

            internal void Set(float value)
            {
                if (disposed) return;
                target = value;
                Snap();
            }

            internal void Snap()
            {
                if (disposed) return;
                bool notify = scheduled || current != target;
                current = target;
                Unschedule();
                if (notify) Changed();
            }

            internal void Step(long now, bool allowed)
            {
                if (!scheduled) return;
                if (!allowed || !CanAnimate(owner)) { Snap(); return; }
                if (duration == 0) { Changed(); return; }
                double elapsedMs = (now - started) * 1000.0 / Stopwatch.Frequency;
                if (springFrequency == 0 && elapsedMs >= duration)
                {
                    Snap();
                    return;
                }
                double velocity;
                current = Interpolate(now, out velocity);
                if (springFrequency != 0 && (elapsedMs >= 2.0 * duration ||
                    Math.Abs(current - target) <= .002 && Math.Abs(velocity) <= .05))
                {
                    Snap();
                    return;
                }
                Changed();
            }

            private float Interpolate(long now, out double velocity)
            {
                double seconds = Math.Max(0, (now - started) / (double)Stopwatch.Frequency);
                if (springFrequency != 0)
                    return (float)SampleSpring(from, target, initialVelocity, springFrequency, seconds, out velocity);

                double remaining = Math.Max(0, 1 - seconds * 1000 / duration);
                if (glide)
                {
                    double t = 1 - remaining;
                    double eased = t * (.25 + .75 * t * (3 - 2 * t));
                    velocity = (target - from) * 1000.0 / duration * (.25 + 4.5 * t * remaining);
                    return from + (target - from) * (float)eased;
                }
                velocity = (target - from) * 3000.0 / duration * remaining * remaining;
                return from + (target - from) * (float)(1 - remaining * remaining * remaining);
            }

            private void Changed()
            {
                if (owner.IsDisposed) return;
                if (changed != null) changed(); else owner.Invalidate();
            }
            private void Visibility(object sender, EventArgs args)
            {
                if (!owner.Visible || !owner.IsHandleCreated) Snap();
            }
            private void OwnerDisposed(object sender, EventArgs args) { Dispose(); }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Unschedule();
                owner.Disposed -= OwnerDisposed;
                owner.VisibleChanged -= Visibility;
                owner.HandleDestroyed -= Visibility;
            }
        }
    }
}
