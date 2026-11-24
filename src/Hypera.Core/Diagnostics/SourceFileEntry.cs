namespace Hypera.Diagnostics;

public readonly record struct SourceFileEntry(
    int FileId,
    string VirtualPath,
    string DisplayName);

public readonly record struct SourceSpan(
    int FileId,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn)
{
    public static SourceSpan SingleLine(int fileId, int lineNo, int startColumn = 1, int endColumn = 1) =>
        new(fileId, lineNo, startColumn, lineNo, endColumn);
}

public readonly record struct SourceLocation(
    int FileId,
    string VirtualPath,
    string DisplayName,
    int LineNo);
