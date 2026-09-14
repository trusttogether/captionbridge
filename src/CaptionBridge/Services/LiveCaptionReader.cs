using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace CaptionBridge.Services;

/// <summary>
/// Mirrors the text of the Windows Live Captions window as fast as UI Automation allows.
/// </summary>
/// <remarks>
/// Speed comes from three choices: the window is located by HWND (cheap user32 enumeration)
/// instead of a UI Automation tree search, every poll reads the caption text in a single
/// cross-process call via a <see cref="CacheRequest"/>, and the loop runs on its own thread
/// with the system timer resolution raised so short sleeps are honoured.
/// </remarks>
internal sealed class LiveCaptionReader : IDisposable
{
    private const int IdlePollIntervalMs = 250;

    private static readonly Condition TextCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

    private readonly CancellationTokenSource _cancellation = new();
    private readonly CacheRequest _cacheRequest;
    private readonly List<string> _fragments = [];
    private readonly Thread _thread;

    private IntPtr _captionHwnd;
    private AutomationElement? _captionWindow;
    private string _lastCaption = string.Empty;
    private bool _lastAvailable;
    private bool _disposed;
    private volatile int _pollIntervalMs;

    public LiveCaptionReader(int pollIntervalMs)
    {
        _pollIntervalMs = ClampInterval(pollIntervalMs);

        _cacheRequest = new CacheRequest
        {
            // Cache-only elements skip the round trip that a live element reference needs.
            AutomationElementMode = AutomationElementMode.None,
            TreeScope = TreeScope.Element,
        };
        _cacheRequest.Add(AutomationElement.NameProperty);

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "LiveCaptionReader",
            Priority = ThreadPriority.AboveNormal,
        };
    }

    /// <summary>Raised on the reader thread whenever the mirrored caption text changes.</summary>
    public event EventHandler<string>? CaptionChanged;

    /// <summary>Raised on the reader thread when Live Captions appears or disappears.</summary>
    public event EventHandler<bool>? AvailabilityChanged;

    /// <summary>Can be changed at any time; the loop picks it up on the next tick.</summary>
    public int PollIntervalMs
    {
        get => _pollIntervalMs;
        set => _pollIntervalMs = ClampInterval(value);
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(1));
        _cancellation.Dispose();
    }

    private void Run()
    {
        var token = _cancellation.Token;
        var raisedTimerResolution = NativeMethods.TimeBeginPeriod(1) == 0;

        try
        {
            var clock = Stopwatch.StartNew();
            var nextTickMs = 0L;

            while (!token.IsCancellationRequested)
            {
                var found = Tick();

                nextTickMs += found ? _pollIntervalMs : IdlePollIntervalMs;
                var waitMs = nextTickMs - clock.ElapsedMilliseconds;
                if (waitMs < 1)
                {
                    // A slow poll (or a resumed machine) put us behind; restart the cadence
                    // rather than spinning to catch up on ticks nobody would have seen.
                    nextTickMs = clock.ElapsedMilliseconds;
                    waitMs = 1;
                }

                if (token.WaitHandle.WaitOne((int)waitMs))
                {
                    break;
                }
            }
        }
        finally
        {
            if (raisedTimerResolution)
            {
                NativeMethods.TimeEndPeriod(1);
            }
        }
    }

    /// <returns><see langword="true"/> when the Live Captions window is present.</returns>
    private bool Tick()
    {
        try
        {
            var window = ResolveCaptionWindow();
            SetAvailability(window is not null);
            if (window is null)
            {
                SetCaption(string.Empty);
                return false;
            }

            SetCaption(CaptionTextCleaner.Clean(ReadTextFragments(window)));
            return true;
        }
        catch (ElementNotAvailableException)
        {
            Forget();
            return false;
        }
        catch (COMException)
        {
            // Live Captions closing mid-poll tears the cross-process call down.
            Forget();
            return false;
        }
        catch (InvalidOperationException)
        {
            Forget();
            return false;
        }
    }

    private IReadOnlyList<string> ReadTextFragments(AutomationElement window)
    {
        _fragments.Clear();

        using (_cacheRequest.Activate())
        {
            foreach (AutomationElement element in window.FindAll(TreeScope.Subtree, TextCondition))
            {
                var name = element.Cached.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _fragments.Add(name);
                }
            }
        }

        if (_fragments.Count == 0)
        {
            ReadTextPatternFragments(window);
        }

        return _fragments;
    }

    /// <summary>
    /// Fallback for Live Captions builds that expose the transcript through a text pattern
    /// instead of an element name. Only runs while the name pass finds nothing.
    /// </summary>
    private void ReadTextPatternFragments(AutomationElement window)
    {
        foreach (AutomationElement element in window.FindAll(TreeScope.Subtree, TextCondition))
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) &&
                pattern is TextPattern textPattern)
            {
                var text = textPattern.DocumentRange.GetText(4096);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _fragments.Add(text);
                }
            }
        }
    }

    private AutomationElement? ResolveCaptionWindow()
    {
        if (_captionWindow is not null &&
            NativeMethods.IsWindow(_captionHwnd) &&
            NativeMethods.IsWindowVisible(_captionHwnd))
        {
            return _captionWindow;
        }

        Forget();

        var hwnd = LiveCaptionsWindowFinder.Find();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        _captionHwnd = hwnd;
        _captionWindow = AutomationElement.FromHandle(hwnd);
        return _captionWindow;
    }

    private void Forget()
    {
        _captionWindow = null;
        _captionHwnd = IntPtr.Zero;
    }

    private void SetCaption(string caption)
    {
        if (string.Equals(caption, _lastCaption, StringComparison.Ordinal))
        {
            return;
        }

        _lastCaption = caption;
        CaptionChanged?.Invoke(this, caption);
    }

    private void SetAvailability(bool available)
    {
        if (available == _lastAvailable)
        {
            return;
        }

        _lastAvailable = available;
        AvailabilityChanged?.Invoke(this, available);
    }

    private static int ClampInterval(int intervalMs) => Math.Clamp(intervalMs, 10, 500);
}
