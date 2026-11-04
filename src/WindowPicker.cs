using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FseMultiMonitor;

internal sealed class WindowPicker : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTopmost = 0x00000008;
    private const int WsExNoActivate = 0x08000000;
    private const int PreviewPageSize = 6;
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

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Native.Rect rect);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUnregisterThumbnail(IntPtr thumbnail);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref ThumbnailProperties properties);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out Size size);

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
            return $"{title}   ·   {State.ProcessName}  ·  {visibility}";
        }
    }

    private sealed record MonitorChoice(int Number, Screen Screen)
    {
        public override string ToString() =>
            $"Monitor {Number}  ·  {Screen.DeviceName}  ·  {Screen.Bounds.Width}×{Screen.Bounds.Height}{(Screen.Primary ? "  ·  principal" : string.Empty)}";
    }

    private readonly CheckedListBox _windows = new();
    private readonly ComboBox _monitors = new();
    private readonly Label _pageLabel = new();
    private readonly Label _status = new();
    private readonly Label _hint = new();
    private readonly Button _updateButton;
    private readonly System.Windows.Forms.Timer _fseTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _centerTimer = new() { Interval = 400 };
    private readonly List<(IntPtr Thumbnail, string Hwnd)> _gridThumbnails = [];
    private Rectangle _previewBounds;
    private int _page;
    private int _pageCount = 1;
    private bool _previewOpen;
    private bool _refreshing;
    private int _fseInactiveTicks;
    private UpdateInfo? _pendingUpdate;

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
        Location = new Point(screens[1].Screen.WorkingArea.Left + 100, screens[1].Screen.WorkingArea.Top + 100);
        ClientSize = new Size(1040, 600);
        BackColor = Color.FromArgb(16, 22, 30);
        ForeColor = Color.White;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            Text = "Elige qué ventanas mostrar",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Location = new Point(24, 18)
        };
        _status.SetBounds(26, 58, 990, 24);
        _status.ForeColor = Color.Gainsboro;
        _hint.Text = "Marca hasta 4 ventanas. La vista del otro monitor es visual; no envía clics ni teclado a la aplicación.";
        _hint.SetBounds(26, 84, 990, 40);
        _hint.ForeColor = Color.FromArgb(177, 195, 211);

        _windows.SetBounds(24, 128, 560, 392);
        _windows.CheckOnClick = true;
        _windows.IntegralHeight = false;
        _windows.BorderStyle = BorderStyle.FixedSingle;
        _windows.BackColor = Color.FromArgb(28, 38, 50);
        _windows.ForeColor = Color.White;
        _windows.Font = new Font("Segoe UI", 10);
        _windows.ItemHeight = 40;
        _windows.DrawMode = DrawMode.OwnerDrawFixed;
        _windows.DrawItem += DrawWindowItem;
        _windows.SelectedIndexChanged += (_, _) => OnWindowSelected();

        var previewLabel = new Label
        {
            Text = "Vista previa",
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11, FontStyle.Bold),
            Location = new Point(604, 100)
        };

        var previousPage = MakeSmallButton("‹", 872, 98, 32);
        previousPage.AccessibleName = "Previous preview page";
        previousPage.Click += (_, _) => ChangePage(-1);
        var nextPage = MakeSmallButton("›", 948, 98, 32);
        nextPage.AccessibleName = "Next preview page";
        nextPage.Click += (_, _) => ChangePage(1);
        _pageLabel.Text = "1/1";
        _pageLabel.SetBounds(906, 102, 40, 20);
        _pageLabel.TextAlign = ContentAlignment.MiddleCenter;
        _pageLabel.ForeColor = Color.Gainsboro;
        _pageLabel.Font = new Font("Segoe UI", 9);

        var monitorLabel = new Label { Text = "Mostrar en:", AutoSize = true, Location = new Point(26, 550), ForeColor = Color.White };
        _monitors.SetBounds(120, 544, 340, 34);
        _monitors.DropDownStyle = ComboBoxStyle.DropDownList;
        _monitors.BackColor = Color.FromArgb(28, 38, 50);
        _monitors.ForeColor = Color.White;
        _monitors.Items.AddRange(screens.Cast<object>().ToArray());
        _monitors.SelectedIndex = Math.Min(1, screens.Length - 1);
        _monitors.SelectedIndexChanged += (_, _) => CenterOnSelectedMonitor();

        var refresh = MakeButton("Actualizar", 470, 544, 96);
        refresh.Click += (_, _) => RefreshWindows();
        var show = MakeButton("Mostrar", 574, 544, 100);
        show.Click += (_, _) => ShowSelectedWindows();
        var close = MakeButton("Salir", 682, 544, 80);
        close.Click += (_, _) => Close();
        _updateButton = MakeButton("Buscar actualización", 770, 544, 246);
        _updateButton.Click += async (_, _) => await HandleUpdateAsync();

        Controls.AddRange([
            heading, _status, _hint, _windows, previewLabel, previousPage, _pageLabel, nextPage,
            monitorLabel, _monitors, refresh, show, close, _updateButton
        ]);
        Paint += PaintPreviewPanel;
        MouseDown += (_, e) => SelectCardAt(e.Location);

        Shown += (_, _) =>
        {
            CenterOnSelectedMonitor();
            LayoutPreview();
            RefreshWindows();
            _fseTimer.Start();
            _centerTimer.Start();
        };
        _centerTimer.Tick += (_, _) =>
        {
            _centerTimer.Stop();
            CenterOnSelectedMonitor();
            LayoutPreview();
            UpdatePreviewGrid();
        };
        DpiChanged += (_, _) => BeginInvoke((Action)(() =>
        {
            CenterOnSelectedMonitor();
            LayoutPreview();
            UpdatePreviewGrid();
        }));
        _fseTimer.Tick += (_, _) =>
        {
            if (_previewOpen) return;
            if (FseActive())
            {
                _fseInactiveTicks = 0;
                return;
            }
            if (++_fseInactiveTicks >= 3) Close();
        };
        FormClosed += (_, _) =>
        {
            _fseTimer.Dispose();
            _centerTimer.Dispose();
            UnregisterGrid();
        };
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

    private static Button MakeSmallButton(string text, int x, int y, int width)
    {
        var button = MakeButton(text, x, y, width);
        button.Height = 26;
        button.Font = new Font("Segoe UI", 9);
        return button;
    }

    private void CenterOnSelectedMonitor()
    {
        if (_monitors.SelectedItem is not MonitorChoice choice) return;
        Rectangle bounds = choice.Screen.WorkingArea;
        if (IsHandleCreated && Native.GetWindowRect(Handle, out Native.Rect current))
        {
            int width = current.Right - current.Left;
            int height = current.Bottom - current.Top;
            int x = bounds.Left + (bounds.Width - width) / 2;
            int y = bounds.Top + (bounds.Height - height) / 2;
            Native.SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0,
                Native.SwpNoSize | Native.SwpNoZOrder | Native.SwpNoActivate);
            return;
        }
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
            .Where(window => window.Cloaked is 0 or 2)
            .Where(window => !string.IsNullOrWhiteSpace(window.Title))
            .Where(window => !IsShellHelper(window))
            .OrderBy(window => window.ZOrder)
            .Select(window => new WindowChoice(window))
            .ToArray();

        _refreshing = true;
        _windows.BeginUpdate();
        try
        {
            _windows.Items.Clear();
            foreach (var choice in choices) _windows.Items.Add(choice);
        }
        finally
        {
            _windows.EndUpdate();
            _refreshing = false;
        }

        _page = 0;
        UpdatePreviewGrid();
        if (_windows.Items.Count > 0) _windows.SelectedIndex = 0;

        _status.Text = $"Xbox Mode activo · {Screen.AllScreens.Length} monitores · {choices.Length} ventanas · las marcadas con “oculta por Xbox Mode” se muestran como miniatura DWM.";
    }

    private void OnWindowSelected()
    {
        if (_refreshing) return;
        int index = _windows.SelectedIndex;
        if (index < 0)
        {
            Invalidate();
            return;
        }

        int page = index / PreviewPageSize;
        if (page != _page)
        {
            _page = page;
            UpdatePreviewGrid();
        }
        else
        {
            Invalidate();
        }
    }

    private void UpdatePreviewGrid()
    {
        if (!IsHandleCreated) return;
        if (_previewBounds.IsEmpty) LayoutPreview();

        UnregisterGrid();
        int count = _windows.Items.Count;
        _pageCount = Math.Max(1, (count + PreviewPageSize - 1) / PreviewPageSize);
        _page = Math.Clamp(_page, 0, _pageCount - 1);
        _pageLabel.Text = $"{_page + 1}/{_pageCount}";
        if (count == 0)
        {
            Invalidate();
            return;
        }

        float scale = _previewBounds.Width / 412f;
        for (int slot = 0; slot < PreviewPageSize; slot++)
        {
            int index = _page * PreviewPageSize + slot;
            if (index >= count) break;

            var choice = (WindowChoice)_windows.Items[index];
            var target = ThumbnailRect(CardRect(slot, scale), scale);
            IntPtr source = Inspector.ParseHwnd(choice.State.Hwnd);
            int result = DwmRegisterThumbnail(Handle, source, out IntPtr thumbnail);
            if (result < 0) continue;

            int sizeResult = DwmQueryThumbnailSourceSize(thumbnail, out Size sourceSize);
            if (sizeResult < 0 || sourceSize.Width <= 0 || sourceSize.Height <= 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                continue;
            }

            var properties = new ThumbnailProperties
            {
                Flags = DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly,
                Destination = Fit(sourceSize, target),
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = true
            };
            if (DwmUpdateThumbnailProperties(thumbnail, ref properties) < 0)
            {
                DwmUnregisterThumbnail(thumbnail);
                continue;
            }

            _gridThumbnails.Add((thumbnail, choice.State.Hwnd));
        }

        Invalidate();
    }

    private void ChangePage(int delta)
    {
        int page = Math.Clamp(_page + delta, 0, _pageCount - 1);
        if (page == _page) return;
        _page = page;
        UpdatePreviewGrid();
    }

    private void SelectCardAt(Point location)
    {
        if (_previewBounds.IsEmpty || !_previewBounds.Contains(location) || _windows.Items.Count == 0) return;
        float scale = _previewBounds.Width / 412f;
        for (int slot = 0; slot < PreviewPageSize; slot++)
        {
            int index = _page * PreviewPageSize + slot;
            if (index >= _windows.Items.Count) break;
            if (CardRect(slot, scale).Contains(location))
            {
                _windows.SelectedIndex = index;
                return;
            }
        }
    }

    private void LayoutPreview()
    {
        if (!IsHandleCreated) return;
        if (!GetClientRect(Handle, out Native.Rect client)) return;
        int width = client.Right - client.Left;
        if (width <= 0) return;
        float scale = width / 1040f;
        _previewBounds = Rectangle.FromLTRB(
            (int)Math.Round(604 * scale),
            (int)Math.Round(128 * scale),
            (int)Math.Round((604 + 412) * scale),
            (int)Math.Round((128 + 392) * scale));
    }

    private Rectangle CardRect(int slot, float scale)
    {
        int column = slot % 2;
        int row = slot / 2;
        int x = _previewBounds.Left + (int)Math.Round(column * 206 * scale);
        int y = _previewBounds.Top + (int)Math.Round(row * 130 * scale);
        return new Rectangle(x, y, (int)Math.Round(200 * scale), (int)Math.Round(124 * scale));
    }

    private static Rectangle ThumbnailRect(Rectangle card, float scale)
    {
        int inset = (int)Math.Round(4 * scale);
        return new Rectangle(card.Left + inset, card.Top + inset,
            card.Width - 2 * inset, (int)Math.Round(108 * scale));
    }

    private void UnregisterGrid()
    {
        foreach (var (thumbnail, _) in _gridThumbnails) DwmUnregisterThumbnail(thumbnail);
        _gridThumbnails.Clear();
    }

    private void DrawWindowItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _windows.Items.Count) return;
        e.DrawBackground();
        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool isChecked = _windows.GetItemChecked(e.Index);
        var box = new Rectangle(e.Bounds.Left + 6, e.Bounds.Top + (e.Bounds.Height - 18) / 2, 18, 18);
        ControlPaint.DrawCheckBox(e.Graphics, box, isChecked ? ButtonState.Checked : ButtonState.Normal);
        var textRect = new Rectangle(box.Right + 8, e.Bounds.Top, e.Bounds.Right - box.Right - 12, e.Bounds.Height);
        Color color = selected ? SystemColors.HighlightText : _windows.ForeColor;
        TextRenderer.DrawText(e.Graphics, _windows.Items[e.Index]?.ToString() ?? string.Empty, _windows.Font, textRect, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
    }

    private static bool IsShellHelper(Inspector.WindowState window) =>
        string.Equals(window.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
        !window.ClassName.Contains("CabinetWClass", StringComparison.OrdinalIgnoreCase) &&
        !window.ClassName.Contains("ExploreWClass", StringComparison.OrdinalIgnoreCase);

    private void PaintPreviewPanel(object? sender, PaintEventArgs e)
    {
        if (_previewBounds.IsEmpty) return;
        int count = _windows.Items.Count;
        if (count == 0)
        {
            using var emptyFont = new Font("Segoe UI", 10);
            TextRenderer.DrawText(e.Graphics, "No hay ventanas disponibles.", emptyFont, _previewBounds,
                Color.FromArgb(120, 145, 168),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }

        float scale = _previewBounds.Width / 412f;
        using var titleFont = new Font("Segoe UI", 7.5f);
        for (int slot = 0; slot < PreviewPageSize; slot++)
        {
            int index = _page * PreviewPageSize + slot;
            if (index >= count) break;

            var choice = (WindowChoice)_windows.Items[index];
            var card = CardRect(slot, scale);
            using (var fill = new SolidBrush(Color.FromArgb(20, 27, 36)))
                e.Graphics.FillRectangle(fill, card);

            bool selected = index == _windows.SelectedIndex;
            bool isChecked = _windows.GetItemChecked(index);
            using (var border = new Pen(selected ? Color.FromArgb(57, 208, 255) : Color.FromArgb(58, 78, 96), selected ? 2f : 1f))
                e.Graphics.DrawRectangle(border, card.Left, card.Top, card.Width - 1, card.Height - 1);

            string title = string.IsNullOrWhiteSpace(choice.State.Title) ? $"[{choice.State.ClassName}]" : choice.State.Title.Trim();
            var titleRect = new Rectangle(
                card.Left + (int)Math.Round(6 * scale),
                card.Top + (int)Math.Round(112 * scale),
                card.Width - (int)Math.Round(12 * scale),
                (int)Math.Round(10 * scale));
            TextRenderer.DrawText(e.Graphics, title, titleFont, titleRect,
                isChecked ? Color.FromArgb(120, 220, 255) : Color.FromArgb(225, 232, 238),
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.Left);

            if (isChecked)
            {
                var checkRect = new Rectangle(
                    card.Left + (int)Math.Round(6 * scale),
                    card.Top + (int)Math.Round(4 * scale),
                    (int)Math.Round(18 * scale),
                    (int)Math.Round(18 * scale));
                TextRenderer.DrawText(e.Graphics, "✓", titleFont, checkRect,
                    Color.FromArgb(57, 208, 255), TextFormatFlags.NoPrefix);
            }
        }
    }

    private async Task HandleUpdateAsync()
    {
        if (_pendingUpdate is null)
        {
            _updateButton.Enabled = false;
            _status.Text = "Buscando la última versión en GitHub…";
            try
            {
                var update = await UpdateChecker.CheckAsync();
                if (update is null)
                {
                    _status.Text = $"Ya tienes la última versión (v{UpdateChecker.CurrentVersion}).";
                }
                else
                {
                    _pendingUpdate = update;
                    _updateButton.Text = $"Instalar v{update.Version}";
                    _status.Text = $"Nueva versión v{update.Version} disponible. Pulsa Instalar para descargarla y actualizar en el sitio.";
                }
            }
            catch (Exception ex)
            {
                _status.Text = $"No se pudo comprobar la actualización: {ex.Message}";
            }
            finally
            {
                _updateButton.Enabled = true;
            }
            return;
        }

        _updateButton.Enabled = false;
        _status.Text = $"Descargando v{_pendingUpdate.Version} y verificando SHA256…";
        try
        {
            string setupPath = await UpdateChecker.DownloadAndVerifyAsync(_pendingUpdate);
            _status.Text = "Iniciando el instalador…";
            UpdateChecker.LaunchInstaller(setupPath);
            Application.Exit();
        }
        catch (Exception ex)
        {
            _status.Text = $"La actualización falló: {ex.Message}";
            _updateButton.Enabled = true;
        }
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
            if (preview.ExitRequested)
            {
                Close();
                return;
            }
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

    private static DwmRect Fit(Size source, Rectangle target)
    {
        double scale = Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        int left = target.Left + (target.Width - width) / 2;
        int top = target.Top + (target.Height - height) / 2;
        return DwmRect.FromLtrb(left, top, left + width, top + height);
    }

    private static bool FseActive()
    {
        try { return Native.IsGamingFullScreenExperienceActive(); }
        catch { return false; }
    }
}
