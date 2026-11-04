using System.Runtime.InteropServices;

namespace FseMultiMonitor;

internal static class Native
{
    internal const uint QdcOnlyActivePaths = 0x00000002;
    internal const uint QdcVirtualModeAware = 0x00000010;
    internal const uint QdcVirtualRefreshRateAware = 0x00000040;
    internal const uint DisplayConfigDeviceInfoGetTargetName = 2;
    internal const uint DisplayConfigDeviceInfoGetSourceName = 1;
    internal const uint DwmwaCloak = 13;
    internal const uint DwmwaCloaked = 14;
    internal const int SwpNoSize = 0x0001;
    internal const int SwpNoZOrder = 0x0004;
    internal const int SwpNoActivate = 0x0010;
    internal const int GwOwner = 4;
    internal const int GwHwndNext = 2;
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const uint DesktopReadObjects = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"({Left},{Top}) {Width}x{Height}";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfoEx
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rational { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PointL { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SizeU { public uint Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Region2D { public uint cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoSignalInfo
    {
        public ulong pixelRate;
        public Rational hSyncFreq, vSyncFreq;
        public SizeU activeSize, totalSize;
        public uint videoStandard, scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SourceMode
    {
        public uint width, height, pixelFormat;
        public PointL position;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    internal struct ModeInfoUnion
    {
        [FieldOffset(0)] public SourceMode sourceMode;
        [FieldOffset(0)] public VideoSignalInfo targetMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigModeInfo
    {
        public uint infoType, id;
        public Luid adapterId;
        public ModeInfoUnion modeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid adapterId;
        public uint id, modeInfoIdx, statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public Rational refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPathInfo
    {
        public PathSourceInfo sourceInfo;
        public PathTargetInfo targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public uint type, size;
        public Luid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.Bool)] public bool flags;
        public uint outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsGamingFullScreenExperienceActive();

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsGamingFullScreenExperienceSupported();

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CanSetGamingFullScreenExperience();

    [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    internal static extern int SetGamingFullScreenExperience([MarshalAs(UnmanagedType.Bool)] bool active);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int QueryDisplayConfig(uint flags, ref uint pathCount, IntPtr paths, ref uint modeCount, IntPtr modes, IntPtr topologyId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName info);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder title, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll", SetLastError = true)]
    internal static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);

    [DllImport("dwmapi.dll", SetLastError = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessMitigationPolicy(IntPtr process, int mitigationPolicy, out uint buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessInformation(IntPtr process, int processInformationClass, out uint processInformation, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder buffer, uint size, out uint needed);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseDesktop(IntPtr handle);

    internal static string? WindowText(IntPtr hwnd)
    {
        var text = new System.Text.StringBuilder(2048);
        return GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : null;
    }

    internal static string DesktopName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "unavailable";
        var value = new System.Text.StringBuilder(512);
        return GetUserObjectInformation(handle, 2, value, (uint)(value.Capacity * 2), out _) ? value.ToString() : $"unavailable (error {Marshal.GetLastWin32Error()})";
    }
}
