# FSE MultiMonitor

An x64 Windows 11 diagnostic utility for observing Xbox Full Screen Experience (FSE), display topology, desktop windows, focus changes, and relevant shell binary versions and hashes.

The utility reports; it does not currently contain a verified multi-monitor bypass. No target shell module or hook is selected. `apply` intentionally refuses to run unless a compatible experimental profile exists under the local profile directory.

## Run

The Windows x64 executable targets .NET 9 and requires the .NET 9 Windows Desktop Runtime.

```powershell
.\FseMultiMonitor.exe self-check
.\FseMultiMonitor.exe capture
.\FseMultiMonitor.exe watch 600
.\FseMultiMonitor.exe test-window
```

The data directory defaults to `%LOCALAPPDATA%\FseMultiMonitor`. To save logs alongside this project:

```powershell
$env:FSE_MM_DATA = (Resolve-Path .).Path + '\data'
.\publish\FseMultiMonitor.exe capture
```

`capture` writes a JSONL record containing the FSE API state; active `QueryDisplayConfig` paths and modes; physical monitor/work-area coordinates and DPI; foreground window; visible and hidden top-level windows; DWM cloak results; desktop names; Windows build; and hashes/versions for selected loaded shell modules. Window titles are omitted by default. Use `--include-titles` only if the resulting local log may safely contain document names, browser tabs, and similar title text.

`watch [seconds]` writes an initial snapshot, state-change snapshots, and a final snapshot to JSONL. The duration defaults to 600 seconds and is capped at 1800 seconds. Stop it with Ctrl+C.

`test-window` opens a harmless text field and button for checking mouse focus and keyboard typing across displays. Its output includes its process ID.

The separate `testwindow\publish\FseTestWindow.exe --try-owner-uncloak` build performs one bounded, owner-process `DwmSetWindowAttribute(DWMWA_CLOAK=false)` attempt when its form is shown, then samples FSE and DWM state for at most 30 seconds. It logs to `FSE_MM_DATA` and closes at the bound. This is a diagnostic intervention, not a bypass.

## Reversible probe

While FSE is confirmed active and Windows reports two active display paths, select a current top-level window by the HWND shown in `capture` or by its controlled test-window PID. Move that one window to the secondary monitor for at most 30 seconds:

```powershell
.\FseMultiMonitor.exe probe 0x0000000000001234 30
```

The probe refuses hidden, minimized, or DWM-cloaked windows. It records the original rectangle and process start time to a transaction file before moving anything, keeps the window size and focus unchanged, samples FSE/window state, and restores the original rectangle automatically. Ctrl+C also triggers restoration. If the utility is interrupted during the probe, use the transaction ID it printed:

```powershell
.\FseMultiMonitor.exe restore <transaction-id>
```

Recovery checks both the HWND owner and process start time before moving it. The probe never persists display changes, installs a service, sets autostart, changes security configuration, or alters a shell binary.

## What the findings can establish

Two or more active display paths establish that Windows did not disable those display routes during the captured transition. The window records can show if a selected app was hidden, minimized, DWM-cloaked, moved, placed on another desktop, or simply ceased to be foreground. They cannot by themselves prove why the shell made that decision.

The Microsoft FSE API exposes both state-query calls and a global `SetGamingFullScreenExperience` call. This tool reads FSE state and reports whether the current session says that mode changes are allowed; it does not call the global setter because that is not a per-display exemption.

## Build

```powershell
dotnet build .\src\FseMultiMonitor.csproj -c Release -p:NuGetAudit=false
dotnet publish .\src\FseMultiMonitor.csproj -c Release -o .\publish -p:SelfContained=false -p:PublishSingleFile=true -p:NuGetAudit=false
```

## Current evidence

An initial unprivileged capture reported `CanSet=false`; later elevated captures reported `CanSet=true`. Both reported FSE active. The Microsoft API setter changes the global gaming full-screen mode; it is not a per-monitor exemption. Windows reported two active 1920×1080 paths, with the secondary display at `(1920, 0)`.

On this PC, the test app's own DWM call succeeded while FSE stayed active, but the shell cloak returned in the same run: once after about 20 seconds and once within 260 ms of the form being shown. A third bounded run stayed cloaked after the one-shot attempt. CUA could read the window's accessibility tree during a brief interval, but could not obtain a usable screenshot/input surface after cloaking. No mouse click or typed-key acceptance has been verified, and no hook or shell patch has been built.

The local test logs are under `data\owner-uncloak-*.jsonl`; the final point-in-time capture is under `data\sessions`. These machine-specific logs are excluded from Git. See `report.md` for sanitized measured outcomes and limits. `DWMWA_CLOAKED=2` means DWM reports that the shell cloaked the window ([Microsoft documentation](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)). Before attempting in-process hooks, identify which component reapplies the cloak and gather traces that attribute the decision to a process/module.

No license is included; code reuse rights remain unspecified.
