using System.Collections;
using System.Collections.Generic;

namespace PicoJetson;

/// <summary>Kind of a JSON value.</summary>
public enum PicoValueKind : byte
{
    Undefined,
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

/// <summary>A lightweight read-only view into a JSON value within a <see cref="PicoDocument"/>.</summary>
public readonly struct PicoElement
{
    private readonly PicoDocument _doc;
    private readonly int _nodeIdx;

    internal PicoElement(PicoDocument doc, int nodeIdx)
    {
        _doc = doc;
        _nodeIdx = nodeIdx;
    }

    public PicoValueKind ValueKind =>
        _nodeIdx >= 0 ? _doc._nodes[_nodeIdx].Kind : PicoValueKind.Undefined;

    public PicoElement this[string key] =>
        TryGetProperty(key, out var v) ? v : throw new KeyNotFoundException($"'{key}' not found.");

    public PicoElement this[ReadOnlySpan<byte> utf8Key] =>
        TryGetProperty(utf8Key, out var v)
            ? v
            : throw new KeyNotFoundException($"'{Encoding.UTF8.GetString(utf8Key)}' not found.");

    public bool TryGetProperty(string key, out PicoElement value) =>
        TryGetProperty(Encoding.UTF8.GetBytes(key), out value);

    public bool TryGetProperty(ReadOnlySpan<byte> utf8Key, out PicoElement value)
    {
        value = default;
        if (ValueKind != PicoValueKind.Object)
            return false;
        int c = _doc._nodes[_nodeIdx].FirstChild;
        while (c >= 0)
        {
            ref readonly var n = ref _doc._nodes[c];
            if (n.NameEnd > n.NameStart)
            {
                var nameSpan = _doc._json.AsSpan(n.NameStart, n.NameEnd - n.NameStart);
                if (nameSpan.SequenceEqual(utf8Key))
                {
                    value = new PicoElement(_doc, c);
                    return true;
                }
            }
            c = n.NextSibling;
        }
        return false;
    }

    public PicoElement this[int index]
    {
        get
        {
            if (ValueKind != PicoValueKind.Array)
                throw new InvalidOperationException("Not an array.");
            int c = _doc._nodes[_nodeIdx].FirstChild;
            for (int i = 0; i < index && c >= 0; i++)
                c = _doc._nodes[c].NextSibling;
            if (c < 0)
                throw new IndexOutOfRangeException();
            return new PicoElement(_doc, c);
        }
    }

    public int GetArrayLength()
    {
        if (ValueKind != PicoValueKind.Array)
            throw new InvalidOperationException("Not an array.");
        int n = 0,
            c = _doc._nodes[_nodeIdx].FirstChild;
        while (c >= 0)
        {
            n++;
            c = _doc._nodes[c].NextSibling;
        }
        return n;
    }

    public string GetString()
    {
        if (ValueKind != PicoValueKind.String)
            throw new InvalidOperationException("Not a string.");
        ref readonly var n = ref _doc._nodes[_nodeIdx];
        if (n.ValueEnd <= n.ValueStart)
            return "";
        return Encoding.UTF8.GetString(_doc._json.AsSpan(n.ValueStart, n.ValueEnd - n.ValueStart));
    }

    public int GetInt32()
    {
        if (ValueKind != PicoValueKind.Number)
            throw new InvalidOperationException("Not a number.");
        ref readonly var n = ref _doc._nodes[_nodeIdx];
        var v =
            n.ValueEnd > n.ValueStart
                ? _doc._json.AsSpan(n.ValueStart, n.ValueEnd - n.ValueStart)
                : default;
        if (v.IsEmpty)
            return 0;
        if (int.TryParse(v, out var r))
            return r;
        if (long.TryParse(v, out var lr))
            return checked((int)lr);
        if (
            double.TryParse(
                v,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var dr
            )
        )
            return (int)dr;
        throw new FormatException($"Cannot parse '{Encoding.UTF8.GetString(v)}' as Int32.");
    }

    public long GetInt64()
    {
        if (ValueKind != PicoValueKind.Number)
            throw new InvalidOperationException("Not a number.");
        ref readonly var n = ref _doc._nodes[_nodeIdx];
        var v =
            n.ValueEnd > n.ValueStart
                ? _doc._json.AsSpan(n.ValueStart, n.ValueEnd - n.ValueStart)
                : default;
        if (v.IsEmpty)
            return 0;
        if (long.TryParse(v, out var r))
            return r;
        if (
            double.TryParse(
                v,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var dr
            )
        )
            return (long)dr;
        throw new FormatException($"Cannot parse '{Encoding.UTF8.GetString(v)}' as Int64.");
    }

    public double GetDouble()
    {
        if (ValueKind != PicoValueKind.Number)
            throw new InvalidOperationException("Not a number.");
        ref readonly var n = ref _doc._nodes[_nodeIdx];
        var v =
            n.ValueEnd > n.ValueStart
                ? _doc._json.AsSpan(n.ValueStart, n.ValueEnd - n.ValueStart)
                : default;
        if (v.IsEmpty)
            return 0;
        if (
            double.TryParse(
                v,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var dr
            )
        )
            return dr;
        throw new FormatException($"Cannot parse '{Encoding.UTF8.GetString(v)}' as Double.");
    }

    public bool TryGetInt32(out int value)
    {
        value = 0;
        if (ValueKind != PicoValueKind.Number)
            return false;
        ref readonly var n = ref _doc._nodes[_nodeIdx];
        var v =
            n.ValueEnd > n.ValueStart
                ? _doc._json.AsSpan(n.ValueStart, n.ValueEnd - n.ValueStart)
                : default;
        if (v.IsEmpty)
            return false;
        return int.TryParse(v, out value);
    }

    public bool TryGetInt64(out long value)
    {
        value = 0;
        if (ValueKind != PicoValueKind.Number)
            return false;
        ref readonly var n = ref _doc._nodes[_nodeIdx];
        var v =
            n.ValueEnd > n.ValueStart
                ? _doc._json.AsSpan(n.ValueStart, n.ValueEnd - n.ValueStart)
                : default;
        if (v.IsEmpty)
            return false;
        return long.TryParse(v, out value);
    }

    public bool GetBoolean()
    {
        return ValueKind switch
        {
            PicoValueKind.True => true,
            PicoValueKind.False => false,
            _ => throw new InvalidOperationException("Not a boolean."),
        };
    }

    /// <summary>Returns the raw UTF-8 bytes of the value (no copy). Throws on containers.</summary>
    public ReadOnlySpan<byte> GetRawValue()
    {
        if (ValueKind is PicoValueKind.Object or PicoValueKind.Array or PicoValueKind.Undefined)
            throw new InvalidOperationException("GetRawValue requires a scalar value.");
        ref readonly var n = ref _doc._nodes[_nodeIdx];
        if (n.ValueEnd <= n.ValueStart)
            return default;
        return _doc._json.AsSpan(n.ValueStart, n.ValueEnd - n.ValueStart);
    }

    /// <summary>Returns true if the object has a property with the given UTF-8 key (no value extraction).</summary>
    public bool HasProperty(ReadOnlySpan<byte> utf8Key)
    {
        if (ValueKind != PicoValueKind.Object)
            return false;
        int c = _doc._nodes[_nodeIdx].FirstChild;
        while (c >= 0)
        {
            ref readonly var n = ref _doc._nodes[c];
            if (
                n.NameEnd > n.NameStart
                && _doc._json.AsSpan(n.NameStart, n.NameEnd - n.NameStart).SequenceEqual(utf8Key)
            )
                return true;
            c = n.NextSibling;
        }
        return false;
    }

    /// <summary>Gets the value as a string, or null if the element is not a string.</summary>
    public string? GetStringOrNull() => ValueKind == PicoValueKind.String ? GetString() : null;

    public ArrayEnumerator EnumerateArray() => new(_doc, _nodeIdx);

    public ObjectEnumerator EnumerateObject() => new(_doc, _nodeIdx);
}

public struct ArrayEnumerator : IEnumerator<PicoElement>, IEnumerable<PicoElement>
{
    private readonly PicoDocument _doc;
    private int _child;
    private bool _started,
        _done;

