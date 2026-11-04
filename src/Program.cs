using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace FseMultiMonitor;

internal static class Program
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    internal static bool ConsoleAttached { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static int Main(string[] args)
    {
        bool guiOnly = args.Length > 0 && args[0].Equals("select", StringComparison.OrdinalIgnoreCase);
        if (!guiOnly) EnsureConsole();

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("FseMultiMonitor runs on Windows 11.");
            return 2;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        try
        {
            if (args.Length == 0 || IsHelp(args[0])) { PrintUsage(); return 0; }
            return args[0].ToLowerInvariant() switch
            {
                "capture" => CaptureCommand(args[1..]),
                "watch" => WatchCommand(args[1..]),
                "windows" => WindowsCommand(args[1..]),
                "preview" => PreviewCommand(args[1..]),
                "select" => SelectCommand(),
                "check-update" => CheckUpdateCommand(),
                "test-window" => TestWindowCommand(),
                "probe" => ProbeCommand(args[1..]),
                "probe-uncloak" => UncloakProbeCommand(args[1..]),
                "restore" => RestoreCommand(args[1..]),
                "apply" => ApplyCommand(args[1..]),
                "mode" => ModeCommand(args[1..]),
                "self-check" => SelfCheckCommand(),
                _ => UnknownCommand(args[0])
            };
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Stopped.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (guiOnly) LogGuiError(ex);
            return 1;
        }
    }

    private static void LogGuiError(Exception exception)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "FseMultiMonitor-gui-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never take the process down.
        }
    }

    private static void EnsureConsole()
    {
        if (ConsoleAttached) return;
        try
        {
            if (!AttachConsole(AttachParentProcess) && !AllocConsole()) return;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            ConsoleAttached = true;
        }
        catch
        {
            // No console is available; file logging still works.
        }
    }

    private static int CaptureCommand(string[] args)
    {
        bool includeTitles = args.Contains("--include-titles", StringComparer.OrdinalIgnoreCase);
        var snapshot = Inspector.Capture(includeTitles);
        string path = Store.WriteJsonLine("capture", snapshot);
        PrintSnapshotSummary(snapshot);
        Console.WriteLine($"Saved: {path}");
        return 0;
    }

    private static int WatchCommand(string[] args)
    {
        bool includeTitles = args.Contains("--include-titles", StringComparer.OrdinalIgnoreCase);
        int seconds = 600;
        string? duration = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (duration is not null && (!int.TryParse(duration, out seconds) || seconds is < 1 or > 1800))
            throw new ArgumentException("watch duration must be between 1 and 1800 seconds.");

        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; Store.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            string path = Store.NewSessionPath("watch");
            var previous = Inspector.Capture(includeTitles);
            Store.Append(path, new EventRecord("baseline", DateTimeOffset.UtcNow, "Initial state", previous));
            PrintSnapshotSummary(previous);
            Console.WriteLine($"Watching for {seconds}s; Ctrl+C stops the watch. Log: {path}");
            var previousSignature = Signature(previous);
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
            int changeCount = 0;
            while (!Store.IsCancelled && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(1000);
                if (Store.IsCancelled) break;
                var current = Inspector.Capture(includeTitles, includeHashes: false);
                string signature = Signature(current);
                if (!StringComparer.Ordinal.Equals(previousSignature, signature))
                {
                    string reason = DescribeDifference(previous, current);
                    Store.Append(path, new EventRecord("change", DateTimeOffset.UtcNow, reason, current));
                    Console.WriteLine($"{current.TimeUtc:HH:mm:ss.fff}Z  {reason}");
                    previous = current;
                    previousSignature = signature;
                    changeCount++;
                }
            }
            var final = Inspector.Capture(includeTitles, includeHashes: true);
            Store.Append(path, new EventRecord("final", DateTimeOffset.UtcNow, $"Watch ended after {changeCount} detected transitions", final));
            Console.WriteLine($"Ended. {changeCount} state changes recorded. Log: {path}");
            return 0;
        }
        finally { Console.CancelKeyPress -= cancel; Store.ResetCancel(); }
    }

    private static int WindowsCommand(string[] args)
    {
        bool includeTitles = args.Contains("--include-titles", StringComparer.OrdinalIgnoreCase);
        var snapshot = Inspector.Capture(includeTitles, includeHashes: false);
        Console.WriteLine($"FSE active={snapshot.Fse.Active}; active displays={snapshot.ActivePaths.Count(path => path.InUse && path.Available)}");
        foreach (var window in snapshot.Windows.Where(window => window.Visible && !window.Iconic).OrderBy(window => window.ZOrder))
        {
            string title = includeTitles && !string.IsNullOrWhiteSpace(window.Title) ? $" title=\"{window.Title}\"" : string.Empty;
            Console.WriteLine($"{window.Hwnd}  {window.ProcessName,-28} cloak={window.Cloaked} rect={window.Rect}{title}");
        }
        return 0;
    }

    private static int PreviewCommand(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            Console.Error.WriteLine("Usage: FseMultiMonitor preview <HWND> [HWND ...] [--monitor 1-based-index]");
            return 2;
        }

        int monitorNumber = 2;
        var hwndArguments = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(args[i], "--monitor"))
            {
                if (++i >= args.Length || !int.TryParse(args[i], out monitorNumber))
                    throw new ArgumentException("--monitor requires a 1-based monitor number.");
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown preview option: {args[i]}");
            }
            else hwndArguments.Add(args[i]);
        }

        if (hwndArguments.Count is < 1 or > 4)
            throw new ArgumentException("Preview one to four window handles at a time.");
        var requested = hwndArguments.Select(Inspector.ParseHwnd).Distinct().ToArray();
        if (requested.Length != hwndArguments.Count)
            throw new ArgumentException("Each source HWND must be unique.");

        var snapshot = Inspector.Capture(includeHashes: false);
        if (snapshot.Fse.Supported != true || snapshot.Fse.Active != true)
            throw new InvalidOperationException("Preview requires Xbox Full Screen Experience to be confirmed active.");
        if (snapshot.ActivePaths.Count(path => path.InUse && path.Available) < 2)
            throw new InvalidOperationException("Preview requires at least two active display paths.");

        var sources = new List<Inspector.WindowState>(requested.Length);
        foreach (IntPtr hwnd in requested)
        {
            string canonicalHwnd = Inspector.Hwnd(hwnd);
            var source = snapshot.Windows.SingleOrDefault(window => StringComparer.OrdinalIgnoreCase.Equals(window.Hwnd, canonicalHwnd))
                ?? throw new ArgumentException($"HWND {canonicalHwnd} is not a current top-level desktop window.");
            if (!source.Visible || source.Iconic)
                throw new InvalidOperationException($"Source {canonicalHwnd} must be visible and unminimized; shell-cloaked sources are supported.");
            sources.Add(source);
        }

        var activeMonitors = Screen.AllScreens;
        if (monitorNumber < 1 || monitorNumber > activeMonitors.Length)
            throw new ArgumentOutOfRangeException(nameof(monitorNumber), $"Monitor {monitorNumber} is not active; detected {activeMonitors.Length} monitor(s).");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new WindowThumbnailPreview(sources, monitorNumber);
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            if (form.IsHandleCreated && !form.IsDisposed)
            {
                try { form.BeginInvoke((Action)form.Close); }
                catch (InvalidOperationException) { }
            }
        };
        Console.CancelKeyPress += cancel;
        try { Application.Run(form); }
        finally { Console.CancelKeyPress -= cancel; }
        return 0;
    }

    private static int SelectCommand()
    {
        var snapshot = Inspector.Capture(includeTitles: true, includeHashes: false);
        if (snapshot.Fse.Supported != true || snapshot.Fse.Active != true)
            throw new InvalidOperationException("Window selection requires Xbox Full Screen Experience to be confirmed active.");
        if (snapshot.ActivePaths.Count(path => path.InUse && path.Available) < 2)
            throw new InvalidOperationException("Window selection requires at least two active display paths.");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogGuiError(e.Exception);
        Application.Run(new WindowPicker());
        return 0;
    }

    private static int CheckUpdateCommand()
    {
        var update = UpdateChecker.CheckAsync().GetAwaiter().GetResult();
        if (update is null)
        {
            Console.WriteLine($"Up to date: v{UpdateChecker.CurrentVersion}.");
            return 0;
        }

        Console.WriteLine($"Update available: v{update.Version} (current v{UpdateChecker.CurrentVersion}).");
        Console.WriteLine($"Installer: {update.SetupUrl}");
        Console.WriteLine($"SHA256:    {update.ChecksumUrl}");
        return 0;
    }

    private static int TestWindowCommand()
    {
        Console.WriteLine($"Opening a harmless input test window. PID: {Environment.ProcessId}");
        Console.WriteLine("Use the text field only to test focus and typing between displays. Close the window when finished.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TestForm(Environment.ProcessId));
        return 0;
    }

    private static int ProbeCommand(string[] args)
    {
        if (args.Length < 1 || IsHelp(args[0]))
        {
            Console.Error.WriteLine("Usage: FseMultiMonitor probe <HWND> [seconds 1-30] [--include-titles]");
            return 2;
        }
        IntPtr hwnd = Inspector.ParseHwnd(args[0]);
        int seconds = 30;
        string? duration = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (duration is not null && (!int.TryParse(duration, out seconds) || seconds is < 1 or > 30))
            throw new ArgumentException("Probe duration must be between 1 and 30 seconds.");
        bool includeTitles = args.Contains("--include-titles", StringComparer.OrdinalIgnoreCase);

        var before = Inspector.Capture(includeTitles);
        if (before.Fse.Supported != true || before.Fse.Active != true)
            throw new InvalidOperationException("Probe refused: Xbox Mode/FSE is not confirmed active. Use watch while entering Xbox Mode, then retry.");
        if (before.ActivePaths.Count(p => p.InUse && p.Available) < 2)
            throw new InvalidOperationException("Probe refused: Windows does not report two available active display paths.");
        var selected = before.Windows.SingleOrDefault(w => StringComparer.OrdinalIgnoreCase.Equals(w.Hwnd, args[0]));
        if (selected is null) throw new ArgumentException($"HWND {args[0]} is not a current top-level window in the captured desktop.");
        if (!selected.Visible || selected.Iconic || selected.Cloaked != 0)
            throw new InvalidOperationException("Probe refused: target must be visible, unminimized, and not DWM-cloaked.");
        if (!Native.GetWindowRect(hwnd, out var originalRect))
            throw new InvalidOperationException($"Cannot read target rectangle (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");

        var secondary = before.Monitors.FirstOrDefault(m => !m.Primary)
            ?? throw new InvalidOperationException("No secondary desktop monitor is available.");
        int x = secondary.WorkArea.Left + Math.Max(0, (secondary.WorkArea.Width - originalRect.Width) / 2);
        int y = secondary.WorkArea.Top + Math.Max(0, (secondary.WorkArea.Height - originalRect.Height) / 2);
        var processStart = selected.ProcessStartUtc
            ?? throw new InvalidOperationException("Could not establish the target process start time; safe recovery is unavailable.");
        var transaction = new ProbeTransaction(
            Guid.NewGuid().ToString("N"), selected.Hwnd, selected.ProcessId, processStart,
            originalRect.Left, originalRect.Top, originalRect.Width, originalRect.Height, false, DateTimeOffset.UtcNow);
        string transactionPath = Store.SaveTransaction(transaction);
        string logPath = Store.NewSessionPath("probe");
        Store.Append(logPath, new EventRecord("before", DateTimeOffset.UtcNow, "Original state; recovery transaction saved before the test", before));

        bool moved = false;
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; Store.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (!Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, originalRect.Width, originalRect.Height,
                    Native.SwpNoZOrder | Native.SwpNoActivate))
                throw new InvalidOperationException($"SetWindowPos failed (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            moved = true;
            Console.WriteLine($"Moved {selected.Hwnd} to {secondary.GdiName} without activating or resizing it. Returning it in at most {seconds}s. Transaction: {transaction.Id}");
            Console.WriteLine("Click the test window and type in its harmless test field if you want to verify interaction. Ctrl+C restores it now.");
            string lastKey = string.Empty;
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
            while (!Store.IsCancelled && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(500);
                if (Store.IsCancelled) break;
                var current = Inspector.Capture(includeTitles, includeHashes: false);
                string key = TargetSignature(current, transaction, hwnd);
                if (!StringComparer.Ordinal.Equals(lastKey, key))
                {
                    Store.Append(logPath, new EventRecord("probe-sample", DateTimeOffset.UtcNow,
                        $"FSE={current.Fse.Active}; foreground={current.ForegroundHwnd}; target={key}", current));
                    lastKey = key;
                }
                if (current.Fse.Active != true)
                {
                    Console.WriteLine("FSE became inactive during the probe. Restoring the target immediately.");
                    break;
                }
                if (!Native.IsWindowVisible(hwnd))
                {
                    Console.WriteLine("Target became invisible during the probe. Restoring immediately.");
                    break;
                }
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            Store.ResetCancel();
            if (moved) RestoreTransaction(transaction.Id);
            var after = Inspector.Capture(includeTitles);
            Store.Append(logPath, new EventRecord("after", DateTimeOffset.UtcNow, "Probe ended; original target rectangle restored", after));
            Console.WriteLine($"Probe log: {logPath}");
        }
    }

    private static int RestoreCommand(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: FseMultiMonitor restore <transaction-id>"); return 2; }
        return RestoreTransaction(args[0]) ? 0 : 3;
    }

    private static bool RestoreTransaction(string id)
    {
        var transaction = Store.ReadTransaction(id);
        if (transaction.Restored)
        {
            Console.WriteLine($"Transaction {id} is already restored.");
            return true;
        }
        if (!int.TryParse(transaction.ProcessId.ToString(CultureInfo.InvariantCulture), out int pid))
            throw new InvalidDataException("Invalid recovery process id.");
        try
        {
            using var process = Process.GetProcessById(pid);
            var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if (startTime != transaction.ProcessStartUtc)
            {
                Console.Error.WriteLine("Recovery refused: HWND owner process no longer matches its recorded start time.");
                return false;
            }
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Recovery cannot move the window: its original process has exited.");
            return false;
        }
        IntPtr hwnd = Inspector.ParseHwnd(transaction.Hwnd);
        Native.GetWindowThreadProcessId(hwnd, out uint actualPid);
        if (actualPid != transaction.ProcessId || !Native.GetWindowRect(hwnd, out _))
        {
            Console.Error.WriteLine("Recovery refused: HWND was closed or reused by another process.");
            return false;
        }
        if (transaction.Kind == "cloak")
        {
            int clearAppCloak = 0;
            int result = Native.DwmSetWindowAttribute(hwnd, Native.DwmwaCloak, ref clearAppCloak, sizeof(int));
            if (result < 0)
            {
                Console.Error.WriteLine($"DWM cloak recovery failed (HRESULT 0x{result:x8}).");
                return false;
            }
            Store.MarkRestored(id);
            Console.WriteLine($"Removed the test-only DWM cloak override from {transaction.Hwnd}; the shell retains control of its own cloak.");
            return true;
        }
        bool restored = Native.SetWindowPos(hwnd, IntPtr.Zero,
            transaction.Left, transaction.Top, transaction.Width, transaction.Height,
            Native.SwpNoZOrder | Native.SwpNoActivate);
        if (!restored)
        {
            Console.Error.WriteLine($"SetWindowPos restore failed (Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
            return false;
        }
        Store.MarkRestored(id);
        Console.WriteLine($"Restored {transaction.Hwnd} to ({transaction.Left},{transaction.Top}) {transaction.Width}x{transaction.Height}.");
        return true;
    }

    private static int UncloakProbeCommand(string[] args)
    {
        if (args.Length < 1 || IsHelp(args[0]))
        {
            Console.Error.WriteLine("Usage: FseMultiMonitor probe-uncloak <HWND> [seconds 1-30]");
            return 2;
        }
        IntPtr hwnd = Inspector.ParseHwnd(args[0]);
        int seconds = 30;
        string? duration = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (duration is not null && (!int.TryParse(duration, out seconds) || seconds is < 1 or > 30))
            throw new ArgumentException("Probe duration must be between 1 and 30 seconds.");

        var before = Inspector.Capture();
        if (before.Fse.Supported != true || before.Fse.Active != true)
            throw new InvalidOperationException("Probe refused: Xbox Mode/FSE is not confirmed active.");
        if (before.ActivePaths.Count(p => p.InUse && p.Available) < 2)
            throw new InvalidOperationException("Probe refused: Windows does not report two active display paths.");
        var selected = before.Windows.SingleOrDefault(w => StringComparer.OrdinalIgnoreCase.Equals(w.Hwnd, args[0]))
            ?? throw new ArgumentException($"HWND {args[0]} is not a current top-level window.");
        if (!selected.Visible || selected.Cloaked != 2)
            throw new InvalidOperationException($"Probe refused: the target must be visibly marked with the shell cloak reason (observed DWMWA_CLOAKED={selected.Cloaked}).");
        string expectedPath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "FseTestWindow.exe");
        using var owner = Process.GetProcessById(checked((int)selected.ProcessId));
        if (!StringComparer.OrdinalIgnoreCase.Equals(owner.ProcessName, "FseTestWindow") ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(owner.MainModule?.FileName ?? string.Empty), Path.GetFullPath(expectedPath)))
            throw new InvalidOperationException("Probe refused: DWM uncloak testing is limited to this project's own FseTestWindow.exe.");
        var startTime = new DateTimeOffset(owner.StartTime.ToUniversalTime(), TimeSpan.Zero);
        if (selected.ProcessStartUtc != startTime || !Native.GetWindowRect(hwnd, out var originalRect))
            throw new InvalidOperationException("Probe refused: the HWND and its owning process could not be tied to a recoverable current window.");

        var transaction = new ProbeTransaction(Guid.NewGuid().ToString("N"), selected.Hwnd,
            selected.ProcessId, startTime, originalRect.Left, originalRect.Top, originalRect.Width, originalRect.Height,
            false, DateTimeOffset.UtcNow, "cloak", 2);
        Store.SaveTransaction(transaction);
        string logPath = Store.NewSessionPath("cloak-probe");
        Store.Append(logPath, new EventRecord("before", DateTimeOffset.UtcNow, "Shell-cloaked owned test window; recovery transaction saved", before));

        bool changed = false;
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; Store.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            int clearAppCloak = 0;
            int result = Native.DwmSetWindowAttribute(hwnd, Native.DwmwaCloak, ref clearAppCloak, sizeof(int));
            if (result < 0) throw new InvalidOperationException($"DwmSetWindowAttribute(DWMWA_CLOAK=false) failed (HRESULT 0x{result:x8}).");
            changed = true;
            Console.WriteLine($"Cleared the app-controlled DWM cloak bit for owned HWND {selected.Hwnd}; observing for at most {seconds}s. FSE was not changed. Transaction: {transaction.Id}");
            Console.WriteLine("Try selecting the temporary test field and typing. Ctrl+C triggers recovery.");
            string previous = string.Empty;
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
            while (!Store.IsCancelled && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(250);
                if (Store.IsCancelled) break;
                var current = Inspector.Capture(includeHashes: false);
                var target = current.Windows.SingleOrDefault(w => StringComparer.OrdinalIgnoreCase.Equals(w.Hwnd, transaction.Hwnd));
                string signature = target is null
                    ? $"missing|FSE={current.Fse.Active}|FG={current.ForegroundHwnd}"
                    : $"{target.Visible}|{target.Cloaked}|{target.Rect}|{current.ForegroundHwnd}|FSE={current.Fse.Active}";
                if (!StringComparer.Ordinal.Equals(signature, previous))
                {
                    Store.Append(logPath, new EventRecord("cloak-sample", DateTimeOffset.UtcNow,
                        $"FSE={current.Fse.Active}; foreground={current.ForegroundProcess}:{current.ForegroundHwnd}; target={signature}", current));
                    Console.WriteLine($"{current.TimeUtc:HH:mm:ss.fff}Z FSE={current.Fse.Active} target-visible={target?.Visible} target-cloaked={target?.Cloaked} foreground={current.ForegroundHwnd}");
                    previous = signature;
                }
                if (current.Fse.Active != true) break;
                if (target is null || !target.Visible) break;
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            Store.ResetCancel();
            if (changed) RestoreTransaction(transaction.Id);
            else Store.MarkRestored(transaction.Id);
            var after = Inspector.Capture();
            Store.Append(logPath, new EventRecord("after", DateTimeOffset.UtcNow, "Removed the probe's app-controlled cloak bit; shell cloak state sampled", after));
            Console.WriteLine($"Cloak probe log: {logPath}");
        }
    }

    private static int ApplyCommand(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("Usage: FseMultiMonitor apply <profile.json>"); return 2; }
        string candidate = Path.GetFullPath(args[0]);
        string profileRoot = Store.ProfilesDirectory;
        if (!candidate.StartsWith(profileRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(candidate))
            throw new InvalidOperationException($"Apply accepts only a locally stored, validated profile under {profileRoot}.");
        throw new InvalidOperationException("No validated build-specific profile is installed. No system or window state was changed.");
    }

    private static int ModeCommand(string[] args)
    {
        if (args.Length != 2 || !StringComparer.OrdinalIgnoreCase.Equals(args[0], "set") ||
            !(StringComparer.OrdinalIgnoreCase.Equals(args[1], "on") || StringComparer.OrdinalIgnoreCase.Equals(args[1], "off")))
        {
            Console.Error.WriteLine("Usage: FseMultiMonitor mode set on|off");
            Console.Error.WriteLine("Example: FseMultiMonitor mode set on");
            return 2;
        }
        bool active = StringComparer.OrdinalIgnoreCase.Equals(args[1], "on");
        var fse = Inspector.Capture(includeHashes: false).Fse;
        if (fse.Supported != true || fse.CanSet != true)
            throw new InvalidOperationException("The FSE API does not report that this session can change Xbox Mode.");
        int hr = Native.SetGamingFullScreenExperience(active);
        if (hr < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
        Thread.Sleep(1000);
        var resulting = Inspector.Capture(includeHashes: false).Fse;
        Console.WriteLine($"Requested FSE active={active}; observed FSE active={resulting.Active}; HRESULT=0x{hr:x8}");
        return resulting.Active == active ? 0 : 3;
    }

    private static int SelfCheckCommand()
    {
        var sizes = new Dictionary<string, int>
        {
            ["DISPLAYCONFIG_PATH_SOURCE_INFO"] = System.Runtime.InteropServices.Marshal.SizeOf<Native.PathSourceInfo>(),
            ["DISPLAYCONFIG_PATH_TARGET_INFO"] = System.Runtime.InteropServices.Marshal.SizeOf<Native.PathTargetInfo>(),
            ["DISPLAYCONFIG_PATH_INFO"] = System.Runtime.InteropServices.Marshal.SizeOf<Native.DisplayConfigPathInfo>(),
            ["DISPLAYCONFIG_MODE_INFO"] = System.Runtime.InteropServices.Marshal.SizeOf<Native.DisplayConfigModeInfo>()
        };
        foreach (var (name, size) in sizes) Console.WriteLine($"{name}={size}");
        bool valid = sizes["DISPLAYCONFIG_PATH_SOURCE_INFO"] == 20 &&
                     sizes["DISPLAYCONFIG_PATH_TARGET_INFO"] == 48 &&
                     sizes["DISPLAYCONFIG_PATH_INFO"] == 72 &&
                     sizes["DISPLAYCONFIG_MODE_INFO"] == 64;
        var snapshot = Inspector.Capture();
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        using var parsed = JsonDocument.Parse(json);
        Console.WriteLine($"Snapshot JSON schema: {parsed.RootElement.ValueKind}; FSE API: {snapshot.Fse.Supported}; active paths: {snapshot.ActivePaths.Count}");
        Console.WriteLine(valid ? "Native ABI layout check passed." : "Native ABI layout check FAILED.");
        return valid ? 0 : 1;
    }

    private static string Signature(Inspector.Snapshot snapshot)
    {
        var signature = new
        {
            snapshot.Fse,
            snapshot.ThreadDesktop,
            snapshot.InputDesktop,
            snapshot.DpiAwareness,
            snapshot.Monitors,
            snapshot.ActivePaths,
            snapshot.TopologyId,
            snapshot.ForegroundHwnd,
            Windows = snapshot.Windows.Select(w => new
            {
                w.Hwnd, w.ProcessId, w.ProcessStartUtc, w.ProcessName, w.ClassName, w.Visible,
                w.Iconic, w.Zoomed, w.Cloaked, w.DwmResult, w.Desktop, w.Rect, w.OwnerHwnd,
                w.ZOrder, w.TitleSha256
            }).ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signature))));
    }

    private static string TargetSignature(Inspector.Snapshot snapshot, ProbeTransaction transaction, IntPtr hwnd)
    {
        var target = snapshot.Windows.SingleOrDefault(w => StringComparer.OrdinalIgnoreCase.Equals(w.Hwnd, transaction.Hwnd));
        return target is null ? "window-missing" : $"{target.Hwnd}|visible={target.Visible}|min={target.Iconic}|cloaked={target.Cloaked}|dwm={target.DwmResult}|rect={target.Rect}|desktop={target.Desktop}|fg={snapshot.ForegroundHwnd == target.Hwnd}";
    }

    private static string DescribeDifference(Inspector.Snapshot previous, Inspector.Snapshot current)
    {
        var labels = new List<string>();
        if (previous.Fse.Active != current.Fse.Active) labels.Add($"Xbox Mode {previous.Fse.Active}->{current.Fse.Active}");
        if (previous.ActivePaths.Count(p => p.InUse && p.Available) != current.ActivePaths.Count(p => p.InUse && p.Available))
            labels.Add($"active display paths {previous.ActivePaths.Count(p => p.InUse && p.Available)}->{current.ActivePaths.Count(p => p.InUse && p.Available)}");
        if (previous.Monitors.Count != current.Monitors.Count) labels.Add($"visible monitors {previous.Monitors.Count}->{current.Monitors.Count}");
        if (previous.ThreadDesktop != current.ThreadDesktop || previous.InputDesktop != current.InputDesktop)
            labels.Add($"desktop {previous.ThreadDesktop}/{previous.InputDesktop}->{current.ThreadDesktop}/{current.InputDesktop}");
        if (previous.ForegroundHwnd != current.ForegroundHwnd)
            labels.Add($"foreground {previous.ForegroundProcess}:{previous.ForegroundHwnd}->{current.ForegroundProcess}:{current.ForegroundHwnd}");
        var oldWindows = previous.Windows.ToDictionary(w => w.Hwnd);
        var changed = current.Windows.Count(w => !oldWindows.TryGetValue(w.Hwnd, out var old) ||
            old.Visible != w.Visible || old.Iconic != w.Iconic || old.Cloaked != w.Cloaked ||
            old.Rect != w.Rect || old.Desktop != w.Desktop);
        int removed = previous.Windows.Count(w => current.Windows.All(c => c.Hwnd != w.Hwnd));
        if (changed > 0 || removed > 0) labels.Add($"window state changed={changed}, closed={removed}");
        if (labels.Count == 0) labels.Add("other observed window state changed");
        return string.Join("; ", labels);
    }

    private static void PrintSnapshotSummary(Inspector.Snapshot snapshot)
    {
        Console.WriteLine($"FSE supported={snapshot.Fse.Supported}; active={snapshot.Fse.Active}; canSet={snapshot.Fse.CanSet}");
        Console.WriteLine($"Windows {snapshot.OsBuild}.{snapshot.OsRevision} {snapshot.OsProduct}; active display paths={snapshot.ActivePaths.Count(p => p.InUse && p.Available)}; EnumDisplayMonitors={snapshot.Monitors.Count}; desktop={snapshot.ThreadDesktop}/{snapshot.InputDesktop}");
        Console.WriteLine($"Foreground={snapshot.ForegroundProcess} PID={snapshot.ForegroundPid} HWND={snapshot.ForegroundHwnd} class={snapshot.ForegroundClass}");
        foreach (var policy in snapshot.ProcessPolicies)
            Console.WriteLine($"  policy {policy.ProcessName} PID={policy.ProcessId}: signature=0x{policy.SignatureFlags:x8}, dynamic-code=0x{policy.DynamicCodeFlags:x8}, image-load=0x{policy.ImageLoadFlags:x8}, protection=0x{policy.ProtectionLevel:x8}{(policy.Error is null ? "" : $" ({policy.Error})")}");
        foreach (var monitor in snapshot.Monitors)
            Console.WriteLine($"  {monitor.GdiName} bounds={monitor.Bounds} work={monitor.WorkArea} primary={monitor.Primary}");
        foreach (var path in snapshot.ActivePaths)
            Console.WriteLine($"  path {path.SourceName} -> {path.TargetName}: source={path.SourceWidth}x{path.SourceHeight}@({path.SourceX},{path.SourceY}), target={path.TargetWidth}x{path.TargetHeight}, refresh={path.RefreshRate}, available={path.Available}, inUse={path.InUse}");
        if (snapshot.ShellBinaries is { Count: > 0 })
        {
            Console.WriteLine("Relevant loaded shell binaries:");
            foreach (var binary in snapshot.ShellBinaries.Where(b => b.ImagePath is not null).DistinctBy(b => (b.ProcessId, b.ImagePath)))
                Console.WriteLine($"  {binary.ProcessName} PID={binary.ProcessId} version={binary.ImageVersion} SHA256={binary.ImageSha256} path={binary.ImagePath}");
        }
        if (snapshot.Errors.Count > 0)
        {
            Console.WriteLine($"Partial collection warnings: {snapshot.Errors.Count}");
            foreach (string error in snapshot.Errors.Take(8)) Console.WriteLine($"  {error}");
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "/?";

    private static void PrintUsage()
    {
        Console.WriteLine("""
            FseMultiMonitor — Xbox Full Screen Experience observation utility

              capture [--include-titles]       Save a point-in-time JSONL snapshot
              watch [seconds] [--include-titles] Observe state changes (default 600, maximum 1800 seconds)
              windows [--include-titles]        List HWNDs for current top-level windows (console only)
              select                          Open a visual window and monitor selector
              preview <HWND> [HWND ...]         Mirror 1-4 windows as DWM thumbnails on display 2
                [--monitor N]                  Select another 1-based display
              check-update                    Query GitHub Releases for a newer installer
              test-window                     Open a harmless focus and typing test window
              probe <HWND> [seconds]           Temporarily move one explicit window (maximum 30 seconds)
              probe-uncloak <HWND> [seconds]   Test uncloak on this build's owned FseTestWindow only
              restore <transaction-id>        Restore a saved probe rectangle
              mode set on|off                 Request an explicit FSE mode transition through Microsoft's API
              apply <profile.json>            Apply a validated profile; refuses when none is installed
              self-check                      Check native ABI layouts and snapshot serialization

            Window titles are excluded by default. Pass --include-titles only when their contents are safe
            to store in a local diagnostic log. Probes require confirmed active FSE and two active displays.
            """);
    }

    private sealed class TestForm : Form
    {
        private readonly Label _status;
        private readonly TextBox _input;
        private int _clicks;

        internal TestForm(int pid)
        {
            Text = $"FSE Focus Test — PID {pid}";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new System.Drawing.Size(520, 260);
            ClientSize = new System.Drawing.Size(650, 320);
            var heading = new Label
            {
                Text = "Two-display interaction test",
                AutoSize = true,
                Font = new System.Drawing.Font("Segoe UI", 16, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(24, 22)
            };
            var instructions = new Label
            {
                Text = "Move this window to the secondary monitor. Click the field, type test text, then click the primary monitor and return.",
                AutoSize = false,
                Size = new System.Drawing.Size(590, 48),
                Location = new System.Drawing.Point(26, 68)
            };
            _input = new TextBox { Location = new System.Drawing.Point(28, 126), Size = new System.Drawing.Size(580, 28) };
            var button = new Button { Text = "Record click", Location = new System.Drawing.Point(28, 176), Size = new System.Drawing.Size(120, 36) };
            _status = new Label { Text = "Ready", AutoSize = true, Location = new System.Drawing.Point(170, 186) };
            button.Click += (_, _) => { _clicks++; _status.Text = $"Click count: {_clicks}"; };
            _input.TextChanged += (_, _) => _status.Text = $"Text received: {_input.TextLength} characters";
            Controls.AddRange([heading, instructions, _input, button, _status]);
        }
    }
}
