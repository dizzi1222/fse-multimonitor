using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace FseMultiMonitor;

internal sealed class WindowThumbnailPreview : Form
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
        internal Rectangle Destination;
        internal Rectangle Source;
        internal byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] internal bool Visible;
        [MarshalAs(UnmanagedType.Bool)] internal bool SourceClientAreaOnly;
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
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _status = new();
    private readonly Button _fullScreenButton;
    private readonly string _logPath;
    private string _lastSample = string.Empty;
    private bool _fullScreen;

    internal WindowThumbnailPreview(IReadOnlyList<Inspector.WindowState> sources, int monitorNumber)
    {
        _sources = sources;
        var screens = Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top).ToArray();
        if (monitorNumber < 1 || monitorNumber > screens.Length)
            throw new ArgumentOutOfRangeException(nameof(monitorNumber), $"Monitor {monitorNumber} is not active; detected {screens.Length} monitor(s).");

        var bounds = screens[monitorNumber - 1].Bounds;
        Text = "FSE MultiMonitor — Vista de ventanas";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Location = bounds.Location;
        ClientSize = bounds.Size;
        BackColor = Color.FromArgb(12, 18, 26);
        ForeColor = Color.White;

        var heading = new Label
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

        var close = new Button
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
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        Controls.AddRange([heading, _status, _fullScreenButton, close]);

        _logPath = Store.NewSessionPath("preview");
        Console.WriteLine($"Showing {sources.Count} DWM thumbnail(s) on display {monitorNumber}; visual only, no input forwarding.");
        Console.WriteLine($"Close the overlay or exit FSE to stop it. JSONL log: {_logPath}");

        Shown += (_, _) =>
        {
            try { RegisterThumbnails(monitorNumber); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not start the visual preview: {ex.Message}");
                AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "preview-error", error = ex.Message });
                Close();
            }
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

    private void RegisterThumbnails(int monitorNumber)
    {
        var cells = CreateCells(_sources.Count, ClientSize.Width, ClientSize.Height);
        bool fseActive;
        try { fseActive = IsGamingFullScreenExperienceActive(); }
        catch { fseActive = false; }
        if (!fseActive) throw new InvalidOperationException("FSE became inactive before the preview overlay opened.");

        for (int i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            IntPtr hwnd = Inspector.ParseHwnd(source.Hwnd);
            int registerResult = DwmRegisterThumbnail(Handle, hwnd, out IntPtr thumbnail);
            if (registerResult < 0)
                throw new InvalidOperationException($"DwmRegisterThumbnail failed for {source.Hwnd} (HRESULT 0x{registerResult:x8}).");

            int sizeResult = DwmQueryThumbnailSourceSize(thumbnail, out Size sourceSize);
            if (sizeResult < 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                throw new InvalidOperationException($"Could not query DWM thumbnail size for {source.Hwnd} (HRESULT 0x{sizeResult:x8}).");
            }

            Rectangle destination = Fit(sourceSize, cells[i]);
            var properties = new ThumbnailProperties
            {
                Flags = DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly,
                Destination = destination,
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = true
            };
            int updateResult = DwmUpdateThumbnailProperties(thumbnail, ref properties);
            if (updateResult < 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                throw new InvalidOperationException($"DwmUpdateThumbnailProperties failed for {source.Hwnd} (HRESULT 0x{updateResult:x8}).");
            }

            _thumbnails.Add(thumbnail);
            _sourceSizes.Add(sourceSize);
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

        int overlayGet = DwmGetWindowAttribute(Handle, DwmwaCloaked, out int overlayCloak, sizeof(int));
        _status.Text = $"Xbox Mode activo · {_sources.Count} ventana(s) en vista · panel cloak={(overlayGet == 0 ? overlayCloak : -1)} · sin entrada";
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

        if (!fseActive || overlayValue != 0)
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
        int margin = _fullScreen ? 0 : 20;
        int header = _fullScreen ? 0 : 72;
        int gutter = _fullScreen ? 0 : 12;
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
            _fullScreen = !_fullScreen;
            _fullScreenButton.Text = _fullScreen ? "Vista ajustada" : "Pantalla completa";
            _status.Text = $"No se pudo cambiar el tamaño de la vista: {ex.Message}";
            return;
        }
        AppendLog(new { timeUtc = DateTimeOffset.UtcNow, @event = "layout", mode = _fullScreen ? "full-screen" : "fitted" });
    }

    private void ApplyThumbnailLayout()
    {
        var cells = CreateCells(_sources.Count, ClientSize.Width, ClientSize.Height);
        for (int i = 0; i < _thumbnails.Count; i++)
        {
            Rectangle destination = Fit(_sourceSizes[i], cells[i]);
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
                throw new InvalidOperationException($"Could not resize a DWM thumbnail (HRESULT 0x{result:x8}).");
        }
    }

    private static Rectangle Fit(Size source, Rectangle target)
    {
        double scale = Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle(target.Left + (target.Width - width) / 2, target.Top + (target.Height - height) / 2, width, height);
    }
}
