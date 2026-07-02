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
        TryGetProperty(key, out var v)
            ? v
            : throw new KeyNotFoundException($"Property '{key}' not found.");

    public bool TryGetProperty(string key, out PicoElement value)
    {
        value = default;
        if (ValueKind != PicoValueKind.Object) return false;
        int c = _doc._nodes[_nodeIdx].FirstChild;
        while (c >= 0)
        {
            if (string.Equals(_doc._nodes[c].Name, key, StringComparison.Ordinal))
            { value = new PicoElement(_doc, c); return true; }
            c = _doc._nodes[c].NextSibling;
        }
        return false;
    }

    public PicoElement this[int index]
    {
        get
        {
            if (ValueKind != PicoValueKind.Array) throw new InvalidOperationException("Not an array.");
            int c = _doc._nodes[_nodeIdx].FirstChild;
            for (int i = 0; i < index && c >= 0; i++) c = _doc._nodes[c].NextSibling;
            if (c < 0) throw new IndexOutOfRangeException();
            return new PicoElement(_doc, c);
        }
    }

    public int GetArrayLength()
    {
        if (ValueKind != PicoValueKind.Array) throw new InvalidOperationException("Not an array.");
        int n = 0, c = _doc._nodes[_nodeIdx].FirstChild;
        while (c >= 0) { n++; c = _doc._nodes[c].NextSibling; }
        return n;
    }

    public string GetString()
    {
        if (ValueKind != PicoValueKind.String) throw new InvalidOperationException("Not a string.");
        return _doc._nodes[_nodeIdx].Value ?? "";
    }

    public int GetInt32()
    {
        if (ValueKind != PicoValueKind.Number) throw new InvalidOperationException("Not a number.");
        var v = _doc._nodes[_nodeIdx].Value ?? "0";
        if (int.TryParse(v, out var r)) return r;
        if (long.TryParse(v, out var lr)) return checked((int)lr);
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
    internal ArrayEnumerator(PicoDocument doc, int nodeIdx) { _doc = doc; _child = nodeIdx >= 0 ? doc._nodes[nodeIdx].FirstChild : -1; _started = false; }
    public PicoElement Current => new(_doc, _child);
    public bool MoveNext() { if (!_started) { _started = true; return _child >= 0; } if (_child < 0) return false; _child = _doc._nodes[_child].NextSibling; return _child >= 0; }
    public ArrayEnumerator GetEnumerator() => this;
}

public struct ObjectEnumerator
{
    private readonly PicoDocument _doc;
    private int _child;
    private bool _started;
    internal ObjectEnumerator(PicoDocument doc, int nodeIdx) { _doc = doc; _child = nodeIdx >= 0 ? doc._nodes[nodeIdx].FirstChild : -1; _started = false; }
    public PicoProperty Current => new(_doc, _child);
    public bool MoveNext() { if (!_started) { _started = true; return _child >= 0; } if (_child < 0) return false; _child = _doc._nodes[_child].NextSibling; return _child >= 0; }
    public ObjectEnumerator GetEnumerator() => this;
}

public readonly ref struct PicoProperty
{
    private readonly PicoDocument _doc;
    private readonly int _nodeIdx;
    internal PicoProperty(PicoDocument doc, int nodeIdx) { _doc = doc; _nodeIdx = nodeIdx; }
    public string Name => _doc._nodes[_nodeIdx].Name ?? "";
    public PicoElement Value => new(_doc, _nodeIdx);
}

internal sealed class PicoDocNode
{
    public PicoValueKind Kind;
    public string? Name;
    public string? Value;
    public byte[]? ValueRaw;
    public int FirstChild = -1;
    public int NextSibling = -1;
}

public class PicoDocument
{
    internal readonly List<PicoDocNode> _nodes;
    private readonly int _rootIdx;

    private PicoDocument(List<PicoDocNode> nodes, int rootIdx)
    { _nodes = nodes; _rootIdx = rootIdx; }

    public PicoElement RootElement => new(this, _rootIdx);

    public static PicoDocument Parse(byte[] json)
    {
        var reader = new JsonReader(json);
        var nodes = new List<PicoDocNode>(64);
        var stack = new Stack<int>(16);
        string? pendingName = null;
        int rootIdx = -1;

        void Add(PicoDocNode n)
        {
            int idx = nodes.Count; nodes.Add(n);
            if (stack.Count > 0)
            {
                int p = stack.Peek(); var parent = nodes[p];
                if (parent.FirstChild < 0) parent.FirstChild = idx;
                else { int s = parent.FirstChild; while (nodes[s].NextSibling >= 0) s = nodes[s].NextSibling; nodes[s].NextSibling = idx; }
            }
            else if (rootIdx < 0) rootIdx = idx;
        }

        if (!reader.Read()) throw new FormatException("Empty JSON input.");
        Process(reader, nodes, ref pendingName, ref rootIdx, ref stack, Add);
        while (reader.Read()) Process(reader, nodes, ref pendingName, ref rootIdx, ref stack, Add);
        if (stack.Count > 0) throw new FormatException("Unclosed container.");
        return new PicoDocument(nodes, rootIdx);
    }

    private static void Process(
        JsonReader reader, List<PicoDocNode> nodes, ref string? pendingName,
        ref int rootIdx, ref Stack<int> stack, Action<PicoDocNode> add)
    {
        switch (reader.TokenType)
        {
            case TokenType.ObjectStart:
                add(new PicoDocNode { Kind = PicoValueKind.Object, Name = pendingName }); stack.Push(nodes.Count - 1); pendingName = null; break;
            case TokenType.ArrayStart:
                add(new PicoDocNode { Kind = PicoValueKind.Array, Name = pendingName }); stack.Push(nodes.Count - 1); pendingName = null; break;
            case TokenType.ObjectEnd: case TokenType.ArrayEnd:
                if (stack.Count == 0) throw new FormatException("Unexpected end."); stack.Pop(); break;
            case TokenType.PropertyName:
                pendingName = Encoding.UTF8.GetString(reader.GetStringRaw()); break;
            case TokenType.String:
                add(new PicoDocNode { Kind = PicoValueKind.String, Name = pendingName, Value = Encoding.UTF8.GetString(reader.GetStringRaw()), ValueRaw = reader.GetStringRaw().ToArray() }); pendingName = null; break;
            case TokenType.Int64:
            case TokenType.Float64:
            case TokenType.Int32:
            case TokenType.Float32:
                add(new PicoDocNode { Kind = PicoValueKind.Number, Name = pendingName, Value = Encoding.UTF8.GetString(reader.GetStringRaw()) }); pendingName = null; break;
            case TokenType.Bool:
            {
                var raw = reader.GetStringRaw();
                bool isTrue = raw.Length == 4; // "true"
                add(new PicoDocNode { Kind = isTrue ? PicoValueKind.True : PicoValueKind.False, Name = pendingName }); pendingName = null; break;
            }
            case TokenType.Null:
                add(new PicoDocNode { Kind = PicoValueKind.Null, Name = pendingName }); pendingName = null; break;
        }
    }

    public static bool IsValid(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty) return false;
        try
        {
            var r = new JsonReader(json);
            if (!r.Read() || r.TokenType == TokenType.None) return false;
            int d = 0;
            do {
                if (r.TokenType is TokenType.ObjectStart or TokenType.ArrayStart) d++;
                else if (r.TokenType is TokenType.ObjectEnd or TokenType.ArrayEnd) { d--; if (d < 0) return false; }
            } while (r.Read());
            return d == 0;
        }
        catch { return false; }
    }
}
