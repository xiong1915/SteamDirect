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
        private static WindowsFormsSynchronizationContext context;
        private static int posted;
        private static System.Threading.EventWaitHandle frameSignal;
        private static System.Threading.RegisteredWaitHandle frameWait;
        private static double framePeriod, nextFrame;

        internal static double FramePeriodTicks(int refreshHz)
        {
            return Math.Max(4.0, 1000.0 / refreshHz) * Stopwatch.Frequency / 1000.0;
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)]
        private static extern Microsoft.Win32.SafeHandles.SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll")]
        private static extern bool SetWaitableTimer(Microsoft.Win32.SafeHandles.SafeWaitHandle timer,
            ref long due, int period, IntPtr completion, IntPtr argument, bool resume);
        [DllImport("kernel32.dll")]
        private static extern bool CancelWaitableTimer(Microsoft.Win32.SafeHandles.SafeWaitHandle timer);
        private static bool ticking, shuttingDown;

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initialization);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        private static void Start(Control owner)
        {
            if (ticking || shuttingDown) return;
            if (context == null) context = new WindowsFormsSynchronizationContext();
            int hz = 60;
            IntPtr dc = CreateDC("DISPLAY", Screen.FromControl(owner).DeviceName, null, IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                try { int reported = GetDeviceCaps(dc, 116); if (reported >= 30) hz = reported; }
                finally { DeleteDC(dc); }
            }
            framePeriod = FramePeriodTicks(hz);
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
                }
            }
            if (frameSignal != null)
                frameWait = System.Threading.ThreadPool.RegisterWaitForSingleObject(frameSignal,
                    (state, timeout) => QueueFrame(state), null, System.Threading.Timeout.Infinite, false);
            ScheduleFrame();
        }

        private static void ScheduleFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (nextFrame <= now)
                nextFrame += (Math.Floor((now - nextFrame) / framePeriod) + 1) * framePeriod;

            double delayMs = (nextFrame - now) * 1000.0 / Stopwatch.Frequency;
            long due = -Math.Max(1L, (long)Math.Round(delayMs * 10000));
            if (frameWait == null || !SetWaitableTimer(frameSignal.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                timer.Change(Math.Max(1, (int)Math.Ceiling(delayMs)), System.Threading.Timeout.Infinite);
        }

        private static void QueueFrame(object state)
        {
            if (System.Threading.Interlocked.Exchange(ref posted, 1) != 0) return;
            try { context.Post(_ => DrawFrame(), null); }
            catch (InvalidOperationException) { System.Threading.Interlocked.Exchange(ref posted, 0); }
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
            if (frameWait != null)
            {
                CancelWaitableTimer(frameSignal.SafeWaitHandle);
                frameWait.Unregister(null);
                frameWait = null;
            }
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
                return !SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled != 0;
            }
        }
        internal static bool Allowed
        {
            get { return !reduced && SystemMotion && (IsGaming == null || !IsGaming()); }
        }

        static Motion()
        {
            Application.ApplicationExit += (sender, args) =>
            {
                shuttingDown = true;
                FinishAll();
                timer.Dispose();
                if (frameSignal != null) frameSignal.Dispose();
                if (context != null) context.Dispose();
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
            private long started, lastSample;
            private int duration;
            private double springFrequency, initialVelocity;
            private bool disposed;
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

            internal void SpringToFromFrame(float value, int settleMilliseconds)
            {
                Retarget(value, settleMilliseconds, true, fromRenderedFrame: true);
            }

            private void Retarget(float value, int milliseconds, bool spring, bool fromRenderedFrame = false)
            {
                if (disposed) return;
                if (target == value && (current == value || active.Contains(this))) return;
                long now = Stopwatch.GetTimestamp();
                double velocity = 0;
                if (duration > 0 && active.Contains(this))
                    current = Interpolate(fromRenderedFrame ? lastSample : now, out velocity);
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
                duration = milliseconds;
                springFrequency = spring ? 7200.0 / milliseconds : 0;
                initialVelocity = spring ? velocity : 0;
                started = lastSample = now;
                if (!active.Contains(this)) active.Add(this);
                Start(owner);
            }

            internal void SetPulse(bool enabled)
            {
                if (disposed) return;
                if (!enabled || !Allowed || !CanAnimate(owner))
                {
                    active.Remove(this);
                    StopIfIdle();
                    return;
                }
                duration = 0;
                if (!active.Contains(this)) active.Add(this);
                Start(owner);
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
                current = target;
                active.Remove(this);
                StopIfIdle();
                Changed();
            }

            internal void Step(long now, bool allowed)
            {
                if (disposed || !active.Contains(this)) return;
                if (!allowed || !CanAnimate(owner)) { Snap(); return; }
                if (duration == 0) { Changed(); return; }
                double elapsedMs = (now - started) * 1000.0 / Stopwatch.Frequency;
                if (springFrequency == 0 && elapsedMs >= duration)
                {
                    Snap();
                    return;
                }
                double velocity;
                lastSample = now;
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
                active.Remove(this);
                StopIfIdle();
                owner.Disposed -= OwnerDisposed;
                owner.VisibleChanged -= Visibility;
                owner.HandleDestroyed -= Visibility;
            }
        }
    }
}
