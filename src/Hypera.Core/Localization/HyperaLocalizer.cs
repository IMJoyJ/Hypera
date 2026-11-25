using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Hypera.Localization;

/// <summary>
/// Hot-pluggable JSON localization engine with built-in embedded packs (ja default, zh-CN, en)
/// and optional external directory overlay with live FileSystemWatcher reloading.
/// </summary>
public sealed class HyperaLocalizer : IDisposable
{
    public const string DefaultLocaleCode = "ja";

    private static readonly Lazy<HyperaLocalizer> DefaultInstance =
        new(() => new HyperaLocalizer(loadBuiltIn: true), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lock _sync = new();
    private readonly Dictionary<string, LocalePack> _builtIn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalePack> _external = new(StringComparer.OrdinalIgnoreCase);

    private volatile LocalizationSnapshot _snapshot = LocalizationSnapshot.Empty;
    private string _currentLocale = DefaultLocaleCode;
    private string? _externalDir;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public static HyperaLocalizer Default => DefaultInstance.Value;

    public HyperaLocalizer(bool loadBuiltIn = true)
    {
        if (loadBuiltIn)
        {
            LoadEmbeddedResources();
            RebuildSnapshotLocked();
        }
    }

    public event Action? LocalesChanged;

    public string CurrentLocale
    {
        get => Volatile.Read(ref _currentLocale);
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? DefaultLocaleCode : value.Trim();
            Volatile.Write(ref _currentLocale, normalized);
        }
    }

    public IReadOnlyList<LocaleInfo> AvailableLocales => _snapshot.AvailableLocales;

    public bool HasLocale(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        return _snapshot.Packs.ContainsKey(code.Trim());
    }

    public LocaleInfo? GetLocaleInfo(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return _snapshot.Packs.TryGetValue(code.Trim(), out LocalePack? pack) ? pack.Info : null;
    }

    /// <summary>
    /// Mounts an external locales directory containing *.json packs.
    /// Community packs (e.g. ko.json) register new locales; packs matching built-in codes override keys.
    /// </summary>
    public void LoadExternalDirectory(string localesDir, bool enableWatcher = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(localesDir);

        lock (_sync)
        {
            DisposeWatcherLocked();
            _externalDir = localesDir;
            ReloadExternalLocked();

            if (enableWatcher && Directory.Exists(localesDir))
            {
                var watcher = new FileSystemWatcher(localesDir, "*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Created += OnExternalFileChanged;
                watcher.Changed += OnExternalFileChanged;
                watcher.Deleted += OnExternalFileChanged;
                watcher.Renamed += OnExternalFileRenamed;
                _watcher = watcher;
            }
        }

        LocalesChanged?.Invoke();
    }

    /// <summary>
    /// Reloads all *.json files from the currently mounted external locales directory.
    /// </summary>
    public void ReloadExternalDirectory()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_sync)
        {
            ReloadExternalLocked();
        }

