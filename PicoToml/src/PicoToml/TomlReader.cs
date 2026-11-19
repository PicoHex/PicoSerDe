namespace PicoToml;

/// <summary>Captures TomlReader state for pause/resume across chunks.</summary>
public struct TomlReaderState
{
    internal int Depth;
    internal int MaxDepth;
    internal long BytesConsumed;
    internal SequencePosition Position;
    internal bool InArray;
    internal int ArrayDepth;
    internal bool ArrayStartEmitted;
    internal bool InInlineTable;
    internal int InlineTableDepth;
    internal bool InlineStartEmitted;
    internal bool IsArrayTable;
    internal bool DottedActive;

    // The duplicate-key and table records travel with the parser state: TomlSerializer builds a
    // NEW reader per chunk, so a document streamed in two pieces must be as strict as the same
    // document read in one. Without these the second chunk would start with an empty record (a
    // duplicate or a redefined table split across the boundary would pass) and with the table
    // scope reset to the root (the keys would be filed under the wrong table).
    internal KeyDefinitionLog? KeyLog;
    internal TableDefinitionLog? TableLog;
    internal string? TableId;
    internal Dictionary<string, int>? ArrayInstances;
    internal int ArrayTableSeq;
}

/// <summary>
/// The keys a document has already defined, per table, in definition order.
/// <para>
/// TOML 1.0 §Keys: "Defining a key multiple times is invalid" — the reference
/// implementation refuses the document (<c>tomllib</c>: "Cannot overwrite a value"). A reader
/// that publishes both definitions reports a document the format does not have, and the
/// second value silently wins, so this record is what makes the refusal possible.
/// </para>
/// <para>
/// The keys are kept in definition ORDER so a transactional rewind can drop exactly the ones
/// the re-read will define again: a token re-read after <c>NeedsMoreData</c> is being read a
/// second time, not defined a second time.
/// </para>
/// </summary>
internal sealed class KeyDefinitionLog
{
    private readonly List<(string TableId, string Key)> _order = [];
    private readonly HashSet<(string TableId, string Key)> _defined = [];

    internal int Count => _order.Count;

    /// <summary>
    /// Records one definition; <see langword="false"/> means the key is already defined in this
    /// table. A token that is READ AGAIN after a rewind is not a redefinition — that case is
    /// handled by <see cref="TruncateTo"/>, which every rollback path calls, so this method is
    /// never asked about a key the parser is in the middle of re-reading.
    /// </summary>
    /// <param name="tableId">
    /// The current table's canonical id ("" at the document root). It carries the
    /// <c>[[array of tables]]</c> element number, so two elements of one array are two namespaces
    /// — that is what makes <c>[[Rows]] A = 1</c> / <c>[[Rows]] A = 2</c> legal while
    /// <c>[T] A = 1</c> / <c>[T] A = 2</c> is not, and what keeps the same sub-table name legal
    /// once per element (<c>[[Rows]]</c> / <c>[Rows.S]</c> twice).
    /// </param>
    /// <param name="key">The key as written, quoted or bare.</param>
    internal bool TryDefine(string tableId, string key)
    {
        var id = (tableId, key);
        if (!_defined.Add(id))
            return false;
        _order.Add(id);
        return true;
    }

    /// <summary>Drops every definition recorded after the first <paramref name="count"/>.</summary>
    internal void TruncateTo(int count)
    {
        for (int i = _order.Count - 1; i >= count; i--)
        {
            _defined.Remove(_order[i]);
            _order.RemoveAt(i);
        }
    }
}

/// <summary>
/// How a table path came to exist. TOML 1.0 does not carry one rule for "this table is declared
/// twice" — it carries one rule per <i>origin</i>:
/// <list type="bullet">
/// <item><description><see cref="HeaderTable"/> — §Table: "you cannot define a table more than
/// once", so a second <c>[T]</c> is invalid.</description></item>
/// <item><description><see cref="ArrayTable"/> — §Array of Tables: "each subsequent instance
/// creates and defines a new table element", so a second <c>[[T]]</c> is legal and a plain
/// <c>[T]</c> is not.</description></item>
/// <item><description><see cref="DottedImplicit"/> — §Table: a table dotted keys "create and
/// define" cannot be opened again with a <c>[table]</c> header.</description></item>
/// <item><description><see cref="ValueKey"/> — the path is a value (scalar, array, …), and §Keys
/// forbids declaring a table over it.</description></item>
/// <item><description><see cref="InlineTable"/> — §Inline Table: inline tables "are fully
/// self-contained", so a header cannot add to them.</description></item>
/// <item><description><see cref="ImplicitSuperTable"/> — a path that exists only because a header
/// named one of its children. §Table: "[x] # defining a super-table afterward is ok", so this one
/// MAY still be declared.</description></item>
/// </list>
/// </summary>
internal enum TableOrigin
{
    HeaderTable,
    ArrayTable,
    DottedImplicit,
    ValueKey,
    InlineTable,
    ImplicitSuperTable,
}

/// <summary>
/// Which table paths a document has and how each one came to exist — the table-level counterpart
/// of <see cref="KeyDefinitionLog"/>, under the same rules.
/// <para>
/// The entries are journaled in definition order so a transactional rewind can undo exactly what
/// the re-read will define again (a replay is not a redefinition). The journal keeps the PREVIOUS
/// origin too, because a definition can be an UPGRADE — <c>[T.S]</c> first leaves <c>T</c> as an
/// implicit super-table and a later <c>[T]</c> makes it a header table — and a rollback has to put
/// that back, or the re-read of <c>[T]</c> would see its own earlier attempt as a redefinition and
/// the chunked path would be stricter than the buffered one.
/// </para>
/// <para>
/// Ids are opaque strings. Each segment is length-prefixed, so <c>a."b.c"</c> (two segments) can
/// never collide with <c>["a.b.c"]</c> (one), and an array-of-tables element carries its element
/// number, so one sub-table name is a fresh path in every element.
/// </para>
/// </summary>
internal sealed class TableDefinitionLog
{
    private readonly Dictionary<string, TableOrigin> _origins = new(StringComparer.Ordinal);
    private readonly List<string> _journal = [];
    private readonly List<TableOrigin?> _previous = [];

    internal int Count => _journal.Count;

    internal bool TryGet(string id, out TableOrigin origin) => _origins.TryGetValue(id, out origin);

    /// <summary>
    /// Records <paramref name="origin"/> for <paramref name="id"/> unless the path is already
    /// known, the two exceptions being an implicit super-table (a real definition upgrades it) and
    /// a value that turns out to be an inline table (§Inline Table names the more precise clause).
    /// A path with a stronger origin keeps it: the refusal decision is made by the caller BEFORE
    /// this call, so a no-op here can never accept an illegal document, and it stops a key from
    /// downgrading a header of the same name in a legal one.
    /// </summary>
    internal void Define(string id, TableOrigin origin)
    {
        TableOrigin? previous = null;
        if (_origins.TryGetValue(id, out var existing))
        {
            if (!CanUpgrade(existing, origin))
                return;
            previous = existing;
        }
        _origins[id] = origin;
        _journal.Add(id);
        _previous.Add(previous);
    }

    private static bool CanUpgrade(TableOrigin existing, TableOrigin incoming) =>
        existing == incoming ? false
        : existing == TableOrigin.ImplicitSuperTable ? true
        : existing == TableOrigin.ValueKey && incoming == TableOrigin.InlineTable;

    /// <summary>Undoes every definition recorded after the first <paramref name="count"/>.</summary>
    internal void TruncateTo(int count)
    {
        for (int i = _journal.Count - 1; i >= count; i--)
        {
            var previous = _previous[i];
            if (previous is null)
                _origins.Remove(_journal[i]);
            else
                _origins[_journal[i]] = previous.Value;
            _journal.RemoveAt(i);
            _previous.RemoveAt(i);
        }
    }
}

