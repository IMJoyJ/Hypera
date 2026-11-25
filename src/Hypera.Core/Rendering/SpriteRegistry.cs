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
    int DelayMs);

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
}

/// <summary>
/// Instance-scoped sprite registry loading <c>resources/*.csv</c> static (<c>SpriteF</c>) &amp; animated (<c>SpriteAnime</c>) definitions
/// (including multi-frame animated WebP/GIF auto-expansion) and script-created sprites (<c>SPRITECREATE</c> / <c>SPRITEANIMECREATE</c>).
/// </summary>
public sealed class SpriteRegistry : IDisposable
{
    private readonly Dictionary<string, SpriteDefinition> _sprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<DecodedImageFrame>> _decodedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public int Count => _sprites.Count;

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
                _sprites[name] = anime;
                count++;
                continue;
            }

            // Static sprite or frame appended to an existing ANIME sprite
            IReadOnlyList<DecodedImageFrame>? decoded = LoadOrGetDecodedImage(vfs, baseDirectory, second);
            if (decoded is null || decoded.Count == 0)
            {
                continue;
            }

            SKBitmap firstBmp = decoded[0].Bitmap;
            int sx = tokens.Length >= 3 && TryParseInt(tokens[2], out int px) ? px : 0;
            int sy = tokens.Length >= 4 && TryParseInt(tokens[3], out int py) ? py : 0;
            int sw = tokens.Length >= 5 && TryParseInt(tokens[4], out int psw) && psw > 0 ? psw : firstBmp.Width - sx;
            int sh = tokens.Length >= 6 && TryParseInt(tokens[5], out int psh) && psh > 0 ? psh : firstBmp.Height - sy;
            int posX = tokens.Length >= 7 && TryParseInt(tokens[6], out int ox) ? ox : 0;
            int posY = tokens.Length >= 8 && TryParseInt(tokens[7], out int oy) ? oy : 0;
            int delay = tokens.Length >= 9 && TryParseInt(tokens[8], out int d) && d > 0 ? d : 100;

            var srcRect = SKRectI.Create(sx, sy, Math.Max(0, sw), Math.Max(0, sh));

            if (_sprites.TryGetValue(name, out SpriteDefinition? existing) && existing.IsAnimated)
            {
                existing.AddFrame(new SpriteFrameSpec(firstBmp, null, srcRect, posX, posY, delay));
                continue;
            }

            if (decoded.Count > 1)
            {
                // Multi-frame animated WebP / GIF automatically registers all decoded frames & durations!
                var animDef = new SpriteDefinition(name, srcRect.Width, srcRect.Height, posX, posY, isAnimated: true, isDynamic: false);
                foreach (DecodedImageFrame f in decoded)
                {
                    animDef.AddFrame(new SpriteFrameSpec(
                        f.Bitmap,
                        null,
                        SKRectI.Create(sx, sy, Math.Min(srcRect.Width, f.Bitmap.Width - sx), Math.Min(srcRect.Height, f.Bitmap.Height - sy)),
                        posX,
                        posY,
                        f.DurationMs > 0 ? f.DurationMs : 100));
                }

                _sprites[name] = animDef;
                count++;
            }
            else
            {
                var def = new SpriteDefinition(name, srcRect.Width, srcRect.Height, posX, posY, isAnimated: false, isDynamic: false);
                def.AddFrame(new SpriteFrameSpec(firstBmp, null, srcRect, posX, posY, 0));
                _sprites[name] = def;
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
        _sprites[def.Name] = def;
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
        _sprites[key] = new SpriteDefinition(key, width, height, 0, 0, isAnimated: true, isDynamic: true);
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
        if (string.IsNullOrWhiteSpace(name) ||
            !_sprites.TryGetValue(name.Trim(), out SpriteDefinition? def) ||
            !def.IsAnimated ||
            width <= 0 ||
            height <= 0)
        {
            return false;
        }

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
        _decodedFiles[$"__mem__/{key}"] = frames;
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

        _sprites[key] = def;
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
        return !string.IsNullOrWhiteSpace(name) && _sprites.Remove(name.Trim());
    }

    public void DisposeAll(bool onlyDynamic = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!onlyDynamic)
        {
            _sprites.Clear();
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
            _sprites.Remove(key);
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
        if (bmp is null && spec.GraphicsId.HasValue && graphics is not null &&
            graphics.TryGetSurface(spec.GraphicsId.Value, out GraphicsSurface surface))
        {
            bmp = surface.Bitmap;
        }

        if (bmp is null)
        {
            return false;
        }

        resolved = new SpriteResolvedFrame(
            bmp,
            spec.SourceRect,
            def.IsAnimated ? spec.PosX : def.PosX,
            def.IsAnimated ? spec.PosY : def.PosY,
            def.Width > 0 ? def.Width : spec.SourceRect.Width,
            def.Height > 0 ? def.Height : spec.SourceRect.Height);
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

    private IReadOnlyList<DecodedImageFrame>? LoadOrGetDecodedImage(
        IVirtualFileSystem vfs,
        string baseDirectory,
        string imageFileName)
    {
        string candidate = string.IsNullOrEmpty(baseDirectory)
            ? VfsPathNormalizer.Normalize(imageFileName)
            : VfsPathNormalizer.Combine(baseDirectory, imageFileName);

        if (!vfs.FileExists(candidate))
        {
            candidate = GraphicsManager.ResolveImagePath(vfs, imageFileName);
            if (string.IsNullOrEmpty(candidate))
            {
                return null;
            }
        }

        if (_decodedFiles.TryGetValue(candidate, out IReadOnlyList<DecodedImageFrame>? cached))
        {
            return cached;
        }

        using Stream stream = vfs.OpenRead(candidate);
        IReadOnlyList<DecodedImageFrame> decoded = ImageCodecDecoder.DecodeFrames(stream);
        if (decoded.Count == 0)
        {
            return null;
        }

        _decodedFiles[candidate] = decoded;
        return decoded;
    }

    private static bool TryParseInt(string s, out int value) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IReadOnlyList<DecodedImageFrame> frames in _decodedFiles.Values)
        {
            foreach (DecodedImageFrame f in frames)
            {
                f.Bitmap.Dispose();
            }
        }

        _decodedFiles.Clear();
        _sprites.Clear();
    }
}
