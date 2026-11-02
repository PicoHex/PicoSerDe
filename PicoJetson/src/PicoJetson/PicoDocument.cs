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
        // Use captured unescaped bytes if available (strings with escape sequences)
        if (n.StringValueIndex >= 0 && n.StringValueIndex < _doc._stringValues.Length)
            return Encoding.UTF8.GetString(_doc._stringValues[n.StringValueIndex]);
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
        if (ulong.TryParse(v, out _))
            throw new FormatException($"Value '{Encoding.UTF8.GetString(v)}' exceeds Int64 range.");
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

    public ulong GetUInt64()
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
        if (ulong.TryParse(v, out var r))
            return r;
        if (
            double.TryParse(
                v,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var dr
            )
        )
            return checked((ulong)dr);
        throw new FormatException($"Cannot parse '{Encoding.UTF8.GetString(v)}' as UInt64.");
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

    public bool TryGetUInt64(out ulong value)
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
        return ulong.TryParse(v, out value);
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

    /// <summary>Index into _stringValues for unescaped string content (-1 = use offsets).</summary>
    public int StringValueIndex;

    public PicoDocNode()
    {
        FirstChild = LastChild = NextSibling = -1;
        StringValueIndex = -1;
    }
}

public class PicoDocument
{
    internal readonly byte[] _json;
    internal readonly PicoDocNode[] _nodes;
    internal readonly byte[][] _stringValues; // unescaped string values
    private readonly int _rootIdx;

    private PicoDocument(byte[] json, PicoDocNode[] nodes, byte[][] stringValues, int rootIdx)
    {
        _json = json;
        _nodes = nodes;
        _stringValues = stringValues;
        _rootIdx = rootIdx;
    }

    public PicoElement RootElement => new(this, _rootIdx);

    /// <summary>
    /// Validates JSON document structure beyond token-level syntax:
    /// container matching, property/value grammar, separators, single root value,
    /// and no trailing content. The token reader only checks token validity
    /// and bracket depth; this validator enforces the full document grammar.
    /// <para>
    /// Requires a span-mode reader: the ',' between two members/elements is a
    /// swallowed separator, not a token, so the validator inspects the raw gap
    /// itself (see <c>ConsumeSeparator</c>).
    /// </para>
    /// </summary>
    private struct JsonStructureValidator(int maxDepth)
    {
        // What the innermost open container accepts next. Kind per container:
        // 0 = object, 1 = array.
        private const byte ExpectValueOrEnd = 0; // array, first element position
        private const byte ExpectNameOrEnd = 1; // object, first property position
        private const byte ExpectValue = 2; // after a property name
        private const byte ExpectSeparatorOrEnd = 3; // after a completed value

        private readonly byte[] _expects = new byte[maxDepth];
        private readonly byte[] _kinds = new byte[maxDepth];
        private int _depth;
        private bool _rootDone;

        public void Accept(TokenType t, ReadOnlySpan<byte> src, int tokenStart)
        {
            if (_rootDone)
                throw new FormatException("Unexpected data after the document root.");

            // A completed value leaves its container expecting ',' or the closing
            // bracket. Settle that before the token is interpreted, so the states
            // below only ever describe a first-element position.
            if (_depth > 0 && _expects[_depth - 1] == ExpectSeparatorOrEnd)
                ConsumeSeparator(t, src, tokenStart);

            switch (t)
            {
                case TokenType.ObjectStart:
                case TokenType.ArrayStart:
                    EnsureCanStartValue();
                    if (_depth >= _expects.Length)
                        throw new FormatException("Maximum nesting depth exceeded.");
                    _kinds[_depth] = t == TokenType.ObjectStart ? (byte)0 : (byte)1;
                    _expects[_depth] =
                        t == TokenType.ObjectStart ? ExpectNameOrEnd : ExpectValueOrEnd;
                    _depth++;
                    // The parent's "expect value" obligation is not settled here:
                    // it becomes "expect separator-or-end" when this container
                    // closes, because until then the parent state is not consulted.
                    break;
                case TokenType.ObjectEnd:
                case TokenType.ArrayEnd:
                {
                    if (_depth == 0)
                        throw new FormatException("Unexpected closing bracket.");
                    byte kind = t == TokenType.ObjectEnd ? (byte)0 : (byte)1;
                    if (_kinds[_depth - 1] != kind)
                        throw new FormatException("Mismatched closing bracket.");
                    if (_expects[_depth - 1] == ExpectValue)
                        throw new FormatException("Expected a value after the property name.");
                    _depth--;
                    if (_depth == 0)
                        _rootDone = true;
                    else
                        _expects[_depth - 1] = ExpectSeparatorOrEnd; // value consumed
                    break;
                }
                case TokenType.PropertyName:
                    if (
                        _depth == 0
                        || _kinds[_depth - 1] != 0
                        || _expects[_depth - 1] != ExpectNameOrEnd
                    )
                        throw new FormatException(
                            "A property name is only allowed inside an object."
                        );
                    _expects[_depth - 1] = ExpectValue;
                    break;
                default: // scalar value tokens
                    EnsureCanStartValue();
                    if (_depth > 0)
                        _expects[_depth - 1] = ExpectSeparatorOrEnd; // value consumed
                    else
                        _rootDone = true; // top-level scalar
                    break;
            }
        }

