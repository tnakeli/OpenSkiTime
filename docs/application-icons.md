# Application icons

OpenSkiTime uses the existing dark teal mountain mark from the public website. Live Timing uses a blue stopwatch with broadcast waves and an amber signal dot, so the two applications are distinguishable in Explorer, shortcuts, taskbar entries and window titles.

The source SVGs, 256 px PNG previews and Windows ICO files live in `assets/icons/`. Each ICO contains transparent 32-bit PNG frames at 16, 24, 32, 48, 64, 128 and 256 px. The executable resource is set with `ApplicationIcon`; the two Avalonia applications also embed the matching resource for their windows. The live control panel, worker and server share the live icon. The isolated Timy USB host uses the main application icon.

The Windows installer uses the main icon for setup and the installed executable for its uninstall entry. Start-menu shortcuts use their target executable's embedded icon.

To regenerate the checked-in ICO and PNG files from their SVG sources on Windows:

```powershell
./scripts/Generate-AppIcons.ps1
```

Builds use the checked-in ICO files and do not need an image conversion dependency. Icon generation uses only PowerShell and Windows System.Drawing. The small SVG renderer deliberately supports the simple shapes used by these two marks and rejects unsupported elements.
