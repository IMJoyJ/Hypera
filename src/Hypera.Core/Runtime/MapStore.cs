using System.Security;
using System.Text;
using System.Xml;

namespace Hypera.Runtime;

/// <summary>
/// Instance-isolated store for EM+EE string dictionary maps (<c>MAP_*</c>, 12 operations + persistence support).
/// </summary>
public sealed class MapStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _maps = new(StringComparer.Ordinal);
    private readonly HashSet<string> _savedataKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalKeys = new(StringComparer.Ordinal);

    public int Count => _maps.Count;

    public IEnumerable<string> MapNames => _maps.Keys;

    public void RegisterVarExt(string name, bool isSavedata, bool isGlobal)
    {
        Create(name);
        if (isSavedata)
        {
            _savedataKeys.Add(name);
        }

        if (isGlobal)
        {
            _globalKeys.Add(name);
        }
    }

    public bool IsSavedata(string name) => _savedataKeys.Contains(name);

    public bool IsGlobal(string name) => _globalKeys.Contains(name);

    public void SetMapFromSnapshot(string name, Dictionary<string, string> entries)
    {
        string key = name ?? string.Empty;
        _maps[key] = new Dictionary<string, string>(entries, StringComparer.Ordinal);
    }

    public Dictionary<string, string>? TryGetMap(string name) =>
        _maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map) ? map : null;

    /// <summary><c>MAP_CREATE</c>: returns 1 if created, 0 if already exists.</summary>
    public long Create(string name)
    {
        string key = name ?? string.Empty;
        if (_maps.ContainsKey(key))
        {
            return 0L;
        }

        _maps[key] = new Dictionary<string, string>(StringComparer.Ordinal);
        return 1L;
    }

    /// <summary><c>MAP_EXIST</c>: returns 1 if map exists, 0 otherwise.</summary>
    public long Exists(string name) => _maps.ContainsKey(name ?? string.Empty) ? 1L : 0L;

    /// <summary><c>MAP_RELEASE</c>: removes map if present and returns 1.</summary>
    public long Release(string name)
    {
        _maps.Remove(name ?? string.Empty);
        return 1L;
    }

    /// <summary><c>MAP_CLEAR</c>: clears entries and returns 1, or -1 if map does not exist.</summary>
    public long Clear(string name)
    {
        if (!_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map))
        {
            return -1L;
        }

        map.Clear();
        return 1L;
    }

    /// <summary><c>MAP_SIZE</c>: returns entry count, or -1 if map does not exist.</summary>
    public long Size(string name) =>
        _maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map) ? map.Count : -1L;

    /// <summary><c>MAP_HAS</c>: returns 1 if key exists, 0 if key missing, or -1 if map missing.</summary>
    public long Has(string name, string entryKey)
    {
        if (!_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map))
        {
            return -1L;
        }

        return map.ContainsKey(entryKey ?? string.Empty) ? 1L : 0L;
    }

    /// <summary><c>MAP_GET</c>: returns mapped value or empty string.</summary>
    public string Get(string name, string entryKey)
    {
        if (_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map) &&
            map.TryGetValue(entryKey ?? string.Empty, out string? val))
        {
            return val;
        }

        return string.Empty;
    }

    /// <summary><c>MAP_SET</c>: sets key-value pair and returns 1, or -1 if map missing.</summary>
    public long Set(string name, string entryKey, string value)
    {
        if (!_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map))
        {
            return -1L;
        }

        map[entryKey ?? string.Empty] = value ?? string.Empty;
        return 1L;
    }

    /// <summary><c>MAP_REMOVE</c>: removes key and returns 1, or -1 if map missing.</summary>
    public long Remove(string name, string entryKey)
    {
        if (!_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map))
        {
            return -1L;
        }

        map.Remove(entryKey ?? string.Empty);
        return 1L;
    }

    /// <summary>
    /// <c>MAP_GETKEYS</c> (1-arg string form): returns comma-separated keys.
    /// </summary>
    public string GetKeysCsv(string name)
    {
        if (!_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map) || map.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(',', map.Keys);
    }

    /// <summary>
    /// <c>MAP_GETKEYS</c> (array output form): copies keys into <paramref name="destination"/> and returns total key count.
    /// </summary>
    public int GetKeysToSpan(string name, Span<string> destination)
    {
        if (!_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map))
        {
            return 0;
        }

        int idx = 0;
        foreach (string k in map.Keys)
        {
            if (idx >= destination.Length)
            {
                break;
            }

            destination[idx++] = k;
        }

        return map.Count;
    }

    /// <summary><c>MAP_TOXML</c>: serializes map to <c>&lt;map&gt;&lt;p&gt;&lt;k&gt;...&lt;/k&gt;&lt;v&gt;...&lt;/v&gt;&lt;/p&gt;&lt;/map&gt;</c>.</summary>
    public string ToXml(string name)
    {
        if (!_maps.TryGetValue(name ?? string.Empty, out Dictionary<string, string>? map))
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<map>");
        foreach ((string k, string v) in map)
        {
            sb.Append("<p><k>")
              .Append(SecurityElement.Escape(k))
              .Append("</k><v>")
              .Append(v)
              .Append("</v></p>");
        }

        sb.Append("</map>");
        return sb.ToString();
    }

    /// <summary><c>MAP_FROMXML</c>: imports <c>/map/p</c> entries into an existing or newly created map.</summary>
    public long FromXml(string name, string xml)
    {
        string key = name ?? string.Empty;
        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            doc.LoadXml(xml ?? string.Empty);
        }
        catch
        {
            return 0L;
        }

        if (!_maps.TryGetValue(key, out Dictionary<string, string>? map))
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            _maps[key] = map;
        }
        else
        {
            map.Clear();
        }

        XmlNodeList? pairs = doc.SelectNodes("/map/p");
        if (pairs is not null)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                XmlNode? pair = pairs[i];
                if (pair is null)
                {
                    continue;
                }

                XmlNodeList? kNodes = pair.SelectNodes("./k");
                XmlNodeList? vNodes = pair.SelectNodes("./v");
                if (kNodes is { Count: 1 } && vNodes is { Count: 1 } && kNodes[0] is not null && vNodes[0] is not null)
                {
                    map[kNodes[0]!.InnerText] = vNodes[0]!.InnerXml;
                }
            }
        }

        return 1L;
    }

    public void ResetNonGlobal()
    {
        var toRemove = new List<string>();
        foreach (string key in _maps.Keys)
        {
            if (!_globalKeys.Contains(key))
            {
                toRemove.Add(key);
            }
        }

        foreach (string key in toRemove)
        {
            if (_savedataKeys.Contains(key))
            {
                _maps[key].Clear();
            }
            else
            {
                _maps.Remove(key);
            }
        }
    }

    public MapStore CloneSnapshot()
    {
        var copy = new MapStore();
        foreach (string s in _savedataKeys)
        {
            copy._savedataKeys.Add(s);
        }

        foreach (string g in _globalKeys)
        {
            copy._globalKeys.Add(g);
        }

        foreach ((string mapName, Dictionary<string, string> entries) in _maps)
        {
            copy._maps[mapName] = new Dictionary<string, string>(entries, StringComparer.Ordinal);
        }

        return copy;
    }

    public void RestoreFrom(MapStore snapshot)
    {
        _maps.Clear();
        _savedataKeys.Clear();
        _globalKeys.Clear();
        foreach (string s in snapshot._savedataKeys)
        {
            _savedataKeys.Add(s);
        }

        foreach (string g in snapshot._globalKeys)
        {
            _globalKeys.Add(g);
        }

        foreach ((string mapName, Dictionary<string, string> entries) in snapshot._maps)
        {
            _maps[mapName] = new Dictionary<string, string>(entries, StringComparer.Ordinal);
        }
    }
}
