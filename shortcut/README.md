# Shortcut

`FSE Multi-Monitor.lnk` is a convenience copy of the shortcut created by the installer. It points at `%LOCALAPPDATA%\Programs\FseMultiMonitor\FseMultiMonitor.exe select` on the machine where it was generated.

A Windows `.lnk` file stores an absolute target path, so this binary is **machine-specific**. It works after the installer has been run on this machine. On any other machine, or after moving the install, regenerate it with:

```powershell
.\installer\Create-Shortcut.ps1
```

The script detects the installed app, `publish\`, or `artifacts\app\`, and creates `FSE Multi-Monitor.lnk` in `publish\` and on the Desktop. The installer itself creates the real Start Menu shortcut, so this copy is optional.
