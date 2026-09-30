# RED RAM

RED RAM is a Windows memory monitoring and adaptive virtual-memory project.

## v0.1 foundation
- Native Windows physical RAM and memory-pressure monitoring
- Live 90-second memory-pressure chart
- Physical RAM, RAM used, memory pressure and commit/virtual capacity dashboard
- Windows 10/11 WPF application on .NET 8
- GitHub Actions workflow producing a self-contained Windows x64 executable

## Safety
The current build is monitoring-only. It does **not** purge working sets, change the pagefile, edit the registry, or claim SSD storage is physical RAM. System-changing optimization will only be added with backup/rollback and measurable safeguards.

## Build
```
dotnet publish RED RAM/RED RAM.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Or open **Actions → Windows Build → Run workflow** and download the `RED RAM-Windows-x64` artifact.


## v0.4 hardening
- Added system-drive free-space percentage and logical CPU detection.
- Refuses pagefile recommendations when system storage is critically low.
- Enforces 4–64 GB safety bounds and preserves at least 10 GB / 10% system-drive reserve.
- Pagefile application now attempts immediate rollback if configuration writing fails.
- Recommendations use measured storage speed more consistently.


## v0.5 diagnostics direction
RED RAM now samples Windows performance counters once per second for commit pressure, available memory, Pages/sec, page reads/writes, disk transfer latency and CPU load. This follows Microsoft's guidance that paging counters need context: Pages/sec alone is not proof of insufficient RAM. Product inspiration reviewed includes Microsoft RAMMap's detailed memory visibility, Process Lasso's conservative threshold-driven automation and responsiveness focus, Mem Reduct/winMemoryOptimizer's lightweight monitoring and tray-style automation. RED RAM deliberately avoids copying their aggressive cache/working-set purge behavior; Windows normally manages working sets and cache itself. The intended differentiator is evidence-based PC profiling, safe pagefile recommendations, measured storage capability, rollback, and before/after impact reporting.


## Windows installation and upgrades
RED RAM now includes an Inno Setup installer definition and CI installer build. The installer uses a permanent AppId, installs into Program Files, creates Start Menu/optional desktop shortcuts, registers uninstall information, detects an existing RED RAM installation, reuses its install directory, closes/restarts the app during replacement, and upgrades files in place. User settings and baselines live under LocalAppData and are not part of the application install directory, so an upgrade preserves them. Current installer/application version: 0.8.0. Future releases must keep the same AppId and increment the version metadata.
