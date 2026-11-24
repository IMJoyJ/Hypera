using Hypera.VirtualFs;
using SkiaSharp;

namespace Hypera.Rendering;

public readonly record struct DecodedImageFrame(SKBitmap Bitmap, int DurationMs);

/// <summary>
/// Cross-platform SkiaSharp + HarfBuzz frame compositor operating in the authoritative <see cref="LogicalViewport"/>.
/// Supports both crisp nearest-neighbor pixel-grid rendering (for FC/NES tile-fonts in font/*.ttf and pixel art)
/// and anti-aliased vector typography, plus static/animated WebP/PNG/BMP decoding via <see cref="SKCodec"/>.
/// </summary>
public sealed class SkiaFrameCompositor : IDisposable
{
    private readonly Dictionary<string, SKTypeface> _customTypefaces = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public static SKSamplingOptions CrispSampling { get; } = new(SKFilterMode.Nearest, SKMipmapMode.None);
    public static SKSamplingOptions SmoothSampling { get; } = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public void RegisterCustomFontsFromVfs(IVirtualFileSystem vfs, string fontDirectory = "font")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(vfs);

        if (!vfs.DirectoryExists(fontDirectory))
        {
            return;
        }

        foreach (string path in vfs.EnumerateFiles(fontDirectory, "*.*", recursive: true))
        {
            string ext = VfsPathNormalizer.GetExtension(path);
            if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using Stream stream = vfs.OpenRead(path);
            SKTypeface? typeface = SKTypeface.FromStream(stream);
            if (typeface is not null && !string.IsNullOrEmpty(typeface.FamilyName))
            {
                _customTypefaces[typeface.FamilyName] = typeface;
            }
        }
    }

    public SKTypeface ResolveTypeface(string? familyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.IsNullOrWhiteSpace(familyName) &&
            _customTypefaces.TryGetValue(familyName, out SKTypeface? custom))
        {
            return custom;
        }

        return string.IsNullOrWhiteSpace(familyName)
            ? SKTypeface.Default
            : SKTypeface.FromFamilyName(familyName) ?? SKTypeface.Default;
    }

    /// <summary>
    /// Decodes static or multi-frame animated images (including WebP, PNG, BMP, GIF) using <see cref="SKCodec"/>.
    /// </summary>
    public static IReadOnlyList<DecodedImageFrame> DecodeFrames(Stream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        using var managedStream = new SKManagedStream(imageStream, disposeManagedStream: false);
        using SKCodec? codec = SKCodec.Create(managedStream);
        if (codec is null)
        {
            return [];
        }

        SKImageInfo info = new(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        int frameCount = codec.FrameCount;
        if (frameCount <= 0)
        {
            var single = new SKBitmap(info);
            codec.GetPixels(info, single.GetPixels());
            return [new DecodedImageFrame(single, 0)];
        }

        SKCodecFrameInfo[] frameInfos = codec.FrameInfo;
        var frames = new List<DecodedImageFrame>(frameCount);
        for (int i = 0; i < frameCount; i++)
        {
            var bmp = new SKBitmap(info);
            var opts = new SKCodecOptions(i);
            codec.GetPixels(info, bmp.GetPixels(), opts);
            int durationMs = i < frameInfos.Length ? Math.Max(16, frameInfos[i].Duration) : 100;
            frames.Add(new DecodedImageFrame(bmp, durationMs));
        }

        return frames;
    }

    public void RenderSnapshot(SKCanvas canvas, int screenWidth, int screenHeight, DisplaySnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(snapshot);

        canvas.Clear(SKColors.Black);
        LetterboxRect box = ViewportTransform.ComputeLetterbox(
            snapshot.Viewport.LogicalWidth,
            snapshot.Viewport.LogicalHeight,
            screenWidth,
            screenHeight);

        if (box.Scale <= 0f)
        {
            return;
        }

        canvas.Save();
        canvas.Translate(box.OffsetX, box.OffsetY);
        canvas.Scale(box.Scale, box.Scale);
        canvas.ClipRect(new SKRect(0, 0, snapshot.Viewport.LogicalWidth, snapshot.Viewport.LogicalHeight));

        using var bgPaint = new SKPaint { Color = UnpackRgba(snapshot.BackgroundColorRgba), Style = SKPaintStyle.Fill };
        canvas.DrawRect(0, 0, snapshot.Viewport.LogicalWidth, snapshot.Viewport.LogicalHeight, bgPaint);

        using var fgPaint = new SKPaint { Style = SKPaintStyle.Fill };
        foreach (DisplayOperation op in snapshot.Operations)
        {
            if (op.Kind is DisplayOperationKind.PrintTextSpan or DisplayOperationKind.PrintButtonSpan &&
                !string.IsNullOrEmpty(op.Text))
            {
                bool crisp = op.SamplingMode == PixelSamplingMode.CrispNearestNeighbor;
                SKTypeface typeface = ResolveTypeface(op.FontFamily);
                using var font = new SKFont(typeface, op.FontSize)
                {
                    Edging = crisp ? SKFontEdging.Alias : SKFontEdging.SubpixelAntialias,
                    Subpixel = !crisp,
                };
                fgPaint.Color = UnpackRgba(op.ColorRgba);
                float x = crisp ? MathF.Round(op.Bounds.X) : op.Bounds.X;
                float y = crisp ? MathF.Round(op.Bounds.Y + op.FontSize) : op.Bounds.Y + op.FontSize;
                canvas.DrawText(op.Text, x, y, SKTextAlign.Left, font, fgPaint);
            }
        }

        canvas.Restore();
    }

    private static SKColor UnpackRgba(uint rgba) =>
        new(
            red: (byte)((rgba >> 24) & 0xFF),
            green: (byte)((rgba >> 16) & 0xFF),
            blue: (byte)((rgba >> 8) & 0xFF),
            alpha: (byte)(rgba & 0xFF));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SKTypeface tf in _customTypefaces.Values)
        {
            tf.Dispose();
        }

        _customTypefaces.Clear();
    }
}
