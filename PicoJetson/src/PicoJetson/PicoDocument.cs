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
            if (n.NameSpan is { } ns && ns.SequenceEqual(utf8Key))
            {
                value = new PicoElement(_doc, c);
                return true;
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
        return _doc._nodes[_nodeIdx].Value ?? "";
    }

    public int GetInt32()
    {
        if (ValueKind != PicoValueKind.Number)
            throw new InvalidOperationException("Not a number.");
        var v = _doc._nodes[_nodeIdx].Value ?? "0";
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
        throw new FormatException($"Cannot parse '{v}' as Int32.");
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

    public ReadOnlySpan<byte> GetRawValue()
    {
        var v = _doc._nodes[_nodeIdx].ValueRaw;
        return v ?? default;
    }

    public ArrayEnumerator EnumerateArray() => new(_doc, _nodeIdx);

    public ObjectEnumerator EnumerateObject() => new(_doc, _nodeIdx);
}

public struct ArrayEnumerator
{
    private readonly PicoDocument _doc;
    private int _child;
    private bool _started;
    private bool _done;

    internal ArrayEnumerator(PicoDocument doc, int nodeIdx)
    {
        _doc = doc;
        _child = nodeIdx >= 0 ? doc._nodes[nodeIdx].FirstChild : -1;
        _started = false;
        _done = false;
    }

    public PicoElement Current => new(_doc, _child);

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

    public ArrayEnumerator GetEnumerator() => this;
}

public struct ObjectEnumerator
{
    private readonly PicoDocument _doc;
    private int _child;
    private bool _started;
    private bool _done;

    internal ObjectEnumerator(PicoDocument doc, int nodeIdx)
    {
        _doc = doc;
        _child = nodeIdx >= 0 ? doc._nodes[nodeIdx].FirstChild : -1;
        _started = false;
        _done = false;
    }

    public PicoProperty Current => new(_doc, _child);

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

    public ObjectEnumerator GetEnumerator() => this;
}

public readonly ref struct PicoProperty
{
    private readonly PicoDocument _doc;
    private readonly int _nodeIdx;

    internal PicoProperty(PicoDocument doc, int nodeIdx)
    {
        _doc = doc;
        _nodeIdx = nodeIdx;
    }

    public string Name => _doc._nodes[_nodeIdx].Name ?? "";
    public PicoElement Value => new(_doc, _nodeIdx);
}

internal sealed class PicoDocNode
{
    public PicoValueKind Kind;
    public string? Name;
    public string? Value;
    public byte[]? NameSpan; // UTF-8 property name (for zero-alloc key lookup)
    public byte[]? ValueRaw;
    public int FirstChild = -1;
    public int LastChild = -1;
    public int NextSibling = -1;
}

public class PicoDocument
{
    internal readonly PicoDocNode[] _nodes;
    private readonly int _rootIdx;

    private PicoDocument(PicoDocNode[] nodes, int rootIdx)
    {
        _nodes = nodes;
        _rootIdx = rootIdx;
    }

    public PicoElement RootElement => new(this, _rootIdx);

    /// <summary>Parses JSON bytes into a document. Max nesting depth = 64.</summary>
    public static PicoDocument Parse(byte[] json) => Parse(json, maxDepth: 64);

    /// <summary>Parses JSON bytes with explicit max depth.</summary>
    public static PicoDocument Parse(byte[] json, int maxDepth)
    {
        var reader = new JsonReader(json, maxDepth: maxDepth);
        var nodes = new List<PicoDocNode>(64);
        var stack = new Stack<int>(16);
        string? pendingName = null;
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
                    parent.FirstChild = idx;
                    parent.LastChild = idx;
                }
                else
                {
                    nodes[parent.LastChild].NextSibling = idx;
                    parent.LastChild = idx;
                }
            }
            else if (rootIdx < 0)
                rootIdx = idx;
        }

        if (!reader.Read())
            throw new FormatException("Empty JSON input.");
        Process(reader, nodes, ref pendingName, stack, Add, maxDepth);
        while (reader.Read())
            Process(reader, nodes, ref pendingName, stack, Add, maxDepth);
        if (stack.Count > 0)
            throw new FormatException("Unclosed container.");
        return new PicoDocument(nodes.ToArray(), rootIdx);
    }

    private static void Process(
        JsonReader reader,
        List<PicoDocNode> nodes,
        ref string? pendingName,
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
                        Name = pendingName,
                        NameSpan = pendingName is not null
                            ? Encoding.UTF8.GetBytes(pendingName)
                            : null,
                    }
                );
                stack.Push(nodes.Count - 1);
                pendingName = null;
                break;
            case TokenType.ArrayStart:
                if (stack.Count >= maxDepth)
                    throw new FormatException($"Max depth {maxDepth} exceeded.");
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.Array,
                        Name = pendingName,
                        NameSpan = pendingName is not null
                            ? Encoding.UTF8.GetBytes(pendingName)
                            : null,
                    }
                );
                stack.Push(nodes.Count - 1);
                pendingName = null;
                break;
            case TokenType.ObjectEnd:
            case TokenType.ArrayEnd:
                if (stack.Count == 0)
                    throw new FormatException("Unexpected end.");
                stack.Pop();
                break;
            case TokenType.PropertyName:
                pendingName = Encoding.UTF8.GetString(reader.GetStringRaw());
                break;
            case TokenType.String:
            {
                var raw = reader.GetStringRaw();
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.String,
                        Name = pendingName,
                        NameSpan = pendingName is not null
                            ? Encoding.UTF8.GetBytes(pendingName)
                            : null,
                        Value = Encoding.UTF8.GetString(raw),
                        ValueRaw = raw.ToArray(),
                    }
                );
                pendingName = null;
                break;
            }
            case TokenType.Int64:
            case TokenType.Float64:
            case TokenType.Int32:
            case TokenType.Float32:
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.Number,
                        Name = pendingName,
                        NameSpan = pendingName is not null
                            ? Encoding.UTF8.GetBytes(pendingName)
                            : null,
                        Value = Encoding.UTF8.GetString(reader.GetStringRaw()),
                    }
                );
                pendingName = null;
                break;
            case TokenType.Bool:
            {
                var raw = reader.GetStringRaw();
                bool isTrue = raw.SequenceEqual("true"u8);
                add(
                    new PicoDocNode
                    {
                        Kind = isTrue ? PicoValueKind.True : PicoValueKind.False,
                        Name = pendingName,
                        NameSpan = pendingName is not null
                            ? Encoding.UTF8.GetBytes(pendingName)
                            : null,
                        Value = isTrue ? "true" : "false",
                    }
                );
                pendingName = null;
                break;
            }
            case TokenType.Null:
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.Null,
                        Name = pendingName,
                        NameSpan = pendingName is not null
                            ? Encoding.UTF8.GetBytes(pendingName)
                            : null,
                    }
                );
                pendingName = null;
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
