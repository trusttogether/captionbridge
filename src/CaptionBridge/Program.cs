using System.Windows.Forms;
using CaptionBridge.Services;

namespace CaptionBridge;

internal static class Program
{
    private const string SingleInstanceMutexName = "CaptionBridge.SingleInstance";
    private const string ActivateRequestEventName = "CaptionBridge.ActivateRequest";

    /// <summary>
    /// Leaves Live Captions alone at startup. Used by tools/verify-mirror.ps1, which mirrors a
    /// stand-in window that a real Live Captions window would win the title match against.
    /// </summary>
    private const string NoAutoStartSwitch = "--no-autostart";

    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Launching the exe again should raise the window that is already running rather
            // than looking like nothing happened.
            RequestExistingInstanceActivation();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var autoStartLiveCaptions = !args.Contains(NoAutoStartSwitch, StringComparer.OrdinalIgnoreCase);

        using var form = new CaptionBridgeForm(autoStartLiveCaptions);
        var listener = StartActivationRequestListener(form);
        try
        {
            Application.Run(form);
        }
        finally
        {
            listener.Registration?.Unregister(null);
            listener.Event?.Dispose();
            singleInstanceMutex.ReleaseMutex();
        }
    }

    private static void RequestExistingInstanceActivation()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateRequestEventName, out var activateRequestEvent))
            {
                using (activateRequestEvent)
                {
                    activateRequestEvent.Set();
                }
            }
        }
        catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
        }
    }

    private static (EventWaitHandle? Event, RegisteredWaitHandle? Registration) StartActivationRequestListener(
        CaptionBridgeForm form)
    {
        try
        {
            var activateRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateRequestEventName);
            var registration = ThreadPool.RegisterWaitForSingleObject(
                activateRequestEvent,
                (_, _) => SurfaceWindow(form),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: false);

            return (activateRequestEvent, registration);
        }
        catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    private static void SurfaceWindow(CaptionBridgeForm form)
    {
        try
        {
            if (form.IsHandleCreated && !form.IsDisposed)
            {
                form.BeginInvoke(form.SurfaceWindow);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }
    }
}
