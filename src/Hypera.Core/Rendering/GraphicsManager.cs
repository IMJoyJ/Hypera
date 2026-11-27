using Hypera.VirtualFs;
using SkiaSharp;

namespace Hypera.Rendering;

/// <summary>
/// Instance-scoped offscreen graphics handle table implementing Emuera's GCREATE / GDISPOSE / GDRAW* / GSAVE / GLOAD family.
/// Holds zero global static state.
/// </summary>
public sealed class GraphicsManager : IDisposable
{
    internal sealed class GraphicsUndoDelta : IDisposable
    {
        private bool _disposed;

        public Dictionary<int, GraphicsSurface?> OriginalSurfaces { get; } = [];

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (GraphicsSurface? s in OriginalSurfaces.Values)
            {
                s?.Dispose();
            }

            OriginalSurfaces.Clear();
        }
    }

    private readonly Dictionary<int, GraphicsSurface> _surfaces = [];
    private readonly Dictionary<int, byte[]> _memorySavedImages = [];
    private GraphicsUndoDelta? _activeUndoDelta;
    private readonly FontRegistry _fontRegistry;
    private readonly GlyphMeasurer _glyphMeasurer;
    private readonly string? _saveDirectory;
    private IWritableUserStorage? _writableStorage;
    private long _nextSurfaceToken;
    private long _globalMutationVersion = 1;
    private bool _disposed;

    public GraphicsManager(
        FontRegistry fontRegistry,
        GlyphMeasurer glyphMeasurer,
        SKColor? defaultForeColor = null,
        string defaultFontName = "MS Gothic",
        int defaultFontSize = 18,
        string? saveDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(fontRegistry);
        ArgumentNullException.ThrowIfNull(glyphMeasurer);

        _fontRegistry = fontRegistry;
        _glyphMeasurer = glyphMeasurer;
        DefaultForeColor = defaultForeColor ?? new SKColor(0xC0, 0xC0, 0xC0, 0xFF);
        DefaultFontName = defaultFontName;
        DefaultFontSize = Math.Max(1, defaultFontSize);
        _saveDirectory = saveDirectory;
    }

    public SKColor DefaultForeColor { get; }
    public string DefaultFontName { get; }
    public int DefaultFontSize { get; }
    public int Count => _surfaces.Count;
    public long GlobalMutationVersion => Volatile.Read(ref _globalMutationVersion);

    private void BumpGlobalMutationVersion() => Interlocked.Increment(ref _globalMutationVersion);

    public void AttachWritableStorage(IWritableUserStorage? writableStorage)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writableStorage = writableStorage;
    }

    public bool IsCreated(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _surfaces.TryGetValue(id, out GraphicsSurface? s) && !s.IsDisposed;
    }

    public bool TryGetSurface(int id, out GraphicsSurface surface)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surfaces.TryGetValue(id, out GraphicsSurface? found) && !found.IsDisposed)
        {
            surface = found;
            return true;
        }

        surface = null!;
        return false;
    }

    /// <summary>
    /// Computes an instance-unique content version for graphics surface <paramref name="id"/>,
    /// combining its creation token and pixel mutation counter so recreate/redraw/undo are always distinguished.
    /// </summary>
    public bool TryGetSurfaceVersion(int id, out long version)
    {
        if (TryGetSurface(id, out GraphicsSurface surface))
        {
            version = unchecked((surface.InstanceToken * 1_000_003L) ^ surface.MutationVersion);
            return true;
        }

        version = 0;
        return false;
    }

    public bool Create(int id, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        ReplaceSurfaceInternal(id, new GraphicsSurface(id, width, height, DefaultForeColor, DefaultFontName, DefaultFontSize));
        return true;
    }

    public bool CreateFromFile(int id, IVirtualFileSystem vfs, string virtualPath, int frameIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(vfs);
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return false;
        }

        string resolved = ResolveImagePath(vfs, virtualPath);
        if (string.IsNullOrEmpty(resolved))
        {
            return false;
        }

        using Stream stream = vfs.OpenRead(resolved);
        return CreateFromStream(id, stream, frameIndex);
    }

    public bool CreateFromStream(int id, Stream imageStream, int frameIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(imageStream);

        IReadOnlyList<DecodedImageFrame> frames = ImageCodecDecoder.DecodeFrames(imageStream);
        if (frames.Count == 0)
        {
            return false;
        }

        int selected = Math.Clamp(frameIndex, 0, frames.Count - 1);
        SKBitmap chosen = frames[selected].Bitmap;
        for (int i = 0; i < frames.Count; i++)
        {
            if (i != selected)
            {
                frames[i].Bitmap.Dispose();
            }
        }

        ReplaceSurfaceInternal(id, new GraphicsSurface(id, chosen, DefaultForeColor, DefaultFontName, DefaultFontSize));
        return true;
    }

    public bool CreateFromBytes(int id, ReadOnlySpan<byte> imageBytes, int frameIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var ms = new MemoryStream(imageBytes.ToArray(), writable: false);
        return CreateFromStream(id, ms, frameIndex);
    }

    public bool DisposeSurface(int id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_surfaces.Remove(id, out GraphicsSurface? surface))
        {
            if (_activeUndoDelta is not null && !_activeUndoDelta.OriginalSurfaces.ContainsKey(id))
            {
                // Zero-copy ownership transfer into active undo delta.
                _activeUndoDelta.OriginalSurfaces[id] = surface;
            }
            else
            {
                surface.Dispose();
            }

            BumpGlobalMutationVersion();
            return true;
        }

        return false;
    }

    internal GraphicsUndoDelta BeginUndoDelta()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var delta = new GraphicsUndoDelta();
        _activeUndoDelta = delta;
        return delta;
    }

    internal void DetachUndoDelta() => _activeUndoDelta = null;

    internal void RestoreUndoDelta(GraphicsUndoDelta delta)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activeUndoDelta = null;
        foreach (KeyValuePair<int, GraphicsSurface?> kv in delta.OriginalSurfaces)
        {
            if (_surfaces.Remove(kv.Key, out GraphicsSurface? current))
            {
                current.Dispose();
            }

            if (kv.Value is not null)
            {
                kv.Value.InstanceToken = ++_nextSurfaceToken;
                _surfaces[kv.Key] = kv.Value;
            }
        }

        // Ownership of restored surfaces has returned to _surfaces.
        delta.OriginalSurfaces.Clear();
        BumpGlobalMutationVersion();
    }

    private void ReplaceSurfaceInternal(int id, GraphicsSurface replacement)
    {
        replacement.InstanceToken = ++_nextSurfaceToken;
        if (_surfaces.Remove(id, out GraphicsSurface? existing))
        {
            if (_activeUndoDelta is not null && !_activeUndoDelta.OriginalSurfaces.ContainsKey(id))
            {
                _activeUndoDelta.OriginalSurfaces[id] = existing;
            }
            else
            {
                existing.Dispose();
            }
        }
        else
        {
            _activeUndoDelta?.OriginalSurfaces.TryAdd(id, null);
        }

        _surfaces[id] = replacement;
        BumpGlobalMutationVersion();
    }

    private void RecordBeforeInPlaceMutation(int id, GraphicsSurface surface)
    {
        if (_activeUndoDelta is not null && !_activeUndoDelta.OriginalSurfaces.ContainsKey(id))
        {
            _activeUndoDelta.OriginalSurfaces[id] = surface.Clone();
        }

        BumpGlobalMutationVersion();
    }

    public int GetWidth(int id) => TryGetSurface(id, out GraphicsSurface s) ? s.Width : 0;
    public int GetHeight(int id) => TryGetSurface(id, out GraphicsSurface s) ? s.Height : 0;

    public bool Clear(int id, uint argb)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.Clear(argb);
        return true;
    }

    public bool ClearRect(int id, uint argb, int x, int y, int width, int height)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.ClearRect(x, y, width, height, argb);
        return true;
    }

    public bool SetColor(int id, uint argb, int x, int y)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        return s.SetPixelColor(x, y, argb);
    }

    public long GetColor(int id, int x, int y) =>
        TryGetSurface(id, out GraphicsSurface s) ? s.GetPixelColor(x, y) : 0L;

    public bool SetBrush(int id, uint argb)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.SetBrush(argb);
        return true;
    }

    public long GetBrush(int id) =>
        TryGetSurface(id, out GraphicsSurface s) ? RenderingColor.ToArgb(s.BrushColor) : 0L;

    public bool SetPen(int id, uint argb, float width = 1f)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.SetPen(argb, width);
        return true;
    }

    public long GetPen(int id) =>
        TryGetSurface(id, out GraphicsSurface s) ? RenderingColor.ToArgb(s.PenColor) : 0L;

    public int GetPenWidth(int id) =>
        TryGetSurface(id, out GraphicsSurface s) ? (int)MathF.Round(s.PenWidth) : 0;

    public bool SetDashStyle(int id, GraphicsDashStyle style, GraphicsDashCap cap = GraphicsDashCap.Flat)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.SetDashStyle(style, cap);
        return true;
    }

    public bool SetFont(int id, string familyName, float size, FontStyleFlags style = FontStyleFlags.Regular)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.SetFont(familyName, size, style);
        return true;
    }

    public string GetFont(int id) => TryGetSurface(id, out GraphicsSurface s) ? s.Font.FamilyName : string.Empty;
    public int GetFontSize(int id) => TryGetSurface(id, out GraphicsSurface s) ? (int)MathF.Round(s.Font.Size) : 0;
    public FontStyleFlags GetFontStyle(int id) => TryGetSurface(id, out GraphicsSurface s) ? s.Font.Style : FontStyleFlags.Regular;

    public SKSize GetTextSize(string text, string? fontFamily, float fontSize, FontStyleFlags style = FontStyleFlags.Regular) =>
        _glyphMeasurer.MeasureTextSize(text, fontSize, fontFamily, style);

    public bool DrawLine(int id, int x1, int y1, int x2, int y2)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.DrawLine(x1, y1, x2, y2);
        return true;
    }

    public bool FillRectangle(int id, int x, int y, int width, int height)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.FillRectangle(x, y, width, height);
        return true;
    }

    public bool DrawText(int id, string text, int x, int y, PixelSamplingMode samplingMode = PixelSamplingMode.SmoothAntialiased)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.DrawText(text, x, y, _fontRegistry, samplingMode);
        return true;
    }

    public bool DrawGraphics(
        int destId,
        int srcId,
        int destX,
        int destY,
        int destW,
        int destH,
        int srcX,
        int srcY,
        int srcW,
        int srcH,
        long[,]? emueraColorMatrix5x5 = null,
        PixelSamplingMode samplingMode = PixelSamplingMode.Auto)
    {
        if (!TryGetSurface(destId, out GraphicsSurface dst) || !TryGetSurface(srcId, out GraphicsSurface src))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(destId, dst);
        float[]? cm = emueraColorMatrix5x5 is not null
            ? GraphicsSurface.ConvertEmueraColorMatrix5x5(emueraColorMatrix5x5)
            : null;
        dst.DrawGraphics(src, destX, destY, destW, destH, srcX, srcY, srcW, srcH, cm, samplingMode);
        return true;
    }

    public bool DrawGraphicsWithMask(int destId, int srcId, int maskId, int destX, int destY)
    {
        if (!TryGetSurface(destId, out GraphicsSurface dst) ||
            !TryGetSurface(srcId, out GraphicsSurface src) ||
            !TryGetSurface(maskId, out GraphicsSurface mask))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(destId, dst);
        dst.DrawGraphicsWithMask(src, mask, destX, destY);
        return true;
    }

    public bool DrawGraphicsWithRotate(int destId, int srcId, float angleDegrees, float pivotX, float pivotY)
    {
        if (!TryGetSurface(destId, out GraphicsSurface dst) || !TryGetSurface(srcId, out GraphicsSurface src))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(destId, dst);
        dst.DrawGraphicsWithRotate(src, angleDegrees, pivotX, pivotY);
        return true;
    }

    public bool Rotate(int id, float angleDegrees, float pivotX, float pivotY)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.RotateInPlace(angleDegrees, pivotX, pivotY);
        return true;
    }

    public bool DrawSprite(
        int destId,
        SpriteRegistry sprites,
        string spriteName,
        int destX = 0,
        int destY = 0,
        int? destWidth = null,
        int? destHeight = null,
        long[,]? emueraColorMatrix5x5 = null,
        long elapsedMs = 0)
    {
        ArgumentNullException.ThrowIfNull(sprites);
        if (!TryGetSurface(destId, out GraphicsSurface dst) ||
            !sprites.TryResolveFrame(spriteName, this, elapsedMs, out SpriteResolvedFrame frame))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(destId, dst);
        int w = destWidth ?? frame.SourceRect.Width;
        int h = destHeight ?? frame.SourceRect.Height;
        float[]? cm = emueraColorMatrix5x5 is not null
            ? GraphicsSurface.ConvertEmueraColorMatrix5x5(emueraColorMatrix5x5)
            : null;

        dst.DrawBitmap(
            frame.Bitmap,
            new SKRect(frame.SourceRect.Left, frame.SourceRect.Top, frame.SourceRect.Right, frame.SourceRect.Bottom),
            new SKRect(destX, destY, destX + w, destY + h),
            cm);
        return true;
    }

    public long[,]? ToArray(int id) =>
        TryGetSurface(id, out GraphicsSurface s) ? s.ToPixelArray() : null;

    public bool FromArray(int id, long[,] pixels)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        RecordBeforeInPlaceMutation(id, s);
        s.FromPixelArray(pixels);
        return true;
    }

    public bool Save(int id, int saveIndex)
    {
        if (!TryGetSurface(id, out GraphicsSurface s))
        {
            return false;
        }

        byte[] png = s.EncodeToPng();
        if (_writableStorage is not null)
        {
            _writableStorage.WriteAllBytes($"img{saveIndex:D4}.png", png);
        }
        else if (!string.IsNullOrWhiteSpace(_saveDirectory))
        {
            Directory.CreateDirectory(_saveDirectory);
            string path = Path.Combine(_saveDirectory, $"img{saveIndex:D4}.png");
            File.WriteAllBytes(path, png);
        }

        _memorySavedImages[saveIndex] = png;
        return true;
    }

    public bool Load(int id, int saveIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_memorySavedImages.TryGetValue(saveIndex, out byte[]? memBytes))
        {
            return CreateFromBytes(id, memBytes);
        }

        if (_writableStorage is not null)
        {
            string rel = $"img{saveIndex:D4}.png";
            if (_writableStorage.FileExists(rel))
            {
                return CreateFromBytes(id, _writableStorage.ReadAllBytes(rel));
            }
        }
        else if (!string.IsNullOrWhiteSpace(_saveDirectory))
        {
            string path = Path.Combine(_saveDirectory, $"img{saveIndex:D4}.png");
            if (File.Exists(path))
            {
                return CreateFromBytes(id, File.ReadAllBytes(path));
            }
        }

        return false;
    }

    internal static string ResolveImagePath(IVirtualFileSystem vfs, string rawPath)
    {
        string normalized = VfsPathNormalizer.Normalize(rawPath);
        if (vfs.FileExists(normalized))
        {
            return normalized;
        }

        string underResources = VfsPathNormalizer.Combine("resources", normalized);
        if (vfs.FileExists(underResources))
        {
            return underResources;
        }

        return string.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activeUndoDelta = null;
        foreach (GraphicsSurface s in _surfaces.Values)
        {
            s.Dispose();
        }

        _surfaces.Clear();
        _memorySavedImages.Clear();
    }
}
