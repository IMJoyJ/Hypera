namespace Hypera.Diagnostics;

/// <summary>
/// Compact run-length entry mapping bytecode program counter [StartPc..next.StartPc) to (FileId, LineNo).
/// </summary>
public readonly record struct SourceMapRun(int StartPc, int FileId, int LineNo);

/// <summary>
/// Maps flat bytecode PC offsets back to original ERB/ERH/CSV source files and line numbers
/// for __FILE__, __LINE__, debuggers, and runtime stack traces.
/// </summary>
public sealed class SourceMap
{
    private readonly SourceFileEntry[] _files;
    private readonly SourceMapRun[] _runs;

    public SourceMap(IEnumerable<SourceFileEntry> files, IEnumerable<SourceMapRun> runs)
    {
        _files = files.OrderBy(static f => f.FileId).ToArray();
        _runs = runs.OrderBy(static r => r.StartPc).ToArray();
    }

    public static SourceMap Empty { get; } = new([], []);

    public IReadOnlyList<SourceFileEntry> Files => _files;

    public IReadOnlyList<SourceMapRun> Runs => _runs;

    public bool TryGetFile(int fileId, out SourceFileEntry entry)
    {
        if ((uint)fileId < (uint)_files.Length && _files[fileId].FileId == fileId)
        {
            entry = _files[fileId];
            return true;
        }

        int lo = 0;
        int hi = _files.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int cmp = _files[mid].FileId.CompareTo(fileId);
            if (cmp == 0)
            {
                entry = _files[mid];
                return true;
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        entry = default;
        return false;
    }

    public bool TryLookup(int pcOffset, out SourceLocation location)
    {
        if (pcOffset < 0 || _runs.Length == 0 || pcOffset < _runs[0].StartPc)
        {
            location = default;
            return false;
        }

        int lo = 0;
        int hi = _runs.Length - 1;
        int bestIndex = 0;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int midPc = _runs[mid].StartPc;
            if (midPc == pcOffset)
            {
                bestIndex = mid;
                break;
            }

            if (midPc < pcOffset)
            {
                bestIndex = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        SourceMapRun run = _runs[bestIndex];
        if (!TryGetFile(run.FileId, out SourceFileEntry file))
        {
            file = new SourceFileEntry(run.FileId, string.Empty, string.Empty);
        }

        location = new SourceLocation(run.FileId, file.VirtualPath, file.DisplayName, run.LineNo);
        return true;
    }
}

public sealed class SourceMapBuilder
{
    private readonly List<SourceFileEntry> _files = [];
    private readonly Dictionary<string, int> _fileIdsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceMapRun> _runs = [];

    public int GetOrAddFile(string virtualPath, string? displayName = null)
    {
        if (_fileIdsByPath.TryGetValue(virtualPath, out int existingId))
        {
            return existingId;
        }

        int id = _files.Count;
        string display = string.IsNullOrEmpty(displayName) ? Path.GetFileName(virtualPath) : displayName;
        _files.Add(new SourceFileEntry(id, virtualPath, display));
        _fileIdsByPath[virtualPath] = id;
        return id;
    }

    public void AddMapping(int pcOffset, int fileId, int lineNo)
    {
        if (_runs.Count > 0)
        {
            SourceMapRun last = _runs[^1];
            if (last.FileId == fileId && last.LineNo == lineNo && pcOffset >= last.StartPc)
            {
                return;
            }

            if (last.StartPc == pcOffset)
            {
                _runs[^1] = new SourceMapRun(pcOffset, fileId, lineNo);
                return;
            }
        }

        _runs.Add(new SourceMapRun(pcOffset, fileId, lineNo));
    }

    public SourceMap Build() => new(_files, _runs);
}
