using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SwVault.Protocol;

namespace SwVault.AddIn.Infrastructure
{
    /// <summary>
    /// SOLIDWORKS API calls must happen on SOLIDWORKS' main thread. Everything async in the add-in
    /// resumes there through a synchronization context bound to a control created on that thread.
    /// (Not WindowsFormsSynchronizationContext: inside SOLIDWORKS, which has no WinForms message
    /// loop, continuations were seen resuming on the agent's pipe thread, where the task pane's
    /// folder tree refuses to be touched.)
    /// </summary>
    internal static class UiThread
    {
        private static Control _marshal;
        private static SynchronizationContext _context;

        public static IWin32Window Owner { get; set; }

        public static void Initialize()
        {
            _marshal = new Control();
            _marshal.CreateControl();
            _context = new MarshalContext(_marshal);
            EnsureContext();
        }

        /// <summary>True on the thread the add-in was loaded on (SOLIDWORKS' main thread).</summary>
        public static bool OnUiThread => _marshal != null && !_marshal.IsDisposed && !_marshal.InvokeRequired;

        /// <summary>Called at every entry point from SOLIDWORKS (commands, events).</summary>
        public static void EnsureContext()
        {
            // Only on the main thread: installed anywhere else it would send continuations there.
            if (_context != null && OnUiThread && SynchronizationContext.Current != _context)
                SynchronizationContext.SetSynchronizationContext(_context);
        }

        /// <summary>
        /// Await this after an agent call and before touching controls or SOLIDWORKS: it completes
        /// on the main thread whatever thread the call came back on.
        /// </summary>
        public static Task OnUi()
        {
            if (OnUiThread || _marshal == null || _marshal.IsDisposed) return Task.CompletedTask;
            var done = new TaskCompletionSource<bool>();
            Post(() => done.SetResult(true));
            return done.Task;
        }

        private sealed class MarshalContext : SynchronizationContext
        {
            private readonly Control _control;

            public MarshalContext(Control control)
            {
                _control = control;
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                try
                {
                    _control.BeginInvoke(d, new[] { state });
                }
                catch (InvalidOperationException)
                {
                    // The add-in is unloading.
                }
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                if (_control.InvokeRequired) _control.Invoke(d, new[] { state });
                else d(state);
            }

            public override SynchronizationContext CreateCopy() => this;
        }

        /// <summary>Runs on the UI thread (from agent notifications, which arrive on background threads).</summary>
        public static void Post(Action action)
        {
            if (_marshal == null || _marshal.IsDisposed) return;
            try
            {
                _marshal.BeginInvoke((MethodInvoker)(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("UI callback failed", ex);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>
        /// Runs a command body, turning every failure into a message instead of an exception that
        /// could take SOLIDWORKS down.
        /// </summary>
        public static async void Run(string what, Func<Task> body)
        {
            if (!OnUiThread && _marshal != null && !_marshal.IsDisposed)
            {
                Post(() => Run(what, body));
                return;
            }
            EnsureContext();
            try
            {
                await body();
            }
            catch (OperationCanceledException)
            {
                Log.Info(what + ": cancelled."); // the user clicked Cancel; nothing to explain
            }
            catch (AgentException ex)
            {
                Log.Warn(what + ": " + ex.Message);
                ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error(what + " failed", ex);
                ShowError("SwVault: " + what + " failed.\n\n" + ex.Message);
            }
        }

        public static void ShowError(string message) =>
            MessageBox.Show(Owner, message, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        public static void ShowInfo(string message) =>
            MessageBox.Show(Owner, message, "SwVault", MessageBoxButtons.OK, MessageBoxIcon.Information);

        public static bool Confirm(string message) =>
            MessageBox.Show(Owner, message, "SwVault", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        public static void Dispose()
        {
            _marshal?.Dispose();
            _marshal = null;
            _context = null;
        }
    }

    /// <summary>Wraps a window handle (the SOLIDWORKS main frame) as a dialog owner.</summary>
    internal sealed class WindowHandle : IWin32Window
    {
        public WindowHandle(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}
