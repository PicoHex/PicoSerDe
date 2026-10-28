namespace PicoSerDe.Core.Tests;

/// <summary>
/// Equivalence tests for <see cref="TokenReaderFused"/> — the reference semantics every
/// <see cref="IFusedTokenReader"/> implementer either forwards to or must reproduce exactly
/// (spec §3.2.2, spec §5.1).
/// <para>
/// The reference arm is written <b>inline</b> in every check (<c>Read() &amp;&amp; TryGetX()</c>) and
/// never calls <see cref="TokenReaderFused"/>: comparing an implementation with itself proves
/// nothing — the reference implementation must not come from the implementation under test.
/// </para>
/// <para>
/// The reader below implements <see cref="ITokenReader"/> and <b>only</b> that interface, i.e. the
/// shape of an implementer that does not opt into <see cref="IFusedTokenReader"/>. That the
/// reference methods work on it pins the "usable without extra members" side of the contract
/// (spec §4.1 A-2); the forwarding stub at the bottom pins the other side.
/// </para>
/// </summary>
public class TokenReaderFusedTests
{
    // Three paths per member, per spec §3.2.2 (a)-(c): success, Read() failure (end of input and a
    // streaming chunk boundary), TryGetX() failure (another token family ⇔ token stays current).

    [Test]
    public async Task TokenOfEachFamily_MatchesInlineReference()
    {
        var problems = new List<string>();
        CompareAllMembers(problems, [ScriptedToken.OfInt32(7)], chunkBoundaryAtEnd: false);
        CompareAllMembers(
            problems,
            [ScriptedToken.OfInt64(9_000_000_000L)],
            chunkBoundaryAtEnd: false
        );
        CompareAllMembers(problems, [ScriptedToken.OfFloat64(2.5)], chunkBoundaryAtEnd: false);
        CompareAllMembers(problems, [ScriptedToken.OfBool(true)], chunkBoundaryAtEnd: false);
        await Assert.That(string.Join("; ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ReadFailure_MatchesInlineReference()
    {
        var problems = new List<string>();
        CompareAllMembers(problems, [], chunkBoundaryAtEnd: false);
        await Assert.That(string.Join("; ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ChunkBoundary_MatchesInlineReference()
    {
        var problems = new List<string>();
        CompareAllMembers(problems, [], chunkBoundaryAtEnd: true);
        problems.Add(CheckBoundaryIsReachable());
        await Assert.That(string.Join("; ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task TypeMismatch_MatchesInlineReference()
    {
        var problems = new List<string>();
        CompareAllMembers(problems, [ScriptedToken.OfString()], chunkBoundaryAtEnd: false);
        await Assert.That(string.Join("; ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Failure_ZeroesValue()
    {
        var problems = new List<string>();
        AssertValueZeroed(
            problems,
            [],
            chunkBoundaryAtEnd: false,
            path: "Read-failure at end of input"
        );
        AssertValueZeroed(
            problems,
            [],
            chunkBoundaryAtEnd: true,
            path: "Read-failure at a chunk boundary"
        );
        AssertValueZeroed(
            problems,
            [ScriptedToken.OfString()],
            chunkBoundaryAtEnd: false,
            path: "TryGetX-failure (String token)"
        );
        await Assert.That(string.Join("; ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FusedRead_AdvancesTheCallersReader()
    {
        await Assert.That(CheckAdvance()).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DerivedInterface_IsImplementableByARefStruct_AndItsForwardsAdvanceTheReader()
    {
        await Assert.That(CheckForwardingImplementation()).IsEqualTo(string.Empty);
    }

    // ──────────────────────────── the two arms per member ────────────────────────────

    private static Probe<int> FusedInt32(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool ok = TokenReaderFused.TryReadInt32(ref reader, out int value);
        return new Probe<int>(ok, value, reader.TokenType, reader.NeedsMoreData, reader.Position);
    }

    private static Probe<int> InlineInt32(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        int value = 0;
        bool ok = reader.Read() && reader.TryGetInt32(out value);
        return new Probe<int>(ok, value, reader.TokenType, reader.NeedsMoreData, reader.Position);
    }

    private static Probe<long> FusedInt64(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool ok = TokenReaderFused.TryReadInt64(ref reader, out long value);
        return new Probe<long>(ok, value, reader.TokenType, reader.NeedsMoreData, reader.Position);
    }

    private static Probe<long> InlineInt64(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        long value = 0;
        bool ok = reader.Read() && reader.TryGetInt64(out value);
        return new Probe<long>(ok, value, reader.TokenType, reader.NeedsMoreData, reader.Position);
    }

    private static Probe<double> FusedFloat64(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool ok = TokenReaderFused.TryReadFloat64(ref reader, out double value);
        return new Probe<double>(
            ok,
            value,
            reader.TokenType,
            reader.NeedsMoreData,
            reader.Position
        );
    }

    private static Probe<double> InlineFloat64(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        double value = 0;
        bool ok = reader.Read() && reader.TryGetFloat64(out value);
        return new Probe<double>(
            ok,
            value,
            reader.TokenType,
            reader.NeedsMoreData,
            reader.Position
        );
    }

    private static Probe<bool> FusedBool(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool ok = TokenReaderFused.TryReadBool(ref reader, out bool value);
        return new Probe<bool>(ok, value, reader.TokenType, reader.NeedsMoreData, reader.Position);
    }

    private static Probe<bool> InlineBool(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        var reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool value = false;
        bool ok = reader.Read() && reader.TryGetBool(out value);
        return new Probe<bool>(ok, value, reader.TokenType, reader.NeedsMoreData, reader.Position);
    }

    // ─────────────────────────────── the comparisons ───────────────────────────────

    private static void CompareAllMembers(
        List<string> problems,
        ScriptedToken[] tokens,
        bool chunkBoundaryAtEnd
    )
    {
        string scenario = Describe(tokens, chunkBoundaryAtEnd);
        Diff(
            problems,
            scenario,
            "TryReadInt32",
            FusedInt32(tokens, chunkBoundaryAtEnd),
            InlineInt32(tokens, chunkBoundaryAtEnd)
        );
        Diff(
            problems,
            scenario,
            "TryReadInt64",
            FusedInt64(tokens, chunkBoundaryAtEnd),
            InlineInt64(tokens, chunkBoundaryAtEnd)
        );
        Diff(
            problems,
            scenario,
            "TryReadFloat64",
            FusedFloat64(tokens, chunkBoundaryAtEnd),
            InlineFloat64(tokens, chunkBoundaryAtEnd)
        );
        Diff(
            problems,
            scenario,
            "TryReadBool",
            FusedBool(tokens, chunkBoundaryAtEnd),
            InlineBool(tokens, chunkBoundaryAtEnd)
        );
    }

    private static void Diff<TValue>(
        List<string> problems,
        string scenario,
        string member,
        Probe<TValue> fused,
        Probe<TValue> reference
    )
        where TValue : IEquatable<TValue>
    {
        if (fused.Ok != reference.Ok)
            problems.Add(
                $"{scenario} / {member}: return flag fused={fused.Ok} reference={reference.Ok}"
            );

        // "The token Read() produced stays current" — a rollback here is the F-3 defect.
        if (fused.Type != reference.Type)
            problems.Add(
                $"{scenario} / {member}: TokenType fused={fused.Type} reference={reference.Type}"
            );

        if (fused.NeedsMoreData != reference.NeedsMoreData)
            problems.Add(
                $"{scenario} / {member}: NeedsMoreData fused={fused.NeedsMoreData} reference={reference.NeedsMoreData}"
            );

        // Exactly one Read() per call: no extra advance, no rewind.
        if (fused.Position != reference.Position)
            problems.Add(
                $"{scenario} / {member}: position fused={fused.Position} reference={reference.Position}"
            );

        // Values are comparable only where the reference path has one: on failure the fused contract
        // pins value = 0 while `Read() && TryGetX()` leaves it unspecified (Failure_ZeroesValue below).
        if (fused.Ok && reference.Ok && !fused.Value.Equals(reference.Value))
            problems.Add(
                $"{scenario} / {member}: value fused={fused.Value} reference={reference.Value}"
            );
    }

    private static void AssertValueZeroed(
        List<string> problems,
        ScriptedToken[] tokens,
        bool chunkBoundaryAtEnd,
        string path
    )
    {
        // A fresh reader per member: each call consumes (or fails to consume) its own token.
        var int32Reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool int32Ok = TokenReaderFused.TryReadInt32(ref int32Reader, out int int32Value);
        if (int32Ok || int32Value != 0)
            problems.Add(
                $"{path} / TryReadInt32: ok={int32Ok} value={int32Value} (expected ok=False value=0)"
            );

        var int64Reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool int64Ok = TokenReaderFused.TryReadInt64(ref int64Reader, out long int64Value);
        if (int64Ok || int64Value != 0)
            problems.Add(
                $"{path} / TryReadInt64: ok={int64Ok} value={int64Value} (expected ok=False value=0)"
            );

        var float64Reader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool float64Ok = TokenReaderFused.TryReadFloat64(
            ref float64Reader,
            out double float64Value
        );
        if (float64Ok || float64Value != 0)
            problems.Add(
                $"{path} / TryReadFloat64: ok={float64Ok} value={float64Value} (expected ok=False value=0)"
            );

        var boolReader = new ScriptedReader(tokens, chunkBoundaryAtEnd);
        bool boolOk = TokenReaderFused.TryReadBool(ref boolReader, out bool boolValue);
        if (boolOk || boolValue)
            problems.Add(
                $"{path} / TryReadBool: ok={boolOk} value={boolValue} (expected ok=False value=False)"
            );
    }

    // ───────────────────────── multi-call and non-vacuity checks ─────────────────────────

    private static string CheckAdvance()
    {
        // The guard against the by-value receiver shape: with a copy as receiver both calls return 7
        // and the caller's position never moves (spec §R1.3 A-1).
        var reader = new ScriptedReader([ScriptedToken.OfInt32(7), ScriptedToken.OfInt32(8)]);
        var problems = new List<string>();

        bool firstOk = TokenReaderFused.TryReadInt32(ref reader, out int firstValue);
        if (!firstOk || firstValue != 7 || reader.Position != 0)
            problems.Add(
                $"first call: ok={firstOk} value={firstValue} position={reader.Position} (expected True/7/0)"
            );

        bool secondOk = TokenReaderFused.TryReadInt32(ref reader, out int secondValue);
        if (!secondOk || secondValue != 8 || reader.Position != 1)
            problems.Add(
                $"second call: ok={secondOk} value={secondValue} position={reader.Position} (expected True/8/1)"
            );

        bool thirdOk = TokenReaderFused.TryReadInt32(ref reader, out int thirdValue);
        if (thirdOk || thirdValue != 0 || reader.Position != 1)
            problems.Add(
                $"call at end: ok={thirdOk} value={thirdValue} position={reader.Position} (expected False/0/1)"
            );

        return string.Join("; ", problems);
    }

    /// <summary>
    /// The <see cref="ChunkBoundary_MatchesInlineReference"/> comparison is vacuous unless a failed
    /// <see cref="ITokenReader.Read"/> really sets <see cref="ITokenReader.NeedsMoreData"/> on the
    /// scripted reader: two arms that both report <c>false</c> would also be "equal".
    /// </summary>
    private static string CheckBoundaryIsReachable()
    {
        var reader = new ScriptedReader([], chunkBoundaryAtEnd: true);
        bool ok = reader.Read();
        return ok || !reader.NeedsMoreData
            ? $"a failed Read at a chunk boundary must set NeedsMoreData: ok={ok} needsMoreData={reader.NeedsMoreData}"
            : string.Empty;
    }

    private static string CheckForwardingImplementation() =>
        // Witness with exactly the constraint spec §5.3 uses for the conformance tests.
        Witness(
            new ForwardingTestReader([
                ScriptedToken.OfInt32(7),
                ScriptedToken.OfInt64(9_000_000_000L),
            ])
        );

    private static string Witness<TReader>(TReader reader)
        where TReader : IFusedTokenReader, allows ref struct
    {
        var problems = new List<string>();

        if (reader.HasCountedCollections)
            problems.Add(
                "HasCountedCollections: a format without a count in its header must answer false"
            );

        if (reader.TryGetCollectionCount(out int count) || count != 0)
            problems.Add($"TryGetCollectionCount: expected (False, 0), got (True, {count})");

        if (
            !reader.TryReadInt32(out int int32Value)
            || int32Value != 7
            || reader.TokenType != TokenType.Int32
        )
            problems.Add(
                $"TryReadInt32 via the interface: value={int32Value} tokenType={reader.TokenType} (expected 7/Int32)"
            );

        if (!reader.TryReadInt64(out long int64Value) || int64Value != 9_000_000_000L)
            problems.Add(
                $"TryReadInt64 via the interface: value={int64Value} (expected 9000000000)"
            );

        bool pastEndOk = reader.TryReadBool(out bool pastEndValue);
        if (pastEndOk || pastEndValue || reader.NeedsMoreData)
            problems.Add(
                $"read past the end: ok={pastEndOk} value={pastEndValue} needsMoreData={reader.NeedsMoreData}"
            );

        return string.Join("; ", problems);
    }

    private static string Describe(ScriptedToken[] tokens, bool chunkBoundaryAtEnd)
    {
        string script =
            tokens.Length == 0 ? "no tokens" : $"{tokens.Length} token(s), first={tokens[0].Type}";
        return chunkBoundaryAtEnd ? $"{script}, chunk boundary" : script;
    }

    /// <summary>What one arm observed: the return flag plus every piece of reader state.</summary>
    private readonly record struct Probe<TValue>(
        bool Ok,
        TValue Value,
        TokenType Type,
        bool NeedsMoreData,
        int Position
    );

    /// <summary>One token in the reader's script: a type plus the value that type decodes to.</summary>
    private readonly record struct ScriptedToken(
        TokenType Type,
        int I32,
        long I64,
        double F64,
        bool B
    )
    {
        public static ScriptedToken OfInt32(int value) => new(TokenType.Int32, value, 0, 0, false);

        public static ScriptedToken OfInt64(long value) => new(TokenType.Int64, 0, value, 0, false);

        public static ScriptedToken OfFloat64(double value) =>
            new(TokenType.Float64, 0, 0, value, false);

        public static ScriptedToken OfBool(bool value) => new(TokenType.Bool, 0, 0, 0, value);

        public static ScriptedToken OfString() => new(TokenType.String, 0, 0, 0, false);
    }

    /// <summary>
    /// A minimal <see cref="ITokenReader"/> — and nothing more. A fixed script, one token per
    /// <see cref="Read"/>, one getter family per token type. A failing getter writes a sentinel into
    /// <c>value</c> first, the way a real decoder can leave a partial value behind — which is what
    /// makes the fused methods' "value = 0 on failure" guarantee observable.
    /// </summary>
    private ref struct ScriptedReader : ITokenReader
    {
        private const int FailInt32 = 111;
        private const long FailInt64 = 222;
        private const double FailFloat64 = 3.5;
        private const bool FailBool = true;

        private readonly ReadOnlySpan<ScriptedToken> _tokens;
        private readonly bool _chunkBoundaryAtEnd;
        private int _index;
        private bool _needsMoreData;

        public ScriptedReader(ReadOnlySpan<ScriptedToken> tokens, bool chunkBoundaryAtEnd = false)
        {
            _tokens = tokens;
            _chunkBoundaryAtEnd = chunkBoundaryAtEnd;
            _index = -1;
            _needsMoreData = false;
        }

        /// <summary>Tokens consumed so far; -1 before the first successful <see cref="Read"/>.</summary>
        public int Position => _index;

        public bool Read()
        {
            if (_index + 1 >= _tokens.Length)
            {
                // Same shape as the built-in readers: a failed Read reports a chunk boundary and
                // leaves TokenType alone (no rollback).
                _needsMoreData = _chunkBoundaryAtEnd;
                return false;
            }

            _index++;
            _needsMoreData = false;
            return true;
        }

        public TokenType TokenType => _index >= 0 ? _tokens[_index].Type : TokenType.None;

        public bool NeedsMoreData => _needsMoreData;

        public int Depth => 0;

        public bool TryGetInt32(out int value)
        {
            if (CurrentIs(TokenType.Int32))
            {
                value = _tokens[_index].I32;
                return true;
            }

            value = FailInt32;
            return false;
        }

        public bool TryGetInt64(out long value)
        {
            if (CurrentIs(TokenType.Int64))
            {
                value = _tokens[_index].I64;
                return true;
            }

            value = FailInt64;
            return false;
        }

        public bool TryGetFloat64(out double value)
        {
            if (CurrentIs(TokenType.Float64))
            {
                value = _tokens[_index].F64;
                return true;
            }

            value = FailFloat64;
            return false;
        }

        public bool TryGetBool(out bool value)
        {
            if (CurrentIs(TokenType.Bool))
            {
                value = _tokens[_index].B;
                return true;
            }

            value = FailBool;
            return false;
        }

        public bool TrySkip() => Read();

        private bool CurrentIs(TokenType type) => _index >= 0 && _tokens[_index].Type == type;
    }

    /// <summary>
    /// The shape the five real readers will take (T1b): a ref struct that implements
    /// <see cref="IFusedTokenReader"/> and forwards every fused read with <c>ref this</c>. It exists so
    /// the contract cannot ship un-implementable — the failure mode that killed the default-interface-
    /// member design (spec §R1.1) — and so the forwarding channel stays compiled <b>and executed</b>
    /// (the query members are <c>readonly</c>; the fused reads must not be, spec §3.1 / CS1605).
    /// </summary>
    private ref struct ForwardingTestReader : IFusedTokenReader
    {
        private ScriptedReader _inner;

        public ForwardingTestReader(ReadOnlySpan<ScriptedToken> tokens) =>
            _inner = new ScriptedReader(tokens);

        public readonly bool HasCountedCollections => false;

        public readonly bool TryGetCollectionCount(out int count)
        {
            count = 0;
            return false;
        }

        public bool Read() => _inner.Read();

        public TokenType TokenType => _inner.TokenType;

        public bool NeedsMoreData => _inner.NeedsMoreData;

        public int Depth => _inner.Depth;

        public bool TryGetInt32(out int value) => _inner.TryGetInt32(out value);

        public bool TryGetInt64(out long value) => _inner.TryGetInt64(out value);

        public bool TryGetFloat64(out double value) => _inner.TryGetFloat64(out value);

        public bool TryGetBool(out bool value) => _inner.TryGetBool(out value);

        public bool TrySkip() => _inner.TrySkip();

        public bool TryReadInt32(out int value) =>
            TokenReaderFused.TryReadInt32(ref this, out value);

        public bool TryReadInt64(out long value) =>
            TokenReaderFused.TryReadInt64(ref this, out value);

        public bool TryReadFloat64(out double value) =>
            TokenReaderFused.TryReadFloat64(ref this, out value);

        public bool TryReadBool(out bool value) =>
            TokenReaderFused.TryReadBool(ref this, out value);
    }
}
