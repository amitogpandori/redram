# RED RAM

RED RAM is an all-in-one Windows 10/11 PC utility built with WPF on .NET 8: live memory intelligence, safe virtual-memory (pagefile) optimization with rollback, a storage cleaner, a file organizer and disk recovery tools — in one dark "Neon Core" interface.

## Features

### Memory dashboard
- Live physical RAM, RAM used, memory pressure and total commit (virtual memory) capacity.
- Glowing live charts for memory pressure (last 5 minutes) and paging activity.
- Top memory-using processes and live diagnostics from Windows performance counters (commit pressure, available memory, Pages/sec, disk latency, CPU).

### Analyze & optimize (pagefile) — with restore
- **Analyze PC** profiles your hardware and current Windows memory configuration.
- **Benchmark** measures system-drive speed so recommendations match your storage.
- **Optimize PC** recommends and (with your confirmation and administrator permission) applies a safe pagefile size: 4–64 GB bounds, keeps at least 10 GB / 10% of the system drive free, refuses when storage is critically low.
- The previous configuration is backed up first; **Restore Previous** puts it back.

### Before / after
- Record a baseline, make a change, then **Measure After** to see whether it actually helped.

### Storage Cleaner
- Fast, cancellable scan of any drive with live progress (pause / resume / stop) that never freezes the window; folders it can't access are skipped and counted.
- **Drive map** bar showing how much space each file type uses.
- **Categories with sizes**: Safe junk, Large files (250 MB+), Duplicates, Similar photos, Old downloads (90+ days), Untouched for a year, Videos, Images, Audio, Documents, Archives, Other, and Locked.
- Sortable, searchable list with file-type icons, size bars and safety badges, plus a **preview panel** (image thumbnails, details, Open / Show in folder / Copy path / Properties).
- **Easy selection**: tick boxes, Select all / None / Invert, **Shift+click** to tick a range, **Space** to tick highlighted rows, and **Smart select** (keep newest, oldest or RED RAM's pick in each duplicate set; keep the best shot of similar photos; files over 1 GB / 100 MB; files not changed for 6 or 12 months).
- A live **"N files ticked · X GB"** bar, and a confirmation window that lists every file before anything moves.
- **Safety first**: everything goes to the **Recycle Bin** (never permanent delete). Windows, installed apps, Program Files, ProgramData, AppData, programs/scripts, files marked "system" and system-drive root files are **locked** and can't be ticked. Cloud-only files, files changed since the scan, and drives without a Recycle Bin are skipped. RED RAM always keeps at least one copy of every duplicate set. **Clean safe junk** only removes old, unused Windows temp files. Each cleanup writes a report to `%LocalAppData%\RED RAM\History`.

### File Organizer
- Sort one folder into a clean library by Type → Year → Month, Year → Month, type only, or camera/device.
- Preview first (nothing changes), choose copy or move, stop at any time, and undo the last move.

### Disk Recovery
- One-click recover-and-repair, deep partition scan, file recovery, and resumable recovery images. RED RAM never formats or initializes the source drive.

### Updates & installer
- **Check for updates** compares your version with the latest GitHub release.
- Optional **Start with Windows**.
- Inno Setup installer that upgrades in place (see below).

## Build
```
dotnet publish SmartRAM/SmartRAM.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

On Linux/macOS you can compile-check with `dotnet build SmartRAM/SmartRAM.csproj -p:EnableWindowsTargeting=true` (the app itself runs on Windows only).

Or open **Actions → Windows Build → Run workflow** and download the `SmartRAM-Windows-x64` artifact (or `SmartRAM-Windows-Installer` from the Windows Installer workflow).

## Safety principles
RED RAM does not purge working sets, does not claim SSD storage is physical RAM, and only changes the pagefile after analysis, your confirmation and administrator permission, with a backup for Restore. The Storage Cleaner never deletes permanently and never touches Windows or application files.

## History
- **v0.10** — Neon Core redesign across every window; rebuilt Storage Cleaner (categories, drive map, preview, smart select, range select, confirmation list, Recycle Bin pipeline with re-checks, faster scanning).
- **v0.9** — Disk Recovery tools; Storage Cleaner safety protections (AppData, installed apps, binaries; only old temp files auto-cleaned).
- **v0.5** — Performance-counter diagnostics. Product inspiration reviewed includes Microsoft RAMMap, Process Lasso and Mem Reduct; RED RAM deliberately avoids aggressive cache/working-set purging.
- **v0.4** — Free-space and CPU detection, pagefile safety bounds and rollback on failure.
- **v0.1** — Native RAM / memory-pressure monitoring dashboard.

## Windows installation and upgrades
RED RAM includes an Inno Setup installer definition and CI installer build. The installer uses a permanent AppId, installs into Program Files, creates Start Menu/optional desktop shortcuts, registers uninstall information, detects an existing RED RAM installation, reuses its install directory, closes/restarts the app during replacement, and upgrades files in place. User settings and baselines live under LocalAppData and are not part of the application install directory, so an upgrade preserves them. Current installer/application version: 0.10.0. Future releases must keep the same AppId and increment the version metadata.
