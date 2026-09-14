using System.Drawing;
using System.IO;
using System.Windows.Forms;
using CaptionBridge.Services;

namespace CaptionBridge;

/// <summary>
/// A read-only mirror of the Windows Live Captions transcript.
/// </summary>
internal sealed class CaptionBridgeForm : Form
{
    private const string BaseTitle = "Caption Bridge";
    private const int ControlBarHeight = 30;
    private const int TextInset = 12;

    private const string IdleHint =
        "Windows Live Captions is not running.\r\n\r\n" +
        "Press Win + Ctrl + L to turn it on, or right-click here and choose \"Start Windows Live Captions\".";

    private static readonly Color Background = Color.FromArgb(21, 25, 34);
    private static readonly Color Foreground = Color.FromArgb(244, 247, 251);
    private static readonly Color ControlBarBackground = Color.FromArgb(32, 38, 50);

    private readonly TextBox _captionTextBox = new();
    private readonly Panel _controlBar = new();
    private readonly TrackBar _fontSizeTrackBar = new();
    private readonly TrackBar _opacityTrackBar = new();
    private readonly System.Windows.Forms.Timer _resumeTimer = new();
    private readonly CaptionSettings _settings;
    private readonly LiveCaptionReader _reader;

    private string _pendingCaption = string.Empty;
    private bool _pendingAvailable;
    private int _flushQueued;

    private string _renderedText = string.Empty;
    private bool _showingIdleHint;
    private string _statusSuffix = string.Empty;

    public CaptionBridgeForm()
    {
        _settings = CaptionSettingsStore.Load();
        _reader = new LiveCaptionReader(_settings.PollIntervalMs);

        InitializeUi();

        _reader.CaptionChanged += OnCaptionChanged;
        _reader.AvailabilityChanged += OnAvailabilityChanged;
    }

    /// <summary>
    /// True while the user has text selected. Rewriting the box would destroy the selection,
    /// so the mirror pauses until the selection is released.
    /// </summary>
    private bool IsHoldingForSelection => !_captionTextBox.IsDisposed && _captionTextBox.SelectionLength > 0;

    public void SurfaceWindow()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Show();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkTitleBar();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyTextInset();
        _reader.Start();
        _resumeTimer.Start();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutUi();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _resumeTimer.Stop();
        _reader.CaptionChanged -= OnCaptionChanged;
        _reader.AvailabilityChanged -= OnAvailabilityChanged;
        _reader.Dispose();
        SaveSettings();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resumeTimer.Dispose();
            _reader.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeUi()
    {
        SuspendLayout();

        Text = BaseTitle;
        BackColor = Background;
        ForeColor = Foreground;
        Icon = LoadAppIcon();
        MinimumSize = new Size(360, 150);
        TopMost = _settings.AlwaysOnTop;
        Opacity = _settings.OpacityPercent / 100.0;
        KeyPreview = true;

        ApplySavedPlacement();

        _captionTextBox.Multiline = true;
        _captionTextBox.ReadOnly = true;
        _captionTextBox.HideSelection = false;
        _captionTextBox.WordWrap = true;
        _captionTextBox.ScrollBars = ScrollBars.Vertical;
        _captionTextBox.BorderStyle = BorderStyle.None;
        _captionTextBox.BackColor = Background;
        _captionTextBox.ForeColor = Foreground;
        _captionTextBox.Font = new Font("Segoe UI", _settings.FontSize, FontStyle.Regular);
        _captionTextBox.TabStop = false;
        _captionTextBox.KeyDown += CaptionTextBox_KeyDown;

        _controlBar.BackColor = ControlBarBackground;
        _controlBar.Height = ControlBarHeight;

        ConfigureTrackBar(_fontSizeTrackBar, CaptionSettings.MinFontSize, CaptionSettings.MaxFontSize, _settings.FontSize);
        ConfigureTrackBar(_opacityTrackBar, CaptionSettings.MinOpacityPercent, CaptionSettings.MaxOpacityPercent, _settings.OpacityPercent);
        _fontSizeTrackBar.ValueChanged += (_, _) => ApplyFontSize(_fontSizeTrackBar.Value);
        _opacityTrackBar.ValueChanged += (_, _) => ApplyOpacity(_opacityTrackBar.Value);

        _controlBar.Controls.Add(_fontSizeTrackBar);
        _controlBar.Controls.Add(_opacityTrackBar);
        Controls.Add(_captionTextBox);
        Controls.Add(_controlBar);

        ContextMenuStrip = BuildContextMenu();
        _captionTextBox.ContextMenuStrip = ContextMenuStrip;

        // Selection release has no dedicated event, so a slow timer resumes the mirror once
        // the user is done selecting and keeps the title in sync.
        _resumeTimer.Interval = 150;
        _resumeTimer.Tick += (_, _) => Render();

        Render();
        LayoutUi();
        ResumeLayout();
    }

