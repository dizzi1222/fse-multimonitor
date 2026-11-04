using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FseMultiMonitor;

internal static class Inspector
{
    private const int ErrorInsufficientBuffer = 122;
    private const uint DisplayConfigSourceMode = 1;
    private const uint DisplayConfigTargetMode = 2;
    private const uint DisplayConfigTargetInUse = 1;
    private static readonly object HashLock = new();
    private static readonly Dictionary<string, (long Length, DateTime LastWrite, string Hash)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record FseState(bool? Supported, bool? Active, bool? CanSet, string? Error);
    internal sealed record MonitorState(string GdiName, Native.Rect Bounds, Native.Rect WorkArea, bool Primary, uint DpiX, uint DpiY);
    internal sealed record DisplayPathState(
        string SourceName, string TargetName, string TargetDevicePath, uint SourceId, uint TargetId,
        string Adapter, int SourceX, int SourceY, uint SourceWidth, uint SourceHeight,
        uint TargetWidth, uint TargetHeight, uint OutputTechnology, string RefreshRate,
        bool Available, bool InUse, uint SourceModeIndex, uint TargetModeIndex, uint PathFlags, int ModeCount,
        string? Error = null);
    internal sealed record WindowState(
        string Hwnd, uint ProcessId, DateTimeOffset? ProcessStartUtc, string ProcessName, string ClassName,
        bool Visible, bool Iconic, bool Zoomed, int Cloaked, int DwmResult, string Desktop,
        string Rect, string Style, string ExtendedStyle, uint Dpi, string OwnerHwnd, int ZOrder, string? Title = null, string? TitleSha256 = null,
        string? Error = null);
    internal sealed record ProcessEvidence(string ProcessName, uint ProcessId, DateTimeOffset? StartUtc, string? ImagePath, string? ImageVersion, string? ImageSha256, string? Error);
    internal sealed record ProcessPolicy(uint ProcessId, string ProcessName, uint? SignatureFlags, uint? DynamicCodeFlags,
        uint? ImageLoadFlags, uint? ProtectionLevel, string? Error);
    internal sealed record Snapshot(
        DateTimeOffset TimeUtc, string ComputerName, string UserSession, string ThreadDesktop,
        string InputDesktop, int DpiAwareness, FseState Fse, string OsProduct, string OsDisplayVersion,
        string OsBuild, string OsRevision, IReadOnlyList<MonitorState> Monitors,
        IReadOnlyList<DisplayPathState> ActivePaths, int? TopologyId,
        string ForegroundHwnd, uint? ForegroundPid, string ForegroundProcess, string ForegroundClass,
        IReadOnlyList<WindowState> Windows, IReadOnlyList<ProcessPolicy> ProcessPolicies, IReadOnlyList<ProcessEvidence>? ShellBinaries,
        IReadOnlyList<string> Errors);

