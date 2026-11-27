using System.Globalization;
using Hypera.VirtualFs;
using SkiaSharp;

namespace Hypera.Rendering;

public readonly record struct SpriteResolvedFrame(
    SKBitmap Bitmap,
    SKRectI SourceRect,
    int PosX,
    int PosY,
    int LogicalWidth,
    int LogicalHeight);

public sealed record SpriteFrameSpec(
    SKBitmap? StaticBitmap,
    int? GraphicsId,
    SKRectI SourceRect,
    int PosX,
    int PosY,
    int DelayMs,
    string? ImageFilePath = null,
    int ImageFrameIndex = 0);

public sealed class SpriteDefinition
{
    private readonly List<SpriteFrameSpec> _frames = [];

    public SpriteDefinition(string name, int width, int height, int posX, int posY, bool isAnimated, bool isDynamic)
    {
        Name = name;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        PosX = posX;
        PosY = posY;
        IsAnimated = isAnimated;
        IsDynamic = isDynamic;
    }

    public string Name { get; }
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public int PosX { get; set; }
    public int PosY { get; set; }
    public bool IsAnimated { get; }
    public bool IsDynamic { get; }
    public IReadOnlyList<SpriteFrameSpec> Frames => _frames;

    internal void AddFrame(SpriteFrameSpec frame)
    {
        _frames.Add(frame);
        if (Width == 0)
        {
            Width = frame.SourceRect.Width;
        }

        if (Height == 0)
        {
            Height = frame.SourceRect.Height;
        }
    }

    internal SpriteDefinition Clone()
    {
        var copy = new SpriteDefinition(Name, Width, Height, PosX, PosY, IsAnimated, IsDynamic);
        for (int i = 0; i < _frames.Count; i++)
        {
            copy._frames.Add(_frames[i]);
        }

        return copy;
    }
}

/// <summary>
/// Instance-scoped sprite registry loading <c>resources/*.csv</c> static (<c>SpriteF</c>) &amp; animated (<c>SpriteAnime</c>) definitions
/// (including multi-frame animated WebP/GIF auto-expansion) and script-created sprites (<c>SPRITECREATE</c> / <c>SPRITEANIMECREATE</c>).
/// File-backed sprites are decoded lazily on first <see cref="TryResolveFrame"/> and bounded by an LRU byte-budget cache.
/// </summary>
public sealed class SpriteRegistry : IDisposable
{
    public const long DefaultMaxDecodedCacheBytes = 128L * 1024 * 1024;

    private sealed class DecodedCacheEntry
    {
        public required string Path { get; init; }
        public required IReadOnlyList<DecodedImageFrame> Frames { get; init; }
        public required long ByteSize { get; init; }
        public required LinkedListNode<string> LruNode { get; init; }
    }

