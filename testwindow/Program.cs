using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace FseTestWindow;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetCompatibleTextRenderingDefault(false);
        bool tryOwnerUncloak = Environment.GetCommandLineArgs().Contains("--try-owner-uncloak", StringComparer.OrdinalIgnoreCase);
        bool overlay = Environment.GetCommandLineArgs().Contains("--overlay", StringComparer.OrdinalIgnoreCase);
        bool preview = Environment.GetCommandLineArgs().Contains("--preview", StringComparer.OrdinalIgnoreCase);
        Application.Run(preview ? new ThumbnailPreviewForm() : new FocusTestForm(tryOwnerUncloak, overlay));
    }

    private sealed class FocusTestForm : Form
    {
        private const uint DwmwaCloak = 13;
        private const uint DwmwaCloaked = 14;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopmost = 0x00000008;
        private const int WsExNoActivate = 0x08000000;
        private readonly TextBox _input = new();
        private readonly Label _status = new();
        private readonly System.Windows.Forms.Timer? _probeTimer;
        private readonly bool _overlay;
        private int _clicks;
        private bool _complete;
        private string? _logPath;

        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);

        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);

        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsGamingFullScreenExperienceActive();

        internal FocusTestForm(bool tryOwnerUncloak, bool overlay, bool startOnSecondary = false)
        {
            _overlay = overlay;
            Text = overlay ? $"FSE Overlay Visibility Test — PID {Environment.ProcessId}" : $"FSE Focus Test — PID {Environment.ProcessId}";
            StartPosition = overlay || startOnSecondary ? FormStartPosition.Manual : FormStartPosition.CenterScreen;
            MinimumSize = new System.Drawing.Size(600, 300);
            ClientSize = overlay ? new System.Drawing.Size(680, 240) : new System.Drawing.Size(720, 360);
            if (overlay || startOnSecondary)
            {
                var secondary = Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).Skip(1).FirstOrDefault();
                if (secondary is not null)
                    Location = new System.Drawing.Point(secondary.Bounds.Left + (overlay ? 80 : 100), secondary.Bounds.Top + (overlay ? 80 : 100));
                if (overlay)
                {
                    FormBorderStyle = FormBorderStyle.None;
                    ShowInTaskbar = false;
                    TopMost = true;
                    BackColor = System.Drawing.Color.FromArgb(24, 64, 96);
                    ForeColor = System.Drawing.Color.White;
                }
            }

            var heading = new Label
            {
                Text = overlay ? "FSE SECONDARY OVERLAY" : "Secondary display focus test",
                AutoSize = true,
                Font = new System.Drawing.Font("Segoe UI", overlay ? 23 : 17, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(24, 22)
            };
            var instructions = new Label
            {
                Text = overlay
                    ? "Static visual test only. This no-activate topmost window targets display 2. It closes automatically after 30 seconds."
                    : tryOwnerUncloak
                        ? "One DWM uncloak test is running in this window's process. If the shell leaves it visible, click this field and type a short message. The field is never saved."
                        : "Move this window to display 2. Click the test field and type a short message. Then click display 1 and return. The field contents stay here and are never saved.",
                AutoSize = false,
                Size = new System.Drawing.Size(overlay ? 630 : 650, 56),
                Location = new System.Drawing.Point(27, 76)
            };
            _input.Location = new System.Drawing.Point(28, 148);
            _input.Size = new System.Drawing.Size(645, 30);
            _input.AccessibleName = "Temporary text input";
            var button = new Button { Text = "Record click", Location = new System.Drawing.Point(28, 205), Size = new System.Drawing.Size(138, 40) };
            button.AccessibleName = "Record click";
            _status.Text = "Ready";
            _status.AutoSize = true;
            _status.Location = new System.Drawing.Point(187, 216);
            button.Click += (_, _) => { _clicks++; _status.Text = $"Click received: {_clicks}"; };
            _input.TextChanged += (_, _) => _status.Text = $"Input received: {_input.TextLength} characters";
            _input.Enter += (_, _) => _status.Text = "Keyboard focus received by display 2";
            _input.Leave += (_, _) => _status.Text = "Keyboard focus left display 2";
            if (overlay)
            {
                instructions.Font = new System.Drawing.Font("Segoe UI", 12, System.Drawing.FontStyle.Regular);
                _status.Text = "If this panel appears, the overlay surface is visible.";
                _status.AutoSize = false;
                _status.Size = new System.Drawing.Size(630, 34);
                _status.Location = new System.Drawing.Point(27, 156);
                Controls.AddRange([heading, instructions, _status]);
            }
            else
            {
                Controls.AddRange([heading, instructions, _input, button, _status]);
            }

            if (tryOwnerUncloak || overlay)
            {
                string root = Environment.GetEnvironmentVariable("FSE_MM_DATA")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FseMultiMonitor");
                Directory.CreateDirectory(root);
                _logPath = Path.Combine(root, $"{(overlay ? "overlay" : "owner-uncloak")}-{Environment.ProcessId}.jsonl");
                _probeTimer = new System.Windows.Forms.Timer { Interval = 250 };
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                string previous = string.Empty;
                _probeTimer.Tick += (_, _) =>
                {
                    bool fseActive;
                    try { fseActive = IsGamingFullScreenExperienceActive(); }
                    catch { fseActive = false; }
                    int getResult = DwmGetWindowAttribute(Handle, DwmwaCloaked, out int cloaked, sizeof(int));
                    string sample = $"{DateTimeOffset.UtcNow:O}|fse={fseActive}|visible={Visible}|dwmHr=0x{getResult:x8}|cloaked={(getResult == 0 ? cloaked : -1)}|foreground={GetForegroundWindowSafe()}";
                    if (!StringComparer.Ordinal.Equals(sample.Split('|')[1] + "|" + sample.Split('|')[2] + "|" + sample.Split('|')[3] + "|" + sample.Split('|')[4] + "|" + sample.Split('|')[5], previous))
                    {
                        File.AppendAllText(_logPath, sample + Environment.NewLine);
                        previous = sample.Split('|')[1] + "|" + sample.Split('|')[2] + "|" + sample.Split('|')[3] + "|" + sample.Split('|')[4] + "|" + sample.Split('|')[5];
                        _status.Text = $"FSE active: {fseActive}; shell cloak: {(getResult == 0 ? cloaked : -1)}";
                    }
                    if (!_complete && (DateTimeOffset.UtcNow >= deadline || !fseActive))
                    {
                        _complete = true;
                        _probeTimer.Stop();
                        File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O}|event=probe-ended|fse={fseActive}|reason={(fseActive ? "30-second bound" : "FSE inactive")}" + Environment.NewLine);
                        Close();
                    }
                };
                Shown += (_, _) =>
                {
                    bool fseActive;
                    try { fseActive = IsGamingFullScreenExperienceActive(); }
                    catch { fseActive = false; }
                    if (tryOwnerUncloak)
                    {
                        int clearAppCloak = 0;
                        int setResult = DwmSetWindowAttribute(Handle, DwmwaCloak, ref clearAppCloak, sizeof(int));
                        File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O}|event=owner-uncloak-on-shown|fse={fseActive}|setHr=0x{setResult:x8}" + Environment.NewLine);
                    }
                    else
                    {
                        File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O}|event=overlay-shown|fse={fseActive}|owner-uncloak=false" + Environment.NewLine);
                    }
                    _probeTimer.Start();
                };
                FormClosed += (_, _) => _probeTimer.Dispose();
            }
        }

        protected override bool ShowWithoutActivation => _overlay;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                if (_overlay) parameters.ExStyle |= WsExToolWindow | WsExTopmost | WsExNoActivate;
                return parameters;
            }
        }

        private static string GetForegroundWindowSafe() => NativeForegroundWindow().ToString("x");

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr NativeForegroundWindow();
    }

    private sealed class ThumbnailPreviewForm : Form
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
            internal System.Drawing.Rectangle Destination;
            internal System.Drawing.Rectangle Source;
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
        private static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out System.Drawing.Size size);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);

        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsGamingFullScreenExperienceActive();

        private readonly Label _status = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
        private readonly DateTimeOffset _deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        private Form? _source;
        private IntPtr _thumbnail;
        private string? _logPath;
        private string _lastSample = string.Empty;

        internal ThumbnailPreviewForm()
        {
            Text = $"FSE Window Preview — PID {Environment.ProcessId}";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = System.Drawing.Color.FromArgb(24, 64, 96);
            ForeColor = System.Drawing.Color.White;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new System.Drawing.Size(700, 430);
            var secondary = Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).Skip(1).FirstOrDefault();
            if (secondary is not null)
                Location = new System.Drawing.Point(secondary.Bounds.Left + 80, secondary.Bounds.Top + 80);

            var heading = new Label
            {
                Text = "LIVE WINDOW PREVIEW",
                AutoSize = true,
                ForeColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Segoe UI", 20, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(18, 14)
            };
            _status.Text = "Starting DWM thumbnail…";
            _status.AutoSize = true;
            _status.ForeColor = System.Drawing.Color.White;
            _status.Font = new System.Drawing.Font("Segoe UI", 10, System.Drawing.FontStyle.Regular);
            _status.Location = new System.Drawing.Point(20, 57);
            Controls.AddRange([heading, _status]);

            Shown += (_, _) => StartPreview();
            _timer.Tick += (_, _) => SampleState();
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

        private void StartPreview()
        {
            string root = Environment.GetEnvironmentVariable("FSE_MM_DATA")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FseMultiMonitor");
            Directory.CreateDirectory(root);
            _logPath = Path.Combine(root, $"thumbnail-preview-{Environment.ProcessId}.jsonl");

            _source = new FocusTestForm(tryOwnerUncloak: false, overlay: false, startOnSecondary: true);
            _source.Show();
            int registerResult = DwmRegisterThumbnail(Handle, _source.Handle, out _thumbnail);
            int updateResult = -1;
            System.Drawing.Size sourceSize = System.Drawing.Size.Empty;
            if (registerResult >= 0)
            {
                DwmQueryThumbnailSourceSize(_thumbnail, out sourceSize);
                var properties = new ThumbnailProperties
                {
                    Flags = DwmTnpRectDestination | DwmTnpOpacity | DwmTnpVisible | DwmTnpSourceClientAreaOnly,
                    Destination = new System.Drawing.Rectangle(20, 92, 660, 320),
                    Opacity = 255,
                    Visible = true,
                    SourceClientAreaOnly = true
                };
                updateResult = DwmUpdateThumbnailProperties(_thumbnail, ref properties);
            }

            bool fseActive;
            try { fseActive = IsGamingFullScreenExperienceActive(); }
            catch { fseActive = false; }
            int sourceGet = DwmGetWindowAttribute(_source.Handle, DwmwaCloaked, out int sourceCloak, sizeof(int));
            int overlayGet = DwmGetWindowAttribute(Handle, DwmwaCloaked, out int overlayCloak, sizeof(int));
            _status.Text = $"FSE={fseActive}  source cloak={(sourceGet == 0 ? sourceCloak : -1)}  thumbnail=0x{registerResult:x8}/0x{updateResult:x8}";
            File.AppendAllText(_logPath,
                $"{DateTimeOffset.UtcNow:O}|event=thumbnail-start|fse={fseActive}|source=0x{_source.Handle.ToInt64():x}|sourceCloak={(sourceGet == 0 ? sourceCloak : -1)}|overlay=0x{Handle.ToInt64():x}|overlayCloak={(overlayGet == 0 ? overlayCloak : -1)}|registerHr=0x{registerResult:x8}|updateHr=0x{updateResult:x8}|sourceSize={sourceSize.Width}x{sourceSize.Height}" + Environment.NewLine);
            _timer.Start();
        }

        private void SampleState()
        {
            bool fseActive;
            try { fseActive = IsGamingFullScreenExperienceActive(); }
            catch { fseActive = false; }
            int sourceCloak = -1;
            int sourceGet = _source is null ? -1 : DwmGetWindowAttribute(_source.Handle, DwmwaCloaked, out sourceCloak, sizeof(int));
            int overlayGet = DwmGetWindowAttribute(Handle, DwmwaCloaked, out int overlayCloak, sizeof(int));
            int sourceValue = _source is null || sourceGet < 0 ? -1 : sourceCloak;
            int overlayValue = overlayGet < 0 ? -1 : overlayCloak;
            string sample = $"fse={fseActive}|sourceCloak={sourceValue}|overlayCloak={overlayValue}|foreground={NativeForegroundWindow():x}";
            if (!StringComparer.Ordinal.Equals(sample, _lastSample))
            {
                File.AppendAllText(_logPath!, $"{DateTimeOffset.UtcNow:O}|{sample}" + Environment.NewLine);
                _lastSample = sample;
                _status.Text = $"FSE={fseActive}  source cloak={sourceValue}  overlay cloak={overlayValue}";
            }
            if (!fseActive || DateTimeOffset.UtcNow >= _deadline)
            {
                File.AppendAllText(_logPath!, $"{DateTimeOffset.UtcNow:O}|event=preview-ended|reason={(fseActive ? "30-second bound" : "FSE inactive")}" + Environment.NewLine);
                Close();
            }
        }

        private void Cleanup()
        {
            _timer.Stop();
            _timer.Dispose();
            if (_thumbnail != IntPtr.Zero) DwmUnregisterThumbnail(_thumbnail);
            _source?.Close();
        }

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr NativeForegroundWindow();
    }
}