    private static void ConfigureTrackBar(TrackBar trackBar, int minimum, int maximum, int value)
    {
        trackBar.AutoSize = false;
        trackBar.Minimum = minimum;
        trackBar.Maximum = maximum;
        trackBar.Value = Math.Clamp(value, minimum, maximum);
        trackBar.TickStyle = TickStyle.None;
        trackBar.BackColor = ControlBarBackground;
    }

    private void LayoutUi()
    {
        var captionHeight = Math.Max(0, ClientSize.Height - ControlBarHeight);
        _captionTextBox.SetBounds(0, 0, ClientSize.Width, captionHeight);
        _controlBar.SetBounds(0, captionHeight, ClientSize.Width, ControlBarHeight);

        const int gap = 8;
        var trackBarWidth = Math.Max(80, (_controlBar.ClientSize.Width - gap - 16) / 2);
        _fontSizeTrackBar.SetBounds(8, 4, trackBarWidth, 22);
        _opacityTrackBar.SetBounds(_fontSizeTrackBar.Right + gap, 4, trackBarWidth, 22);

        ApplyTextInset();
    }

    /// <summary>
    /// A multiline TextBox ignores <see cref="Control.Padding"/>, so the breathing room around
    /// the caption has to be set as the edit control's formatting rectangle.
    /// </summary>
    private void ApplyTextInset()
    {
        if (!_captionTextBox.IsHandleCreated)
        {
            return;
        }

        var client = _captionTextBox.ClientRectangle;
        if (client.Width <= TextInset * 2 || client.Height <= TextInset)
        {
            return;
        }

        var formattingRectangle = new NativeMethods.Rect
        {
            Left = TextInset,
            Top = TextInset / 2,
            Right = client.Width - TextInset,
            Bottom = client.Height,
        };

        NativeMethods.SendMessage(
            _captionTextBox.Handle,
            NativeMethods.EmSetRect,
            IntPtr.Zero,
            ref formattingRectangle);
    }

    private void ApplyDarkTitleBar()
    {
        var enabled = 1;
        if (NativeMethods.DwmSetWindowAttribute(
                Handle, NativeMethods.DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                Handle, NativeMethods.DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var alwaysOnTop = new ToolStripMenuItem("Always on top") { CheckOnClick = true, Checked = _settings.AlwaysOnTop };
        alwaysOnTop.CheckedChanged += (_, _) =>
        {
            _settings.AlwaysOnTop = alwaysOnTop.Checked;
            TopMost = alwaysOnTop.Checked;
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) => CopySelection()));
        menu.Items.Add(new ToolStripMenuItem("Select all", null, (_, _) => _captionTextBox.SelectAll()));
        menu.Items.Add(new ToolStripMenuItem("Clear", null, (_, _) => ClearCaptions()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildRefreshRateMenu());
        menu.Items.Add(alwaysOnTop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Start Windows Live Captions", null, (_, _) => LiveCaptionsLauncher.Toggle()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Close()));
        return menu;
    }

    private ToolStripMenuItem BuildRefreshRateMenu()
    {
        var parent = new ToolStripMenuItem("Refresh rate");
        (string Label, int IntervalMs)[] rates =
        [
            ("Fastest (10 ms)", 10),
            ("Fast (25 ms)", 25),
            ("Balanced (50 ms)", 50),
            ("Relaxed (100 ms)", 100),
        ];

        foreach (var (label, intervalMs) in rates)
        {
            var item = new ToolStripMenuItem(label) { Checked = _settings.PollIntervalMs == intervalMs };
            item.Click += (_, _) =>
            {
                _settings.PollIntervalMs = intervalMs;
                _reader.PollIntervalMs = intervalMs;
                foreach (ToolStripMenuItem sibling in parent.DropDownItems)
                {
                    sibling.Checked = ReferenceEquals(sibling, item);
                }
            };

            parent.DropDownItems.Add(item);
        }

        return parent;
    }

    private void CaptionTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control)
        {
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.A:
                _captionTextBox.SelectAll();
                e.SuppressKeyPress = true;
                break;
            case Keys.C:
                CopySelection();
                e.SuppressKeyPress = true;
                break;
        }
    }

    private void CopySelection()
    {
        var text = _captionTextBox.SelectionLength > 0 ? _captionTextBox.SelectedText : _captionTextBox.Text;
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private void ClearCaptions()
    {
        _captionTextBox.Clear();
        _renderedText = string.Empty;
        _showingIdleHint = false;
    }

    private void ApplyFontSize(int fontSize)
    {
        _settings.FontSize = fontSize;
        _captionTextBox.Font = new Font("Segoe UI", fontSize, FontStyle.Regular);
        ApplyTextInset();
        ScrollToBottom();
    }

    private void ApplyOpacity(int opacityPercent)
    {
        _settings.OpacityPercent = opacityPercent;
        Opacity = opacityPercent / 100.0;
    }

    private void OnCaptionChanged(object? sender, string caption)
    {
        Volatile.Write(ref _pendingCaption, caption);
        QueueRender();
    }

    private void OnAvailabilityChanged(object? sender, bool available)
    {
        Volatile.Write(ref _pendingAvailable, available);
        QueueRender();
    }

    /// <summary>
    /// Coalesces reader updates onto the UI thread. Only one render is ever in flight, so a
    /// burst of caption changes cannot build a backlog of stale repaints.
    /// </summary>
    private void QueueRender()
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (Interlocked.Exchange(ref _flushQueued, 1) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke(Render);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Exchange(ref _flushQueued, 0);
        }
    }

