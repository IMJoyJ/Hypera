using System.Data;
using System.Globalization;
using System.Text;
using Hypera.Persistence;

namespace Hypera.Runtime;

/// <summary>
/// Instance-isolated store for EM+EE relational <see cref="DataTable"/> containers (<c>DT_*</c>, all 22 operations).
/// </summary>
public sealed class DataTableStore
{
    private readonly Dictionary<string, DataTable> _tables = new(StringComparer.Ordinal);
    private readonly HashSet<string> _savedataKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globalKeys = new(StringComparer.Ordinal);
    private long _lastRowId = DateTime.UtcNow.Ticks;

    public int Count => _tables.Count;

    public IEnumerable<string> TableNames => _tables.Keys;

    public bool IsSavedata(string name) => _savedataKeys.Contains(name);

    public bool IsGlobal(string name) => _globalKeys.Contains(name);

    public DataTable? TryGetTable(string name) =>
        _tables.TryGetValue(name ?? string.Empty, out DataTable? dt) ? dt : null;

    public EraDataTableSnapshot? ExportSnapshot(string tableName)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return null;
        }

        var columns = new List<EraDataColumnDefinition>(dt.Columns.Count);
        for (int c = 0; c < dt.Columns.Count; c++)
        {
            DataColumn col = dt.Columns[c];
            columns.Add(new EraDataColumnDefinition(col.ColumnName, TypeToCanonicalName(col.DataType)));
        }

        var rows = new List<object?[]>(dt.Rows.Count);
        for (int r = 0; r < dt.Rows.Count; r++)
        {
            DataRow dr = dt.Rows[r];
            object?[] cells = new object?[dt.Columns.Count];
            for (int c = 0; c < dt.Columns.Count; c++)
            {
                object val = dr[c];
                if (val == DBNull.Value || val is null)
                {
                    cells[c] = null;
                }
                else if (val is sbyte or short or int or long)
                {
                    cells[c] = Convert.ToInt64(val, CultureInfo.InvariantCulture);
                }
                else
                {
                    cells[c] = val.ToString() ?? string.Empty;
                }
            }

            rows.Add(cells);
        }

        return new EraDataTableSnapshot { Columns = columns, Rows = rows };
    }

    public void ImportSnapshot(string tableName, EraDataTableSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string key = tableName ?? string.Empty;
        if (_tables.Remove(key, out DataTable? old))
        {
            old.Dispose();
        }

        var dt = new DataTable(key)
        {
            CaseSensitive = true,
            Locale = CultureInfo.InvariantCulture,
        };

        bool hasIdCol = false;
        foreach (EraDataColumnDefinition colDef in snapshot.Columns)
        {
            Type colType = NameToType(colDef.TypeName) ?? typeof(string);
            DataColumn dc = dt.Columns.Add(colDef.Name, colType);
            if (string.Equals(colDef.Name, "id", StringComparison.OrdinalIgnoreCase))
            {
                dc.AllowDBNull = false;
                dc.Unique = true;
                dt.PrimaryKey = [dc];
                hasIdCol = true;
            }
            else
            {
                dc.AllowDBNull = true;
            }
        }

        if (!hasIdCol)
        {
            DataColumn idCol = dt.Columns.Add("id", typeof(long));
            idCol.SetOrdinal(0);
            idCol.AllowDBNull = false;
            idCol.Unique = true;
            dt.PrimaryKey = [idCol];
        }

        foreach (object?[] rowCells in snapshot.Rows)
        {
            DataRow dr = dt.NewRow();
            for (int c = 0; c < snapshot.Columns.Count; c++)
            {
                string colName = snapshot.Columns[c].Name;
                DataColumn? dc = dt.Columns[colName];
                if (dc is null)
                {
                    continue;
                }

                object? cell = c < rowCells.Length ? rowCells[c] : null;
                if (cell is null)
                {
                    dr[dc] = dc.AllowDBNull ? DBNull.Value : (dc.DataType == typeof(string) ? string.Empty : ConvertIntForColumn(0L, dc.DataType));
                }
                else if (dc.DataType == typeof(string))
                {
                    dr[dc] = cell.ToString() ?? string.Empty;
                }
                else
                {
                    long intVal = Convert.ToInt64(cell, CultureInfo.InvariantCulture);
                    dr[dc] = ConvertIntForColumn(intVal, dc.DataType);
                    if (string.Equals(colName, "id", StringComparison.OrdinalIgnoreCase) && intVal > _lastRowId)
                    {
                        _lastRowId = intVal;
                    }
                }
            }

            if (!hasIdCol)
            {
                dr["id"] = NextUniqueRowId();
            }

            dt.Rows.Add(dr);
        }

        _tables[key] = dt;
    }

    private static string TypeToCanonicalName(Type type)
    {
        if (type == typeof(sbyte)) return "int8";
        if (type == typeof(short)) return "int16";
        if (type == typeof(int)) return "int32";
        if (type == typeof(long)) return "int64";
        return "string";
    }

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

    public static long TypeToCode(Type type)
    {
        if (type == typeof(sbyte)) return 1L;
        if (type == typeof(short)) return 2L;
        if (type == typeof(int)) return 3L;
        if (type == typeof(long)) return 4L;
        if (type == typeof(string)) return 5L;
        return long.MaxValue;
    }

    public static Type? CodeToType(long code) =>
        code switch
        {
            1 => typeof(sbyte),
            2 => typeof(short),
            3 => typeof(int),
            4 => typeof(long),
            5 => typeof(string),
            _ => null,
        };

    public static Type? NameToType(string? typeName) =>
        (typeName ?? string.Empty).ToLowerInvariant() switch
        {
            "int8" or "sbyte" => typeof(sbyte),
            "int16" or "short" => typeof(short),
            "int32" or "int" => typeof(int),
            "int64" or "long" => typeof(long),
            "string" or "str" => typeof(string),
            _ => null,
        };

    public static object ConvertIntForColumn(long value, Type targetType)
    {
        if (targetType == typeof(sbyte))
        {
            return (sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);
        }

        if (targetType == typeof(short))
        {
            return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        if (targetType == typeof(int))
        {
            return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        }

        return value;
    }

    /// <summary>1. <c>DT_CREATE</c>: creates table with unique <c>id</c> primary key column.</summary>
    public long Create(string tableName)
    {
        string key = tableName ?? string.Empty;
        if (_tables.ContainsKey(key))
        {
            return 0L;
        }

        var dt = new DataTable(key)
        {
            CaseSensitive = true,
            Locale = CultureInfo.InvariantCulture,
        };
        DataColumn idCol = dt.Columns.Add("id", typeof(long));
        idCol.AllowDBNull = false;
        idCol.Unique = true;
        dt.PrimaryKey = [idCol];
        _tables[key] = dt;
        return 1L;
    }

    /// <summary>2. <c>DT_EXIST</c>: returns 1 if table exists, 0 otherwise.</summary>
    public long Exists(string tableName) => _tables.ContainsKey(tableName ?? string.Empty) ? 1L : 0L;

    /// <summary>3. <c>DT_RELEASE</c>: removes table if present and returns 1.</summary>
    public long Release(string tableName)
    {
        if (_tables.Remove(tableName ?? string.Empty, out DataTable? removed))
        {
            removed.Dispose();
        }

        return 1L;
    }

    /// <summary>4. <c>DT_CLEAR</c>: clears rows and returns 1, or -1 if missing.</summary>
    public long Clear(string tableName)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        dt.Clear();
        return 1L;
    }

    /// <summary>5. <c>DT_NOCASE</c>: sets case-insensitivity (<c>ignoreCase != 0</c> -> <c>CaseSensitive = false</c>).</summary>
    public long SetNoCase(string tableName, long ignoreCase)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        dt.CaseSensitive = ignoreCase == 0;
        return 1L;
    }

    /// <summary>6. <c>DT_COLUMN_ADD</c> (by numeric type code 1..5 or null -> string).</summary>
    public long AddColumn(string tableName, string columnName, Type? columnType = null, bool allowNull = true)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        string colName = columnName ?? string.Empty;
        if (dt.Columns.Contains(colName))
        {
            return 0L;
        }

        DataColumn dc = columnType is not null ? dt.Columns.Add(colName, columnType) : dt.Columns.Add(colName, typeof(string));
        dc.AllowDBNull = allowNull;
        return 1L;
    }

    public long AddColumnByCode(string tableName, string columnName, long typeCode, bool allowNull = true)
    {
        Type t = CodeToType(typeCode) ?? throw new ArgumentException($"Unsupported DT column type code: {typeCode}", nameof(typeCode));
        return AddColumn(tableName, columnName, t, allowNull);
    }

    public long AddColumnByTypeName(string tableName, string columnName, string typeName, bool allowNull = true)
    {
        Type t = NameToType(typeName) ?? throw new ArgumentException($"Unsupported DT column type name: '{typeName}'", nameof(typeName));
        return AddColumn(tableName, columnName, t, allowNull);
    }

    /// <summary>7. <c>DT_COLUMN_EXIST</c>: returns column type code (1..5), 0 if missing, -1 if table missing.</summary>
    public long ColumnExists(string tableName, string columnName)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        string colName = columnName ?? string.Empty;
        DataColumn? col = dt.Columns[colName];
        return col is not null ? TypeToCode(col.DataType) : 0L;
    }

    /// <summary>8. <c>DT_COLUMN_REMOVE</c>: removes column (except <c>id</c>).</summary>
    public long RemoveColumn(string tableName, string columnName)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        string colName = columnName ?? string.Empty;
        if (string.Equals(colName, "id", StringComparison.OrdinalIgnoreCase) || !dt.Columns.Contains(colName))
        {
            return 0L;
        }

        dt.Columns.Remove(colName);
        return 1L;
    }

    /// <summary>9. <c>DT_COLUMN_LENGTH</c>: returns column count or -1.</summary>
    public long ColumnLength(string tableName) =>
        _tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt) ? dt.Columns.Count : -1L;

    /// <summary>10. <c>DT_COLUMN_NAMES</c>: copies column names into <paramref name="destination"/> and returns column count or -1.</summary>
    public long ColumnNames(string tableName, Span<string> destination)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        int limit = Math.Min(dt.Columns.Count, destination.Length);
        for (int i = 0; i < limit; i++)
        {
            destination[i] = dt.Columns[i].ColumnName;
        }

        return dt.Columns.Count;
    }

    /// <summary>11. <c>DT_COLUMN_OPTIONS</c>: sets default value on a column.</summary>
    public long SetColumnDefault(string tableName, string columnName, object? defaultValue)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        DataColumn? col = dt.Columns[columnName ?? string.Empty];
        if (col is null)
        {
            return 0L;
        }

        if (defaultValue is null)
        {
            col.DefaultValue = DBNull.Value;
        }
        else if (col.DataType == typeof(string))
        {
            if (defaultValue is not string s)
            {
                throw new InvalidOperationException($"DT_COLUMN_OPTIONS expected string default for column '{columnName}'.");
            }

            col.DefaultValue = s;
        }
        else
        {
            long intVal = Convert.ToInt64(defaultValue, CultureInfo.InvariantCulture);
            col.DefaultValue = ConvertIntForColumn(intVal, col.DataType);
        }

        return 1L;
    }

    /// <summary>12. <c>DT_ROW_ADD</c>: adds a new row with unique monotonic <c>id</c> and optional column pairs.</summary>
    public long AddRow(string tableName, ReadOnlySpan<(string Column, object? Value)> fields = default)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        DataRow row = dt.NewRow();
        long rowId = NextUniqueRowId();
        row[0] = rowId;

        for (int i = 0; i < fields.Length; i++)
        {
            ApplyRowField(dt, row, fields[i].Column, fields[i].Value, tableName);
        }

        dt.Rows.Add(row);
        return rowId;
    }

    /// <summary>13. <c>DT_ROW_SET</c>: updates fields on an existing row by <paramref name="rowId"/>.</summary>
    public long SetRow(string tableName, long rowId, ReadOnlySpan<(string Column, object? Value)> fields)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        if (dt.Rows.Find(rowId) is not DataRow row)
        {
            return -2L;
        }

        for (int i = 0; i < fields.Length; i++)
        {
            ApplyRowField(dt, row, fields[i].Column, fields[i].Value, tableName);
        }

        return fields.Length;
    }

    /// <summary>14a. <c>DT_ROW_REMOVE</c> (single row ID): removes row by primary key <paramref name="rowId"/>.</summary>
    public long RemoveRow(string tableName, long rowId)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        if (dt.Rows.Find(rowId) is DataRow row)
        {
            dt.Rows.Remove(row);
            return 1L;
        }

        return 0L;
    }

    /// <summary>14b. <c>DT_ROW_REMOVE</c> (batch array of row IDs).</summary>
    public long RemoveRows(string tableName, ReadOnlySpan<long> rowIds, int count)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        int actual = Math.Min(count, rowIds.Length);
        if (actual <= 0)
        {
            return 0L;
        }

        long removed = 0;
        for (int i = 0; i < actual; i++)
        {
            if (dt.Rows.Find(rowIds[i]) is DataRow row)
            {
                dt.Rows.Remove(row);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>15. <c>DT_ROW_LENGTH</c>: returns row count or -1.</summary>
    public long RowLength(string tableName) =>
        _tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt) ? dt.Rows.Count : -1L;

    /// <summary>16. <c>DT_CELL_GET</c>: reads integer cell by row index or primary key <c>id</c>.</summary>
    public long GetCellInt(string tableName, long indexOrId, string columnName, bool asId = false)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return 0L;
        }

        DataRow? row = ResolveRow(dt, indexOrId, asId);
        if (row is null || !dt.Columns.Contains(columnName ?? string.Empty))
        {
            return 0L;
        }

        object val = row[columnName!];
        if (val == DBNull.Value || val is string)
        {
            return 0L;
        }

        return Convert.ToInt64(val, CultureInfo.InvariantCulture);
    }

    /// <summary>17. <c>DT_CELL_GETS</c>: reads string cell by row index or primary key <c>id</c>.</summary>
    public string GetCellStr(string tableName, long indexOrId, string columnName, bool asId = false)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return string.Empty;
        }

        DataRow? row = ResolveRow(dt, indexOrId, asId);
        if (row is null || !dt.Columns.Contains(columnName ?? string.Empty))
        {
            return string.Empty;
        }

        object val = row[columnName!];
        if (val == DBNull.Value)
        {
            return string.Empty;
        }

        if (asId && val is not string)
        {
            return Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return val.ToString() ?? string.Empty;
    }

    /// <summary>18. <c>DT_CELL_ISNULL</c>: returns 1 if DBNull, 0 if non-null, -1 if table missing, -2 if row/col missing.</summary>
    public long IsCellNull(string tableName, long indexOrId, string columnName, bool asId = false)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        DataRow? row = ResolveRow(dt, indexOrId, asId);
        if (row is null || !dt.Columns.Contains(columnName ?? string.Empty))
        {
            return -2L;
        }

        return row[columnName!] == DBNull.Value ? 1L : 0L;
    }

    /// <summary>19. <c>DT_CELL_SET</c>: sets cell value (returns 1 on success, 0 for id, -1 missing table, -2 type mismatch, -3 missing row/col).</summary>
    public long SetCell(string tableName, long indexOrId, string columnName, object? value, bool asId = false)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        string colName = columnName ?? string.Empty;
        if (string.Equals(colName, "id", StringComparison.OrdinalIgnoreCase))
        {
            return 0L;
        }

        DataRow? row = ResolveRow(dt, indexOrId, asId);
        DataColumn? col = dt.Columns[colName];
        if (row is null || col is null)
        {
            return -3L;
        }

        if (value is null)
        {
            row[col] = DBNull.Value;
            return 1L;
        }

        bool colIsString = col.DataType == typeof(string);
        if (colIsString)
        {
            if (value is not string strVal)
            {
                return -2L;
            }

            row[col] = strVal;
        }
        else
        {
            if (value is string)
            {
                return -2L;
            }

            long intVal = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            row[col] = ConvertIntForColumn(intVal, col.DataType);
        }

        return 1L;
    }

    /// <summary>
    /// 20. <c>DT_SELECT</c>: filters &amp; sorts rows, writing matched row IDs into <paramref name="destination"/>.
    /// When <paramref name="writeCountAtIndexZero"/> is true (default <c>RESULT</c> array mode), writes <c>count</c> to <c>destination[0]</c> and IDs starting at <c>destination[1]</c>.
    /// </summary>
    public long Select(
        string tableName,
        string? filter = null,
        string? sort = null,
        Span<long> destination = default,
        bool writeCountAtIndexZero = false)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            return -1L;
        }

        DataRow[] rows;
        if (!string.IsNullOrEmpty(sort))
        {
            rows = dt.Select(filter ?? string.Empty, sort);
        }
        else if (!string.IsNullOrEmpty(filter))
        {
            rows = dt.Select(filter);
        }
        else
        {
            rows = dt.Select();
        }

        if (!destination.IsEmpty)
        {
            if (writeCountAtIndexZero)
            {
                destination[0] = rows.Length;
                int limit = Math.Min(rows.Length, destination.Length - 1);
                for (int i = 0; i < limit; i++)
                {
                    destination[i + 1] = Convert.ToInt64(rows[i][0], CultureInfo.InvariantCulture);
                }
            }
            else
            {
                int limit = Math.Min(rows.Length, destination.Length);
                for (int i = 0; i < limit; i++)
                {
                    destination[i] = Convert.ToInt64(rows[i][0], CultureInfo.InvariantCulture);
                }
            }
        }

        return rows.Length;
    }

    /// <summary>21. <c>DT_TOXML</c>: exports schema XML into <paramref name="schemaXml"/> and returns data XML.</summary>
    public string ToXml(string tableName, out string schemaXml)
    {
        if (!_tables.TryGetValue(tableName ?? string.Empty, out DataTable? dt))
        {
            schemaXml = string.Empty;
            return string.Empty;
        }

        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
        {
            dt.WriteXmlSchema(sw);
            schemaXml = sb.ToString();
            sb.Clear();
            dt.WriteXml(sw);
            return sb.ToString();
        }
    }

    /// <summary>22. <c>DT_FROMXML</c>: imports schema + data XML into <paramref name="tableName"/>.</summary>
    public long FromXml(string tableName, string schemaXml, string dataXml)
    {
        string key = tableName ?? string.Empty;
        try
        {
            var dt = new DataTable
            {
                Locale = CultureInfo.InvariantCulture,
            };
            using (var srSchema = new StringReader(schemaXml ?? string.Empty))
            {
                dt.ReadXmlSchema(srSchema);
            }

            using (var srData = new StringReader(dataXml ?? string.Empty))
            {
                dt.ReadXml(srData);
            }

            dt.TableName = key;
            DataColumn? idCol = dt.Columns["id"];
            if (idCol is not null && dt.PrimaryKey.Length == 0)
            {
                idCol.AllowDBNull = false;
                idCol.Unique = true;
                dt.PrimaryKey = [idCol];
            }

            if (idCol is not null)
            {
                for (int i = 0; i < dt.Rows.Count; i++)
                {
                    long rowId = Convert.ToInt64(dt.Rows[i][idCol], CultureInfo.InvariantCulture);
                    if (rowId > _lastRowId)
                    {
                        _lastRowId = rowId;
                    }
                }
            }

            if (_tables.Remove(key, out DataTable? old))
            {
                old.Dispose();
            }

            _tables[key] = dt;
            return 1L;
        }
        catch
        {
            return 0L;
        }
    }

    public void ResetNonGlobal()
    {
        var toRemove = new List<string>();
        foreach (string key in _tables.Keys)
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
                _tables[key].Clear();
            }
            else if (_tables.Remove(key, out DataTable? dt))
            {
                dt.Dispose();
            }
        }
    }

    public DataTableStore CloneSnapshot()
    {
        var copy = new DataTableStore { _lastRowId = _lastRowId };
        foreach (string s in _savedataKeys)
        {
            copy._savedataKeys.Add(s);
        }

        foreach (string g in _globalKeys)
        {
            copy._globalKeys.Add(g);
        }

        foreach ((string key, _) in _tables)
        {
            string dataXml = ToXml(key, out string schemaXml);
            copy.FromXml(key, schemaXml, dataXml);
            copy._tables[key].CaseSensitive = _tables[key].CaseSensitive;
        }

        return copy;
    }

    public void RestoreFrom(DataTableStore snapshot)
    {
        foreach (DataTable dt in _tables.Values)
        {
            dt.Dispose();
        }

        _tables.Clear();
        _savedataKeys.Clear();
        _globalKeys.Clear();
        _lastRowId = snapshot._lastRowId;

        foreach (string s in snapshot._savedataKeys)
        {
            _savedataKeys.Add(s);
        }

        foreach (string g in snapshot._globalKeys)
        {
            _globalKeys.Add(g);
        }

        foreach ((string key, DataTable srcDt) in snapshot._tables)
        {
            string dataXml = snapshot.ToXml(key, out string schemaXml);
            FromXml(key, schemaXml, dataXml);
            _tables[key].CaseSensitive = srcDt.CaseSensitive;
        }
    }

    private long NextUniqueRowId()
    {
        long candidate = DateTime.UtcNow.Ticks;
        _lastRowId = candidate > _lastRowId ? candidate : _lastRowId + 1;
        return _lastRowId;
    }

    private static DataRow? ResolveRow(DataTable dt, long indexOrId, bool asId)
    {
        if (asId)
        {
            return dt.Rows.Find(indexOrId);
        }

        return (ulong)indexOrId < (ulong)dt.Rows.Count ? dt.Rows[(int)indexOrId] : null;
    }

    private static void ApplyRowField(DataTable dt, DataRow row, string columnName, object? value, string? tableName)
    {
        if (string.Equals(columnName, "id", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot directly assign primary key 'id' in DataTable '{tableName}'.");
        }

        DataColumn? col = dt.Columns[columnName];
        if (col is null)
        {
            throw new KeyNotFoundException($"Column '{columnName}' does not exist in DataTable '{tableName}'.");
        }

        if (value is null)
        {
            row[col] = DBNull.Value;
            return;
        }

        if (col.DataType == typeof(string))
        {
            if (value is not string strVal)
            {
                throw new InvalidOperationException($"Column '{columnName}' in DataTable '{tableName}' expects string.");
            }

            row[col] = strVal;
        }
        else
        {
            if (value is string)
            {
                throw new InvalidOperationException($"Column '{columnName}' in DataTable '{tableName}' expects integer.");
            }

            long intVal = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            row[col] = ConvertIntForColumn(intVal, col.DataType);
        }
    }
}
