using System.Globalization;
using System.Xml;

namespace Hypera.Runtime;

/// <summary>
/// Instance-isolated store for EM+EE XML DOM documents (<c>XML_*</c>, all 18 XPath operations).
/// Supports integer IDs, named string keys (<c>_BYNAME</c>), and inline XML string mutation.
/// </summary>
public sealed class XmlStore
{
    private readonly Dictionary<string, XmlDocument> _documents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _savedataKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalKeys = new(StringComparer.Ordinal);

    public int Count => _documents.Count;

    public IEnumerable<string> DocumentKeys => _documents.Keys;

    public bool IsSavedata(string name) => _savedataKeys.Contains(name);

    public bool IsGlobal(string name) => _globalKeys.Contains(name);

    public void SetDocumentFromSnapshot(string key, string xml)
    {
        string docKey = key ?? string.Empty;
        _documents[docKey] = ParseXml(xml);
    }

    public static string KeyFromInt(long id) => id.ToString(CultureInfo.InvariantCulture);

    public void RegisterVarExt(string name, bool isSavedata, bool isGlobal, string? initialXml = null)
    {
        if (!string.IsNullOrEmpty(initialXml))
        {
            CreateDocument(name, initialXml);
        }

        if (isSavedata)
        {
            _savedataKeys.Add(name);
        }

        if (isGlobal)
        {
            _globalKeys.Add(name);
        }
    }

    public XmlDocument? TryGetDocument(string key) =>
        _documents.TryGetValue(key ?? string.Empty, out XmlDocument? doc) ? doc : null;

    /// <summary><c>XML_DOCUMENT</c>: parses XML and stores under <paramref name="key"/>. Returns 1 if created, 0 if key already exists.</summary>
    public long CreateDocument(string key, string xml)
    {
        string docKey = key ?? string.Empty;
        if (_documents.ContainsKey(docKey))
        {
            return 0L;
        }

        _documents[docKey] = ParseXml(xml);
        return 1L;
    }

    public long CreateDocument(long id, string xml) => CreateDocument(KeyFromInt(id), xml);

    /// <summary><c>XML_EXIST</c>: returns 1 if document exists, 0 otherwise.</summary>
    public long Exists(string key) => _documents.ContainsKey(key ?? string.Empty) ? 1L : 0L;

    public long Exists(long id) => Exists(KeyFromInt(id));

    /// <summary><c>XML_RELEASE</c>: removes document and returns 1 if existed, 0 otherwise.</summary>
    public long Release(string key) => _documents.Remove(key ?? string.Empty) ? 1L : 0L;

    public long Release(long id) => Release(KeyFromInt(id));

    /// <summary><c>XML_TOSTR</c>: returns <c>OuterXml</c> or empty string.</summary>
    public string ToStr(string key) =>
        _documents.TryGetValue(key ?? string.Empty, out XmlDocument? doc) ? doc.OuterXml : string.Empty;

    public string ToStr(long id) => ToStr(KeyFromInt(id));

    /// <summary>
    /// <c>XML_GET</c> / <c>XML_GET_BYNAME</c>: evaluates XPath and writes node strings using <paramref name="outputStyle"/>
    /// (<c>0=Value, 1=InnerText, 2=InnerXml, 3=OuterXml, 4=Name</c>) into <paramref name="destination"/>.
    /// Returns matched node count, or <c>-1</c> if registered document key is missing.
    /// </summary>
    public long Get(
        string keyOrInlineXml,
        bool isDocumentKey,
        string xpath,
        Span<string> destination = default,
        long outputStyle = 0)
    {
        XmlDocument? doc;
        if (isDocumentKey)
        {
            if (!_documents.TryGetValue(keyOrInlineXml ?? string.Empty, out doc))
            {
                return -1L;
            }
        }
        else
        {
            doc = ParseXml(keyOrInlineXml);
        }

        XmlNodeList? nodes = doc.SelectNodes(xpath ?? string.Empty);
        if (nodes is null)
        {
            return 0L;
        }

        if (!destination.IsEmpty)
        {
            int limit = Math.Min(nodes.Count, destination.Length);
            for (int i = 0; i < limit; i++)
            {
                XmlNode? n = nodes[i];
                destination[i] = n is null ? string.Empty : FormatNodeOutput(n, outputStyle);
            }
        }

        return nodes.Count;
    }

