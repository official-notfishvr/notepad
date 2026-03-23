# FastNote

FastNote is a Visual Studio WPF project that mimics the Windows 11 Notepad shell while keeping large-file opening fast through streaming and background indexing.

## What it does

- Windows 11 Notepad-inspired light and dark themes
- Native-style menu bar, single-tab shell, plain editor canvas, and status bar
- Progressive open path: the first chunk appears immediately, then the rest of the file indexes in the background
- Virtualized renderer for large files instead of loading the entire document into a text box
- Word wrap and zoom controls

## Projects

- `FastNote.App`: WPF desktop UI
- `FastNote.Core`: streaming file reader and line indexer
- `FastNote.Bench`: benchmark harness for initial-open versus full-index timing

## Build

```powershell
dotnet build FastNote.slnx -c Release
```

## Benchmark

```powershell
dotnet run --project FastNote.Bench\FastNote.Bench.csproj -c Release -- <path-to-file>
```