        LocalesChanged?.Invoke();
    }

    /// <summary>
    /// Loads a JSON locale pack directly from a string into the external overlay layer (or built-in layer).
    /// </summary>
    public void LoadJsonOverlay(string json, string? fallbackCode = null, bool isBuiltIn = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(json);

        LocalePack? parsed = ParseLocaleJson(json, fallbackCode, isBuiltIn);
        if (parsed is null)
        {
            return;
        }

        lock (_sync)
        {
            Dictionary<string, LocalePack> target = isBuiltIn ? _builtIn : _external;
            target[parsed.Info.Code] = parsed;
            RebuildSnapshotLocked();
        }

        LocalesChanged?.Invoke();
    }

    public bool TryGetTemplate(string key, string? cultureCode, out string template)
    {
        if (string.IsNullOrEmpty(key))
        {
            template = string.Empty;
            return false;
        }

        LocalizationSnapshot snap = _snapshot;
        string requested = string.IsNullOrWhiteSpace(cultureCode) ? CurrentLocale : cultureCode.Trim();

        if (TryResolvePack(snap, requested, out LocalePack? pack)
            && pack is not null
            && pack.Entries.TryGetValue(key, out string? found))
        {
            template = found;
            return true;
        }

        if (snap.Packs.TryGetValue(DefaultLocaleCode, out LocalePack? defaultPack)
            && defaultPack.Entries.TryGetValue(key, out string? defaultFound))
        {
            template = defaultFound;
            return true;
        }

        template = string.Empty;
        return false;
    }

    public string Get(string key, string? cultureCode = null, string? fallback = null)
    {
        return TryGetTemplate(key, cultureCode, out string template)
            ? template
            : fallback ?? key;
    }

    public string Format(
        string key,
        string? cultureCode = null,
        string? fallbackTemplate = null,
        params ReadOnlySpan<string> args)
    {
        string template = TryGetTemplate(key, cultureCode, out string resolved)
            ? resolved
            : fallbackTemplate ?? key;

        return FormatTemplateSafe(template, args);
    }

    public string Format(
        string key,
        string? cultureCode,
        ImmutableArray<string> args,
        string? fallbackTemplate = null)
    {
        string template = TryGetTemplate(key, cultureCode, out string resolved)
            ? resolved
            : fallbackTemplate ?? key;

        if (args.IsDefaultOrEmpty)
        {
            return template;
        }

        return FormatTemplateSafe(template, args.AsSpan());
    }

    public static string FormatTemplateSafe(string template, ReadOnlySpan<string> args)
    {
        if (args.IsEmpty || string.IsNullOrEmpty(template))
        {
            return template;
        }

        object[] boxed = new object[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            boxed[i] = args[i] ?? string.Empty;
        }

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, boxed);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeWatcherLocked();
        }
    }

    private void OnExternalFileChanged(object sender, FileSystemEventArgs e)
    {
        TryReloadFromWatcher();
    }

    private void OnExternalFileRenamed(object sender, RenamedEventArgs e)
    {
        TryReloadFromWatcher();
    }

    private void TryReloadFromWatcher()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            ReloadExternalLocked();
        }

        LocalesChanged?.Invoke();
    }

    private void DisposeWatcherLocked()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnExternalFileChanged;
        _watcher.Changed -= OnExternalFileChanged;
        _watcher.Deleted -= OnExternalFileChanged;
        _watcher.Renamed -= OnExternalFileRenamed;
        _watcher.Dispose();
        _watcher = null;
    }

    private void LoadEmbeddedResources()
    {
        Assembly asm = typeof(HyperaLocalizer).Assembly;
        foreach (string resName in asm.GetManifestResourceNames())
        {
            if (!resName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || !resName.Contains(".Localization.Locales.", StringComparison.Ordinal))
            {
                continue;
            }

            using Stream? stream = asm.GetManifestResourceStream(resName);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            string fileCode = ExtractCodeFromResourceName(resName);
            LocalePack? pack = ParseLocaleJson(json, fileCode, isBuiltIn: true);
            if (pack is not null)
            {
                _builtIn[pack.Info.Code] = pack;
            }
        }
    }

    private void ReloadExternalLocked()
    {
        _external.Clear();

        if (!string.IsNullOrEmpty(_externalDir) && Directory.Exists(_externalDir))
        {
            foreach (string file in Directory.EnumerateFiles(_externalDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                string? json = TryReadFileWithRetry(file);
                if (json is null)
                {
                    continue;
                }

                string fileCode = Path.GetFileNameWithoutExtension(file);
                LocalePack? pack = ParseLocaleJson(json, fileCode, isBuiltIn: false);
                if (pack is not null)
                {
                    _external[pack.Info.Code] = pack;
                }
            }
        }

        RebuildSnapshotLocked();
    }

    private void RebuildSnapshotLocked()
    {
        var merged = new Dictionary<string, LocalePack>(StringComparer.OrdinalIgnoreCase);

        foreach ((string code, LocalePack builtInPack) in _builtIn)
        {
            merged[code] = builtInPack;
        }

        foreach ((string code, LocalePack extPack) in _external)
        {
            if (merged.TryGetValue(code, out LocalePack? basePack))
            {
                var combined = new Dictionary<string, string>(basePack.Entries, StringComparer.Ordinal);
                foreach ((string k, string v) in extPack.Entries)
                {
                    combined[k] = v;
                }

                LocaleInfo info = new(
                    Code: basePack.Info.Code,
                    NativeName: !string.IsNullOrWhiteSpace(extPack.Info.NativeName) && !string.Equals(extPack.Info.NativeName, extPack.Info.Code, StringComparison.OrdinalIgnoreCase)
                        ? extPack.Info.NativeName
                        : basePack.Info.NativeName,
                    EnglishName: !string.IsNullOrWhiteSpace(extPack.Info.EnglishName) && !string.Equals(extPack.Info.EnglishName, extPack.Info.Code, StringComparison.OrdinalIgnoreCase)
                        ? extPack.Info.EnglishName
                        : basePack.Info.EnglishName,
                    IsBuiltIn: true);

                merged[code] = new LocalePack(info, combined);
            }
            else
            {
                merged[code] = extPack;
            }
        }

        var orderedInfos = new List<LocaleInfo>(merged.Count);
        string[] preferredOrder = [DefaultLocaleCode, "zh-CN", "en"];
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pref in preferredOrder)
        {
            if (merged.TryGetValue(pref, out LocalePack? pack))
            {
                orderedInfos.Add(pack.Info);
                emitted.Add(pack.Info.Code);
            }
        }

        foreach (LocalePack pack in merged.Values
            .Where(p => !emitted.Contains(p.Info.Code))
            .OrderBy(static p => p.Info.Code, StringComparer.OrdinalIgnoreCase))
        {
            orderedInfos.Add(pack.Info);
        }

        _snapshot = new LocalizationSnapshot(merged, [.. orderedInfos]);
    }

    private static bool TryResolvePack(LocalizationSnapshot snap, string requested, out LocalePack? pack)
    {
        if (snap.Packs.TryGetValue(requested, out pack))
        {
            return true;
        }

        int dash = requested.IndexOf('-');
        if (dash > 0)
        {
            string prefix = requested[..dash];
            if (snap.Packs.TryGetValue(prefix, out pack))
            {
                return true;
            }

            foreach (LocalePack candidate in snap.Packs.Values)
            {
                if (candidate.Info.Code.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase))
                {
                    pack = candidate;
                    return true;
                }
            }
        }
        else
        {
            foreach (LocalePack candidate in snap.Packs.Values)
            {
                if (candidate.Info.Code.StartsWith(requested + "-", StringComparison.OrdinalIgnoreCase))
                {
                    pack = candidate;
                    return true;
                }
            }
        }

        pack = null;
        return false;
    }

    private static LocalePack? ParseLocaleJson(string json, string? fallbackCode, bool isBuiltIn)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? code = null;
            string? nativeName = null;
            string? englishName = null;
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "_meta", StringComparison.Ordinal)
                    && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (prop.Value.TryGetProperty("code", out JsonElement codeEl) && codeEl.ValueKind == JsonValueKind.String)
                    {
                        code = codeEl.GetString();
                    }

                    if (prop.Value.TryGetProperty("nativeName", out JsonElement nativeEl) && nativeEl.ValueKind == JsonValueKind.String)
                    {
                        nativeName = nativeEl.GetString();
                    }

                    if (prop.Value.TryGetProperty("englishName", out JsonElement enEl) && enEl.ValueKind == JsonValueKind.String)
                    {
                        englishName = enEl.GetString();
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    entries[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }

            string finalCode = !string.IsNullOrWhiteSpace(code) ? code.Trim() : (fallbackCode?.Trim() ?? string.Empty);
            if (string.IsNullOrEmpty(finalCode))
            {
                return null;
            }

            string finalNative = !string.IsNullOrWhiteSpace(nativeName) ? nativeName.Trim() : finalCode;
            string finalEnglish = !string.IsNullOrWhiteSpace(englishName) ? englishName.Trim() : finalNative;

            return new LocalePack(
                new LocaleInfo(finalCode, finalNative, finalEnglish, isBuiltIn),
                entries);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractCodeFromResourceName(string resourceName)
    {
        const string marker = ".Localization.Locales.";
        int idx = resourceName.IndexOf(marker, StringComparison.Ordinal);
        string tail = idx >= 0 ? resourceName[(idx + marker.Length)..] : resourceName;
        return tail.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? tail[..^5]
            : tail;
    }

    private static string? TryReadFileWithRetry(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    private sealed record LocalePack(LocaleInfo Info, Dictionary<string, string> Entries);

    private sealed record LocalizationSnapshot(
        Dictionary<string, LocalePack> Packs,
        ImmutableArray<LocaleInfo> AvailableLocales)
    {
        public static readonly LocalizationSnapshot Empty =
            new(new Dictionary<string, LocalePack>(StringComparer.OrdinalIgnoreCase), ImmutableArray<LocaleInfo>.Empty);
    }
}