public ref struct TomlReader : ITokenReader, ITransactionalTokenReader, IFusedTokenReader
{
    // Span mode fields
    private ReadOnlySpan<byte> _data;
    private int _position;

    // Sequence mode fields
    private SequenceReader<byte> _seqReader;
    private readonly bool _isSequence;

    // Common
    private TokenType _tokenType;
    private ReadOnlySpan<byte> _keySpan;
    private ReadOnlySpan<byte> _valueSpan;

    // True when the current value came from quoted string syntax: an empty
    // quoted string ("") is the empty string, never a null literal.
    private bool _valueWasQuoted;
    private ReadOnlySpan<byte> _tablePath;
    private bool _isArrayTable;
    private bool _inArray;
    private int _arrayDepth;
    private bool _arrayStartEmitted;
    private bool _inInlineTable;
    private int _inlineTableDepth;
    private bool _inlineStartEmitted;

    // Every pooled array a published span points into. The retention policy (eight slots,
    // which one is reclaimed when, what Dispose means) lives in ByteBufferRing and is not
    // TOML's business: nothing about it depends on TOML's grammar.
    private ByteBufferRing _buffers;
    private int _depth;
    private readonly int _maxDepth;

    // Inline dotted-key token buffer — stores offsets into _keySpan
    private bool _dottedActive;
    private int _dottedPartCount;
    private int _dottedPartIndex;
    private int _dottedO0,
        _dottedL0,
        _dottedO1,
        _dottedL1,
        _dottedO2,
        _dottedL2,
        _dottedO3,
        _dottedL3;

    private readonly bool _isFinalBlock;
    private bool _needsMoreData;

    // One-time UTF-8 BOM resolution (sequence mode).
    private bool _bomChecked;

    // Sequence-mode normalization: the incoming chunk is copied into a
    // contiguous buffer and parsed with span semantics. Unconsumed bytes are
    // retained by the serializer through ExportState.Position.
    private ReadOnlySequence<byte> _sourceSequence;
    private bool _normalizedSequence;
    private byte[]? _chunkCopy;
    private int _dataOffset;

    // True length of the copied chunk (the parse buffer never exceeds it).
    private int _realLength;
    private int _tokenStart;

    // Duplicate-key detection (TOML 1.0 §Keys). Lazily created on the first key; carried
    // through ExportState/ImportState so a chunked read is as strict as a buffered one.
    private KeyDefinitionLog? _keyLog;

    // Table redefinition detection (TOML 1.0 §Table / §Array of Tables / §Inline Table), same
    // lazily-created-and-exported discipline. _tableId is the canonical id of the table the keys
    // currently belong to (it carries array-of-tables element numbers); _arrayInstances remembers
    // which element of each array of tables is the most recent, because every path under it is
    // scoped to that element (§Array of Tables).
    private TableDefinitionLog? _tables;
    private string _tableId;
    private Dictionary<string, int>? _arrayInstances;
    private int _arrayTableSeq;

    // Transactional storage: parser state snapshots for Mark/RewindToMark and
    // for the internal per-Read transaction. Token spans are cleared on
    // rollback (callers must re-read; see ITransactionalTokenReader).
    private ParserMark _mark;

    private struct ParserMark
    {
        public int Position;
        public TokenType TokenType;
        public int Depth;
        public int ArrayDepth;
        public int InlineTableDepth;
        public int DottedPartCount;
        public int DottedPartIndex;
        public bool IsArrayTable;
        public bool InArray;
        public bool ArrayStartEmitted;
        public bool InInlineTable;
        public bool InlineStartEmitted;
        public bool DottedActive;
        public int DottedO0;
        public int DottedL0;
        public int DottedO1;
        public int DottedL1;
        public int DottedO2;
        public int DottedL2;
        public int DottedO3;
        public int DottedL3;

        // How many keys were defined when the mark was taken: a rewind un-defines the ones
        // the re-read will define again (a replay is not a redefinition).
        public int KeyCount;

        // The same for table paths, plus the table the keys belong to (a header that is rewound
        // must not leave the key scope on the table it opened).
        public int TableCount;
        public string? TableId;
    }

    private void SaveMark(ref ParserMark m)
    {
        m.Position = _position;
        m.TokenType = _tokenType;
        m.Depth = _depth;
        m.ArrayDepth = _arrayDepth;
        m.InlineTableDepth = _inlineTableDepth;
        m.DottedPartCount = _dottedPartCount;
        m.DottedPartIndex = _dottedPartIndex;
        m.IsArrayTable = _isArrayTable;
        m.InArray = _inArray;
        m.ArrayStartEmitted = _arrayStartEmitted;
        m.InInlineTable = _inInlineTable;
        m.InlineStartEmitted = _inlineStartEmitted;
        m.DottedActive = _dottedActive;
        m.DottedO0 = _dottedO0;
        m.DottedL0 = _dottedL0;
        m.DottedO1 = _dottedO1;
        m.DottedL1 = _dottedL1;
        m.DottedO2 = _dottedO2;
        m.DottedL2 = _dottedL2;
        m.DottedO3 = _dottedO3;
        m.DottedL3 = _dottedL3;
        m.KeyCount = _keyLog?.Count ?? 0;
        m.TableCount = _tables?.Count ?? 0;
        m.TableId = _tableId;
    }

    private void RestoreMark(in ParserMark m)
    {
        _position = m.Position;
        _tokenType = m.TokenType;
        _depth = m.Depth;
        _arrayDepth = m.ArrayDepth;
        _inlineTableDepth = m.InlineTableDepth;
        _dottedPartCount = m.DottedPartCount;
        _dottedPartIndex = m.DottedPartIndex;
        _isArrayTable = m.IsArrayTable;
        _inArray = m.InArray;
        _arrayStartEmitted = m.ArrayStartEmitted;
        _inInlineTable = m.InInlineTable;
        _inlineStartEmitted = m.InlineStartEmitted;
        _dottedActive = m.DottedActive;
        _dottedO0 = m.DottedO0;
        _dottedL0 = m.DottedL0;
        _dottedO1 = m.DottedO1;
        _dottedL1 = m.DottedL1;
        _dottedO2 = m.DottedO2;
        _dottedL2 = m.DottedL2;
        _dottedO3 = m.DottedO3;
        _dottedL3 = m.DottedL3;
        _keySpan = default;
        _valueSpan = default;
        _tablePath = default;
        _needsMoreData = false;
        // A rewound token will be read again: forget the keys and table paths it defined.
        _keyLog?.TruncateTo(m.KeyCount);
        _tables?.TruncateTo(m.TableCount);
        _tableId = m.TableId ?? "";
    }

    /// <inheritdoc />
    public void Mark() => SaveMark(ref _mark);

    /// <inheritdoc />
    public void RewindToMark() => RestoreMark(in _mark);

    public bool NeedsMoreData => _needsMoreData;

    /// <summary>Start offset of the token most recently produced by <see cref="Read"/>.</summary>
    public long TokenStart => _tokenStart;

    /// <summary>True when this reader resumes a previously exported state.</summary>
    public bool IsResumed { get; private set; }

    public TomlReader(ReadOnlySpan<byte> data, bool isFinalBlock = true)
    {
        _data = SkipBom(data);
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _isFinalBlock = isFinalBlock;
        _bomChecked = true;
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _tablePath = default;
        _isArrayTable = false;
        _inArray = false;
        _arrayDepth = 0;
        _arrayStartEmitted = false;
        _inInlineTable = false;
        _inlineTableDepth = 0;
        _inlineStartEmitted = false;
        _buffers = default;
        _depth = 0;
        _maxDepth = 256;
        _dottedActive = false;
        _dottedPartCount = 0;
        _dottedPartIndex = 0;
        _keyLog = null;
        _tables = null;
        _tableId = "";
        _arrayInstances = null;
        _arrayTableSeq = 0;
    }

    public TomlReader(ReadOnlySequence<byte> data, bool isFinalBlock = true)
    {
        _data = default;
        _position = 0;
        _seqReader = default;
        _isSequence = false;
        _normalizedSequence = true;
        _sourceSequence = data;
        _chunkCopy = null;
        _dataOffset = 0;
        _isFinalBlock = isFinalBlock;
        _bomChecked = false;
        if (!data.IsEmpty)
        {
            int len = (int)data.Length;
            _realLength = len;
            _chunkCopy = ArrayPool<byte>.Shared.Rent(len);
            data.CopyTo(_chunkCopy);
            _data = _chunkCopy.AsSpan(0, len);
        }
        _needsMoreData = false;
        _tokenType = TokenType.None;
        _keySpan = default;
        _valueSpan = default;
        _tablePath = default;
        _isArrayTable = false;
        _inArray = false;
        _arrayDepth = 0;
        _arrayStartEmitted = false;
        _inInlineTable = false;
        _inlineTableDepth = 0;
        _inlineStartEmitted = false;
        _buffers = default;
        _depth = 0;
        _maxDepth = 256;
        _dottedActive = false;
        _dottedPartCount = 0;
        _dottedPartIndex = 0;
        _keyLog = null;
        _tables = null;
        _tableId = "";
        _arrayInstances = null;
        _arrayTableSeq = 0;
    }

    public TokenType TokenType => _tokenType;
    public ReadOnlySpan<byte> KeySpan => _keySpan;
    public ReadOnlySpan<byte> ValueSpan => _valueSpan;

    /// <summary>
    /// True when the current value is a null literal: a bare key with no value
    /// (<c>Key =</c>) or the unquoted word <c>null</c> (any case). A quoted
    /// value is never null — <c>Key = ""</c> is the empty string.
    /// </summary>
    public bool IsNullLiteral
    {
        get
        {
            if (_valueWasQuoted)
                return false;
            var v = _valueSpan;
            return v.IsEmpty || System.Text.Ascii.EqualsIgnoreCase(v, "null"u8);
        }
    }
    public ReadOnlySpan<byte> TablePath => _tablePath;
    public bool IsArrayTable => _isArrayTable;
    public long BytesConsumed => _isSequence ? _seqReader.Consumed : _position;
    public int Depth => _depth;

    public readonly TomlReaderState ExportState()
    {
        return new TomlReaderState
        {
            Depth = _depth,
            MaxDepth = _maxDepth,
            BytesConsumed = _normalizedSequence ? _position : _seqReader.Consumed,
            Position = _normalizedSequence
                ? _sourceSequence.GetPosition(Math.Min(_dataOffset + _position, _realLength))
                : _seqReader.Position,
            InArray = _inArray,
            ArrayDepth = _arrayDepth,
            ArrayStartEmitted = _arrayStartEmitted,
            InInlineTable = _inInlineTable,
            InlineTableDepth = _inlineTableDepth,
            InlineStartEmitted = _inlineStartEmitted,
            IsArrayTable = _isArrayTable,
            DottedActive = _dottedActive,
            KeyLog = _keyLog,
            TableLog = _tables,
            TableId = _tableId,
            ArrayInstances = _arrayInstances,
            ArrayTableSeq = _arrayTableSeq,
        };
    }

    public TomlReader(ReadOnlySequence<byte> data, bool isFinalBlock, TomlReaderState state)
        : this(data, isFinalBlock)
    {
        // A default state (first chunk) carries MaxDepth == 0; keep the
        // constructor default in that case.
        if (state.MaxDepth > 0)
            _maxDepth = state.MaxDepth;
        _depth = state.Depth;
        _needsMoreData = false;
        // Only a genuinely resumed reader has already passed the BOM.
        _bomChecked = state.BytesConsumed > 0 || state.Depth > 0;
        IsResumed = state.BytesConsumed > 0 || state.Depth > 0;
        _inArray = state.InArray;
        _arrayDepth = state.ArrayDepth;
        _arrayStartEmitted = state.ArrayStartEmitted;
        _inInlineTable = state.InInlineTable;
        _inlineTableDepth = state.InlineTableDepth;
        _inlineStartEmitted = state.InlineStartEmitted;
        _isArrayTable = state.IsArrayTable;
        _dottedActive = state.DottedActive;
        _keyLog = state.KeyLog;
        _tables = state.TableLog;
        _tableId = state.TableId ?? "";
        _arrayInstances = state.ArrayInstances;
        _arrayTableSeq = state.ArrayTableSeq;
    }

    public bool Read()
    {
        _tokenStart = _position;
        _needsMoreData = false;
        // The per-token transaction covers the definition records too: every path below that
        // puts the position back will read this token AGAIN, and a re-read defines nothing.
        int __keyCount = _keyLog?.Count ?? 0;
        int __tableCount = _tables?.Count ?? 0;
        if (!_bomChecked)
        {
            if (_normalizedSequence)
            {
                if (IsBomAtStart(_data))
                {
                    _dataOffset += BomLength;
                    _data = _data[BomLength..];
                }
                else if (_data.Length < BomLength && !_isFinalBlock)
                {
                    // May be a BOM split across chunks; retain from position 0.
                    _needsMoreData = true;
                    return false;
                }
                _bomChecked = true;
            }
            else if (_isSequence)
            {
                SkipBomSeq(ref _seqReader, _isFinalBlock, out var bomNeedsMore);
                if (bomNeedsMore)
                {
                    _needsMoreData = true;
                    return false;
                }
                _bomChecked = true;
            }
            else
                _bomChecked = true;
        }
        if (_normalizedSequence)
        {
            int __tokenStart = _position;
            try
            {
                var normalized = ReadSpan();
                if (!normalized)
                {
                    // Never consume an incomplete token: the next attempt (with
                    // more data) restarts exactly at this token.
                    _position = __tokenStart;
                    _keyLog?.TruncateTo(__keyCount);
                    _tables?.TruncateTo(__tableCount);
                    return FailRead();
                }
                // A token ending at a non-final buffer end may continue in the
                // next chunk (no trailing newline, or an open multi-line
                // construct); defer it instead of consuming a partial value.
                if (
                    !_isFinalBlock
                    && _position >= _data.Length
                    && (
                        _inArray
                        || _arrayDepth > 0
                        || _inInlineTable
                        || _inlineTableDepth > 0
                        || _data.Length == 0
                        || _data[_data.Length - 1] != (byte)10
                    )
                )
                {
                    _position = __tokenStart;
                    _keyLog?.TruncateTo(__keyCount);
                    _tables?.TruncateTo(__tableCount);
                    _needsMoreData = true;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
                when (!_isFinalBlock
                    && ex
                        is IncompleteInputException
                            or FormatException
                            or IndexOutOfRangeException
                            or ArgumentOutOfRangeException
                )
            {
                _position = __tokenStart;
                _keyLog?.TruncateTo(__keyCount);
                _tables?.TruncateTo(__tableCount);
                _needsMoreData = true;
                return false;
            }
        }
        try
        {
            var result = _isSequence ? ReadSeq() : ReadSpan();
            return result || FailRead();
        }
        catch (IncompleteInputException)
        {
            // Non-final span reads must report NeedsMoreData, never throw.
            _keyLog?.TruncateTo(__keyCount);
            _needsMoreData = true;
            return false;
        }
    }

    /// <summary>
    /// Reports a failed read. At a genuine end of document the current token must stop
    /// describing the last token emitted: a caller that loops on <see cref="TokenType"/>
    /// would otherwise re-dispatch that stale token for ever - a bare <c>[V]</c> table
    /// (an empty table, D-9) re-dispatched its <see cref="TokenType.ObjectStart"/> on
    /// every pass and never terminated. A non-final read keeps the token, because there
    /// the caller is expected to feed more data and retry that same token.
    /// </summary>
    private bool FailRead()
    {
        _needsMoreData = !_isFinalBlock;
        if (_isFinalBlock)
            _tokenType = TokenType.None;
        return false;
    }

    /// <summary>Streaming resume: rewinds to an earlier span-relative offset.</summary>
    public void RewindTo(long consumedOffset)
    {
        if (consumedOffset < 0 || consumedOffset > _position)
            throw new ArgumentOutOfRangeException(nameof(consumedOffset));
        _position = (int)consumedOffset;
        _needsMoreData = false;
    }

    public bool TryGetInt32(out int v)
    {
        v = 0;
        if (_valueSpan.IsEmpty)
            return false;
        if (!TryParseTomlInteger(_valueSpan, out var l) || l < int.MinValue || l > int.MaxValue)
            return false;
        v = (int)l;
        return true;
    }

    public bool TryGetInt64(out long v)
    {
        v = 0;
        if (_valueSpan.IsEmpty)
            return false;
        return TryParseTomlInteger(_valueSpan, out v);
    }

    public bool TryGetUInt64(out ulong v)
    {
        v = 0;
        if (_valueSpan.IsEmpty)
            return false;
        if (!TryParseTomlUnsigned(_valueSpan, out var magnitude, out var negative) || negative)
            return false;
        v = magnitude;
        return true;
    }

    /// <summary>
    /// Parses a complete TOML integer token — optional sign, decimal digits or a
    /// <c>0x</c>/<c>0o</c>/<c>0b</c> prefixed integer, with single '_' digit
    /// separators. The token must be consumed in full: a trailing byte makes the
    /// parse fail instead of silently returning a prefix value (C-12).
    /// </summary>
    private static bool TryParseTomlInteger(ReadOnlySpan<byte> s, out long value)
    {
        value = 0;
        if (!TryParseTomlUnsigned(s, out var magnitude, out var negative))
            return false;
        if (negative)
        {
            // long.MinValue is representable as a magnitude of 2^63.
            if (magnitude > 9223372036854775808UL)
                return false;
            value = magnitude == 9223372036854775808UL ? long.MinValue : -(long)magnitude;
            return true;
        }
        if (magnitude > long.MaxValue)
            return false;
        value = (long)magnitude;
        return true;
    }

    /// <summary>
    /// Parses a TOML integer token into its magnitude plus sign, honouring the
    /// radix prefixes and '_' separators. Underscores must sit between digits.
    /// </summary>
    private static bool TryParseTomlUnsigned(
        ReadOnlySpan<byte> s,
        out ulong magnitude,
        out bool negative
    )
    {
        magnitude = 0;
        negative = false;
        if (s.IsEmpty)
            return false;

        int i = 0;
        if (s[0] is (byte)'+' or (byte)'-')
        {
            negative = s[0] == (byte)'-';
            i++;
        }
        if (i >= s.Length)
            return false;

        ulong radix = 10;
        if (s.Length - i > 2 && s[i] == (byte)'0')
        {
            switch (s[i + 1])
            {
                case (byte)'x':
                    radix = 16;
                    i += 2;
                    break;
                case (byte)'o':
                    radix = 8;
                    i += 2;
                    break;
                case (byte)'b':
                    radix = 2;
                    i += 2;
                    break;
            }
        }
        if (i >= s.Length)
            return false;

        bool anyDigit = false;
        for (; i < s.Length; i++)
        {
            byte b = s[i];
            if (b == (byte)'_')
            {
                // '_' is only a separator: it needs a digit on both sides.
                if (!anyDigit || i + 1 >= s.Length || s[i + 1] == (byte)'_')
                    return false;
                continue;
            }
            int d = HexValue(b);
            if (d < 0 || (ulong)d >= radix)
                return false;
            if (magnitude > (ulong.MaxValue - (ulong)d) / radix)
                return false;
            magnitude = magnitude * radix + (ulong)d;
            anyDigit = true;
        }
        return anyDigit;
    }

    private static int HexValue(byte b) =>
        b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
            >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
            _ => -1,
        };

    public bool TryGetBool(out bool v)
    {
        if (_valueSpan.SequenceEqual("true"u8))
        {
            v = true;
            return true;
        }
        if (_valueSpan.SequenceEqual("false"u8))
        {
            v = false;
            return true;
        }
        v = false;
        return false;
    }

    /// <summary>
    /// TOML does emit <see cref="TokenType.ArrayStart"/>/<see cref="TokenType.ArrayEnd"/> tokens, but the
    /// wire form carries no element count (spec §3.3): the closing bracket is found by scanning, so the
    /// reader holds no counter to report. Declared, not hinted — with <see langword="false"/> the contract
    /// (§3.1) requires <see cref="TryGetCollectionCount"/> to answer <see langword="false"/> for every token,
    /// and consumers terminate their loops with <see cref="ITokenReader.Read"/> plus the end token.
    /// </summary>
    public readonly bool HasCountedCollections => false;

    /// <summary>
    /// Always <see langword="false"/>: TOML has no count to report (see <see cref="HasCountedCollections"/>).
    /// A query, not a read (spec §3.1): no position move, no token, no state change.
    /// </summary>
    public readonly bool TryGetCollectionCount(out int count)
    {
        count = 0;
        return false;
    }

    /// <summary>
    /// Task T1b forwards this to the reference semantics (<see cref="TokenReaderFused"/>): the exact
    /// <c>Read() &amp;&amp; TryGetX(out value)</c> combination of spec §3.2.1, written once so it cannot drift
    /// per format. Not <c>readonly</c> — inside a <c>readonly</c> member <c>this</c> is a read-only variable and
    /// <c>ref this</c> is <c>CS1605</c> (spec §3.1 last paragraph).
    /// <para>
    /// TOML optimization is task T18's, and only if that task is not dropped per spec §6 S3.
    /// </para>
    /// </summary>
    public bool TryReadInt32(out int value) => TokenReaderFused.TryReadInt32(ref this, out value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="long"/>.</summary>
    public bool TryReadInt64(out long value) => TokenReaderFused.TryReadInt64(ref this, out value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="double"/>.</summary>
    public bool TryReadFloat64(out double value) =>
        TokenReaderFused.TryReadFloat64(ref this, out value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="bool"/>.</summary>
    public bool TryReadBool(out bool value) => TokenReaderFused.TryReadBool(ref this, out value);

    public bool TryGetFloat64(out double v)
    {
        if (_valueSpan.IsEmpty)
        {
            v = 0;
            return false;
        }
        // TOML special float values
        if (_valueSpan.SequenceEqual("inf"u8) || _valueSpan.SequenceEqual("+inf"u8))
        {
            v = double.PositiveInfinity;
            return true;
        }
        if (_valueSpan.SequenceEqual("-inf"u8))
        {
            v = double.NegativeInfinity;
            return true;
        }
        if (
            _valueSpan.SequenceEqual("nan"u8)
            || _valueSpan.SequenceEqual("+nan"u8)
            || _valueSpan.SequenceEqual("-nan"u8)
        )
        {
            v = double.NaN;
            return true;
        }
        // The whole token must be consumed: "1.5abc" is not a TOML float.
        if (
            !Utf8Parser.TryParse(_valueSpan, out v, out var consumed)
            || consumed != _valueSpan.Length
        )
        {
            v = 0;
            return false;
        }
        return true;
    }

    public bool TryReadNextInt32(out int v)
    {
        if (_isSequence)
            return TryReadNextInt32Seq(out v);
        return TryReadNextInt32Span(out v);
    }

    private bool TryReadNextInt32Span(out int v)
    {
        v = 0;
        if (_position >= _data.Length)
            return false;
        // Skip whitespace and commas
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (b <= 32 || b == (byte)',')
                _position++;
            else
                break;
        }
        if (_position >= _data.Length || _data[_position] == (byte)']')
            return false;

        bool neg = false;
        if (_data[_position] == (byte)'-')
        {
            neg = true;
            _position++;
        }
        if (
            _position >= _data.Length
            || _data[_position] < (byte)'0'
            || _data[_position] > (byte)'9'
        )
            return false;

        int result = 0;
        do
        {
            result = result * 10 + (_data[_position] - (byte)'0');
            _position++;
        } while (
            _position < _data.Length
            && _data[_position] >= (byte)'0'
            && _data[_position] <= (byte)'9'
        );
        v = neg ? -result : result;
        return true;
    }

    private bool TryReadNextInt32Seq(out int v)
    {
        v = 0;
        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b <= 32 || b == (byte)',')
                _seqReader.Advance(1);
            else
                break;
        }
        if (_seqReader.End || _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)']')
            return false;

        var buf = _buffers.Rent(16);
        int di = 0;
        while (
            !_seqReader.End
            && TextHelpers.IsDigit(_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex])
        )
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        return Utf8Parser.TryParse(buf.AsSpan(0, di), out v, out _);
    }

    public void Skip()
    {
        if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
        {
            int targetDepth = 1;
            while (Read())
            {
                if (_tokenType is TokenType.ObjectStart or TokenType.ArrayStart)
                    targetDepth++;
                else if (_tokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
                {
                    targetDepth--;
                    if (targetDepth == 0)
                        return;
                }
            }
        }
    }

    public bool TrySkip()
    {
        try
        {
            Skip();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_chunkCopy is not null)
        {
            ArrayPool<byte>.Shared.Return(_chunkCopy);
            _chunkCopy = null;
        }
        _buffers.Dispose();
    }

    /// <summary>
    /// Cuts a bare (unquoted) value span at an inline comment. A '#' that starts
    /// a token begins a comment that runs to the end of the line; quoted values
    /// never reach here, so a '#' inside a string is untouched.
    /// </summary>
    private static ReadOnlySpan<byte> StripComment(ReadOnlySpan<byte> raw)
    {
        int hash = raw.IndexOf((byte)'#');
        return hash < 0 ? raw : raw[..hash];
    }

    // ── Span-mode Read ──

    private bool ReadSpan()
    {
        // Drain dotted key buffer first
        if (_dottedActive)
            return EmitDottedTokenSpan();

        if (_inInlineTable)
            return ReadInlineTableSpan();
        if (_inArray)
            return ReadArraySpan();

        return SkipToMeaningfulLine() ? ReadTokenSpan() : false;
    }

    private bool SkipToMeaningfulLine()
    {
        while (true)
        {
            _position = SimdHelpers.SkipWhitespace(_data, _position);
            bool lineWasComment = false;
            while (_position < _data.Length)
            {
                byte b = _data[_position];
                if (b == (byte)'\n' || b == (byte)'\r')
                {
                    _position++;
                    continue;
                }
                if (b == (byte)'#')
                {
                    SkipLineSpan();
                    lineWasComment = true;
                    break;
                }
                return true;
            }
            if (!lineWasComment)
                return false;
        }
    }

    // ── Dotted key emission ──

    /// <summary>
    /// Emits the next token from the dotted-key buffer.
    /// For "a.b.c = value", emits: ObjectStart("a") → ObjectStart("b") → PropertyName("c") + value.
    /// </summary>
    private bool EmitDottedTokenSpan()
    {
        if (_dottedPartIndex < _dottedPartCount - 1)
        {
            // Emit ObjectStart for an intermediate dotted key part
            var off = GetDottedOffset(_dottedPartIndex);
            var len = GetDottedLength(_dottedPartIndex);
            _keySpan = _data.Slice(off, len);
            // Build full dotted path up to this part for tablePath
            _tablePath = BuildDottedPathUpTo(_dottedPartIndex);
            _dottedPartIndex++;
            _depth++;
            if (_depth > _maxDepth)
                throw new FormatException($"Maximum depth of {_maxDepth} exceeded");
            _tokenType = TokenType.ObjectStart;
            return true;
        }
        else if (_dottedPartIndex == _dottedPartCount - 1)
        {
            // Emit PropertyName for the final dotted key part (value was already stored)
            var off = GetDottedOffset(_dottedPartIndex);
            var len = GetDottedLength(_dottedPartIndex);
            _keySpan = _data.Slice(off, len);
            _dottedActive = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }
        _dottedActive = false;
        return false;
    }

    private ReadOnlySpan<byte> BuildDottedPathUpTo(int endPart)
    {
        int totalLen = 0;
        for (int i = 0; i <= endPart; i++)
        {
            if (i > 0)
                totalLen++; // dot
            totalLen += GetDottedLength(i);
        }
        // The composed path goes into a rented buffer the reader keeps alive until
        // Dispose: the span it publishes must outlive this call.
        var buf = ArrayPool<byte>.Shared.Rent(totalLen);
        _buffers.Track(buf);
        int w = 0;
        for (int i = 0; i <= endPart; i++)
        {
            if (i > 0)
                buf[w++] = (byte)'.';
            var off = GetDottedOffset(i);
            var len = GetDottedLength(i);
            _data.Slice(off, len).CopyTo(buf.AsSpan(w));
            w += len;
        }
        return buf.AsSpan(0, totalLen);
    }

    private int GetDottedOffset(int idx) =>
        idx switch
        {
            0 => _dottedO0,
            1 => _dottedO1,
            2 => _dottedO2,
            _ => _dottedO3,
        };

    private int GetDottedLength(int idx) =>
        idx switch
        {
            0 => _dottedL0,
            1 => _dottedL1,
            2 => _dottedL2,
            _ => _dottedL3,
        };

    /// <summary>Splits a dotted key like "a.b.c" into parts and stores offsets for emission.</summary>
    private void TrySplitDottedKey(ReadOnlySpan<byte> key, int keyStart)
    {
        // Count dots to determine part count
        int dotCount = 0;
        for (int i = 0; i < key.Length; i++)
            if (key[i] == (byte)'.')
                dotCount++;

        if (dotCount == 0 || dotCount > 3)
            return; // no dots or too many parts (max 4)

        _dottedPartCount = dotCount + 1;
        _dottedPartIndex = 0;
        _dottedActive = true;

        // Store offsets/lengths for each part
        int partIdx = 0;
        int segStart = keyStart;
        for (int i = 0; i <= key.Length; i++)
        {
            if (i == key.Length || key[i] == (byte)'.')
            {
                var segLen = (keyStart + i) - segStart;
                SetDottedPart(partIdx, segStart, segLen);
                partIdx++;
                if (i < key.Length)
                    segStart = keyStart + i + 1; // skip the dot
            }
        }
    }

    private void SetDottedPart(int idx, int offset, int length)
    {
        switch (idx)
        {
            case 0:
                _dottedO0 = offset;
                _dottedL0 = length;
                break;
            case 1:
                _dottedO1 = offset;
                _dottedL1 = length;
                break;
            case 2:
                _dottedO2 = offset;
                _dottedL2 = length;
                break;
            default:
                _dottedO3 = offset;
                _dottedL3 = length;
                break;
        }
    }

    private bool ReadTokenSpan()
    {
        if (_data[_position] == (byte)'[')
            return ReadTableHeaderSpan();

        return ReadKeyValueSpan();
    }

    private bool ReadTableHeaderSpan()
    {
        _position++;
        _isArrayTable = false;
        if (_position < _data.Length && _data[_position] == (byte)'[')
        {
            _isArrayTable = true;
            _position++;
        }

        int tblStart = _position;
        while (_position < _data.Length && _data[_position] != (byte)']')
            _position++;
        _tablePath = TrimEnd(_data[tblStart.._position]);
        DefineTableHeader();
        _position++;
        if (_isArrayTable && _position < _data.Length && _data[_position] == (byte)']')
            _position++;
        SkipLineSpan();
        _depth = 1; // TOML sections are flat (non-nesting)
        _tokenType = _isArrayTable ? TokenType.ArrayStart : TokenType.ObjectStart;
        return true;
    }

    /// <summary>
    /// Registers the key of the line just parsed against the current table, and refuses a second
    /// definition of it. TOML 1.0 §Keys: "Defining a key multiple times is invalid" — the
    /// reference implementation refuses the document (<c>tomllib</c>: "Cannot overwrite a
    /// value"), so publishing both definitions would report a document the format does not have
    /// while the second value silently won.
    /// <para>
    /// This runs BEFORE any token for the new definition is published, so a refused document
    /// stops at the offending line instead of after a partial read of it.
    /// </para>
    /// </summary>
    /// <param name="quotedKey">
    /// A quoted key is ONE key (<c>"a.b"</c> is the key <c>a.b</c>), so it never takes part in the
    /// dotted-key path that <see cref="DefineKeyPath"/> records.
    /// </param>
    /// <returns>
    /// The canonical path of the value this key defines — what the inline-table branch upgrades to
    /// <see cref="TableOrigin.InlineTable"/> when the value turns out to be <c>{ … }</c>.
    /// </returns>
    private string DefineKey(bool quotedKey)
    {
        _keyLog ??= new KeyDefinitionLog();
        var key = Encoding.UTF8.GetString(_keySpan);
        // Keys are defined per TABLE (§Keys), and an [[array of tables]] element is its own table
        // (§Array of Tables): _tableId carries the element number, so `[[Rows]] A = 1` /
        // `[[Rows]] A = 2` stays legal while `[T] A = 1` / `[T] A = 2` does not.
        if (!_keyLog.TryDefine(_tableId, key))
            throw new FormatException(
                $"Duplicate key '{key}' at offset {_position}: TOML 1.0 §Keys forbids defining a key twice in the same table."
            );
        return DefineKeyPath(quotedKey);
    }

    /// <summary>
    /// Records the table paths a key causes to exist, so a later <c>[header]</c> can tell whether
    /// it is reopening a table a dotted key created (§Table: "redefining such tables using a
    /// [table] header is not allowed") or naming a path that already holds a value (§Keys).
    /// <para>
    /// The distinction the last segment carries is the same one the specification draws: the
    /// tables BEFORE it are <see cref="TableOrigin.DottedImplicit"/> (created and defined by the
    /// dotted key, so no header may open them) and the last one holds the value
    /// (<see cref="TableOrigin.ValueKey"/>). <c>a.b = 1</c> then <c>[a.c]</c> therefore stays
    /// legal, which is the half of the rule the spec states explicitly: "the [table] form can,
    /// however, be used to define sub-tables within tables defined via dotted keys".
    /// </para>
    /// </summary>
    private string DefineKeyPath(bool quotedKey)
    {
        _tables ??= new TableDefinitionLog();
        // The overwhelming majority of keys are one bare key, so they skip the split entirely:
        // this runs once per key/value line and must not cost a list for the common case.
        if (quotedKey || _keySpan.IndexOf((byte)'.') < 0)
        {
            var single = AppendTableSegment(_tableId, Encoding.UTF8.GetString(_keySpan));
            _tables.Define(single, TableOrigin.ValueKey);
            return single;
        }

        var segments = SplitTablePath(_keySpan);
        var id = _tableId;
        for (int i = 0; i < segments.Count; i++)
        {
            var segment = AppendTableSegment(id, segments[i]);
            if (
                i < segments.Count - 1
                && _tables.TryGet(segment, out var existing)
                && existing is TableOrigin.ValueKey or TableOrigin.InlineTable
            )
                RefusePathUnderValue(existing, segments, i);
            id = segment;
            _tables.Define(
                id,
                i == segments.Count - 1 ? TableOrigin.ValueKey : TableOrigin.DottedImplicit
            );
        }
        return id;
    }

    /// <summary>
    /// Registers a table header, refusing the ones TOML 1.0 refuses — and accepting the ones it
    /// accepts, which is the harder half: a super-table may be declared AFTER its sub-table
    /// (§Table: "[x] # defining a super-table afterward is ok"), an array of tables appends a new
    /// element every time it appears (§Array of Tables), and a sub-table under an array element is
    /// a fresh path in every element, because "any reference to an array of tables points to the
    /// most recently defined table element".
    /// </summary>
    private void DefineTableHeader()
    {
        _tables ??= new TableDefinitionLog();
        _arrayInstances ??= new Dictionary<string, int>(StringComparer.Ordinal);
        var segments = SplitTablePath(_tablePath);
        var display = string.Join('.', segments);

        // Walk the super-tables first: they are this path's ancestors, and an array of tables
        // contributes its current element number to every path beneath it.
        var prefix = "";
        for (int i = 0; i < segments.Count - 1; i++)
        {
            var collection = AppendTableSegment(prefix, segments[i]);
            if (_tables.TryGet(collection, out var ancestor))
            {
                if (ancestor is TableOrigin.ValueKey or TableOrigin.InlineTable)
                    RefusePathUnderValue(ancestor, segments, i);
                if (
                    ancestor == TableOrigin.ArrayTable
                    && _arrayInstances.TryGetValue(collection, out var element)
                )
                {
                    prefix = collection + ElementSuffix(element);
                    continue;
                }
            }
            prefix = collection;
            _tables.Define(collection, TableOrigin.ImplicitSuperTable);
        }

        var id = AppendTableSegment(prefix, segments[^1]);
        var known = _tables.TryGet(id, out var existing);

        if (_isArrayTable)
        {
            if (known && existing != TableOrigin.ArrayTable)
                throw new FormatException(
                    $"Array of tables '{display}' at offset {_position}: {ArrayOfTablesRefusal(existing)}"
                );
            _tables.Define(id, TableOrigin.ArrayTable);
            var element = ++_arrayTableSeq;
            _arrayInstances[id] = element;
            _tableId = id + ElementSuffix(element);
            return;
        }

        // An implicit super-table is the one origin a [header] may still declare, and declaring it
        // is an upgrade, not a redefinition. Everything else — a header already seen, an array of
        // tables, a table a dotted key created, a value — is a redefinition.
        if (known && existing != TableOrigin.ImplicitSuperTable)
            throw new FormatException(
                $"Table '{display}' at offset {_position}: {TableRefusal(existing)}"
            );

        _tables.Define(id, TableOrigin.HeaderTable);
        _tableId = id;
    }

    /// <summary>
    /// Refuses a header or a dotted key that reaches THROUGH a value. <c>A = 1</c> followed by
    /// <c>[A.B]</c> or by <c>A.B = 2</c> has to overwrite a value to get there, which §Keys
    /// forbids (the reference implementation: "Cannot overwrite a value"); an inline table is the
    /// same wall from the other side — §Inline Table: "keys and sub-tables cannot be added outside
    /// the braces".
    /// </summary>
    /// <param name="origin">The origin of the path segment in the way.</param>
    /// <param name="segments">The written path, so the message can name the blocking prefix.</param>
    /// <param name="upTo">Index of the blocking segment in <paramref name="segments"/>.</param>
    private void RefusePathUnderValue(TableOrigin origin, List<string> segments, int upTo)
    {
        var path = string.Join('.', segments.GetRange(0, upTo + 1));
        throw new FormatException(
            $"Table '{path}' at offset {_position}: "
                + (
                    origin == TableOrigin.InlineTable
                        ? "TOML 1.0 §Inline Table forbids adding keys or sub-tables outside the braces."
                        : "TOML 1.0 §Keys forbids declaring a path under a key that is already defined as a value."
                )
        );
    }

    /// <summary>The clause a plain <c>[table]</c> header breaks, as a sentence (TOML 1.0).</summary>
    private static string TableRefusal(TableOrigin why) =>
        why switch
        {
            TableOrigin.ArrayTable =>
                "TOML 1.0 §Array of Tables forbids defining a table with the same name as an established array of tables.",
            TableOrigin.DottedImplicit =>
                "TOML 1.0 §Table forbids opening with a [table] header a table that a dotted key created.",
            TableOrigin.ValueKey =>
                "TOML 1.0 §Keys forbids declaring a table over a key that is already defined as a value.",
            TableOrigin.InlineTable =>
                "TOML 1.0 §Inline Table forbids adding keys or sub-tables outside the braces.",
            _ => "TOML 1.0 §Table forbids defining a table more than once.",
        };

    /// <summary>The clause an <c>[[array of tables]]</c> header breaks, as a sentence.</summary>
    private static string ArrayOfTablesRefusal(TableOrigin why) =>
        why switch
        {
            TableOrigin.DottedImplicit =>
                "TOML 1.0 §Table forbids redefining as an array of tables a table that a dotted key created.",
            TableOrigin.ValueKey =>
                "TOML 1.0 §Array of Tables forbids appending to a statically defined array.",
            TableOrigin.InlineTable =>
                "TOML 1.0 §Inline Table forbids adding keys or sub-tables outside the braces.",
            _ =>
                "TOML 1.0 §Array of Tables forbids redefining a normal table as an array of tables.",
        };

    /// <summary>
    /// Splits a table path (<c>_tablePath</c>, or a dotted key) into its decoded segments.
    /// <c>a."b.c"</c> is two segments (<c>a</c>, <c>b.c</c>) and <c>["a.b"]</c> is one, because the
    /// quote is part of the grammar (§Keys: "a dotted key is a sequence of bare or quoted keys
    /// joined with dots"). Whitespace around a bare segment is ignored (§Table: <c>[ g . h ]</c> is
    /// <c>[g.h]</c>) while a quoted segment keeps every character it holds, so <c>[" a "]</c> is a
    /// different key from <c>[a]</c>.
    /// </summary>
    private List<string> SplitTablePath(ReadOnlySpan<byte> path)
    {
        var segments = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < path.Length && (path[i] == (byte)' ' || path[i] == (byte)'\t'))
                i++;

            if (i < path.Length && path[i] is (byte)'"' or (byte)'\'')
            {
                var quote = path[i++];
                var start = i;
                if (quote == (byte)'"')
                {
                    while (i < path.Length && path[i] != quote)
                    {
                        if (path[i] == (byte)'\\' && i + 1 < path.Length)
                            i++;
                        i++;
                    }
                    var content = path[start..Math.Min(i, path.Length)];
                    segments.Add(Encoding.UTF8.GetString(UnescapeBasicString(content)));
                }
                else
                {
                    while (i < path.Length && path[i] != quote)
                        i++;
                    segments.Add(Encoding.UTF8.GetString(path[start..Math.Min(i, path.Length)]));
                }
                while (i < path.Length && path[i] != (byte)'.')
                    i++;
            }
            else
            {
                var start = i;
                while (i < path.Length && path[i] != (byte)'.')
                    i++;
                var bare = path[start..i];
                var end = bare.Length;
                while (end > 0 && (bare[end - 1] == (byte)' ' || bare[end - 1] == (byte)'\t'))
                    end--;
                segments.Add(Encoding.UTF8.GetString(bare[..end]));
            }

            if (i >= path.Length)
                return segments;
            i++; // the dot
        }
    }

    /// <summary>
    /// Appends one segment to a canonical table id. The length prefix is what keeps the id
    /// unambiguous for paths whose segments hold a dot: <c>a."b.c"</c> and <c>["a.b.c"]</c> are
    /// two different tables (§Table: "naming rules for tables are the same as for keys").
    /// </summary>
    private static string AppendTableSegment(string prefix, string segment) =>
        prefix + segment.Length.ToString(CultureInfo.InvariantCulture) + ":" + segment;

    /// <summary>
    /// The element number an <c>[[array of tables]]</c> path is scoped to. The separator cannot
    /// occur in the encoding <see cref="AppendTableSegment"/> produces, so an element id never
    /// collides with a segment id.
    /// </summary>
    private static string ElementSuffix(int element) =>
        "\u0000#" + element.ToString(CultureInfo.InvariantCulture);

    private bool ReadKeyValueSpan()
    {
        int keyStart = _position; // offset in _data; used by else branch and TrySplitDottedKey
        // A quoted key is a single literal key: its content never takes part in
        // dotted-key splitting ("a.b" is the key a.b, not a table a), so the
        // recorded offsets must start at the content, not at the opening quote.
        bool quotedKey = false;
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            quotedKey = true;
            keyStart = ++_position;
            _keySpan = ReadBasicStringSpan();
        }
        else if (_position < _data.Length && _data[_position] == (byte)'\'')
        {
            // A literal-string key is a quoted key too: 'a.b' is the single key
            // a.b — no escape processing and no dotted-key splitting.
            quotedKey = true;
            keyStart = ++_position;
            _keySpan = ReadLiteralStringSpan();
        }
        else
        {
            // Line-bound key scan: never consume past \n/\r (audit BUG-03).
            while (
                _position < _data.Length
                && _data[_position] != (byte)'='
                && _data[_position] is not ((byte)'\n' or (byte)'\r')
            )
                _position++;
            _keySpan = TrimEnd(_data[keyStart.._position]);
        }

        while (
            _position < _data.Length
            && _data[_position] != (byte)'='
            && _data[_position] is not ((byte)'\n' or (byte)'\r')
        )
            _position++;
        if (_position >= _data.Length && !_isFinalBlock)
            throw new IncompleteInputException();
        if (_position >= _data.Length || _data[_position] != (byte)'=')
            throw new FormatException("Invalid TOML line: expected '='.");
        _position++;
        var keyPath = DefineKey(quotedKey);
        while (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        if (_position < _data.Length && _data[_position] == (byte)'[')
        {
            // Arrays may span lines: read them atomically so the token stream
            // never exposes a partial array (resume lands on the member start).
            if (!BracketedValueComplete(_position) && !_isFinalBlock)
                throw new IncompleteInputException();
            _position++;
            _inArray = true;
            _arrayDepth = 1;
            _arrayStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        if (_position < _data.Length && _data[_position] == (byte)'{')
        {
            if (!BracketedValueComplete(_position) && !_isFinalBlock)
                throw new IncompleteInputException();
            _position++;
            _inInlineTable = true;
            _inlineTableDepth = 1;
            _inlineStartEmitted = false;
            // §Inline Table: the braces are self-contained, so no header may add to them. Naming
            // that clause needs the value's kind, which is only known now.
            _tables?.Define(keyPath, TableOrigin.InlineTable);
            _tokenType = TokenType.PropertyName;
            return true;
        }

        ReadValueSpan();
        SkipLineSpan();
        if (!quotedKey)
            TrySplitDottedKey(_keySpan, keyStart);
        if (_dottedActive)
        {
            // Emit first dotted token directly (next Read() will drain the rest)
            EmitDottedTokenSpan();
            return true;
        }
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private void ReadValueSpan()
    {
        _valueWasQuoted = false;
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            _valueWasQuoted = true;
            if (
                _position + 1 < _data.Length
                && _data[_position] == (byte)'"'
                && _data[_position + 1] == (byte)'"'
            )
            {
                _position += 2;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
                int ms = _position;
                while (_position + 2 < _data.Length)
                {
                    if (
                        _data[_position] == (byte)'"'
                        && _data[_position + 1] == (byte)'"'
                        && _data[_position + 2] == (byte)'"'
                    )
                    {
                        _valueSpan = _data[ms.._position];
                        _position += 3;
                        return;
                    }
                    _position++;
                }
                if (!_isFinalBlock)
                    throw new IncompleteInputException();
            }
            else
            {
                _valueSpan = ReadBasicStringSpan();
            }
        }
        else if (_position < _data.Length && _data[_position] == (byte)'\'')
        {
            _position++;
            _valueWasQuoted = true;
            if (
                _position + 1 < _data.Length
                && _data[_position] == (byte)'\''
                && _data[_position + 1] == (byte)'\''
            )
            {
                _position += 2;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
                int ms = _position;
                while (_position + 2 < _data.Length)
                {
                    if (
                        _data[_position] == (byte)'\''
                        && _data[_position + 1] == (byte)'\''
                        && _data[_position + 2] == (byte)'\''
                    )
                    {
                        _valueSpan = _data[ms.._position];
                        _position += 3;
                        return;
                    }
                    _position++;
                }
                if (!_isFinalBlock)
                    throw new IncompleteInputException();
            }
            else
            {
                int vs = _position;
                while (_position < _data.Length && _data[_position] != (byte)'\'')
                    _position++;
                _valueSpan = _data[vs.._position];
                if (_position >= _data.Length && !_isFinalBlock)
                    throw new IncompleteInputException();
                _position++;
            }
        }
        else
        {
            int vs = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            if (_position >= _data.Length && !_isFinalBlock)
                throw new IncompleteInputException();
            _valueSpan = Trim(StripComment(_data[vs.._position]));
        }
    }

    private bool ReadInlineTableSpan()
    {
        if (!_inlineStartEmitted)
        {
            _inlineStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }

        while (_position < _data.Length)
        {
            _position = SimdHelpers.SkipWhitespace(_data, _position);
            if (_position < _data.Length && _data[_position] == (byte)',')
            {
                _position++;
                continue;
            }
            break;
        }
        if (_position >= _data.Length)
            return false;

        if (_data[_position] == (byte)'}')
        {
            if (_position >= _data.Length && !_isFinalBlock)
                throw new IncompleteInputException();
            _position++;
            _inlineTableDepth--;
            if (_inlineTableDepth == 0)
            {
                _inInlineTable = false;
                if (_position < _data.Length && _data[_position] == (byte)'\r')
                    _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
            }
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        int ks = _position;
        while (_position < _data.Length && _data[_position] != (byte)'=')
            _position++;
        _keySpan = TrimEnd(_data[ks.._position]);
        _position++;
        while (_position < _data.Length && _data[_position] == (byte)' ')
            _position++;

        _valueWasQuoted = false;
        if (_position < _data.Length && _data[_position] == (byte)'"')
        {
            _position++;
            _valueWasQuoted = true;
            _valueSpan = ReadBasicStringSpan();
        }
        else
        {
            int vs = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)','
                && _data[_position] != (byte)'}'
                && _data[_position] != (byte)'\n'
            )
                _position++;
            _valueSpan = Trim(StripComment(_data[vs.._position]));
        }
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private bool ReadArraySpan()
    {
        if (!_arrayStartEmitted)
        {
            _arrayStartEmitted = true;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        while (_position < _data.Length)
        {
            _position = SimdHelpers.SkipWhitespace(_data, _position);
            if (_position < _data.Length && _data[_position] == (byte)',')
            {
                _position++;
                continue;
            }
            if (_position < _data.Length && _data[_position] == (byte)'#')
            {
                SkipLineSpan();
                continue;
            }
            break;
        }
        if (_position >= _data.Length)
            return false;

        if (_data[_position] == (byte)']')
        {
            _position++;
            _arrayDepth--;
            if (_arrayDepth == 0)
            {
                _inArray = false;
                if (_position < _data.Length && _data[_position] == (byte)'\r')
                    _position++;
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                    _position++;
            }
            _tokenType = TokenType.ArrayEnd;
            return true;
        }

        if (_data[_position] == (byte)'[')
        {
            _position++;
            _arrayDepth++;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        if (_data[_position] == (byte)'{')
        {
            // An INLINE TABLE as an array element (`V = [[{ Name = "a" }]]`, spec §3
            // S4): the same `{ … }` the member position reads, entered from inside an
            // array. Without this branch the value scan below would stop at the first
            // comma INSIDE the braces and hand back `{ Name = "a"` as a scalar.
            _position++;
            _inInlineTable = true;
            _inlineTableDepth++;
            // The ObjectStart token is this branch's job: ReadInlineTableSpan emits one
            // only for the member position, where the brace is still unconsumed.
            _inlineStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }

        return ReadArrayValueSpan();
    }

    private bool ReadArrayValueSpan()
    {
        _valueWasQuoted = false;
        if (_data[_position] == (byte)'"')
        {
            _position++;
            _valueWasQuoted = true;
            _valueSpan = ReadBasicStringSpan();
        }
        else if (_data[_position] == (byte)'\'')
        {
            _position++;
            _valueWasQuoted = true;
            int vs = _position;
            while (_position < _data.Length && _data[_position] != (byte)'\'')
                _position++;
            _valueSpan = _data[vs.._position];
            _position++;
        }
        else
        {
            int vs = _position;
            while (
                _position < _data.Length
                && _data[_position] != (byte)','
                && _data[_position] != (byte)']'
                && _data[_position] != (byte)'\n'
                && _data[_position] != (byte)'\r'
            )
                _position++;
            _valueSpan = Trim(StripComment(_data[vs.._position]));
        }
        _tokenType = TokenType.String;
        return true;
    }

    private void SkipLineSpan()
    {
        while (
            _position < _data.Length
            && _data[_position] != (byte)'\n'
            && _data[_position] != (byte)'\r'
        )
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'\r')
            _position++;
        if (_position < _data.Length && _data[_position] == (byte)'\n')
            _position++;
    }

    // ── Basic-string scan + unescape (TOML spec: \" \\ \b \t \n \f \r \uXXXX \UXXXXXXXX) ──

    /// <summary>
    /// Scans a TOML basic string starting just after the opening quote has been
    /// consumed. Honours backslash escapes so an escaped quote (\") does not
    /// terminate the value, then unescapes the content. When the content has no
    /// backslash the original buffer slice is returned (zero allocation); when it
    /// does, a pooled buffer is rented, tracked for disposal, and returned.
    /// </summary>
    /// <summary>
    /// True when the bracketed value starting at <paramref name="start"/>
    /// (a '[' or '{') has its matching closing bracket inside the buffer.
    /// Quotes and comments are honoured so brackets in strings do not count.
    /// </summary>
    private bool BracketedValueComplete(int start)
    {
        int i = start;
        int depth = 0;
        while (i < _data.Length)
        {
            byte b = _data[i];
            if (b == (byte)35)
            {
                while (i < _data.Length && _data[i] != (byte)10 && _data[i] != (byte)13)
                    i++;
                continue;
            }
            if (b == (byte)34)
            {
                i++;
                while (i < _data.Length)
                {
                    if (_data[i] == (byte)92)
                    {
                        i += 2;
                        continue;
                    }
                    if (_data[i] == (byte)34)
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (b == (byte)39)
            {
                i++;
                while (i < _data.Length && _data[i] != (byte)39)
                    i++;
                if (i >= _data.Length)
                    return false;
                i++;
                continue;
            }
            if (b is (byte)91 or (byte)123)
                depth++;
            else if (b is (byte)93 or (byte)125)
            {
                depth--;
                if (depth == 0)
                    return true;
            }
            i++;
        }
        return false;
    }

    private ReadOnlySpan<byte> ReadBasicStringSpan()
    {
        int start = _position;
        bool hasEscape = false;
        while (_position < _data.Length)
        {
            byte b = _data[_position];
            if (b == (byte)'"')
                break;
            if (b == (byte)'\\')
            {
                hasEscape = true;
                _position++;
                if (_position < _data.Length)
                    _position++;
                continue;
            }
            _position++;
        }
        if (_position >= _data.Length && !_isFinalBlock)
            throw new IncompleteInputException();
        var content = _data[start.._position];
        if (_position < _data.Length)
            _position++; // consume closing quote
        if (!hasEscape)
            return content;
        return UnescapeBasicString(content);
    }

    /// <summary>
    /// Reads a literal string, the opening <c>'</c> already consumed. A literal
    /// string has no escape processing at all: every byte up to the closing
    /// <c>'</c> is taken verbatim, so a backslash is just a backslash.
    /// </summary>
    private ReadOnlySpan<byte> ReadLiteralStringSpan()
    {
        int start = _position;
        while (_position < _data.Length && _data[_position] != (byte)'\'')
            _position++;
        if (_position >= _data.Length)
        {
            if (!_isFinalBlock)
                throw new IncompleteInputException();
            throw new FormatException("Invalid TOML line: unterminated literal string.");
        }
        var content = _data[start.._position];
        _position++; // consume closing quote
        return content;
    }

    private ReadOnlySpan<byte> UnescapeBasicString(ReadOnlySpan<byte> src)
    {
        // Every escape sequence is at least as many bytes as its UTF-8 output, so a
        // buffer of src.Length is always large enough (\uXXXX 6→4, \UXXXXXXXX 10→4).
        var buf = ArrayPool<byte>.Shared.Rent(src.Length);
        _buffers.Track(buf);
        int di = 0;
        int si = 0;
        while (si < src.Length)
        {
            if (src[si] == (byte)'\\' && si + 1 < src.Length)
            {
                si++;
                byte esc = src[si];
                si++;
                switch (esc)
                {
                    case (byte)'"':
                        buf[di++] = (byte)'"';
                        break;
                    case (byte)'\\':
                        buf[di++] = (byte)'\\';
                        break;
                    case (byte)'b':
                        buf[di++] = (byte)'\b';
                        break;
                    case (byte)'t':
                        buf[di++] = (byte)'\t';
                        break;
                    case (byte)'n':
                        buf[di++] = (byte)'\n';
                        break;
                    case (byte)'f':
                        buf[di++] = (byte)'\f';
                        break;
                    case (byte)'r':
                        buf[di++] = (byte)'\r';
                        break;
                    case (byte)'u':
                        // A supplementary character may be written as two escapes
                        // (\uD83D\uDE00): join the surrogate pair before encoding.
                        di = TextHelpers.AppendCodepoint(
                            buf,
                            di,
                            TextHelpers.ReadUnicodeEscape(src, ref si)
                        );
                        break;
                    case (byte)'U':
                        di = TextHelpers.AppendCodepoint(
                            buf,
                            di,
                            TextHelpers.ReadHexEscape(src, ref si, 8)
                        );
                        break;
                    default:
                        buf[di++] = esc;
                        break;
                }
            }
            else
            {
                buf[di++] = src[si++];
            }
        }
        return buf.AsSpan(0, di);
    }

    // ── Sequence-mode basic string scan + unescape ──

    /// <summary>
    /// Sequence-mode counterpart of <see cref="ReadBasicStringSpan"/>. Reads a basic
    /// string (opening quote already consumed) from the sequence into a rented,
    /// tracked buffer, honouring backslash escapes so \" does not terminate the
    /// value, then unescapes when any backslash was seen.
    /// </summary>
    private ReadOnlySpan<byte> ReadBasicStringSeq()
    {
        var buf = _buffers.Rent(256);
        int di = 0;
        bool hasEscape = false;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'"')
        {
            byte b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b == (byte)'\\')
            {
                hasEscape = true;
                if (di + 2 > buf.Length)
                    buf = GrowSeqBuf(buf, di);
                buf[di++] = b;
                _seqReader.Advance(1);
                if (!_seqReader.End)
                {
                    buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    _seqReader.Advance(1);
                }
                continue;
            }
            if (di >= buf.Length)
                buf = GrowSeqBuf(buf, di);
            buf[di++] = b;
            _seqReader.Advance(1);
        }
        if (!_seqReader.End)
            _seqReader.Advance(1); // consume closing quote
        var content = buf.AsSpan(0, di);
        if (!hasEscape)
            return content;
        return UnescapeBasicString(content);
    }

    private byte[] GrowSeqBuf(byte[] buf, int di)
    {
        var nb = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
        buf.AsSpan(0, di).CopyTo(nb);
        _buffers.Track(nb);
        return nb;
    }

    // ── Sequence-mode Read ──

    private bool ReadSeq()
    {
        if (_inInlineTable)
            return ReadInlineTableSeq();
        if (_inArray)
            return ReadArraySeq();

        while (true)
        {
            bool lineWasComment = false;
            while (!_seqReader.End)
            {
                var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                if (b == (byte)'\n' || b == (byte)'\r')
                {
                    _seqReader.Advance(1);
                    continue;
                }
                if (b == (byte)'#')
                {
                    SkipLineSeq();
                    lineWasComment = true;
                    break;
                }
                // Found a meaningful token
                if (b == (byte)'[')
                    return ReadTableHeaderSeq();
                return ReadKeyValueSeq();
            }
            if (!lineWasComment)
                return false;
        }
    }

    private bool ReadTableHeaderSeq()
    {
        _seqReader.Advance(1);
        _isArrayTable = false;
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'[')
        {
            _isArrayTable = true;
            _seqReader.Advance(1);
        }

        var buf = _buffers.Rent(64);
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _tablePath = buf.AsSpan(0, di);
        _seqReader.Advance(1);
        if (
            _isArrayTable
            && !_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)']'
        )
            _seqReader.Advance(1);
        SkipLineSeq();
        _depth = 1; // TOML sections are flat (non-nesting)
        _tokenType = _isArrayTable ? TokenType.ArrayStart : TokenType.ObjectStart;
        return true;
    }

    private bool ReadKeyValueSeq()
    {
        var buf = _buffers.Rent(256);
        int di = 0;

        // Quoted key
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            _keySpan = ReadBasicStringSeq();
        }
        else
        {
            di = 0;
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'='
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _keySpan = TrimEnd(buf.AsSpan(0, di));
        }

        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'=')
            _seqReader.Advance(1);
        _seqReader.Advance(1);
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        var nb = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
        if (nb == (byte)'[')
        {
            _seqReader.Advance(1);
            _inArray = true;
            _arrayDepth = 1;
            _arrayStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        if (nb == (byte)'{')
        {
            _seqReader.Advance(1);
            _inInlineTable = true;
            _inlineTableDepth = 1;
            _inlineStartEmitted = false;
            _tokenType = TokenType.PropertyName;
            return true;
        }

        ReadValueSeq();
        SkipLineSeq();
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private void ReadValueSeq()
    {
        _valueWasQuoted = false;
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            _valueWasQuoted = true;
            // Multiline basic string
            if (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"'
                && _seqReader.Length > 1
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex + 1] == (byte)'"'
            )
            {
                _seqReader.Advance(2);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                )
                    _seqReader.Advance(1);
                var buf = _buffers.Rent(256);
                int di = 0;
                while (!_seqReader.End)
                {
                    var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                    if (b == (byte)'"' && !_seqReader.End)
                    {
                        var remaining = _seqReader.Remaining;
                        if (remaining >= 3)
                        {
                            var s = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex..];
                            if (s[0] == (byte)'"' && s[1] == (byte)'"' && s[2] == (byte)'"')
                            {
                                _seqReader.Advance(3);
                                break;
                            }
                        }
                    }
                    buf[di++] = b;
                    _seqReader.Advance(1);
                }
                _valueSpan = buf.AsSpan(0, di);
            }
            else
            {
                _valueSpan = ReadBasicStringSeq();
            }
        }
        else
        {
            var buf = _buffers.Rent(256);
            int di = 0;
            while (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _valueSpan = Trim(StripComment(buf.AsSpan(0, di)));
        }
    }

    private bool ReadInlineTableSeq()
    {
        if (!_inlineStartEmitted)
        {
            _inlineStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }

        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
                _seqReader.Advance(1);
            else
                break;
        }
        if (_seqReader.End)
            return false;

        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'}')
        {
            _seqReader.Advance(1);
            _inlineTableDepth--;
            if (_inlineTableDepth == 0)
            {
                _inInlineTable = false;
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r'
                )
                    _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                )
                    _seqReader.Advance(1);
            }
            _tokenType = TokenType.ObjectEnd;
            return true;
        }

        var buf = _buffers.Rent(256);
        int di = 0;
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'=')
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _keySpan = TrimEnd(buf.AsSpan(0, di));
        _seqReader.Advance(1);
        while (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)' ')
            _seqReader.Advance(1);

        _valueWasQuoted = false;
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            _valueWasQuoted = true;
            _valueSpan = ReadBasicStringSeq();
            _tokenType = TokenType.PropertyName;
            return true;
        }

        di = 0;
        while (
            !_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)','
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'}'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
        )
        {
            buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            _seqReader.Advance(1);
        }
        _valueSpan = Trim(StripComment(buf.AsSpan(0, di)));
        _tokenType = TokenType.PropertyName;
        return true;
    }

    private bool ReadArraySeq()
    {
        if (!_arrayStartEmitted)
        {
            _arrayStartEmitted = true;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        while (!_seqReader.End)
        {
            var b = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',')
                _seqReader.Advance(1);
            else if (b == (byte)'#')
                SkipLineSeq();
            else
                break;
        }
        if (_seqReader.End)
            return false;

        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)']')
        {
            _seqReader.Advance(1);
            _arrayDepth--;
            if (_arrayDepth == 0)
            {
                _inArray = false;
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r'
                )
                    _seqReader.Advance(1);
                if (
                    !_seqReader.End
                    && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n'
                )
                    _seqReader.Advance(1);
            }
            _tokenType = TokenType.ArrayEnd;
            return true;
        }

        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'[')
        {
            _seqReader.Advance(1);
            _arrayDepth++;
            _tokenType = TokenType.ArrayStart;
            return true;
        }

        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'{')
        {
            // Inline table as an array element, sequence-mode half (see ReadArraySpan).
            _seqReader.Advance(1);
            _inInlineTable = true;
            _inlineTableDepth++;
            _inlineStartEmitted = true;
            _tokenType = TokenType.ObjectStart;
            return true;
        }

        return ReadArrayValueSeq();
    }

    private bool ReadArrayValueSeq()
    {
        var buf = _buffers.Rent(256);
        int di = 0;
        _valueWasQuoted = false;
        if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'"')
        {
            _seqReader.Advance(1);
            _valueWasQuoted = true;
            _valueSpan = ReadBasicStringSeq();
            _tokenType = TokenType.String;
            return true;
        }
        else if (_seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\'')
        {
            _seqReader.Advance(1);
            _valueWasQuoted = true;
            while (
                !_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\''
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
            _seqReader.Advance(1);
        }
        else
        {
            while (
                !_seqReader.End
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)','
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)']'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
                && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
            )
            {
                buf[di++] = _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex];
                _seqReader.Advance(1);
            }
        }
        _valueSpan = Trim(StripComment(buf.AsSpan(0, di)));
        _tokenType = TokenType.String;
        return true;
    }

    private void SkipLineSeq()
    {
        while (
            !_seqReader.End
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\n'
            && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] != (byte)'\r'
        )
            _seqReader.Advance(1);
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\r')
            _seqReader.Advance(1);
        if (!_seqReader.End && _seqReader.CurrentSpan[_seqReader.CurrentSpanIndex] == (byte)'\n')
            _seqReader.Advance(1);
    }

    // ── Helpers ──

    // ── Fast path array reading (used by source generators) ──
    // The scan itself lives in FastArrayReaders (PicoSerDe.Core): this format's flow array and
    // the other two text formats' are the same '[a, b, c]' grammar, so there is one
    // implementation. Reader state (_data / _position / _isSequence) stays here — reaching the
    // element list is each format's own business, scanning it is not.

    /// <summary>Fast path: read int32 array from raw buffer (span mode only). Returns count read, or 0 to fallback.</summary>
    public int TryReadInt32ArrayFast(scoped Span<int> dest) =>
        FastArrayReaders.TryReadInt32Array(_data, ref _position, _isSequence, dest);

    /// <summary>Fast path: read int64 array from raw buffer (span mode only). Returns count read, or 0 to fallback.</summary>
    public int TryReadInt64ArrayFast(scoped Span<long> dest) =>
        FastArrayReaders.TryReadInt64Array(_data, ref _position, _isSequence, dest);

    /// <summary>Fast path: read bool array from raw buffer (span mode only). Returns count read, or 0 to fallback.</summary>
    public int TryReadBoolArrayFast(scoped Span<bool> dest) =>
        FastArrayReaders.TryReadBoolArray(_data, ref _position, _isSequence, dest);
}
