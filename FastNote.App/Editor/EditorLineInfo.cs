namespace FastNote.App.Editor;

public readonly record struct EditorLineInfo(int LineNumber, int Offset, int Length, int TotalLength);
