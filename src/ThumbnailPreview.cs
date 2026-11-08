using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace FseMultiMonitor;

internal class WindowThumbnailPreview : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExNoActivate = 0x08000000;
    private const uint DwmwaCloaked = 14;
    private const uint DwmTnpRectDestination = 0x00000001;
    private const uint DwmTnpOpacity = 0x00000004;
    private const uint DwmTnpVisible = 0x00000008;
    private const uint DwmTnpSourceClientAreaOnly = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        internal uint Flags;
        internal DwmRect Destination;
        internal DwmRect Source;
        internal byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] internal bool Visible;
        [MarshalAs(UnmanagedType.Bool)] internal bool SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmRect
    {
        internal int Left, Top, Right, Bottom;
        internal static DwmRect FromLtrb(int left, int top, int right, int bottom) =>
            new() { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsGamingFullScreenExperienceActive();

    private readonly IReadOnlyList<Inspector.WindowState> _sources;
    private readonly List<IntPtr> _thumbnails = [];
    private readonly List<Size> _sourceSizes = [];
    private readonly List<int> _thumbnailSourceIndex = [];
    private readonly List<Rectangle> _destinations = [];
    private readonly List<Label> _occlusionBadges = [];
    private readonly bool _requireFse;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _heading;
    private readonly Label _status = new();
    private readonly Button _fullScreenButton;
    private readonly Button _backButton;
    private readonly Button _exitButton;
    private readonly Button _closeButton;
    private readonly Rectangle _monitorBounds;
    private readonly string _logPath;
    private string _lastSample = string.Empty;
    private string _lastOcclusion = string.Empty;
    private float _uiScale = 1f;
    private bool _fullScreen;

    internal IReadOnlyList<Rectangle> ThumbnailDestinations => _destinations;
    internal int RegisteredThumbnailCount => _thumbnails.Count;

    internal bool ExitRequested { get; private set; }

    internal WindowThumbnailPreview(IReadOnlyList<Inspector.WindowState> sources, int monitorNumber, bool requireFse = true)
    {
        _sources = sources;
        _requireFse = requireFse;
        var screens = Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top).ToArray();
        if (monitorNumber < 1 || monitorNumber > screens.Length)
            throw new ArgumentOutOfRangeException(nameof(monitorNumber), $"Monitor {monitorNumber} is not active; detected {screens.Length} monitor(s).");

        _monitorBounds = screens[monitorNumber - 1].Bounds;
        var bounds = _monitorBounds;
        Text = "FSE MultiMonitor — Vista de ventanas";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = bounds.Location;
        ClientSize = bounds.Size;
        BackColor = Color.FromArgb(12, 18, 26);
        ForeColor = Color.White;
        AutoScaleMode = AutoScaleMode.None;

        _heading = new Label
        {
            Text = "VISTA DE VENTANAS · SOLO VISUAL",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            Location = new Point(18, 12)
        };
        _status.Text = "Iniciando vistas DWM…";
        _status.AutoSize = true;
        _status.ForeColor = Color.Gainsboro;
        _status.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        _status.Location = new Point(20, 39);

        _fullScreenButton = new Button
        {
            Text = "Pantalla completa",
            AccessibleName = "Toggle full screen preview",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 58, 72),
            Size = new Size(160, 34),
            Location = new Point(bounds.Width - 226, 10),
            TabStop = false
        };
        _fullScreenButton.FlatAppearance.BorderSize = 0;
        _fullScreenButton.Click += (_, _) => ToggleFullScreen();

        _closeButton = new Button
        {
            Text = "×",
            AccessibleName = "Close visual mirror",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 58, 72),
            Size = new Size(42, 34),
            Location = new Point(bounds.Width - 56, 10),
            TabStop = false
        };
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.Click += (_, _) => Close();

        _backButton = new Button
        {
            Text = "Volver",
            AccessibleName = "Return to window selector",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 58, 72),
            Size = new Size(70, 34),
            Location = new Point(bounds.Width - 306, 10),
            TabStop = false
        };
        _backButton.FlatAppearance.BorderSize = 0;
        _backButton.Click += (_, _) =>
        {
            ExitRequested = false;
            Close();
        };

        _exitButton = new Button
        {
            Text = "Salir",
            AccessibleName = "Exit FSE Multi-Monitor",
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 58, 72),
            Size = new Size(70, 34),
            Location = new Point(bounds.Width - 382, 10),
            TabStop = false
        };
        _exitButton.FlatAppearance.BorderSize = 0;
        _exitButton.Click += (_, _) =>
        {
            ExitRequested = true;
            Close();
        };
        Controls.AddRange([_heading, _status, _fullScreenButton, _backButton, _exitButton, _closeButton]);

        _logPath = Store.NewSessionPath("preview");
        AppendLog(new
        {
            timeUtc = DateTimeOffset.UtcNow,
            @event = "preview-started",
            sources = sources.Select(source => source.Hwnd).ToArray(),
            monitorNumber,
            visualOnly = true,
            logPath = _logPath
        });

        Shown += (_, _) =>
        {
            _uiScale = Math.Max(1f, DeviceDpi / 96f);
            LayoutChrome();
            try { RegisterThumbnails(monitorNumber); }
            catch (Exception ex)
            {
                AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "preview-error", error = ex.Message });
                Close();
            }
        };
        DpiChanged += (_, _) =>
        {
            _uiScale = Math.Max(1f, DeviceDpi / 96f);
            LayoutChrome();
            try { ApplyThumbnailLayout(); } catch (Exception ex) { AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "layout-error", error = ex.Message }); }
        };
        _timer.Tick += (_, _) => SampleState(monitorNumber);
        FormClosed += (_, _) => Cleanup();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExTopmost | WsExNoActivate;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _uiScale = Math.Max(1f, DeviceDpi / 96f);
        LayoutChrome();
    }

    protected virtual void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
    }

    private void LayoutChrome()
    {
        float scale = _uiScale <= 0 ? 1f : _uiScale;
        int S(int value) => (int)Math.Round(value * scale);

        _heading.Location = new Point(S(18), S(10));
        _status.Location = new Point(S(20), _heading.Top + _heading.PreferredHeight + S(4));

        int top = S(10);
        _fullScreenButton.Bounds = new Rectangle(_monitorBounds.Width - S(226), top, S(160), S(34));
        _backButton.Bounds = new Rectangle(_monitorBounds.Width - S(306), top, S(70), S(34));
        _exitButton.Bounds = new Rectangle(_monitorBounds.Width - S(382), top, S(70), S(34));
        _closeButton.Bounds = new Rectangle(_monitorBounds.Width - S(56), top, S(42), S(34));
    }

    private void RegisterThumbnails(int monitorNumber)
    {
        var cells = CreateCells(_sources.Count, ClientSize.Width, ClientSize.Height);
        bool fseActive;
        try { fseActive = IsGamingFullScreenExperienceActive(); }
        catch { fseActive = false; }
        if (_requireFse && !fseActive) throw new InvalidOperationException("FSE became inactive before the preview overlay opened.");

        for (int i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            IntPtr hwnd = Inspector.ParseHwnd(source.Hwnd);
            int registerResult = DwmRegisterThumbnail(Handle, hwnd, out IntPtr thumbnail);
            if (registerResult < 0)
            {
                AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "thumbnail-skipped", source.Hwnd, registerResult });
                continue;
            }

            int sizeResult = DwmQueryThumbnailSourceSize(thumbnail, out Size sourceSize);
            if (sizeResult < 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "thumbnail-skipped", source.Hwnd, sizeResult });
                continue;
            }

            int slot = _thumbnails.Count;
            Rectangle destination = cells[Math.Min(slot, cells.Length - 1)];
            DwmRect fitted = Fit(sourceSize, destination);
            var properties = new ThumbnailProperties
            {
                Flags = DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly,
                Destination = fitted,
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = true
            };
            int updateResult = DwmUpdateThumbnailProperties(thumbnail, ref properties);
            if (updateResult < 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "thumbnail-skipped", source.Hwnd, updateResult });
                continue;
            }

            _thumbnails.Add(thumbnail);
            _sourceSizes.Add(sourceSize);
            _thumbnailSourceIndex.Add(i);
            _destinations.Add(Rectangle.FromLTRB(fitted.Left, fitted.Top, fitted.Right, fitted.Bottom));
            _occlusionBadges.Add(CreateOcclusionBadge(fitted));
            int sourceGet = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int sourceCloak, sizeof(int));
            AppendLog(new
            {
                timeUtc = DateTimeOffset.UtcNow,
                @event = "thumbnail-registered",
                fseActive,
                source.Hwnd,
                source.ProcessId,
                source.ProcessName,
                sourceCloak = sourceGet == 0 ? sourceCloak : -1,
                overlayHwnd = Inspector.Hwnd(Handle),
                monitorNumber,
                sourceWidth = sourceSize.Width,
                sourceHeight = sourceSize.Height,
                registerResult,
                updateResult
            });
        }

        if (_thumbnails.Count == 0)
            throw new InvalidOperationException("None of the selected windows could be mirrored with a DWM thumbnail.");

        int overlayGet = DwmGetWindowAttribute(Handle, DwmwaCloaked, out int overlayCloak, sizeof(int));
        _status.Text = $"Xbox Mode activo · {_thumbnails.Count} ventana(s) en vista · panel cloak={(overlayGet == 0 ? overlayCloak : -1)} · sin entrada";
        _timer.Start();
        SampleState(monitorNumber);
    }

    private void SampleState(int monitorNumber)
    {
        bool fseActive;
        try { fseActive = IsGamingFullScreenExperienceActive(); }
        catch { fseActive = false; }
        int overlayGet = DwmGetWindowAttribute(Handle, DwmwaCloaked, out int overlayCloak, sizeof(int));
        int overlayValue = overlayGet == 0 ? overlayCloak : -1;
        var sourceStates = new List<object>(_sources.Count);
        bool anySourceAlive = false;
        for (int i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            IntPtr hwnd = Inspector.ParseHwnd(source.Hwnd);
            uint threadId = Native.GetWindowThreadProcessId(hwnd, out uint currentPid);
            int getResult = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloak, sizeof(int));
            bool alive = threadId != 0 && currentPid == source.ProcessId && ProcessStartMatches(source);
            anySourceAlive |= alive;
            sourceStates.Add(new
            {
                source.Hwnd,
                source.ProcessName,
                source.ProcessId,
                alive,
                cloaked = getResult == 0 ? cloak : -1
            });
        }

        string signature = JsonSerializer.Serialize(new { fseActive, overlayValue, sourceStates });
        if (!StringComparer.Ordinal.Equals(signature, _lastSample))
        {
            AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "state", fseActive, overlayCloaked = overlayValue, sources = sourceStates, monitorNumber });
            _lastSample = signature;
            _status.Text = fseActive
                ? $"Xbox Mode activo · {_sources.Count} ventana(s) en vista · panel cloak={overlayValue} · sin entrada"
                : "Xbox Mode inactivo · cerrando vista";
        }

        UpdateOcclusionBadges();

        if ((_requireFse && !fseActive) || overlayValue != 0)
        {
            AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "preview-ended", reason = !fseActive ? "FSE inactive" : $"overlay cloak={overlayValue}" });
            Close();
        }
        else if (!anySourceAlive)
        {
            AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "preview-ended", reason = "all source windows closed" });
            Close();
        }
    }

    private void UpdateOcclusionBadges()
    {
        if (_thumbnailSourceIndex.Count == 0 || _occlusionBadges.Count == 0) return;

        var snapshot = Inspector.Capture(includeTitles: false, includeHashes: false);
        var byHwnd = snapshot.Windows.ToDictionary(window => window.Hwnd, StringComparer.OrdinalIgnoreCase);
        var parts = new List<object>(_thumbnailSourceIndex.Count);
        int count = Math.Min(_thumbnailSourceIndex.Count, _occlusionBadges.Count);
        for (int i = 0; i < count; i++)
        {
            var source = _sources[_thumbnailSourceIndex[i]];
            var badge = _occlusionBadges[i];
            if (!byHwnd.TryGetValue(source.Hwnd, out var current))
            {
                badge.Visible = false;
                continue;
            }

            var result = OcclusionAdvisor.FindOccluder(snapshot.Windows, current, Environment.ProcessId);
            bool browser = OcclusionAdvisor.IsBrowserLike(source.ProcessName);
            bool risky = result.Occluded && browser;
            badge.Text = risky ? $"⏸ Pausada por oclusión · {result.OccluderProcess}" : string.Empty;
            badge.Visible = risky;

            parts.Add(new
            {
                source.Hwnd,
                source.ProcessName,
                occluded = result.Occluded,
                occluder = result.OccluderProcess,
                browser
            });
        }

        string signature = JsonSerializer.Serialize(parts);
        if (!StringComparer.Ordinal.Equals(signature, _lastOcclusion))
        {
            AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "occlusion", sources = parts });
            _lastOcclusion = signature;
        }
    }

    private Label CreateOcclusionBadge(DwmRect fitted)
    {
        int S(int value) => (int)Math.Round(value * (_uiScale <= 0 ? 1f : _uiScale));
        var badge = new Label
        {
            Text = string.Empty,
            AutoSize = false,
            Visible = false,
            BackColor = Color.FromArgb(92, 58, 8),
            ForeColor = Color.FromArgb(255, 214, 120),
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(fitted.Left + S(8), fitted.Top + S(8), S(250), S(28))
        };
        Controls.Add(badge);
        badge.BringToFront();
        return badge;
    }

    private static bool ProcessStartMatches(Inspector.WindowState source)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)source.ProcessId));
            return source.ProcessStartUtc is null ||
                new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) == source.ProcessStartUtc;
        }
        catch { return false; }
    }

    private void Cleanup()
    {
        _timer.Stop();
        _timer.Dispose();
        foreach (IntPtr thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
    }

    private void AppendLog(object record) =>
        File.AppendAllText(_logPath, JsonSerializer.Serialize(record) + Environment.NewLine);

    private Rectangle[] CreateCells(int count, int width, int height)
    {
        if (count <= 0) return Array.Empty<Rectangle>();
        int margin = _fullScreen ? 0 : (int)Math.Round(20 * _uiScale);
        int header = _fullScreen ? 0 : Math.Max((int)Math.Round(72 * _uiScale), _status.Bottom + (int)Math.Round(8 * _uiScale));
        int gutter = _fullScreen ? 0 : (int)Math.Round(12 * _uiScale);
        int columns = count == 1 ? 1 : 2;
        int rows = (count + columns - 1) / columns;
        int cellWidth = (width - 2 * margin - (columns - 1) * gutter) / columns;
        int cellHeight = (height - header - margin - (rows - 1) * gutter) / rows;
        var cells = new Rectangle[count];
        for (int i = 0; i < count; i++)
        {
            int row = i / columns;
            int column = i % columns;
            cells[i] = new Rectangle(margin + column * (cellWidth + gutter), header + row * (cellHeight + gutter), cellWidth, cellHeight);
        }
        return cells;
    }

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        _fullScreenButton.Text = _fullScreen ? "Vista ajustada" : "Pantalla completa";
        _fullScreenButton.AccessibleName = _fullScreen ? "Return to fitted preview" : "Toggle full screen preview";
        try { ApplyThumbnailLayout(); }
        catch (Exception ex)
        {
            AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "layout-error", error = ex.Message });
        }
        _heading.Visible = !_fullScreen;
        _status.Visible = !_fullScreen;
        AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "layout", mode = _fullScreen ? "full-screen" : "fitted" });
    }

    private void ApplyThumbnailLayout()
    {
        if (_thumbnails.Count == 0) return;
        int S(int value) => (int)Math.Round(value * (_uiScale <= 0 ? 1f : _uiScale));
        var cells = CreateCells(_thumbnails.Count, ClientSize.Width, ClientSize.Height);
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            DwmRect destination = Fit(_sourceSizes[i], cells[i]);
            var properties = new ThumbnailProperties
            {
                Flags = DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly,
                Destination = destination,
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = true
            };
            int result = DwmUpdateThumbnailProperties(_thumbnails[i], ref properties);
            if (result < 0)
                AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "layout-thumbnail-skipped", index = i, result });

            if (i < _destinations.Count)
                _destinations[i] = Rectangle.FromLTRB(destination.Left, destination.Top, destination.Right, destination.Bottom);
            if (i < _occlusionBadges.Count)
                _occlusionBadges[i].Bounds = new Rectangle(
                    destination.Left + S(8), destination.Top + S(8), S(250), S(28));
        }
    }

    private static DwmRect Fit(Size source, Rectangle target)
    {
        double scale = Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        int left = target.Left + (target.Width - width) / 2;
        int top = target.Top + (target.Height - height) / 2;
        return DwmRect.FromLtrb(left, top, left + width, top + height);
    }
}
