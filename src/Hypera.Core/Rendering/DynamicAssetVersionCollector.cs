using System.Collections.ObjectModel;
using System.Globalization;

namespace Hypera.Rendering;

/// <summary>
/// Collects a deduplicated per-scene side table of dynamic graphics surface and dynamic sprite content versions
/// on demand without mutating or deep-copying immutable <see cref="ConsoleLine"/> records.
/// </summary>
internal static class DynamicAssetVersionCollector
{
    private const int MaxDivTraversalDepth = 64;

    public static IReadOnlyDictionary<string, long> Collect(
        ConsoleScene scene,
        GraphicsManager? graphics,
        SpriteRegistry? sprites)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return Collect(
            scene.BackgroundImages,
            scene.CbgLayers,
            scene.VisibleLines,
            scene.IslandLayers,
            graphics,
            sprites);
    }

    public static IReadOnlyDictionary<string, long> Collect(
        IReadOnlyList<BackgroundImageLayer> backgroundImages,
        IReadOnlyList<CbgLayer> cbgLayers,
        IReadOnlyList<ConsoleLine> visibleLines,
        IReadOnlyList<HtmlIslandLayer> islandLayers,
        GraphicsManager? graphics,
        SpriteRegistry? sprites)
    {
        if (graphics is null && sprites is null)
        {
            return ReadOnlyDictionary<string, long>.Empty;
        }

        Dictionary<string, long>? versions = null;

        void RecordKey(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            string trimmed = raw.Trim();
            if (trimmed.StartsWith("g:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("graphics:", StringComparison.OrdinalIgnoreCase))
            {
                if (versions is not null && versions.ContainsKey(trimmed))
                {
                    return;
                }

                int colon = trimmed.IndexOf(':');
                if (graphics is not null &&
                    int.TryParse(trimmed[(colon + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gId) &&
                    graphics.TryGetSurfaceVersion(gId, out long surfVer))
                {
                    versions ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    versions[trimmed] = surfVer;
                }

                return;
            }

            if (trimmed.StartsWith("vfs:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string spriteName = trimmed.StartsWith("sprite:", StringComparison.OrdinalIgnoreCase)
                ? trimmed["sprite:".Length..].Trim()
                : trimmed;
            if (spriteName.Length == 0)
            {
                return;
            }

            string canonicalKey = $"sprite:{spriteName}";
            if (versions is not null && versions.ContainsKey(canonicalKey))
            {
                return;
            }

            if (sprites is not null &&
                sprites.TryGetAssetVersion(spriteName, graphics, out long spriteVer, out bool isDynamic) &&
                isDynamic)
            {
                versions ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                versions[canonicalKey] = spriteVer;
            }
        }

        void VisitLines(IReadOnlyList<ConsoleLine> lines, int depth)
        {
            if (depth > MaxDivTraversalDepth)
            {
                return;
            }

            for (int i = 0; i < lines.Count; i++)
            {
                IReadOnlyList<ConsoleRenderPart> parts = lines[i].Parts;
                for (int p = 0; p < parts.Count; p++)
                {
                    ConsoleRenderPart part = parts[p];
                    if (part.Kind == ConsolePartKind.Image)
                    {
                        RecordKey(part.ImageSrc);
                        RecordKey(part.ImageSrcHover);
                    }
                    else if (part.Kind == ConsolePartKind.Div && part.Div is not null)
                    {
                        VisitLines(part.Div.Children, depth + 1);
                    }
                }
            }
        }

        for (int i = 0; i < backgroundImages.Count; i++)
        {
            RecordKey(backgroundImages[i].SpriteName);
        }

        for (int i = 0; i < cbgLayers.Count; i++)
        {
            CbgLayer cbg = cbgLayers[i];
            if (!string.IsNullOrWhiteSpace(cbg.SpriteName))
            {
                RecordKey(cbg.SpriteName);
            }
            else if (cbg.GraphicsId.HasValue)
            {
                RecordKey($"g:{cbg.GraphicsId.Value}");
            }

            if (!string.IsNullOrWhiteSpace(cbg.HoverSpriteName))
            {
                RecordKey(cbg.HoverSpriteName);
            }
        }

        VisitLines(visibleLines, depth: 0);
        for (int i = 0; i < islandLayers.Count; i++)
        {
            VisitLines(islandLayers[i].Lines, depth: 0);
        }

        return versions is not null
            ? new ReadOnlyDictionary<string, long>(versions)
            : ReadOnlyDictionary<string, long>.Empty;
    }

    public static bool ContainsAnimatedSprites(ConsoleScene scene, SpriteRegistry? sprites)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (sprites is null || !sprites.HasAnimatedSprites)
        {
            return false;
        }

        for (int i = 0; i < scene.BackgroundImages.Count; i++)
        {
            if (IsAnimatedSpriteRef(scene.BackgroundImages[i].SpriteName, sprites))
            {
                return true;
            }
        }

        for (int i = 0; i < scene.CbgLayers.Count; i++)
        {
            CbgLayer cbg = scene.CbgLayers[i];
            if (IsAnimatedSpriteRef(cbg.SpriteName, sprites) ||
                IsAnimatedSpriteRef(cbg.HoverSpriteName, sprites))
            {
                return true;
            }
        }

        if (ContainsAnimatedInLines(scene.VisibleLines, sprites, depth: 0))
        {
            return true;
        }

        for (int i = 0; i < scene.IslandLayers.Count; i++)
        {
            if (ContainsAnimatedInLines(scene.IslandLayers[i].Lines, sprites, depth: 0))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAnimatedInLines(IReadOnlyList<ConsoleLine> lines, SpriteRegistry sprites, int depth)
    {
        if (depth > MaxDivTraversalDepth)
        {
            return false;
        }

        for (int i = 0; i < lines.Count; i++)
        {
            IReadOnlyList<ConsoleRenderPart> parts = lines[i].Parts;
            for (int p = 0; p < parts.Count; p++)
            {
                ConsoleRenderPart part = parts[p];
                if (part.Kind == ConsolePartKind.Image)
                {
                    if (IsAnimatedSpriteRef(part.ImageSrc, sprites) ||
                        IsAnimatedSpriteRef(part.ImageSrcHover, sprites))
                    {
                        return true;
                    }
                }
                else if (part.Kind == ConsolePartKind.Div &&
                         part.Div is not null &&
                         ContainsAnimatedInLines(part.Div.Children, sprites, depth + 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsAnimatedSpriteRef(string? raw, SpriteRegistry sprites)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith("g:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("graphics:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("vfs:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string spriteName = trimmed.StartsWith("sprite:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["sprite:".Length..].Trim()
            : trimmed;
        return spriteName.Length > 0 &&
               sprites.TryGetDefinition(spriteName, out SpriteDefinition def) &&
               def.IsAnimated;
    }
}
