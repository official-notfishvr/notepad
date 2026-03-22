# FastNote

FastNote is a Visual Studio WPF project aimed at very large text files. Instead of loading the entire file into a text control, it scans the file once, stores byte offsets for each line, and renders only the visible lines.

## Architecture

- `FastNote.App`: C# WPF UI with a custom virtualized viewport.
- `FastNote.Core`: file indexing and random-access line reader.

## Large-file strategy

- Single pass over the file to build line-start offsets.
- No full-document string allocation.
- Visible lines are pulled from disk on demand.
- Long lines are clipped in the viewport to keep scrolling responsive.

## Open in Visual Studio

Open `FastNote.slnx` in Visual Studio 2026 or newer.

## Build from terminal

```powershell
dotnet sln FastNote.slnx add FastNote.App\FastNote.App.csproj FastNote.Core\FastNote.Core.csproj
dotnet build FastNote.slnx -c Release
```