    internal ArrayEnumerator(PicoDocument doc, int nodeIdx)
    {
        _doc = doc;
        _child = nodeIdx >= 0 ? doc._nodes[nodeIdx].FirstChild : -1;
        _started = _done = false;
    }

    public PicoElement Current => new(_doc, _child);
    object IEnumerator.Current => Current;

    public bool MoveNext()
    {
        if (_done)
            return false;
        if (!_started)
        {
            _started = true;
            if (_child < 0)
            {
                _done = true;
                return false;
            }
            return true;
        }
        _child = _doc._nodes[_child].NextSibling;
        if (_child < 0)
        {
            _done = true;
            return false;
        }
        return true;
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose() { }

    public ArrayEnumerator GetEnumerator() => this;

    IEnumerator<PicoElement> IEnumerable<PicoElement>.GetEnumerator() => this;

    IEnumerator IEnumerable.GetEnumerator() => this;
}

public struct ObjectEnumerator : IEnumerator<PicoProperty>, IEnumerable<PicoProperty>
{
    private readonly PicoDocument _doc;
    private int _child;
    private bool _started,
        _done;

    internal ObjectEnumerator(PicoDocument doc, int nodeIdx)
    {
        _doc = doc;
        _child = nodeIdx >= 0 ? doc._nodes[nodeIdx].FirstChild : -1;
        _started = _done = false;
    }

