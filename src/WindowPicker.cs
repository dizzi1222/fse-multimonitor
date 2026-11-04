using System.Drawing;
using System.Windows.Forms;

namespace FseMultiMonitor;

internal sealed class WindowPicker : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExNoActivate = 0x08000000;

    private sealed record WindowChoice(Inspector.WindowState State)
    {
        public override string ToString()
        {
            string title = string.IsNullOrWhiteSpace(State.Title) ? $"[{State.ClassName}]" : State.Title.Trim();
            string visibility = State.Cloaked switch
            {
                2 => "oculta por Xbox Mode",
                1 => "oculta por aplicación",
                4 => "oculta por shell",
                _ when State.Cloaked > 0 => $"oculta (cloak {State.Cloaked})",
                _ => "visible"
            };
            return $"{title}   ·   {State.ProcessName}  ·  PID {State.ProcessId}  ·  {visibility}";
        }
    }

    private sealed record MonitorChoice(int Number, Screen Screen)
    {
        public override string ToString() =>
            $"Monitor {Number}  ·  {Screen.DeviceName}  ·  {Screen.Bounds.Width}×{Screen.Bounds.Height}{(Screen.Primary ? "  ·  principal" : string.Empty)}";
    }

    private readonly CheckedListBox _windows = new();
    private readonly ComboBox _monitors = new();
    private readonly Label _status = new();
    private readonly Label _hint = new();
    private readonly System.Windows.Forms.Timer _fseTimer = new() { Interval = 1000 };
    private bool _previewOpen;

    internal WindowPicker()
    {
        var screens = Screen.AllScreens
            .OrderBy(screen => screen.Bounds.Left)
            .ThenBy(screen => screen.Bounds.Top)
            .Select((screen, index) => new MonitorChoice(index + 1, screen))
            .ToArray();
        if (screens.Length < 2)
            throw new InvalidOperationException("Window selection requires two active monitors.");

        Text = "FSE MultiMonitor — Selector de ventanas";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(800, 600);
        BackColor = Color.FromArgb(16, 22, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            Text = "Elige qué ventanas mostrar",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Location = new Point(24, 18)
        };
        _status.SetBounds(26, 58, 730, 24);
        _status.ForeColor = Color.Gainsboro;
        _hint.Text = "Marca hasta 4 ventanas. La vista del otro monitor es visual; no envía clics ni teclado a la aplicación.";
        _hint.SetBounds(26, 84, 750, 40);
        _hint.ForeColor = Color.FromArgb(177, 195, 211);

        _windows.SetBounds(24, 132, 752, 360);
        _windows.CheckOnClick = true;
        _windows.IntegralHeight = false;
        _windows.BorderStyle = BorderStyle.FixedSingle;
        _windows.BackColor = Color.FromArgb(28, 38, 50);
        _windows.ForeColor = Color.White;
        _windows.Font = new Font("Segoe UI", 10);
        _windows.ItemHeight = 32;

        var monitorLabel = new Label { Text = "Mostrar en:", AutoSize = true, Location = new Point(26, 517), ForeColor = Color.White };
        _monitors.SetBounds(116, 512, 350, 34);
        _monitors.DropDownStyle = ComboBoxStyle.DropDownList;
        _monitors.BackColor = Color.FromArgb(28, 38, 50);
        _monitors.ForeColor = Color.White;
        _monitors.Items.AddRange(screens.Cast<object>().ToArray());
        _monitors.SelectedIndex = Math.Min(1, screens.Length - 1);
        _monitors.SelectedIndexChanged += (_, _) => CenterOnSelectedMonitor();

        var refresh = MakeButton("Actualizar", 480, 511, 100);
        refresh.Click += (_, _) => RefreshWindows();
        var show = MakeButton("Mostrar", 588, 511, 92);
        show.Click += (_, _) => ShowSelectedWindows();
        var close = MakeButton("Cerrar", 688, 511, 88);
        close.Click += (_, _) => Close();
        Controls.AddRange([heading, _status, _hint, _windows, monitorLabel, _monitors, refresh, show, close]);

        Shown += (_, _) =>
        {
            CenterOnSelectedMonitor();
            RefreshWindows();
            _fseTimer.Start();
        };
        _fseTimer.Tick += (_, _) =>
        {
            if (_previewOpen) return;
            if (!FseActive()) Close();
        };
        FormClosed += (_, _) => _fseTimer.Dispose();
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

    private static Button MakeButton(string text, int x, int y, int width)
    {
        var button = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, width, 36),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(45, 68, 91),
            TabStop = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(74, 102, 128);
        return button;
    }

    private void CenterOnSelectedMonitor()
    {
        if (_monitors.SelectedItem is not MonitorChoice choice) return;
        Rectangle bounds = choice.Screen.WorkingArea;
        Location = new Point(bounds.Left + (bounds.Width - Width) / 2, bounds.Top + (bounds.Height - Height) / 2);
    }

    private void RefreshWindows()
    {
        if (!FseActive())
        {
            _status.Text = "Xbox Mode dejó de estar activo.";
            Close();
            return;
        }

        var snapshot = Inspector.Capture(includeTitles: true, includeHashes: false);
        var choices = snapshot.Windows
            .Where(window => window.Visible && !window.Iconic && window.ProcessId != Environment.ProcessId)
            .Where(window => !string.IsNullOrWhiteSpace(window.Title) || !string.IsNullOrWhiteSpace(window.ClassName))
            .OrderBy(window => window.ZOrder)
            .Select(window => new WindowChoice(window))
            .ToArray();

        _windows.BeginUpdate();
        _windows.Items.Clear();
        foreach (var choice in choices) _windows.Items.Add(choice);
        _windows.EndUpdate();
        _status.Text = $"Xbox Mode activo · {Screen.AllScreens.Length} monitores · {choices.Length} ventanas · las marcadas con “oculta por Xbox Mode” se muestran como miniatura DWM.";
    }

    private void ShowSelectedWindows()
    {
        var selected = _windows.CheckedItems.Cast<WindowChoice>().ToArray();
        if (selected.Length is < 1 or > 4)
        {
            _status.Text = "Marca entre 1 y 4 ventanas para mostrar.";
            return;
        }
        if (_monitors.SelectedItem is not MonitorChoice monitor) return;

        var snapshot = Inspector.Capture(includeTitles: true, includeHashes: false);
        var currentByHwnd = snapshot.Windows.ToDictionary(window => window.Hwnd, StringComparer.OrdinalIgnoreCase);
        var currentSources = new List<Inspector.WindowState>(selected.Length);
        foreach (var choice in selected)
        {
            var old = choice.State;
            if (!currentByHwnd.TryGetValue(old.Hwnd, out var current) || current.ProcessId != old.ProcessId ||
                current.ProcessStartUtc != old.ProcessStartUtc || !current.Visible || current.Iconic)
            {
                _status.Text = "Una ventana cambió o se cerró. Pulsa Actualizar y vuelve a elegir.";
                return;
            }
            currentSources.Add(current);
        }

        Hide();
        _previewOpen = true;
        try
        {
            using var preview = new WindowThumbnailPreview(currentSources, monitor.Number);
            preview.ShowDialog();
        }
        catch (Exception ex)
        {
            _status.Text = $"No se pudo abrir la vista: {ex.Message}";
        }
        finally
        {
            _previewOpen = false;
        }

        if (FseActive())
        {
            Show();
            RefreshWindows();
        }
        else Close();
    }

    private static bool FseActive()
    {
        try { return Native.IsGamingFullScreenExperienceActive(); }
        catch { return false; }
    }
}