    private void Render()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        if (IsDisposed || _captionTextBox.IsDisposed)
        {
            return;
        }

        var available = Volatile.Read(ref _pendingAvailable);
        UpdateTitle(available);

        if (IsHoldingForSelection)
        {
            return;
        }

        if (!available)
        {
            if (!_showingIdleHint)
            {
                SetCaptionText(IdleHint);
                _showingIdleHint = true;
            }

            return;
        }

        if (_showingIdleHint)
        {
            // The hint shares no prefix with real captions; drop it before diffing.
            SetCaptionText(string.Empty);
            _showingIdleHint = false;
        }

        SetCaptionText(Volatile.Read(ref _pendingCaption));
    }

    private void UpdateTitle(bool available)
    {
        var suffix = !available
            ? " \u2014 waiting for Live Captions"
            : IsHoldingForSelection
                ? " \u2014 paused while text is selected"
                : string.Empty;

        if (!string.Equals(suffix, _statusSuffix, StringComparison.Ordinal))
        {
            _statusSuffix = suffix;
            Text = BaseTitle + suffix;
        }
    }

    private void SetCaptionText(string text)
    {
        if (string.Equals(_renderedText, text, StringComparison.Ordinal))
        {
            return;
        }

        var prefix = CommonPrefixLength(_renderedText, text);
        var replacedLength = _renderedText.Length - prefix;

        // Replacing just the tail lets the edit control repaint only the lines that changed,
        // which is what keeps a 25 ms refresh from looking like a strobe. A large replacement
        // means the transcript rolled over, and a plain assignment repaints less than that.
        if (prefix > 0 && replacedLength <= 256)
        {
            ReplaceTail(prefix, replacedLength, text[prefix..]);
        }
        else
        {
            SetRedraw(false);
            try
            {
                _captionTextBox.Text = text;
            }
            finally
            {
                SetRedraw(true);
            }
        }

        _renderedText = text;
        ScrollToBottom();
    }

    private void ReplaceTail(int start, int length, string insertedText)
    {
        // EM_REPLACESEL is ignored while ES_READONLY is set, so the flag comes off for the
        // duration of the programmatic edit only.
        _captionTextBox.ReadOnly = false;
        try
        {
            _captionTextBox.SelectionStart = start;
            _captionTextBox.SelectionLength = length;
            _captionTextBox.SelectedText = insertedText;
        }
        finally
        {
            _captionTextBox.ReadOnly = true;
        }
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var max = Math.Min(left.Length, right.Length);
        var length = 0;
        while (length < max && left[length] == right[length])
        {
            length++;
        }

        return length > 0 && char.IsHighSurrogate(left[length - 1]) ? length - 1 : length;
    }

    private void SetRedraw(bool enabled)
    {
        if (!_captionTextBox.IsHandleCreated)
        {
            return;
        }

        NativeMethods.SendMessage(
            _captionTextBox.Handle,
            NativeMethods.WmSetRedraw,
            enabled ? new IntPtr(1) : IntPtr.Zero,
            IntPtr.Zero);

        if (enabled)
        {
            _captionTextBox.Invalidate();
        }
    }

    private void ScrollToBottom()
    {
        if (!_captionTextBox.IsHandleCreated)
        {
            return;
        }

        NativeMethods.SendMessage(
            _captionTextBox.Handle,
            NativeMethods.WmVScroll,
            new IntPtr(NativeMethods.SbBottom),
            IntPtr.Zero);
    }

    private void ApplySavedPlacement()
    {
        if (!_settings.HasWindowPlacement)
        {
            Width = 680;
            Height = 280;
            StartPosition = FormStartPosition.CenterScreen;
            return;
        }

        StartPosition = FormStartPosition.Manual;
        var bounds = new Rectangle(
            _settings.WindowLeft,
            _settings.WindowTop,
            Math.Max(MinimumSize.Width, _settings.WindowWidth),
            Math.Max(MinimumSize.Height, _settings.WindowHeight));

        // A monitor that was attached last run may be gone now.
        if (!Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds)))
        {
            StartPosition = FormStartPosition.CenterScreen;
            Size = bounds.Size;
            return;
        }

        Bounds = bounds;
    }

    private void SaveSettings()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        CaptionSettingsStore.Save(_settings);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return SystemIcons.Application;
        }
    }
}