    public PicoProperty Current => new(_doc, _child);
    object IEnumerator.Current => Current;

    public bool MoveNext()
    {
        if (_done)
            return false;
        if (!_started)
        {
            _started = true;
            if (_child < 0)
            {
                _done = true;
                return false;
            }
            return true;
        }
        _child = _doc._nodes[_child].NextSibling;
        if (_child < 0)
        {
            _done = true;
            return false;
        }
        return true;
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose() { }

    public ObjectEnumerator GetEnumerator() => this;

    IEnumerator<PicoProperty> IEnumerable<PicoProperty>.GetEnumerator() => this;

    IEnumerator IEnumerable.GetEnumerator() => this;
}

public readonly struct PicoProperty
{
    private readonly PicoDocument _doc;
    private readonly int _nodeIdx;

    internal PicoProperty(PicoDocument doc, int nodeIdx)
    {
        _doc = doc;
        _nodeIdx = nodeIdx;
    }

    public string Name
    {
        get
        {
            ref readonly var n = ref _doc._nodes[_nodeIdx];
            if (n.NameEnd <= n.NameStart)
                return "";
            return Encoding.UTF8.GetString(_doc._json.AsSpan(n.NameStart, n.NameEnd - n.NameStart));
        }
    }

    /// <summary>Property name as raw UTF-8 bytes (zero allocation).</summary>
    public ReadOnlySpan<byte> NameSpan
    {
        get
        {
            ref readonly var n = ref _doc._nodes[_nodeIdx];
            if (n.NameEnd <= n.NameStart)
                return default;
            return _doc._json.AsSpan(n.NameStart, n.NameEnd - n.NameStart);
        }
    }

    public PicoElement Value => new(_doc, _nodeIdx);
}

internal struct PicoDocNode
{
    public PicoValueKind Kind;
    public int NameStart,
        NameEnd;
    public int ValueStart,
        ValueEnd;
    public int FirstChild,
        LastChild,
        NextSibling;

    public PicoDocNode()
    {
        FirstChild = LastChild = NextSibling = -1;
    }
}

public class PicoDocument
{
    internal readonly byte[] _json;
    internal readonly PicoDocNode[] _nodes;
    private readonly int _rootIdx;

    private PicoDocument(byte[] json, PicoDocNode[] nodes, int rootIdx)
    {
        _json = json;
        _nodes = nodes;
        _rootIdx = rootIdx;
    }

    public PicoElement RootElement => new(this, _rootIdx);

    public static PicoDocument Parse(byte[] json) => Parse(json, maxDepth: 64);

