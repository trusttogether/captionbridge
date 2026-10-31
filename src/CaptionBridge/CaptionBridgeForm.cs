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

    private static readonly Color Background = Color.FromArgb(21, 25, 34);
    private static readonly Color Foreground = Color.FromArgb(244, 247, 251);
    private static readonly Color ControlBarBackground = Color.FromArgb(32, 38, 50);

    private readonly CaptionTextBox _captionTextBox = new();
    private readonly Panel _controlBar = new();
    private readonly TrackBar _fontSizeTrackBar = new();
    private readonly TrackBar _opacityTrackBar = new();
    private readonly CheckBox _alwaysOnTopCheckBox = new();
    private readonly Button _clearButton = new();
    private readonly CaptionSettings _settings;
    private readonly LiveCaptionReader _reader;
    private readonly bool _autoStartLiveCaptions;

    /// <summary>
    /// Grown on the reader thread, so that no reading of the Live Captions window is ever
    /// skipped: the transcript is built from every one of them, not from whichever happened to
    /// be the latest when the window got around to repainting.
    /// </summary>
    private readonly CaptionTranscript _transcript = new();
    private readonly object _transcriptLock = new();

    private ToolStripMenuItem? _alwaysOnTopMenuItem;
    private bool _syncingAlwaysOnTop;

    private string _pendingCaption = string.Empty;
    private bool _pendingAvailable;
    private int _flushQueued;

    private string _renderedText = string.Empty;
    private string _statusSuffix = string.Empty;

    public CaptionBridgeForm(bool autoStartLiveCaptions)
    {
        _autoStartLiveCaptions = autoStartLiveCaptions;
        _settings = CaptionSettingsStore.Load();
        _reader = new LiveCaptionReader(_settings.PollIntervalMs);

        InitializeUi();

        _reader.CaptionChanged += OnCaptionChanged;
        _reader.AvailabilityChanged += OnAvailabilityChanged;
    }

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

        if (_autoStartLiveCaptions)
        {
            _ = StartLiveCaptionsAsync();
        }

        _reader.Start();
    }

    private async Task StartLiveCaptionsAsync()
    {
        // Reading the process list and asking the shell to start a program are both slow enough
        // to hold up the first paint, and the reader picks the window up whenever it turns up.
        await Task.Run(LiveCaptionsLauncher.EnsureRunning);

        // Live Captions is itself an always-on-top window, and it comes up in front of this
        // one. Once it has had time to appear, this window takes the front back.
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (!IsDisposed && _settings.AlwaysOnTop)
        {
            ApplyAlwaysOnTop(true);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutUi();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
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

        _captionTextBox.ReadOnly = true;
        _captionTextBox.HideSelection = false;
        _captionTextBox.WordWrap = true;
        _captionTextBox.ScrollBars = RichTextBoxScrollBars.Vertical;

        // A caption is speech, not a document: turning words into clickable links would only
        // repaint text the mirror is about to replace anyway.
        _captionTextBox.DetectUrls = false;
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

        _alwaysOnTopCheckBox.Text = "Always on top";
        _alwaysOnTopCheckBox.Checked = _settings.AlwaysOnTop;
        _alwaysOnTopCheckBox.AutoSize = true;
        _alwaysOnTopCheckBox.BackColor = ControlBarBackground;
        _alwaysOnTopCheckBox.ForeColor = Foreground;

        // The themed tick is drawn on a light background whatever the control's colours say,
        // so the flat one, which takes them, is the only version that reads on a dark bar.
        _alwaysOnTopCheckBox.FlatStyle = FlatStyle.Flat;
        _alwaysOnTopCheckBox.CheckedChanged += (_, _) => ApplyAlwaysOnTop(_alwaysOnTopCheckBox.Checked);

        _clearButton.Text = "Clear";
        _clearButton.Font = new Font("Segoe UI", 8f, FontStyle.Regular);
        _clearButton.Padding = new Padding(4, 0, 4, 0);
        _clearButton.FlatStyle = FlatStyle.Flat;
        _clearButton.BackColor = ControlBarBackground;
        _clearButton.ForeColor = Foreground;
        _clearButton.FlatAppearance.BorderColor = Color.FromArgb(90, 100, 120);
        _clearButton.TabStop = false;
        _clearButton.Click += (_, _) => ClearCaptions();

        _controlBar.Controls.Add(_fontSizeTrackBar);
        _controlBar.Controls.Add(_opacityTrackBar);
        _controlBar.Controls.Add(_alwaysOnTopCheckBox);
        _controlBar.Controls.Add(_clearButton);
        Controls.Add(_captionTextBox);
        Controls.Add(_controlBar);

        ContextMenuStrip = BuildContextMenu();
        _captionTextBox.ContextMenuStrip = ContextMenuStrip;

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
        const int edge = 8;
        var checkBoxWidth = _alwaysOnTopCheckBox.PreferredSize.Width;
        var clearSize = _clearButton.PreferredSize;
        var clearWidth = clearSize.Width;
        var clearHeight = Math.Min(18, clearSize.Height);
        var forTrackBars = _controlBar.ClientSize.Width - (edge * 2) - (gap * 3) - checkBoxWidth - clearWidth;
        var trackBarWidth = Math.Max(52, forTrackBars / 2);

        _fontSizeTrackBar.SetBounds(edge, 4, trackBarWidth, 22);
        _opacityTrackBar.SetBounds(_fontSizeTrackBar.Right + gap, 4, trackBarWidth, 22);
        _alwaysOnTopCheckBox.Location = new Point(
            _opacityTrackBar.Right + gap,
            (ControlBarHeight - _alwaysOnTopCheckBox.PreferredSize.Height) / 2);
        _clearButton.SetBounds(
            _alwaysOnTopCheckBox.Right + gap,
            (ControlBarHeight - clearHeight) / 2,
            clearWidth,
            clearHeight);

        ApplyTextInset();

        // Re-wrapping at the new width moves the text under the scroll position.
        _captionTextBox.ScrollToBottom();
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
        alwaysOnTop.CheckedChanged += (_, _) => ApplyAlwaysOnTop(alwaysOnTop.Checked);
        _alwaysOnTopMenuItem = alwaysOnTop;

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Copy", null, (_, _) => CopySelection()));
        menu.Items.Add(new ToolStripMenuItem("Select all", null, (_, _) => _captionTextBox.SelectAllText()));
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
                _captionTextBox.SelectAllText();
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
        var text = _captionTextBox.GetTextToCopy();
        if (text.Length > 0)
        {
            Clipboard.SetText(text);
        }
    }

    private void ClearCaptions()
    {
        lock (_transcriptLock)
        {
            _transcript.Clear();
        }

        Volatile.Write(ref _pendingCaption, string.Empty);
        _captionTextBox.ClearTranscript();
        _renderedText = string.Empty;
    }

    private void ApplyFontSize(int fontSize)
    {
        _settings.FontSize = fontSize;
        _captionTextBox.Font = new Font("Segoe UI", fontSize, FontStyle.Regular);
        ApplyTextInset();
        _captionTextBox.ScrollToBottom();
    }

    /// <summary>
    /// Keeps the setting, the checkbox and the menu entry saying the same thing, whichever of
    /// them was used.
    /// </summary>
    private void ApplyAlwaysOnTop(bool onTop)
    {
        if (_syncingAlwaysOnTop)
        {
            return;
        }

        _syncingAlwaysOnTop = true;
        try
        {
            _settings.AlwaysOnTop = onTop;

            // Assigning the value it already has does nothing, so it is cleared first. That is
            // what puts the window back above anything since placed in front of it.
            TopMost = false;
            TopMost = onTop;

            _alwaysOnTopCheckBox.Checked = onTop;
            if (_alwaysOnTopMenuItem is not null)
            {
                _alwaysOnTopMenuItem.Checked = onTop;
            }
        }
        finally
        {
            _syncingAlwaysOnTop = false;
        }
    }

    private void ApplyOpacity(int opacityPercent)
    {
        _settings.OpacityPercent = opacityPercent;
        Opacity = opacityPercent / 100.0;
    }

    private void OnCaptionChanged(object? sender, string caption)
    {
        string transcript;
        lock (_transcriptLock)
        {
            transcript = _transcript.Absorb(caption);
        }

        Volatile.Write(ref _pendingCaption, transcript);
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

        UpdateTitle(Volatile.Read(ref _pendingAvailable));

        // The transcript stands whether or not Live Captions is still running. Closing it stops
        // the text growing rather than taking it away; the title bar carries the explanation.
        SetCaptionText(Volatile.Read(ref _pendingCaption));
    }

    private void UpdateTitle(bool available)
    {
        var suffix = available ? string.Empty : " \u2014 waiting for Live Captions";

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

        _captionTextBox.ApplyTranscript(_renderedText, text);
        _renderedText = text;

        // Pulling the view down mid-drag would slide the words out from under the pointer.
        if (!_captionTextBox.IsSelecting)
        {
            _captionTextBox.ScrollToBottom();
        }
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