    internal static Snapshot Capture(bool includeTitles = false, bool includeHashes = true)
    {
        var errors = new List<string>();
        var display = ReadDisplays(errors);
        var foreground = ReadForeground(errors);
        var windows = ReadWindows(includeTitles, errors);
        var os = ReadOs();
        var fse = ReadFse();
        var processPolicies = ReadProcessPolicies(windows, foreground.Pid);
        var shell = includeHashes ? ReadShellBinaries(errors) : null;
        return new Snapshot(
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            $"session-{Process.GetCurrentProcess().SessionId}",
            Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId())),
            ReadInputDesktop(),
            Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext()),
            fse,
            os.Product,
            os.DisplayVersion,
            os.Build,
            os.Revision,
            display.Monitors,
            display.Paths,
            display.Topology,
            foreground.Hwnd,
            foreground.Pid,
            foreground.Process,
            foreground.Class,
            windows,
            processPolicies,
            shell,
            errors);
    }

    private static FseState ReadFse()
    {
        try
        {
            bool supported = Native.IsGamingFullScreenExperienceSupported();
            bool active = Native.IsGamingFullScreenExperienceActive();
            bool canSet = Native.CanSetGamingFullScreenExperience();
            return new FseState(supported, active, canSet, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new FseState(null, null, null, $"FSE API unavailable: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new FseState(null, null, null, $"FSE query failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ReadInputDesktop()
    {
        IntPtr desktop = Native.OpenInputDesktop(0, false, Native.DesktopReadObjects);
        if (desktop == IntPtr.Zero) return $"unavailable (error {Marshal.GetLastWin32Error()})";
        try { return Native.DesktopName(desktop); }
        finally { Native.CloseDesktop(desktop); }
    }

    private static (string Product, string DisplayVersion, string Build, string Revision) ReadOs()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return (
                key?.GetValue("ProductName")?.ToString() ?? "unknown",
                key?.GetValue("DisplayVersion")?.ToString() ?? "unknown",
                key?.GetValue("CurrentBuildNumber")?.ToString() ?? "unknown",
                key?.GetValue("UBR")?.ToString() ?? "unknown");
        }
        catch (Exception ex) { return ($"unavailable: {ex.Message}", "unknown", "unknown", "unknown"); }
    }

    private static (IReadOnlyList<MonitorState> Monitors, IReadOnlyList<DisplayPathState> Paths, int? Topology)
        ReadDisplays(List<string> errors)
    {
        var monitors = new List<MonitorState>();
        Native.MonitorEnumProc monitorCallback = (IntPtr handle, IntPtr _, ref Native.Rect rect, IntPtr __) =>
        {
            var info = new Native.MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<Native.MonitorInfoEx>(), szDevice = string.Empty };
            if (!Native.GetMonitorInfo(handle, ref info))
            {
                errors.Add($"GetMonitorInfo error {Marshal.GetLastWin32Error()}");
                return true;
            }
            uint dpiX = 0, dpiY = 0;
            try { Native.GetDpiForMonitor(handle, 0, out dpiX, out dpiY); }
            catch (DllNotFoundException) { }
            monitors.Add(new MonitorState(info.szDevice, info.rcMonitor, info.rcWork, (info.dwFlags & 1) != 0, dpiX, dpiY));
            return true;
        };
        if (!Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorCallback, IntPtr.Zero))
            errors.Add($"EnumDisplayMonitors error {Marshal.GetLastWin32Error()}");
        GC.KeepAlive(monitorCallback);

        var paths = new List<DisplayPathState>();
        int? topology = null;
        uint flags = Native.QdcOnlyActivePaths;
        int sizeStatus = Native.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
        if (sizeStatus != 0)
        {
            errors.Add($"GetDisplayConfigBufferSizes returned {sizeStatus}");
            return (monitors, paths, topology);
        }

        int pathSize = Marshal.SizeOf<Native.DisplayConfigPathInfo>();
        int modeSize = Marshal.SizeOf<Native.DisplayConfigModeInfo>();
        if (Marshal.SizeOf<Native.PathSourceInfo>() != 20 || Marshal.SizeOf<Native.PathTargetInfo>() != 48 ||
            pathSize != 72 || modeSize != 64)
        {
            errors.Add($"Native display-configuration structure layout mismatch: source={Marshal.SizeOf<Native.PathSourceInfo>()}, target={Marshal.SizeOf<Native.PathTargetInfo>()}, path={pathSize}, mode={modeSize}");
            return (monitors, paths, topology);
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr pathMemory = Marshal.AllocHGlobal(checked((int)Math.Max(1, pathCount) * pathSize));
            IntPtr modeMemory = Marshal.AllocHGlobal(checked((int)Math.Max(1, modeCount) * modeSize));
            try
            {
                uint reportedPaths = pathCount, reportedModes = modeCount;
                int status = Native.QueryDisplayConfig(flags, ref reportedPaths, pathMemory, ref reportedModes, modeMemory, IntPtr.Zero);
                if (status == ErrorInsufficientBuffer)
                {
                    int retryStatus = Native.GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount);
                    if (retryStatus != 0) { errors.Add($"Display configuration changed during query; retry failed with {retryStatus}"); break; }
                    continue;
                }
                if (status != 0)
                {
                    errors.Add($"QueryDisplayConfig returned {status}");
                    break;
                }
                pathCount = reportedPaths;
                modeCount = reportedModes;
                for (int i = 0; i < pathCount; i++)
                {
                    var path = Marshal.PtrToStructure<Native.DisplayConfigPathInfo>(IntPtr.Add(pathMemory, i * pathSize));
                    paths.Add(ReadPath(path, modeMemory, modeCount, modeSize, errors));
                }
                break;
            }
            finally
            {
                Marshal.FreeHGlobal(pathMemory);
                Marshal.FreeHGlobal(modeMemory);
            }
        }
        return (monitors, paths, topology);
    }

    private static DisplayPathState ReadPath(Native.DisplayConfigPathInfo path, IntPtr modes, uint modeCount, int modeSize, List<string> errors)
    {
        string sourceName = $"source-{path.sourceInfo.id}";
        string targetName = $"target-{path.targetInfo.id}";
        string devicePath = string.Empty;
        var sourceDevice = new Native.SourceDeviceName
        {
            header = new Native.DeviceInfoHeader
            {
                type = Native.DisplayConfigDeviceInfoGetSourceName,
                size = (uint)Marshal.SizeOf<Native.SourceDeviceName>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id
            },
            viewGdiDeviceName = string.Empty
        };
        int sourceStatus = Native.DisplayConfigGetDeviceInfo(ref sourceDevice);
        if (sourceStatus == 0) sourceName = sourceDevice.viewGdiDeviceName;

        var targetDevice = new Native.TargetDeviceName
        {
            header = new Native.DeviceInfoHeader
            {
                type = Native.DisplayConfigDeviceInfoGetTargetName,
                size = (uint)Marshal.SizeOf<Native.TargetDeviceName>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty
        };
        int targetStatus = Native.DisplayConfigGetDeviceInfo(ref targetDevice);
        if (targetStatus == 0)
        {
            targetName = targetDevice.monitorFriendlyDeviceName;
            devicePath = targetDevice.monitorDevicePath;
        }

        int x = 0, y = 0;
        uint sourceWidth = 0, sourceHeight = 0, targetWidth = 0, targetHeight = 0;
        if (path.sourceInfo.modeInfoIdx < modeCount)
        {
            var sourceMode = Marshal.PtrToStructure<Native.DisplayConfigModeInfo>(IntPtr.Add(modes, checked((int)path.sourceInfo.modeInfoIdx * modeSize)));
            if (sourceMode.infoType == DisplayConfigSourceMode)
            {
                x = sourceMode.modeInfo.sourceMode.position.X;
                y = sourceMode.modeInfo.sourceMode.position.Y;
                sourceWidth = sourceMode.modeInfo.sourceMode.width;
                sourceHeight = sourceMode.modeInfo.sourceMode.height;
            }
        }
        string rate = "unknown";
        if (path.targetInfo.modeInfoIdx < modeCount)
        {
            var targetMode = Marshal.PtrToStructure<Native.DisplayConfigModeInfo>(IntPtr.Add(modes, checked((int)path.targetInfo.modeInfoIdx * modeSize)));
            if (targetMode.infoType == DisplayConfigTargetMode)
            {
                var signal = targetMode.modeInfo.targetMode;
                targetWidth = signal.activeSize.Width;
                targetHeight = signal.activeSize.Height;
                rate = signal.vSyncFreq.Denominator == 0 ? "unknown" : $"{signal.vSyncFreq.Numerator}/{signal.vSyncFreq.Denominator} Hz";
            }
        }

        string? error = null;
        if (sourceStatus != 0 || targetStatus != 0)
        {
            error = $"Device-info status: source={sourceStatus}, target={targetStatus}";
            errors.Add(error);
        }
        return new DisplayPathState(sourceName, targetName, devicePath, path.sourceInfo.id, path.targetInfo.id,
            $"{path.targetInfo.adapterId.HighPart:x8}:{path.targetInfo.adapterId.LowPart:x8}",
            x, y, sourceWidth, sourceHeight, targetWidth, targetHeight, path.targetInfo.outputTechnology,
            rate, path.targetInfo.targetAvailable, (path.targetInfo.statusFlags & DisplayConfigTargetInUse) != 0,
            path.sourceInfo.modeInfoIdx, path.targetInfo.modeInfoIdx, path.flags, checked((int)modeCount), error);
    }

    private static (string Hwnd, uint? Pid, string Process, string Class) ReadForeground(List<string> errors)
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ("0x0000000000000000", null, "none", "");
        Native.GetWindowThreadProcessId(hwnd, out uint processId);
        string className = ReadClass(hwnd);
        string process = "unknown";
        try { process = Process.GetProcessById((int)processId).ProcessName; }
        catch (Exception ex) { errors.Add($"Foreground process {processId}: {ex.GetType().Name}"); }
        return (Hwnd(hwnd), processId, process, className);
    }

    private static IReadOnlyList<WindowState> ReadWindows(bool includeTitles, List<string> errors)
    {
        var windows = new List<WindowState>();
        Native.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!Native.GetWindowRect(hwnd, out var rect)) return true;
                uint threadId = Native.GetWindowThreadProcessId(hwnd, out uint pid);
                var process = TryGetProcess(pid);
                int dwmResult = Native.DwmGetWindowAttribute(hwnd, Native.DwmwaCloaked, out int cloaked, sizeof(int));
                string? title = includeTitles ? Native.WindowText(hwnd) : null;
                string? titleHash = null;
                if (title is { Length: > 0 }) titleHash = Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(title))).ToLowerInvariant();
                windows.Add(new WindowState(Hwnd(hwnd), pid, process.Started, process.Name, ReadClass(hwnd),
                    Native.IsWindowVisible(hwnd), Native.IsIconic(hwnd), Native.IsZoomed(hwnd),
                    dwmResult == 0 ? cloaked : -1, dwmResult, Native.DesktopName(Native.GetThreadDesktop(threadId)),
                    rect.ToString(),
                    $"0x{unchecked((ulong)Native.GetWindowLongPtr(hwnd, Native.GwlStyle).ToInt64()):x8}",
                    $"0x{unchecked((ulong)Native.GetWindowLongPtr(hwnd, Native.GwlExStyle).ToInt64()):x8}",
                    SafeGetDpi(hwnd), Hwnd(Native.GetWindow(hwnd, Native.GwOwner)), windows.Count,
                    title, titleHash));
            }
            catch (Exception ex) { errors.Add($"Window {Hwnd(hwnd)}: {ex.GetType().Name}: {ex.Message}"); }
            return windows.Count < 1000;
        };
        if (!Native.EnumWindows(callback, IntPtr.Zero)) errors.Add($"EnumWindows error {Marshal.GetLastWin32Error()}");
        GC.KeepAlive(callback);
        return windows;
    }

    private static string ReadClass(IntPtr hwnd)
    {
        var value = new StringBuilder(512);
        int length = Native.GetClassName(hwnd, value, value.Capacity);
        return length > 0 ? value.ToString() : "";
    }

    private static uint SafeGetDpi(IntPtr hwnd)
    {
        try { return Native.GetDpiForWindow(hwnd); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private static (string Name, DateTimeOffset? Started) TryGetProcess(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            DateTimeOffset start = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (process.ProcessName, start);
        }
        catch (Exception ex) { return ($"unavailable-{ex.GetType().Name}", null); }
    }

    private static IReadOnlyList<ProcessEvidence> ReadShellBinaries(List<string> errors)
    {
        string[] names = ["explorer", "ShellExperienceHost", "ShellHost", "GameBar", "XboxPcApp", "ApplicationFrameHost"];
        string[] moduleNames = ["explorer.exe", "twinui.pcshell.dll", "Windows.UI.ShellCommon.dll", "GameBar.exe", "GameBarFTServer.exe"];
        var result = new List<ProcessEvidence>();
        foreach (string name in names)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (Exception ex) { errors.Add($"Find process {name}: {ex.Message}"); continue; }
            if (processes.Length == 0)
                result.Add(new ProcessEvidence($"{name} (not running)", 0, null, null, null, null, "not present in the process snapshot"));
            foreach (var process in processes)
            {
                using (process)
                {
                    DateTimeOffset? start = null;
                    string? image = null, version = null, hash = null, problem = null;
                    try
                    {
                        start = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                        image = process.MainModule?.FileName;
                        if (image is not null)
                        {
                            version = FileVersionInfo.GetVersionInfo(image).FileVersion;
                            hash = HashFile(image);
                        }
                    }
                    catch (Exception ex) { problem = $"main image unavailable ({ex.GetType().Name}: {ex.Message})"; }
                    result.Add(new ProcessEvidence(process.ProcessName, (uint)process.Id, start, image, version, hash, problem));

                    try
                    {
                        foreach (ProcessModule module in process.Modules)
                        {
                            if (!moduleNames.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase)) continue;
                            result.Add(new ProcessEvidence(module.ModuleName, (uint)process.Id, start, module.FileName,
                                module.FileVersionInfo.FileVersion, HashFile(module.FileName), null));
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Modules for {process.ProcessName} ({process.Id}) unavailable: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<ProcessPolicy> ReadProcessPolicies(IReadOnlyList<WindowState> windows, uint? foregroundPid)
    {
        var result = new List<ProcessPolicy>();
        foreach (var group in windows.Where(w => w.Visible || w.ProcessId == foregroundPid).GroupBy(w => w.ProcessId))
        {
            uint pid = group.Key;
            if (pid == 0) continue;
            string name = group.First().ProcessName;
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero)
            {
                result.Add(new ProcessPolicy(pid, name, null, null, null, null,
                    $"OpenProcess error {Marshal.GetLastWin32Error()}"));
                continue;
            }
            try
            {
                uint? signature = ReadMitigation(process, 8);
                uint? dynamicCode = ReadMitigation(process, 2);
                uint? imageLoad = ReadMitigation(process, 10);
                uint? protection = Native.GetProcessInformation(process, 7, out uint level, sizeof(uint)) ? level : null;
                result.Add(new ProcessPolicy(pid, name, signature, dynamicCode, imageLoad, protection, null));
            }
            catch (Exception ex)
            {
                result.Add(new ProcessPolicy(pid, name, null, null, null, null, $"{ex.GetType().Name}: {ex.Message}"));
            }
            finally { Native.CloseHandle(process); }
        }
        return result;
    }

    private static uint? ReadMitigation(IntPtr process, int policy) =>
        Native.GetProcessMitigationPolicy(process, policy, out uint value, (nuint)sizeof(uint)) ? value : null;

    private static string? HashFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            lock (HashLock)
            {
                if (HashCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.LastWrite == info.LastWriteTimeUtc)
                    return cached.Hash;
                using var stream = File.OpenRead(path);
                string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                HashCache[path] = (info.Length, info.LastWriteTimeUtc, hash);
                return hash;
            }
        }
        catch { return null; }
    }

    internal static string Hwnd(IntPtr hwnd) => $"0x{unchecked((ulong)hwnd.ToInt64()):x16}";

    internal static IntPtr ParseHwnd(string value)
    {
        string trimmed = value.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? new IntPtr(unchecked((long)Convert.ToUInt64(trimmed[2..], 16)))
            : new IntPtr(long.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture));
    }
}