    /// <summary>
    /// <c>XML_SET</c> / <c>XML_SET_BYNAME</c>: updates matched XPath nodes (<c>0=Value, 1=InnerText, 2=InnerXml</c>).
    /// If <paramref name="isDocumentKey"/> is false, outputs mutated <c>OuterXml</c> via <paramref name="mutatedInlineXml"/>.
    /// </summary>
    public long Set(
        string keyOrInlineXml,
        bool isDocumentKey,
        string xpath,
        string value,
        out string? mutatedInlineXml,
        bool setAllNodes = false,
        long style = 0)
    {
        mutatedInlineXml = null;
        if (!TryResolveTargetDoc(keyOrInlineXml, isDocumentKey, out XmlDocument? doc) || doc is null)
        {
            return -1L;
        }

        XmlNodeList? nodes = doc.SelectNodes(xpath ?? string.Empty);
        if (nodes is null || nodes.Count == 0)
        {
            return 0L;
        }

        long normalizedStyle = style is >= 0 and <= 2 ? style : 0;
        string val = value ?? string.Empty;

        if (nodes.Count == 1)
        {
            ApplyNodeSet(nodes[0]!, val, normalizedStyle);
        }
        else if (setAllNodes)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is XmlNode n)
                {
                    ApplyNodeSet(n, val, normalizedStyle);
                }
            }
        }

        if (!isDocumentKey)
        {
            mutatedInlineXml = doc.OuterXml;
        }

        return nodes.Count;
    }

    /// <summary>
    /// <c>XML_ADDNODE</c> / <c>XML_ADDNODE_BYNAME</c>: inserts parsed XML node (<c>method: 0=AppendChild, 1=InsertBefore, 2=InsertAfter</c>).
    /// </summary>
    public long AddNode(
        string keyOrInlineXml,
        bool isDocumentKey,
        string xpath,
        string childXml,
        out string? mutatedInlineXml,
        int method = 0,
        bool setAllNodes = false)
    {
        mutatedInlineXml = null;
        XmlDocument parsedChild = ParseXml(childXml);
        if (parsedChild.DocumentElement is null)
        {
            return 0L;
        }

        if (!TryResolveTargetDoc(keyOrInlineXml, isDocumentKey, out XmlDocument? doc) || doc is null)
        {
            return -1L;
        }

        XmlNodeList? nodes = doc.SelectNodes(xpath ?? string.Empty);
        if (nodes is null || nodes.Count == 0)
        {
            return 0L;
        }

        int normMethod = method is >= 0 and <= 2 ? method : 0;
        if (nodes.Count == 1)
        {
            XmlNode imported = ImportElement(doc, parsedChild.DocumentElement);
            if (!InsertNode(nodes[0]!, imported, normMethod) && normMethod > 0)
            {
                return 0L;
            }
        }
        else if (setAllNodes)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is XmlNode target)
                {
                    XmlNode imported = ImportElement(doc, parsedChild.DocumentElement);
                    InsertNode(target, imported, normMethod);
                }
            }
        }

        if (!isDocumentKey)
        {
            mutatedInlineXml = doc.OuterXml;
        }

        return nodes.Count;
    }

    /// <summary>
    /// <c>XML_ADDATTRIBUTE</c> / <c>XML_ADDATTRIBUTE_BYNAME</c>: adds attribute to matched XPath nodes.
    /// </summary>
    public long AddAttribute(
        string keyOrInlineXml,
        bool isDocumentKey,
        string xpath,
        string attributeName,
        string attributeValue,
        out string? mutatedInlineXml,
        int method = 0,
        bool setAllNodes = false)
    {
        mutatedInlineXml = null;
        if (!TryResolveTargetDoc(keyOrInlineXml, isDocumentKey, out XmlDocument? doc) || doc is null)
        {
            return -1L;
        }

        XmlNodeList? nodes = doc.SelectNodes(xpath ?? string.Empty);
        if (nodes is null || nodes.Count == 0)
        {
            return 0L;
        }

        int normMethod = method is >= 0 and <= 2 ? method : 0;
        if (nodes.Count == 1)
        {
            XmlAttribute attr = doc.CreateAttribute(attributeName);
            attr.Value = attributeValue ?? string.Empty;
            if (!InsertAttribute(nodes[0]!, attr, normMethod) && normMethod > 0)
            {
                return 0L;
            }
        }
        else if (setAllNodes)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is XmlNode target)
                {
                    XmlAttribute attr = doc.CreateAttribute(attributeName);
                    attr.Value = attributeValue ?? string.Empty;
                    InsertAttribute(target, attr, normMethod);
                }
            }
        }

        if (!isDocumentKey)
        {
            mutatedInlineXml = doc.OuterXml;
        }

        return nodes.Count;
    }

    /// <summary>
    /// <c>XML_REMOVENODE</c> / <c>XML_REMOVENODE_BYNAME</c> and <c>XML_REMOVEATTRIBUTE</c> / <c>XML_REMOVEATTRIBUTE_BYNAME</c>.
    /// </summary>
    public long Remove(
        string keyOrInlineXml,
        bool isDocumentKey,
        string xpath,
        bool isAttribute,
        out string? mutatedInlineXml,
        bool setAllNodes = false)
    {
        mutatedInlineXml = null;
        if (!TryResolveTargetDoc(keyOrInlineXml, isDocumentKey, out XmlDocument? doc) || doc is null)
        {
            return -1L;
        }

        XmlNodeList? nodes = doc.SelectNodes(xpath ?? string.Empty);
        if (nodes is null || nodes.Count == 0)
        {
            return 0L;
        }

        if (nodes.Count == 1)
        {
            if (!RemoveSingle(nodes[0]!, isAttribute))
            {
                return 0L;
            }
        }
        else if (setAllNodes)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is XmlNode n)
                {
                    RemoveSingle(n, isAttribute);
                }
            }
        }

        if (!isDocumentKey)
        {
            mutatedInlineXml = doc.OuterXml;
        }

        return nodes.Count;
    }

    /// <summary>
    /// <c>XML_REPLACE</c> / <c>XML_REPLACE_BYNAME</c>: replaces whole registered document (when <paramref name="xpath"/> is null)
    /// or replaces XPath-matched nodes with <paramref name="replacementXml"/>.
    /// </summary>
    public long Replace(
        string keyOrInlineXml,
        bool isDocumentKey,
        string? xpath,
        string replacementXml,
        out string? mutatedInlineXml,
        bool setAllNodes = false)
    {
        mutatedInlineXml = null;
        XmlDocument parsedReplacement = ParseXml(replacementXml);

        if (xpath is null)
        {
            string docKey = keyOrInlineXml ?? string.Empty;
            if (!_documents.ContainsKey(docKey))
            {
                return -1L;
            }

            _documents[docKey] = parsedReplacement;
            return 1L;
        }

        if (parsedReplacement.DocumentElement is null)
        {
            return 0L;
        }

        if (!TryResolveTargetDoc(keyOrInlineXml, isDocumentKey, out XmlDocument? doc) || doc is null)
        {
            return -1L;
        }

        XmlNodeList? nodes = doc.SelectNodes(xpath);
        if (nodes is null || nodes.Count == 0)
        {
            return 0L;
        }

        if (nodes.Count == 1)
        {
            XmlNode target = nodes[0]!;
            if (target.ParentNode is null)
            {
                return 0L;
            }

            XmlNode imported = ImportElement(doc, parsedReplacement.DocumentElement);
            target.ParentNode.ReplaceChild(imported, target);
        }
        else if (setAllNodes)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is XmlNode target && target.ParentNode is not null)
                {
                    XmlNode imported = ImportElement(doc, parsedReplacement.DocumentElement);
                    target.ParentNode.ReplaceChild(imported, target);
                }
            }
        }

        if (!isDocumentKey)
        {
            mutatedInlineXml = doc.OuterXml;
        }

        return nodes.Count;
    }

    public void ResetNonGlobal()
    {
        var toRemove = new List<string>();
        foreach (string key in _documents.Keys)
        {
            if (!_globalKeys.Contains(key))
            {
                toRemove.Add(key);
            }
        }

        foreach (string key in toRemove)
        {
            _documents.Remove(key);
        }
    }

    public XmlStore CloneSnapshot()
    {
        var copy = new XmlStore();
        foreach (string s in _savedataKeys)
        {
            copy._savedataKeys.Add(s);
        }

        foreach (string g in _globalKeys)
        {
            copy._globalKeys.Add(g);
        }

        foreach ((string k, XmlDocument doc) in _documents)
        {
            copy._documents[k] = ParseXml(doc.OuterXml);
        }

        return copy;
    }

    public void RestoreFrom(XmlStore snapshot)
    {
        _documents.Clear();
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

        foreach ((string k, XmlDocument doc) in snapshot._documents)
        {
            _documents[k] = ParseXml(doc.OuterXml);
        }
    }

    private bool TryResolveTargetDoc(string keyOrInlineXml, bool isDocumentKey, out XmlDocument? doc)
    {
        if (isDocumentKey)
        {
            return _documents.TryGetValue(keyOrInlineXml ?? string.Empty, out doc);
        }

        doc = ParseXml(keyOrInlineXml);
        return true;
    }

    private static XmlDocument ParseXml(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(xml ?? string.Empty);
        return doc;
    }

    private static XmlNode ImportElement(XmlDocument targetDoc, XmlElement sourceElement)
    {
        XmlNode created = targetDoc.CreateNode(sourceElement.NodeType, sourceElement.Name, sourceElement.NamespaceURI);
        if (created.Attributes is not null)
        {
            for (int i = 0; i < sourceElement.Attributes.Count; i++)
            {
                XmlAttribute srcAttr = sourceElement.Attributes[i];
                XmlAttribute dstAttr = targetDoc.CreateAttribute(srcAttr.Name);
                dstAttr.Value = srcAttr.Value;
                created.Attributes.Append(dstAttr);
            }
        }

        created.InnerXml = sourceElement.InnerXml;
        return created;
    }

    private static string FormatNodeOutput(XmlNode node, long style) =>
        style switch
        {
            1 => node.InnerText,
            2 => node.InnerXml,
            3 => node.OuterXml,
            4 => node.Name,
            _ => node.NodeType == XmlNodeType.Element ? node.InnerText : (node.Value ?? string.Empty),
        };

    private static void ApplyNodeSet(XmlNode node, string val, long style)
    {
        switch (style)
        {
            case 1:
                node.InnerText = val;
                break;
            case 2:
                node.InnerXml = val;
                break;
            default:
                if (node.NodeType == XmlNodeType.Element)
                {
                    node.InnerText = val;
                }
                else
                {
                    node.Value = val;
                }

                break;
        }
    }

    private static bool InsertNode(XmlNode target, XmlNode child, int method)
    {
        switch (method)
        {
            case 0:
                target.AppendChild(child);
                return true;
            case 1:
                if (target.ParentNode is null)
                {
                    return false;
                }

                target.ParentNode.InsertBefore(child, target);
                return true;
            case 2:
                if (target.ParentNode is null)
                {
                    return false;
                }

                target.ParentNode.InsertAfter(child, target);
                return true;
            default:
                return false;
        }
    }

    private static bool InsertAttribute(XmlNode target, XmlAttribute newAttr, int method)
    {
        if (method == 0)
        {
            if (target.Attributes is null)
            {
                return false;
            }

            target.Attributes.Append(newAttr);
            return true;
        }

        if (target is not XmlAttribute existingAttr || existingAttr.OwnerElement?.Attributes is null)
        {
            return false;
        }

        if (method == 1)
        {
            existingAttr.OwnerElement.Attributes.InsertBefore(newAttr, existingAttr);
            return true;
        }

        if (method == 2)
        {
            existingAttr.OwnerElement.Attributes.InsertAfter(newAttr, existingAttr);
            return true;
        }

        return false;
    }

    private static bool RemoveSingle(XmlNode node, bool isAttribute)
    {
        if (isAttribute)
        {
            if (node is XmlAttribute attr && attr.OwnerElement?.Attributes is not null)
            {
                attr.OwnerElement.Attributes.Remove(attr);
                return true;
            }

            return false;
        }

        if (node.ParentNode is not null)
        {
            node.ParentNode.RemoveChild(node);
            return true;
        }

        return false;
    }
}
