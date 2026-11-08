using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace FseMultiMonitor;

/// <summary>
/// Laboratorio de congelamiento: mide si las miniaturas DWM siguen recibiendo frames
/// cuando una ventana opaca tapa la fuente. Solo diagnóstico; no inyecta ni hookea nada.
/// </summary>
internal static class FreezeLab
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    internal static int Run(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
            return SelfTest();

        var sources = new List<string>();
        string occluder = "F0";
        int seconds = 15;
        int monitorNumber = 2;
        bool launch = false;
        string prefix = "FSE-LAB";
        string? testWindowPath = null;
        string? animPath = null;
        int waitForeground = 120;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--source":
                case "--sources":
                    if (++i >= args.Length) throw new ArgumentException("--source requires S1, S2 or S3.");
                    sources.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--occluder":
                    if (++i >= args.Length) throw new ArgumentException("--occluder requires F0..F4.");
                    occluder = args[i].ToUpperInvariant();
                    break;
                case "--seconds":
                    if (++i >= args.Length || !int.TryParse(args[i], out seconds) || seconds is < 5 or > 120)
                        throw new ArgumentException("--seconds must be between 5 and 120.");
                    break;
                case "--monitor":
                    if (++i >= args.Length || !int.TryParse(args[i], out monitorNumber))
                        throw new ArgumentException("--monitor requires a 1-based monitor number.");
                    break;
                case "--launch":
                    launch = true;
                    break;
                case "--no-launch":
                    launch = false;
                    break;
                case "--title-prefix":
                    if (++i >= args.Length) throw new ArgumentException("--title-prefix requires a value.");
                    prefix = args[i];
                    break;
                case "--test-window":
                    if (++i >= args.Length) throw new ArgumentException("--test-window requires a path.");
                    testWindowPath = args[i];
                    break;
                case "--anim":
                    if (++i >= args.Length) throw new ArgumentException("--anim requires a path.");
                    animPath = args[i];
                    break;
                case "--wait-foreground":
                    if (++i >= args.Length || !int.TryParse(args[i], out waitForeground) || waitForeground is < 0 or > 600)
                        throw new ArgumentException("--wait-foreground must be between 0 and 600 seconds.");
                    break;
                default:
                    throw new ArgumentException($"Unknown freeze-lab option: {args[i]}");
            }
        }

        if (sources.Count == 0) sources.Add("S1");
        sources = sources.Select(s => s.ToUpperInvariant()).Distinct().ToList();
        foreach (string source in sources)
            if (source is not ("S1" or "S2" or "S3"))
                throw new ArgumentException($"Unknown source {source}; use S1, S2 or S3.");
        if (occluder is not ("F0" or "F1" or "F2" or "F3" or "F4"))
            throw new ArgumentException($"Unknown occluder {occluder}; use F0..F4.");

        var screens = Screen.AllScreens
            .OrderBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top).ToArray();
        if (monitorNumber < 1 || monitorNumber > screens.Length)
            throw new ArgumentOutOfRangeException(nameof(monitorNumber), $"Monitor {monitorNumber} is not active.");
        Rectangle overlayBounds = screens[monitorNumber - 1].Bounds;

        string logPath = Store.NewSessionPath("freeze-lab");
        void Log(object record) => File.AppendAllText(logPath, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);

        string repoRoot = FindRepoRoot();
        string? resolvedTestWindow = testWindowPath ?? FindTestWindow();
        string? resolvedAnim = animPath ?? FindAnim(repoRoot);

        var environment = DescribeEnvironment();
        Log(new
        {
            timeUtc = DateTimeOffset.UtcNow,
            @event = "lab-started",
            sources,
            occluder,
            seconds,
            monitorNumber,
            overlayBounds,
            launch,
            environment,
            logPath
        });
        Console.WriteLine($"freeze-lab: sources={string.Join(",", sources)} occluder={occluder} seconds={seconds} monitor={monitorNumber}");
        Console.WriteLine($"log: {logPath}");

        var killProcesses = new List<Process>();
        try
        {
            if (launch)
            {
                if (resolvedTestWindow is null)
                    throw new InvalidOperationException("FseTestWindow.exe not found; pass --test-window <path> or build testwindow.");
                foreach (string source in sources)
                {
                    if (source == "S1")
                    {
                        killProcesses.Add(LaunchTestWindow(resolvedTestWindow, $"--animate --monitor 1 --title \"{prefix}-S1\""));
                    }
                    else
                    {
                        killProcesses.Add(LaunchBrowser(source, prefix, resolvedAnim));
                    }
                }
                if (occluder is "F3" or "F4")
                {
                    string tool = occluder == "F4" ? " --toolwindow" : string.Empty;
                    killProcesses.Add(LaunchTestWindow(resolvedTestWindow,
                        $"--occluder --monitor 1 --topmost --title \"{prefix}-OCC\"{tool}"));
                }
            }

            Console.WriteLine("Waiting for windows...");
            var selected = new List<Inspector.WindowState>();
            foreach (string source in sources)
            {
                Console.WriteLine($"  Looking for {prefix}-{source}...");
                var window = WaitForWindow($"{prefix}-{source}", 30, out _);
                if (window is null)
                    throw new InvalidOperationException($"No window titled {prefix}-{source} was found. Launch it or use --launch.");
                selected.Add(window);
                Console.WriteLine($"  {source}: {window.Hwnd} {window.ProcessName} (title: {window.Title})");
            }

            string? occluderHwnd = null;
            int occluderCloak = -1;
            if (occluder is "F3" or "F4")
            {
                var window = WaitForWindow($"{prefix}-OCC", 15, out occluderCloak);
                if (window is null)
                    throw new InvalidOperationException($"Occluder window {prefix}-OCC was not found.");
                occluderHwnd = window.Hwnd;
                Console.WriteLine($"  occluder: {window.Hwnd} cloak={occluderCloak}");
            }
            else if (occluder is "F1" or "F2")
            {
                string expected = occluder == "F1" ? "Riot" : "sekiro";
                Console.WriteLine($"  waiting up to {waitForeground}s for {expected} in the foreground (F{occluder[1]})...");
                if (!WaitForForeground(expected, waitForeground))
                    throw new InvalidOperationException($"Foreground process matching '{expected}' did not appear within {waitForeground}s.");
            }

            return Measure(selected, occluder, occluderHwnd, occluderCloak, monitorNumber, overlayBounds, seconds, logPath, Log, environment);
        }
        catch (Exception ex)
        {
            Log(new { timeUtc = DateTimeOffset.UtcNow, @event = "lab-error", error = ex.Message, stackTrace = ex.StackTrace });
            Console.Error.WriteLine($"Lab error: {ex.Message}");
            return 1;
        }
        finally
        {
            foreach (var process in killProcesses)
                TryKill(process);
        }
    }

    private static int Measure(
        List<Inspector.WindowState> selected,
        string occluder,
        string? occluderHwnd,
        int occluderCloak,
        int monitorNumber,
        Rectangle overlayBounds,
        int seconds,
        string logPath,
        Action<object> log,
        object environment)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var preview = new LabThumbnailPreview(selected, monitorNumber);
        preview.Show();

        var wait = Stopwatch.StartNew();
        while (preview.RegisteredThumbnailCount < selected.Count && wait.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
        if (preview.RegisteredThumbnailCount == 0)
        {
            log(new { timeUtc = DateTimeOffset.UtcNow, @event = "lab-error", error = "no thumbnails registered" });
            Console.Error.WriteLine("No thumbnails registered.");
            return 1;
        }

        var destinations = preview.ThumbnailDestinations;
        int count = Math.Min(destinations.Count, selected.Count);
        var hashes = new HashSet<ulong>[count];
        for (int i = 0; i < count; i++) hashes[i] = [];
        int samples = 0;

        var stopwatch = Stopwatch.StartNew();
        DateTimeOffset nextSample = DateTimeOffset.UtcNow;
        int frames = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(seconds) && !preview.IsDisposed)
        {
            Application.DoEvents();
            bool capturedAny = false;
            for (int i = 0; i < count; i++)
            {
                var rect = destinations[i];
                var screenRect = new Rectangle(overlayBounds.Left + rect.Left, overlayBounds.Top + rect.Top, rect.Width, rect.Height);
                ulong? hash = HashScreen(screenRect);
                if (hash is not null) { hashes[i].Add(hash.Value); capturedAny = true; }
            }

            if (capturedAny) frames++;
            if (DateTimeOffset.UtcNow >= nextSample)
            {
                samples++;
                nextSample = nextSample.AddSeconds(1);
                SampleSources(selected, occluder, occluderHwnd, occluderCloak, monitorNumber, preview, frames, log);
            }

            int sleep = 100 - (int)(stopwatch.ElapsedMilliseconds % 100);
            if (sleep > 0) Thread.Sleep(sleep);
        }

        preview.Hide();

        var distinct = hashes.Select((set, index) => new
        {
            source = selected[index].Hwnd,
            processName = selected[index].ProcessName,
            thumbDistinct = set.Count,
            live = set.Count >= 10
        }).ToArray();
        bool overlayAlive = frames >= 5;
        bool allLive = distinct.All(item => item.live);
        string verdict = overlayAlive
            ? (allLive ? "all-live" : "source-frozen-or-paused")
            : "overlay-stalled";

        log(new
        {
            timeUtc = DateTimeOffset.UtcNow,
            @event = "lab-result",
            seconds,
            frames,
            samples,
            occluder,
            environment,
            thumbs = distinct,
            overlayAlive,
            verdict
        });

        Console.WriteLine();
        Console.WriteLine("  source                         distinct  live");
        foreach (var item in distinct)
            Console.WriteLine($"  {item.processName,-12} {item.source}  {item.thumbDistinct,8}  {(item.live ? "LIVE" : "FROZEN")}");
        Console.WriteLine($"  frames={frames} overlayAlive={overlayAlive}");
        Console.WriteLine($"  verdict: {verdict}");
        Console.WriteLine($"  log: {logPath}");
        return 0;
    }

    private static void SampleSources(
        List<Inspector.WindowState> selected,
        string occluder,
        string? occluderHwnd,
        int occluderCloak,
        int monitorNumber,
        LabThumbnailPreview preview,
        int frames,
        Action<object> log)
    {
        Inspector.Snapshot snapshot;
        try { snapshot = Inspector.Capture(includeTitles: false, includeHashes: false); }
        catch (Exception ex)
        {
            log(new { timeUtc = DateTimeOffset.UtcNow, @event = "sample-error", error = ex.Message });
            return;
        }

        var byHwnd = snapshot.Windows.ToDictionary(window => window.Hwnd, StringComparer.OrdinalIgnoreCase);
        var states = new List<object>(selected.Count);
        foreach (var source in selected)
        {
            if (!byHwnd.TryGetValue(source.Hwnd, out var current))
            {
                states.Add(new { source.Hwnd, source.ProcessName, alive = false });
                continue;
            }
            var result = OcclusionAdvisor.FindOccluder(snapshot.Windows, current, Environment.ProcessId);
            states.Add(new
            {
                source.Hwnd,
                source.ProcessName,
                cloaked = current.Cloaked,
                occluded = result.Occluded,
                occluder = result.OccluderProcess,
                browser = OcclusionAdvisor.IsBrowserLike(source.ProcessName)
            });
        }

        log(new
        {
            timeUtc = DateTimeOffset.UtcNow,
            @event = "sample",
            fseActive = snapshot.Fse.Active,
            foregroundProcess = snapshot.ForegroundProcess,
            monitorNumber,
            occluder,
            occluderHwnd,
            occluderCloak,
            tick = preview.TickColorName,
            frames,
            sources = states
        });
    }

    private static ulong? HashScreen(Rectangle rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return null;
        try
        {
            using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
            return HashBitmap(bitmap);
        }
        catch { return null; }
    }

    private static ulong HashBitmap(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * data.Height;
            byte[] buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);
            ulong hash = 14695981039346656037UL;
            int step = Math.Max(1, bytes / 8192);
            for (int i = 0; i < bytes; i += step)
            {
                hash ^= buffer[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static Inspector.WindowState? WaitForWindow(string titlePrefix, int seconds, out int cloak)
    {
        cloak = -1;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = Inspector.Capture(includeTitles: true, includeHashes: false);
            var match = snapshot.Windows.FirstOrDefault(window =>
                window.Visible && !window.Iconic && window.Title is not null &&
                window.Title.TrimStart().StartsWith(titlePrefix, StringComparison.OrdinalIgnoreCase));
            if (match is not null) { cloak = match.Cloaked; return match; }
            Thread.Sleep(250);
        }
        return null;
    }

    private static bool WaitForForeground(string expected, int seconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var snapshot = Inspector.Capture(includeTitles: false, includeHashes: false);
            if (snapshot.ForegroundProcess.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                (expected == "Riot" && snapshot.ForegroundProcess.Contains("Riot", StringComparison.OrdinalIgnoreCase)))
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static Process LaunchTestWindow(string path, string arguments)
    {
        var start = new ProcessStartInfo(path, arguments) { UseShellExecute = true };
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start FseTestWindow.");
    }

    private static Process LaunchBrowser(string source, string prefix, string? animPath)
    {
        if (animPath is null) throw new InvalidOperationException("lab/anim.html not found; pass --anim <path>.");
        string fullPath = Path.GetFullPath(animPath);
        string url = new Uri(fullPath).AbsoluteUri + "?title=" + Uri.EscapeDataString($"{prefix}-{source}");
        string executable = source == "S2"
            ? FirstExisting(@"C:\Program Files\Zen Browser\zen.exe", @"C:\Program Files\Mozilla Firefox\firefox.exe")
              ?? throw new InvalidOperationException("Zen/Firefox not found.")
            : FirstExisting(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe")
              ?? throw new InvalidOperationException("Edge/Chrome not found.");
        string arguments = source == "S2"
            ? $"-new-window \"{url}\""
            : $"--new-window --disable-features=CalculateNativeWinOcclusion \"{url}\"";
        var start = new ProcessStartInfo(executable, arguments) { UseShellExecute = true };
        return Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
    }

    private static string? FirstExisting(params string[] candidates) => candidates.FirstOrDefault(File.Exists);

    private static string? FindTestWindow()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "FseTestWindow.exe");
        if (File.Exists(local)) return local;
        string? root = FindRepoRootOrNull();
        if (root is null) return null;
        string[] configurations = ["Release", "Debug"];
        foreach (string configuration in configurations)
        {
            string candidate = Path.Combine(root, "testwindow", "bin", configuration, "net9.0-windows", "win-x64", "FseTestWindow.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string? FindAnim(string repoRoot)
    {
        string local = Path.Combine(AppContext.BaseDirectory, "lab", "anim.html");
        if (File.Exists(local)) return local;
        string candidate = Path.Combine(repoRoot, "lab", "anim.html");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string FindRepoRoot() =>
        FindRepoRootOrNull() ?? AppContext.BaseDirectory;

    private static string? FindRepoRootOrNull()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "FseMultiMonitor.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static object DescribeEnvironment()
    {
        string gpu = "unknown";
        string driver = "unknown";
        try
        {
            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (baseKey is not null)
            {
                foreach (string name in baseKey.GetSubKeyNames())
                {
                    if (!name.All(char.IsDigit)) continue;
                    using var key = baseKey.OpenSubKey(name);
                    string? desc = key?.GetValue("DriverDesc")?.ToString();
                    if (string.IsNullOrWhiteSpace(desc)) continue;
                    gpu = desc;
                    driver = key?.GetValue("DriverVersion")?.ToString() ?? "unknown";
                    break;
                }
            }
        }
        catch { }

        bool gameMode = false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
            gameMode = (key?.GetValue("AutoGameModeEnabled") as int?) == 1;
        }
        catch { }

        bool fse = false;
        try { fse = Native.IsGamingFullScreenExperienceActive(); } catch { }
        return new
        {
            os = Environment.OSVersion.VersionString,
            build = $"{Environment.OSVersion.Version.Build}.{Environment.OSVersion.Version.Revision}",
            gpu,
            driver,
            gameMode,
            fse
        };
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private static int SelfTest()
    {
        bool ok = true;
        void Check(bool condition, string label)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {label}");
            ok &= condition;
        }

        Check(OcclusionAdvisor.FamilyOf("zen") == OcclusionAdvisor.BrowserFamily.Gecko, "zen is Gecko");
        Check(OcclusionAdvisor.FamilyOf("firefox") == OcclusionAdvisor.BrowserFamily.Gecko, "firefox is Gecko");
        Check(OcclusionAdvisor.FamilyOf("msedge") == OcclusionAdvisor.BrowserFamily.Chromium, "msedge is Chromium");
        Check(OcclusionAdvisor.FamilyOf("notepad") == OcclusionAdvisor.BrowserFamily.None, "notepad is not a browser");

        var source = new Inspector.WindowState("0x1", 10, null, "zen", "class", true, false, false, 0, 0, "Default",
            "(0,0) 100x100", "0x0", "0x0", 96, "0x0", 1);
        var occluder = source with { Hwnd = "0x2", ProcessId = 20, ProcessName = "Riot Client", ZOrder = 0 };
        var cloaked = occluder with { Cloaked = 2 };
        Check(OcclusionAdvisor.FindOccluder([occluder, source], source, 999).Occluded, "opaque window above occludes");
        Check(!OcclusionAdvisor.FindOccluder([cloaked, source], source, 999).Occluded, "cloaked window does not occlude");
        Check(!OcclusionAdvisor.FindOccluder([source], source, 999).Occluded, "window does not occlude itself");

        Console.WriteLine(ok ? "freeze-lab self-test passed." : "freeze-lab self-test FAILED.");
        return ok ? 0 : 1;
    }

    internal sealed class LabThumbnailPreview : WindowThumbnailPreview
    {
        private readonly System.Windows.Forms.Timer _tickTimer = new() { Interval = 97 };
        private int _tickIndex;
        private static readonly Color[] TickColors =
        [
            Color.FromArgb(255, 0, 255),
            Color.FromArgb(0, 255, 0)
        ];

        internal LabThumbnailPreview(IReadOnlyList<Inspector.WindowState> sources, int monitorNumber)
            : base(sources, monitorNumber, requireFse: false)
        {
            _tickTimer.Tick += (_, _) =>
            {
                _tickIndex++;
                Invalidate(new Rectangle(ClientSize.Width - 40, ClientSize.Height - 40, 32, 32));
            };
            Shown += (_, _) => _tickTimer.Start();
            FormClosed += (_, _) => _tickTimer.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var rect = new Rectangle(ClientSize.Width - 40, ClientSize.Height - 40, 32, 32);
            using var brush = new SolidBrush(TickColors[_tickIndex % 2]);
            e.Graphics.FillRectangle(brush, rect);
            using var pen = new Pen(Color.White, 2);
            e.Graphics.DrawRectangle(pen, rect);
        }

        internal string TickColorName => _tickIndex % 2 == 0 ? "magenta" : "lime";
        internal int TickIndex => _tickIndex;
        internal Rectangle TickScreenBounds
        {
            get
            {
                if (!IsHandleCreated) return Rectangle.Empty;
                var screenPos = PointToScreen(new Point(ClientSize.Width - 40, ClientSize.Height - 40));
                return new Rectangle(screenPos.X, screenPos.Y, 32, 32);
            }
        }
    }
}