    public static PicoDocument Parse(byte[] json, int maxDepth)
    {
        var reader = new JsonReader(json, maxDepth: maxDepth);
        var nodes = new List<PicoDocNode>(64);
        var stack = new Stack<int>(16);
        int pendingNameStart = 0,
            pendingNameEnd = 0;
        int rootIdx = -1;

        void Add(PicoDocNode n)
        {
            int idx = nodes.Count;
            nodes.Add(n);
            if (stack.Count > 0)
            {
                int p = stack.Peek();
                var parent = nodes[p];
                if (parent.FirstChild < 0)
                {
                    nodes[p] = parent with { FirstChild = idx, LastChild = idx };
                }
                else
                {
                    var last = nodes[parent.LastChild];
                    nodes[parent.LastChild] = last with { NextSibling = idx };
                    nodes[p] = nodes[p] with { LastChild = idx };
                }
            }
            else if (rootIdx < 0)
                rootIdx = idx;
        }

        if (!reader.Read())
            throw new FormatException("Empty JSON input.");
        Process(reader, nodes, ref pendingNameStart, ref pendingNameEnd, stack, Add, maxDepth);
        while (reader.Read())
            Process(reader, nodes, ref pendingNameStart, ref pendingNameEnd, stack, Add, maxDepth);
        if (stack.Count > 0)
            throw new FormatException("Unclosed container.");
        return new PicoDocument(json, nodes.ToArray(), rootIdx);
    }

    private static void Process(
        JsonReader reader,
        List<PicoDocNode> nodes,
        ref int pendingNameStart,
        ref int pendingNameEnd,
        Stack<int> stack,
        Action<PicoDocNode> add,
        int maxDepth
    )
    {
        switch (reader.TokenType)
        {
            case TokenType.ObjectStart:
                if (stack.Count >= maxDepth)
                    throw new FormatException($"Max depth {maxDepth} exceeded.");
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.Object,
                        NameStart = pendingNameStart,
                        NameEnd = pendingNameEnd,
                    }
                );
                stack.Push(nodes.Count - 1);
                pendingNameStart = pendingNameEnd = 0;
                break;
            case TokenType.ArrayStart:
                if (stack.Count >= maxDepth)
                    throw new FormatException($"Max depth {maxDepth} exceeded.");
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.Array,
                        NameStart = pendingNameStart,
                        NameEnd = pendingNameEnd,
                    }
                );
                stack.Push(nodes.Count - 1);
                pendingNameStart = pendingNameEnd = 0;
                break;
            case TokenType.ObjectEnd:
            case TokenType.ArrayEnd:
                if (stack.Count == 0)
                    throw new FormatException("Unexpected end.");
                stack.Pop();
                break;
            case TokenType.PropertyName:
                pendingNameStart = reader.TokenValueStart;
                pendingNameEnd = reader.TokenValueEnd;
                break;
            case TokenType.String:
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.String,
                        NameStart = pendingNameStart,
                        NameEnd = pendingNameEnd,
                        ValueStart = reader.TokenValueStart,
                        ValueEnd = reader.TokenValueEnd,
                    }
                );
                pendingNameStart = pendingNameEnd = 0;
                break;
            case TokenType.Int64:
            case TokenType.Float64:
            case TokenType.Int32:
            case TokenType.Float32:
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.Number,
                        NameStart = pendingNameStart,
                        NameEnd = pendingNameEnd,
                        ValueStart = reader.TokenValueStart,
                        ValueEnd = reader.TokenValueEnd,
                    }
                );
                pendingNameStart = pendingNameEnd = 0;
                break;
            case TokenType.Bool:
            {
                bool isTrue = reader.GetStringRaw().SequenceEqual("true"u8);
                add(
                    new PicoDocNode
                    {
                        Kind = isTrue ? PicoValueKind.True : PicoValueKind.False,
                        NameStart = pendingNameStart,
                        NameEnd = pendingNameEnd,
                        ValueStart = reader.TokenValueStart,
                        ValueEnd = reader.TokenValueEnd,
                    }
                );
                pendingNameStart = pendingNameEnd = 0;
                break;
            }
            case TokenType.Null:
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.Null,
                        NameStart = pendingNameStart,
                        NameEnd = pendingNameEnd,
                    }
                );
                pendingNameStart = pendingNameEnd = 0;
                break;
            default:
                reader.TrySkip();
                break;
        }
    }

    public static bool IsValid(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
            return false;
        try
        {
            var r = new JsonReader(json);
            if (!r.Read() || r.TokenType == TokenType.None)
                return false;
            int d = 0;
            do
            {
                if (r.TokenType is TokenType.ObjectStart or TokenType.ArrayStart)
                    d++;
                else if (r.TokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
                {
                    d--;
                    if (d < 0)
                        return false;
                }
            } while (r.Read());
            return d == 0;
        }
        catch
        {
            return false;
        }
    }
}