    internal sealed class SpriteUndoDelta
    {
        public Dictionary<string, SpriteDefinition?> OriginalSprites { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly object _cacheSync = new();
    private readonly Dictionary<string, SpriteDefinition> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private SpriteUndoDelta? _activeUndoDelta;
    private readonly Dictionary<string, DecodedCacheEntry> _decodedFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lruOrder = new();
    private readonly Dictionary<string, ImageHeaderInfo> _headerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<DecodedImageFrame>> _pinnedMemoryFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IVirtualFileSystem> _fileVfsOverrides = new(StringComparer.OrdinalIgnoreCase);
    private IVirtualFileSystem? _primaryVfs;
    private long _maxDecodedCacheBytes = DefaultMaxDecodedCacheBytes;
    private long _currentDecodedBytes;
    private int _animatedSpriteCount;
    private bool _disposed;

    public int Count => _sprites.Count;
    public bool HasAnimatedSprites => _animatedSpriteCount > 0;

    /// <summary>
    /// Maximum decoded native pixel-buffer bytes kept in the lazy file-backed LRU cache.
    /// Setting a lower value immediately evicts least-recently-used entries down to the new budget.
    /// </summary>
    public long MaxDecodedCacheBytes
    {
        get
        {
            lock (_cacheSync)
            {
                return _maxDecodedCacheBytes;
            }
        }
        set
        {
            lock (_cacheSync)
            {
                _maxDecodedCacheBytes = Math.Max(0L, value);
                if (!_disposed)
                {
                    TrimDecodedCacheLocked(protectedKey: null, _maxDecodedCacheBytes);
                }
            }
        }
    }

    /// <summary>
    /// Current total decoded pixel bytes resident in the file-backed sprite LRU cache.
    /// </summary>
    public long CurrentDecodedCacheBytes
    {
        get
        {
            lock (_cacheSync)
            {
                return _currentDecodedBytes;
            }
        }
    }

    /// <summary>
    /// Number of decoded image files currently resident in the file-backed sprite LRU cache.
    /// </summary>
    public int DecodedFileCount
    {
        get
        {
            lock (_cacheSync)
            {
                return _decodedFileCache.Count;
            }
        }
    }

    /// <summary>
    /// Evicts least-recently-used decoded file-backed bitmaps until resident bytes are at or below
    /// <paramref name="maxBytes"/> (or <see cref="MaxDecodedCacheBytes"/> when null).
    /// </summary>
    public int TrimDecodedCache(long? maxBytes = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_cacheSync)
        {
            long budget = maxBytes.HasValue ? Math.Max(0L, maxBytes.Value) : _maxDecodedCacheBytes;
            return TrimDecodedCacheLocked(protectedKey: null, budget);
        }
    }

    internal SpriteUndoDelta BeginUndoDelta()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var delta = new SpriteUndoDelta();
        _activeUndoDelta = delta;
        return delta;
    }

    internal void DetachUndoDelta() => _activeUndoDelta = null;

    internal void RestoreUndoDelta(SpriteUndoDelta delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeUndoDelta = null;
        foreach (KeyValuePair<string, SpriteDefinition?> kv in delta.OriginalSprites)
        {
            if (kv.Value is not null)
            {
                SetSpriteRaw(kv.Key, kv.Value);
            }
            else
            {
                RemoveSpriteRaw(kv.Key);
            }
        }
    }

    private void RecordSpriteBeforeMutation(string key)
    {
        if (_activeUndoDelta is not null && !_activeUndoDelta.OriginalSprites.ContainsKey(key))
        {
            _activeUndoDelta.OriginalSprites[key] = _sprites.TryGetValue(key, out SpriteDefinition? existing)
                ? existing.Clone()
                : null;
        }
    }

    private void SetSpriteInternal(string key, SpriteDefinition def)
    {
        RecordSpriteBeforeMutation(key);
        SetSpriteRaw(key, def);
    }

    private void SetSpriteRaw(string key, SpriteDefinition def)
    {
        if (_sprites.TryGetValue(key, out SpriteDefinition? previous) && previous.IsAnimated)
        {
            _animatedSpriteCount = Math.Max(0, _animatedSpriteCount - 1);
        }

        _sprites[key] = def;
        if (def.IsAnimated)
        {
            _animatedSpriteCount++;
        }
    }

    private bool RemoveSpriteInternal(string key)
    {
        if (!_sprites.ContainsKey(key))
        {
            return false;
        }

        RecordSpriteBeforeMutation(key);
        return RemoveSpriteRaw(key);
    }

