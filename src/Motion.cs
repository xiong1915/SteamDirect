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
        private static readonly Timer timer = new Timer { Interval = 16 };
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
            timer.Tick += (sender, args) =>
            {
                foreach (var value in active.ToArray()) value.Step();
                if (active.Count == 0) timer.Stop();
            };
            Application.ApplicationExit += (sender, args) => { FinishAll(); timer.Dispose(); };
        }

        private static void FinishAll()
        {
            foreach (var value in active.ToArray()) value.Snap();
            timer.Stop();
        }

        internal static Color Blend(Color from, Color to, float fraction)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            return Color.FromArgb((int)(from.R + (to.R - from.R) * fraction),
                (int)(from.G + (to.G - from.G) * fraction), (int)(from.B + (to.B - from.B) * fraction));
        }

        internal sealed class Value : IDisposable
        {
            private readonly Control owner;
            private readonly Action changed;
            private float current, from, target;
            private long started;
            private int duration;
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
                if (disposed) return;
                target = value;
                if (milliseconds <= 0 || !Allowed || !owner.IsHandleCreated || !owner.Visible || owner.IsDisposed)
                {
                    Snap();
                    return;
                }
                if (Math.Abs(current - target) < .001F) { Snap(); return; }
                from = current;
                duration = milliseconds;
                started = Stopwatch.GetTimestamp();
                if (!active.Contains(this)) active.Add(this);
                timer.Start();
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
                if (active.Count == 0) timer.Stop();
                Changed();
            }

            internal void Step()
            {
                if (disposed) return;
                if (!Allowed || !owner.Visible || !owner.IsHandleCreated || owner.IsDisposed) { Snap(); return; }
                double progress = Math.Min(1, (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency / duration);
                float eased = (float)(1 - Math.Pow(1 - progress, 3));
                current = from + (target - from) * eased;
                if (progress >= 1) { Snap(); return; }
                Changed();
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
                if (active.Count == 0) timer.Stop();
                owner.Disposed -= OwnerDisposed;
                owner.VisibleChanged -= Visibility;
                owner.HandleDestroyed -= Visibility;
            }
        }
    }
}