        /// <summary>
        /// The container just completed a value: the next token must be its closing
        /// bracket or follow a ','. The reader swallows the comma without emitting a
        /// token, so the raw byte gap decides — a missing separator has no token of
        /// its own, and this is the only place that can see it.
        /// </summary>
        private void ConsumeSeparator(TokenType t, ReadOnlySpan<byte> src, int tokenStart)
        {
            if (t is TokenType.ObjectEnd or TokenType.ArrayEnd)
                return; // no separator before the closing bracket
            if (!HasComma(src, tokenStart))
                throw new FormatException("Expected ',' between values.");
            _expects[_depth - 1] = _kinds[_depth - 1] == 0 ? ExpectNameOrEnd : ExpectValueOrEnd;
        }

        /// <summary>
        /// True when a ',' sits between the previous token and the first
        /// non-whitespace byte of the current one. A gap holds at most one comma:
        /// the reader already rejects leading, doubled and trailing commas before
        /// any later token is emitted.
        /// </summary>
        private static bool HasComma(ReadOnlySpan<byte> src, int tokenStart)
        {
            for (int i = tokenStart; i < src.Length; i++)
            {
                byte b = src[i];
                if (b == (byte)',')
                    return true;
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
                    return false;
            }
            return false;
        }

        private void EnsureCanStartValue()
        {
            if (_depth == 0)
                return; // root value not yet read
            if (_kinds[_depth - 1] == 1)
            {
                if (_expects[_depth - 1] != ExpectValueOrEnd)
                    throw new FormatException("Unexpected value in array.");
                return;
            }
            if (_expects[_depth - 1] != ExpectValue)
                throw new FormatException(
                    "A value is only allowed after a property name in an object."
                );
        }

        public void EnsureComplete()
        {
            if (!_rootDone)
                throw new FormatException("Unexpected end of document.");
        }
    }

    public static PicoDocument Parse(byte[] json) => Parse(json, maxDepth: 64);

    public static PicoDocument Parse(byte[] json, int maxDepth)
    {
        var reader = new JsonReader(json, maxDepth: maxDepth);
        var nodes = new List<PicoDocNode>(64);
        var stack = new Stack<int>(16);
        int pendingNameStart = 0,
            pendingNameEnd = 0;
        int rootIdx = -1;
        var stringValues = new List<byte[]>();

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
        try
        {
            var validator = new JsonStructureValidator(maxDepth);
            Process(
                reader,
                nodes,
                stringValues,
                ref pendingNameStart,
                ref pendingNameEnd,
                stack,
                Add,
                maxDepth,
                ref validator
            );
            while (reader.Read())
                Process(
                    reader,
                    nodes,
                    stringValues,
                    ref pendingNameStart,
                    ref pendingNameEnd,
                    stack,
                    Add,
                    maxDepth,
                    ref validator
                );
            if (stack.Count > 0)
                throw new FormatException("Unclosed container.");
            validator.EnsureComplete();
        }
        finally
        {
            reader.Dispose();
        }
        return new PicoDocument(json, nodes.ToArray(), stringValues.ToArray(), rootIdx);
    }

    private static void Process(
        JsonReader reader,
        List<PicoDocNode> nodes,
        List<byte[]> stringValues,
        ref int pendingNameStart,
        ref int pendingNameEnd,
        Stack<int> stack,
        Action<PicoDocNode> add,
        int maxDepth,
        ref JsonStructureValidator validator
    )
    {
        validator.Accept(reader.TokenType, reader.RawBuffer, (int)reader.TokenStart);
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
            {
                var raw = reader.GetStringRaw();
                int svIdx = stringValues.Count;
                stringValues.Add(raw.ToArray());
                add(
                    new PicoDocNode
                    {
                        Kind = PicoValueKind.String,
                        NameStart = pendingNameStart,
                        NameEnd = pendingNameEnd,
                        ValueStart = reader.TokenValueStart,
                        ValueEnd = reader.TokenValueEnd,
                        StringValueIndex = svIdx,
                    }
                );
                pendingNameStart = pendingNameEnd = 0;
                break;
            }
            case TokenType.Int64:
            case TokenType.UInt64:
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
            try
            {
                if (!r.Read() || r.TokenType == TokenType.None)
                    return false;
                var validator = new JsonStructureValidator(256);
                do
                {
                    validator.Accept(r.TokenType, r.RawBuffer, (int)r.TokenStart);
                } while (r.Read());
                validator.EnsureComplete();
                return true;
            }
            finally
            {
                r.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }
}