    private bool RemoveSpriteRaw(string key)
    {
        if (_sprites.Remove(key, out SpriteDefinition? removed))
        {
            if (removed.IsAnimated)
            {
                _animatedSpriteCount = Math.Max(0, _animatedSpriteCount - 1);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Loads all sprite definitions from <c>resources/*.csv</c> in the provided <see cref="IVirtualFileSystem"/>.
    /// </summary>
    public int LoadResourcesFromVfs(IVirtualFileSystem vfs, string resourcesDirectory = "resources")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(vfs);

        if (!vfs.DirectoryExists(resourcesDirectory))
        {
            return 0;
        }

        int loaded = 0;
        foreach (string csvPath in vfs.EnumerateFiles(resourcesDirectory, "*.csv", recursive: true))
        {
            string normalizedCsv = VfsPathNormalizer.Normalize(csvPath);
            int slash = normalizedCsv.LastIndexOf('/');
            string dir = slash > 0 ? normalizedCsv[..slash] : string.Empty;
            string text = vfs.ReadAllText(csvPath);
            loaded += ParseResourceCsv(text, dir, vfs);
        }

        return loaded;
    }

    /// <summary>
    /// Parses Emuera resource CSV lines (static SpriteF and ANIME SpriteAnime definitions).
    /// </summary>
    public int ParseResourceCsv(string csvText, string baseDirectory, IVirtualFileSystem vfs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(vfs);
        if (string.IsNullOrWhiteSpace(csvText))
        {
            return 0;
        }

        int count = 0;
        using var reader = new StringReader(csvText);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }

            string[] tokens = line.Split(',');
            for (int i = 0; i < tokens.Length; i++)
            {
                tokens[i] = tokens[i].Trim();
            }

            if (tokens.Length < 2 || string.IsNullOrEmpty(tokens[0]) || string.IsNullOrEmpty(tokens[1]))
            {
                continue;
            }

            string name = tokens[0];
            string second = tokens[1];

            // Animated sprite declaration: name, ANIME, width, height
            if (second.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
            {
                int w = tokens.Length >= 3 && TryParseInt(tokens[2], out int pw) ? pw : 0;
                int h = tokens.Length >= 4 && TryParseInt(tokens[3], out int ph) ? ph : 0;
                var anime = new SpriteDefinition(name, w, h, 0, 0, isAnimated: true, isDynamic: false);
                SetSpriteInternal(name, anime);
                count++;
                continue;
            }

            // Static sprite or frame appended to an existing ANIME sprite (lazy decode — zero pixel buffers allocated here)
            string? resolvedPath = ResolveResourceImagePath(vfs, baseDirectory, second);
            if (resolvedPath is null)
            {
                continue;
            }

            BindFileSource(resolvedPath, vfs);

            int sx = tokens.Length >= 3 && TryParseInt(tokens[2], out int px) ? px : 0;
            int sy = tokens.Length >= 4 && TryParseInt(tokens[3], out int py) ? py : 0;
            int psw = 0;
            int psh = 0;
            bool hasExplicitSw = tokens.Length >= 5 && TryParseInt(tokens[4], out psw) && psw > 0;
            bool hasExplicitSh = tokens.Length >= 6 && TryParseInt(tokens[5], out psh) && psh > 0;
            int posX = tokens.Length >= 7 && TryParseInt(tokens[6], out int ox) ? ox : 0;
            int posY = tokens.Length >= 8 && TryParseInt(tokens[7], out int oy) ? oy : 0;
            int delay = tokens.Length >= 9 && TryParseInt(tokens[8], out int d) && d > 0 ? d : 100;

            bool needsHeader = !hasExplicitSw || !hasExplicitSh || IsPotentiallyAnimatedExtension(resolvedPath);
            ImageHeaderInfo header = default;
            if (needsHeader && !TryPeekOrGetHeader(vfs, resolvedPath, out header))
            {
                continue;
            }

            int sw = hasExplicitSw ? psw : header.Width - sx;
            int sh = hasExplicitSh ? psh : header.Height - sy;
            var srcRect = SKRectI.Create(sx, sy, Math.Max(0, sw), Math.Max(0, sh));

            if (_sprites.TryGetValue(name, out SpriteDefinition? existing) && existing.IsAnimated)
            {
                existing.AddFrame(new SpriteFrameSpec(null, null, srcRect, posX, posY, delay, resolvedPath, 0));
                continue;
            }

            if (needsHeader && header.FrameCount > 1)
            {
                // Multi-frame animated WebP / GIF automatically registers all frames & durations without decoding pixels upfront!
                var animDef = new SpriteDefinition(name, srcRect.Width, srcRect.Height, posX, posY, isAnimated: true, isDynamic: false);
                int clampedW = Math.Min(srcRect.Width, Math.Max(0, header.Width - sx));
                int clampedH = Math.Min(srcRect.Height, Math.Max(0, header.Height - sy));
                SKRectI frameRect = SKRectI.Create(sx, sy, clampedW, clampedH);
                for (int fi = 0; fi < header.FrameCount; fi++)
                {
                    int durationMs = fi < header.FrameDurationsMs.Length && header.FrameDurationsMs[fi] > 0
                        ? header.FrameDurationsMs[fi]
                        : 100;
                    animDef.AddFrame(new SpriteFrameSpec(
                        null,
                        null,
                        frameRect,
                        posX,
                        posY,
                        durationMs,
                        resolvedPath,
                        fi));
                }

                SetSpriteInternal(name, animDef);
                count++;
            }
            else
            {
                var def = new SpriteDefinition(name, srcRect.Width, srcRect.Height, posX, posY, isAnimated: false, isDynamic: false);
                def.AddFrame(new SpriteFrameSpec(null, null, srcRect, posX, posY, 0, resolvedPath, 0));
                SetSpriteInternal(name, def);
                count++;
            }
        }

        return count;
    }

    public bool CreateSprite(
        string name,
        int graphicsId,
        GraphicsManager graphics,
        int x = 0,
        int y = 0,
        int? width = null,
        int? height = null,
        int posX = 0,
        int posY = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(graphics);
        if (string.IsNullOrWhiteSpace(name) || !graphics.TryGetSurface(graphicsId, out GraphicsSurface surface))
        {
            return false;
        }

        int w = width ?? (surface.Width - x);
        int h = height ?? (surface.Height - y);
        if (w <= 0 || h <= 0)
        {
            return false;
        }

        var def = new SpriteDefinition(name.Trim(), w, h, posX, posY, isAnimated: false, isDynamic: true);
        def.AddFrame(new SpriteFrameSpec(null, graphicsId, SKRectI.Create(x, y, w, h), posX, posY, 0));
        SetSpriteInternal(def.Name, def);
        return true;
    }

    public bool CreateAnimatedSprite(string name, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(name) || width <= 0 || height <= 0)
        {
            return false;
        }

        string key = name.Trim();
        SetSpriteInternal(key, new SpriteDefinition(key, width, height, 0, 0, isAnimated: true, isDynamic: true));
        return true;
    }

    public bool AddAnimatedFrame(
        string name,
        int graphicsId,
        int x,
        int y,
        int width,
        int height,
        int posX = 0,
        int posY = 0,
        int delayMs = 100)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string key = name.Trim();
        if (string.IsNullOrWhiteSpace(key) ||
            !_sprites.TryGetValue(key, out SpriteDefinition? def) ||
            !def.IsAnimated ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

        RecordSpriteBeforeMutation(key);
        def.AddFrame(new SpriteFrameSpec(null, graphicsId, SKRectI.Create(x, y, width, height), posX, posY, Math.Max(1, delayMs)));
        return true;
    }

    public bool RegisterDecodedFrames(string name, IReadOnlyList<DecodedImageFrame> frames, int posX = 0, int posY = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frames);
        if (string.IsNullOrWhiteSpace(name) || frames.Count == 0)
        {
            return false;
        }

        string key = name.Trim();
        string memKey = $"__mem__/{key}";
        lock (_cacheSync)
        {
            if (_pinnedMemoryFrames.TryGetValue(memKey, out IReadOnlyList<DecodedImageFrame>? previous) &&
                !ReferenceEquals(previous, frames))
            {
                DisposeFrames(previous);
            }

            _pinnedMemoryFrames[memKey] = frames;
        }

        SKBitmap first = frames[0].Bitmap;
        var def = new SpriteDefinition(key, first.Width, first.Height, posX, posY, isAnimated: frames.Count > 1, isDynamic: true);
        foreach (DecodedImageFrame frame in frames)
        {
            def.AddFrame(new SpriteFrameSpec(
                frame.Bitmap,
                null,
                SKRectI.Create(0, 0, frame.Bitmap.Width, frame.Bitmap.Height),
                posX,
                posY,
                frame.DurationMs));
        }

        SetSpriteInternal(key, def);
        return true;
    }

    public bool Exists(string? name) =>
        !string.IsNullOrWhiteSpace(name) && _sprites.ContainsKey(name.Trim());

    public bool TryGetDefinition(string? name, out SpriteDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.IsNullOrWhiteSpace(name) && _sprites.TryGetValue(name.Trim(), out SpriteDefinition? found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    public bool DisposeSprite(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return !string.IsNullOrWhiteSpace(name) && RemoveSpriteInternal(name.Trim());
    }

    public void DisposeAll(bool onlyDynamic = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!onlyDynamic)
        {
            if (_activeUndoDelta is not null)
            {
                foreach (string k in _sprites.Keys)
                {
                    RecordSpriteBeforeMutation(k);
                }
            }

            _sprites.Clear();
            _animatedSpriteCount = 0;
            return;
        }

        List<string> toRemove = [];
        foreach (KeyValuePair<string, SpriteDefinition> kv in _sprites)
        {
            if (kv.Value.IsDynamic)
            {
                toRemove.Add(kv.Key);
            }
        }

        foreach (string key in toRemove)
        {
            RemoveSpriteInternal(key);
        }
    }

    public int GetWidth(string name) => TryGetDefinition(name, out SpriteDefinition d) ? d.Width : 0;
    public int GetHeight(string name) => TryGetDefinition(name, out SpriteDefinition d) ? d.Height : 0;
    public int GetPosX(string name) => TryGetDefinition(name, out SpriteDefinition d) ? d.PosX : 0;
    public int GetPosY(string name) => TryGetDefinition(name, out SpriteDefinition d) ? d.PosY : 0;

    public bool SetPos(string name, int posX, int posY)
    {
        if (!TryGetDefinition(name, out SpriteDefinition d))
        {
            return false;
        }

        RecordSpriteBeforeMutation(name.Trim());
        d.PosX = posX;
        d.PosY = posY;
        return true;
    }

    public bool Move(string name, int deltaX, int deltaY)
    {
        if (!TryGetDefinition(name, out SpriteDefinition d))
        {
            return false;
        }

        RecordSpriteBeforeMutation(name.Trim());
        d.PosX += deltaX;
        d.PosY += deltaY;
        return true;
    }

    /// <summary>
    /// Resolves the active frame bitmap and source rectangle for <paramref name="name"/> at <paramref name="elapsedMs"/>.
    /// </summary>
    public bool TryResolveFrame(
        string? name,
        GraphicsManager? graphics,
        long elapsedMs,
        out SpriteResolvedFrame resolved)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        resolved = default;
        if (!TryGetDefinition(name, out SpriteDefinition def) || def.Frames.Count == 0)
        {
            return false;
        }

        SpriteFrameSpec spec = SelectFrame(def, elapsedMs);
        SKBitmap? bmp = spec.StaticBitmap;
        if (bmp is null && !string.IsNullOrEmpty(spec.ImageFilePath))
        {
            IReadOnlyList<DecodedImageFrame>? frames = GetOrDecodeFileFrames(spec.ImageFilePath);
            if (frames is not null && frames.Count > 0)
            {
                int idx = (uint)spec.ImageFrameIndex < (uint)frames.Count
                    ? spec.ImageFrameIndex
                    : frames.Count - 1;
                bmp = frames[idx].Bitmap;
            }
        }
        else if (bmp is null && spec.GraphicsId.HasValue && graphics is not null &&
                 graphics.TryGetSurface(spec.GraphicsId.Value, out GraphicsSurface surface))
        {
            bmp = surface.Bitmap;
        }

        if (bmp is null)
        {
            return false;
        }

        SKRectI rect = spec.SourceRect;
        if (rect.Right > bmp.Width || rect.Bottom > bmp.Height)
        {
            int clampedW = Math.Max(0, Math.Min(rect.Width, bmp.Width - rect.Left));
            int clampedH = Math.Max(0, Math.Min(rect.Height, bmp.Height - rect.Top));
            rect = SKRectI.Create(rect.Left, rect.Top, clampedW, clampedH);
        }

        resolved = new SpriteResolvedFrame(
            bmp,
            rect,
            def.IsAnimated ? spec.PosX : def.PosX,
            def.IsAnimated ? spec.PosY : def.PosY,
            def.Width > 0 ? def.Width : rect.Width,
            def.Height > 0 ? def.Height : rect.Height);
        return true;
    }

    /// <summary>
    /// Implements SPRITEGETCOLOR: samples the ARGB pixel color at local sprite coordinate (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    public uint GetColor(string name, int x, int y, GraphicsManager? graphics = null, long elapsedMs = 0)
    {
        if (!TryResolveFrame(name, graphics, elapsedMs, out SpriteResolvedFrame frame))
        {
            return 0u;
        }

        if (x < 0 || y < 0 || x >= frame.SourceRect.Width || y >= frame.SourceRect.Height)
        {
            return 0u;
        }

        int bx = frame.SourceRect.Left + x;
        int by = frame.SourceRect.Top + y;
        if ((uint)bx >= (uint)frame.Bitmap.Width || (uint)by >= (uint)frame.Bitmap.Height)
        {
            return 0u;
        }

        return RenderingColor.ToArgb(frame.Bitmap.GetPixel(bx, by));
    }

    private static SpriteFrameSpec SelectFrame(SpriteDefinition def, long elapsedMs)
    {
        if (def.Frames.Count == 1)
        {
            return def.Frames[0];
        }

        long totalDuration = 0;
        for (int i = 0; i < def.Frames.Count; i++)
        {
            totalDuration += Math.Max(1, def.Frames[i].DelayMs);
        }

        if (totalDuration <= 0)
        {
            return def.Frames[0];
        }

        long mod = ((elapsedMs % totalDuration) + totalDuration) % totalDuration;
        long acc = 0;
        for (int i = 0; i < def.Frames.Count; i++)
        {
            acc += Math.Max(1, def.Frames[i].DelayMs);
            if (mod < acc)
            {
                return def.Frames[i];
            }
        }

        return def.Frames[^1];
    }

    private static string? ResolveResourceImagePath(
        IVirtualFileSystem vfs,
        string baseDirectory,
        string imageFileName)
    {
        string candidate = string.IsNullOrEmpty(baseDirectory)
            ? VfsPathNormalizer.Normalize(imageFileName)
            : VfsPathNormalizer.Combine(baseDirectory, imageFileName);

        if (vfs.FileExists(candidate))
        {
            return candidate;
        }

        candidate = GraphicsManager.ResolveImagePath(vfs, imageFileName);
        return string.IsNullOrEmpty(candidate) ? null : candidate;
    }

    private static bool IsPotentiallyAnimatedExtension(string path)
    {
        string ext = VfsPathNormalizer.GetExtension(path);
        return !(ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase));
    }

    private void BindFileSource(string resolvedPath, IVirtualFileSystem vfs)
    {
        lock (_cacheSync)
        {
            if (_primaryVfs is null)
            {
                _primaryVfs = vfs;
            }
            else if (!ReferenceEquals(_primaryVfs, vfs))
            {
                _fileVfsOverrides[resolvedPath] = vfs;
            }
        }
    }

    private bool TryPeekOrGetHeader(IVirtualFileSystem vfs, string resolvedPath, out ImageHeaderInfo header)
    {
        lock (_cacheSync)
        {
            if (_headerCache.TryGetValue(resolvedPath, out header))
            {
                return true;
            }
        }

        using Stream stream = vfs.OpenRead(resolvedPath);
        if (!ImageCodecDecoder.TryPeekHeader(stream, out header))
        {
            return false;
        }

        lock (_cacheSync)
        {
            _headerCache[resolvedPath] = header;
        }

        return true;
    }

    private IReadOnlyList<DecodedImageFrame>? GetOrDecodeFileFrames(string resolvedPath)
    {
        IVirtualFileSystem? vfs;
        lock (_cacheSync)
        {
            if (_decodedFileCache.TryGetValue(resolvedPath, out DecodedCacheEntry? cached))
            {
                TouchLruLocked(cached);
                return cached.Frames;
            }

            if (!_fileVfsOverrides.TryGetValue(resolvedPath, out vfs))
            {
                vfs = _primaryVfs;
            }
        }

        if (vfs is null || !vfs.FileExists(resolvedPath))
        {
            return null;
        }

        IReadOnlyList<DecodedImageFrame> decoded;
        using (Stream stream = vfs.OpenRead(resolvedPath))
        {
            decoded = ImageCodecDecoder.DecodeFrames(stream);
        }

        if (decoded.Count == 0)
        {
            return null;
        }

        long byteSize = ComputeFramesByteSize(decoded);
        lock (_cacheSync)
        {
            if (_disposed)
            {
                DisposeFrames(decoded);
                return null;
            }

            if (_decodedFileCache.TryGetValue(resolvedPath, out DecodedCacheEntry? raced))
            {
                DisposeFrames(decoded);
                TouchLruLocked(raced);
                return raced.Frames;
            }

            LinkedListNode<string> node = _lruOrder.AddFirst(resolvedPath);
            var entry = new DecodedCacheEntry
            {
                Path = resolvedPath,
                Frames = decoded,
                ByteSize = byteSize,
                LruNode = node,
            };

            _decodedFileCache[resolvedPath] = entry;
            _currentDecodedBytes += byteSize;
            TrimDecodedCacheLocked(protectedKey: resolvedPath, _maxDecodedCacheBytes);
            return decoded;
        }
    }

    private void TouchLruLocked(DecodedCacheEntry entry)
    {
        if (!ReferenceEquals(_lruOrder.First, entry.LruNode))
        {
            _lruOrder.Remove(entry.LruNode);
            _lruOrder.AddFirst(entry.LruNode);
        }
    }

    private int TrimDecodedCacheLocked(string? protectedKey, long maxBytes)
    {
        int evicted = 0;
        LinkedListNode<string>? cursor = _lruOrder.Last;
        while (_currentDecodedBytes > maxBytes && cursor is not null)
        {
            LinkedListNode<string>? prev = cursor.Previous;
            string key = cursor.Value;
            if (protectedKey is not null && string.Equals(key, protectedKey, StringComparison.OrdinalIgnoreCase))
            {
                cursor = prev;
                continue;
            }

            _lruOrder.Remove(cursor);
            if (_decodedFileCache.Remove(key, out DecodedCacheEntry? removed))
            {
                _currentDecodedBytes = Math.Max(0L, _currentDecodedBytes - removed.ByteSize);
                DisposeFrames(removed.Frames);
                evicted++;
            }

            cursor = prev;
        }

        return evicted;
    }

    private static long ComputeFramesByteSize(IReadOnlyList<DecodedImageFrame> frames)
    {
        long total = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            total += Math.Max(0L, frames[i].Bitmap.ByteCount);
        }

        return total;
    }

    private static void DisposeFrames(IReadOnlyList<DecodedImageFrame> frames)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            frames[i].Bitmap.Dispose();
        }
    }

    private static bool TryParseInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public void Dispose()
    {
        lock (_cacheSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (DecodedCacheEntry entry in _decodedFileCache.Values)
            {
                DisposeFrames(entry.Frames);
            }

            foreach (IReadOnlyList<DecodedImageFrame> frames in _pinnedMemoryFrames.Values)
            {
                DisposeFrames(frames);
            }

            _decodedFileCache.Clear();
            _lruOrder.Clear();
            _headerCache.Clear();
            _pinnedMemoryFrames.Clear();
            _fileVfsOverrides.Clear();
            _primaryVfs = null;
            _currentDecodedBytes = 0;
        }

        _sprites.Clear();
        _animatedSpriteCount = 0;
    }
}
