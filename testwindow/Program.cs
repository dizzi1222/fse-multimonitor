using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FseTestWindow;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool tryOwnerUncloak = Environment.GetCommandLineArgs().Contains("--try-owner-uncloak", StringComparer.OrdinalIgnoreCase);
        Application.Run(new FocusTestForm(tryOwnerUncloak));
    }

    private sealed class FocusTestForm : Form
    {
        private const uint DwmwaCloak = 13;
        private const uint DwmwaCloaked = 14;
        private readonly TextBox _input = new();
        private readonly Label _status = new();
        private readonly System.Windows.Forms.Timer? _probeTimer;
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

        internal FocusTestForm(bool tryOwnerUncloak)
        {
            Text = $"FSE Focus Test — PID {Environment.ProcessId}";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new System.Drawing.Size(600, 300);
            ClientSize = new System.Drawing.Size(720, 360);

            var heading = new Label
            {
                Text = "Secondary display focus test",
                AutoSize = true,
                Font = new System.Drawing.Font("Segoe UI", 17, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(24, 22)
            };
            var instructions = new Label
            {
                Text = tryOwnerUncloak
                    ? "One DWM uncloak test is running in this window's process. If the shell leaves it visible, click this field and type a short message. The field is never saved."
                    : "Move this window to display 2. Click the test field and type a short message. Then click display 1 and return. The field contents stay here and are never saved.",
                AutoSize = false,
                Size = new System.Drawing.Size(650, 56),
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
            Controls.AddRange([heading, instructions, _input, button, _status]);

            if (tryOwnerUncloak)
            {
                string root = Environment.GetEnvironmentVariable("FSE_MM_DATA")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FseMultiMonitor");
                Directory.CreateDirectory(root);
                _logPath = Path.Combine(root, $"owner-uncloak-{Environment.ProcessId}.jsonl");
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
                    int clearAppCloak = 0;
                    int setResult = DwmSetWindowAttribute(Handle, DwmwaCloak, ref clearAppCloak, sizeof(int));
                    File.AppendAllText(_logPath, $"{DateTimeOffset.UtcNow:O}|event=owner-uncloak-on-shown|fse={fseActive}|setHr=0x{setResult:x8}" + Environment.NewLine);
                    _probeTimer.Start();
                };
                FormClosed += (_, _) => _probeTimer.Dispose();
            }
        }

        private static string GetForegroundWindowSafe() => NativeForegroundWindow().ToString("x");

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr NativeForegroundWindow();
    }
}
