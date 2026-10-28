namespace PicoMsgPack.Tests;

/// <summary>
/// Task 4e guardrail: <see cref="MsgPackReader.Read"/> now dispatches between a
/// span fast path (<c>ReadSpan</c>) and the original sequence state machine
/// (<c>ReadSequential</c>). These tests decode the same payload both ways and
/// demand the two token streams agree on everything a caller can observe:
/// <see cref="TokenType"/>, the value span's bytes and length, the extension
/// tag, the decoded scalars, <see cref="MsgPackReader.BytesConsumed"/> and
/// <see cref="MsgPackReader.Depth"/> after every token.
/// <para>
/// The payload matrix lives in <see cref="MsgPackSpanTokenDump"/> so the golden
/// dump taken from the pre-change reader
/// (<c>.superpowers/sdd/perf-mp-readspan/harness/DumpTokens</c>) walks exactly
/// the same cases. Data sources hand out names rather than buffers: a test
/// parameter that is a mutable array would be shared between test instances.
/// </para>
/// </summary>
public class MsgPackReaderSpanSequenceEquivalenceTests
{
    public static IEnumerable<string> PayloadNames() =>
        MsgPackSpanTokenDump.Payloads().Select(p => p.Name);

    public static IEnumerable<string> SmallPayloadNames() =>
        MsgPackSpanTokenDump.SmallPayloads().Select(p => p.Name);

    public static IEnumerable<(string Name, int ChunkSize)> ChunkedCases() =>
        MsgPackSpanTokenDump
            .SmallPayloads()
            .SelectMany(p => new[] { 1, 2, 3, 5, 7 }.Select(c => (p.Name, ChunkSize: c)));

    [Test]
    [MethodDataSource(nameof(PayloadNames))]
    public async Task SpanAndSequence_ProduceIdenticalTokens(string name)
    {
        var payload = MsgPackSpanTokenDump.PayloadByName(name);
        var spanTokens = MsgPackSpanTokenDump.DrainSpan(payload);
        var sequenceTokens = MsgPackSpanTokenDump.DrainSequence(payload);

        await AssertTokenStreamsMatchAsync(name, payload, spanTokens, sequenceTokens);
    }

    /// <summary>
    /// Same payload, <c>isFinalBlock: false</c>: both paths must still walk the
    /// same tokens and end with <see cref="MsgPackReader.NeedsMoreData"/> set
    /// (the buffer was exhausted, not the document).
    /// </summary>
    [Test]
    [MethodDataSource(nameof(PayloadNames))]
    public async Task SpanAndSequence_ProduceIdenticalTokens_NonFinalBlock(string name)
    {
        var payload = MsgPackSpanTokenDump.PayloadByName(name);
        var spanTokens = MsgPackSpanTokenDump.DrainSpan(payload, isFinalBlock: false);
        var sequenceTokens = MsgPackSpanTokenDump.DrainSequence(payload, isFinalBlock: false);

        await AssertTokenStreamsMatchAsync(name, payload, spanTokens, sequenceTokens);

        bool spanNeedsMore;
        {
            var reader = new MsgPackReader(payload.AsSpan(), isFinalBlock: false);
            try
            {
                while (reader.Read()) { }
                spanNeedsMore = reader.NeedsMoreData;
            }
            finally
            {
                reader.Dispose();
            }
        }
        bool sequenceNeedsMore;
        {
            var reader = new MsgPackReader(
                new ReadOnlySequence<byte>(payload),
                isFinalBlock: false
            );
            try
            {
                while (reader.Read()) { }
                sequenceNeedsMore = reader.NeedsMoreData;
            }
            finally
            {
                reader.Dispose();
            }
        }

        await Assert.That(spanNeedsMore).IsTrue().Because($"{name}: span path ran out of bytes");
        await Assert
            .That(sequenceNeedsMore)
            .IsTrue()
            .Because($"{name}: sequence path ran out of bytes");
    }

    /// <summary>
    /// Streaming: accumulate the payload in <paramref name="chunkSize"/>-byte
    /// chunks and re-parse from the start on every refill (what
    /// <c>DeserializeStreamingCore</c> does). The final successful parse must
    /// produce the same token stream the span path produces from the whole
    /// buffer — including strings, bins and extensions that straddle a chunk
    /// boundary.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ChunkedCases))]
    public async Task ChunkedSequence_MatchesSpanTokens(string name, int chunkSize)
    {
        var payload = MsgPackSpanTokenDump.PayloadByName(name);
        var spanTokens = MsgPackSpanTokenDump.DrainSpan(payload);
        var streamed = MsgPackSpanTokenDump.DrainStreaming(payload, chunkSize);

        await AssertTokenStreamsMatchAsync(
            $"{name} @ chunk {chunkSize}",
            payload,
            spanTokens,
            streamed
        );
    }

    /// <summary>
    /// Well-formed random payloads (containers closed, exact length prefixes):
    /// both paths must walk them to the end with identical tokens and no
    /// exception. This reaches token combinations the named matrix does not
    /// enumerate, without needing any tolerance for the two documented mode
    /// differences (trailing garbage, length-prefix message wording) - the
    /// generator never produces either situation.
    /// </summary>
    [Test]
    public async Task RandomWellFormedPayloads_AgreeBetweenPaths()
    {
        var compared = 0;
        var tokens = 0;
        foreach (var (name, payload) in MsgPackSpanTokenDump.RandomPayloads())
        {
            var spanTokens = MsgPackSpanTokenDump.DrainSpan(payload);
            var sequenceTokens = MsgPackSpanTokenDump.DrainSequence(payload);
            await AssertTokenStreamsMatchAsync(name, payload, spanTokens, sequenceTokens);
            compared++;
            tokens += spanTokens.Count;
        }

        await Assert
            .That(compared)
            .IsEqualTo(2000)
            .Because("the corpus is fixed-size, so a shrinking run is visible");
        await Assert.That(tokens).IsGreaterThan(2000).Because("the corpus is not degenerate");
    }

    /// <summary>
    /// The trailing-garbage verdict is a span-mode rule (the sequence path is
    /// streaming and cannot tell "document closed" from "more bytes may follow"),
    /// so it is asserted per path rather than as an equivalence.
    /// </summary>
    [Test]
    public async Task TrailingGarbage_ThrowsOnSpanPath_AndStreamsOnSequencePath()
    {
        var garbage = new byte[] { 0x81, 0x00, 0x01, 0xFF };

        var message = "";
        {
            var reader = new MsgPackReader(garbage.AsSpan());
            try
            {
                try
                {
                    while (reader.Read()) { }
                }
                catch (FormatException ex)
                {
                    message = ex.Message;
                }
            }
            finally
            {
                reader.Dispose();
            }
        }
        await Assert
            .That(message)
            .IsEqualTo("Unexpected data after the document at offset 3")
            .Because("span mode still rejects trailing bytes at the same offset");

        var types = new List<TokenType>();
        {
            var reader = new MsgPackReader(new ReadOnlySequence<byte>(garbage));
            try
            {
                while (reader.Read())
                    types.Add(reader.TokenType);
            }
            finally
            {
                reader.Dispose();
            }
        }
        await Assert
            .That(types.Count)
            .IsEqualTo(5)
            .Because(
                "sequence mode keeps its streaming semantics: no trailing-garbage check, so the "
                    + "byte after the closed map is tokenized as another value"
            );
        await Assert.That(types[3]).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(types[^1]).IsEqualTo(TokenType.Int32);
    }

    /// <summary>
    /// Malformed payloads must fail with the same exception type and the same
    /// message as before Task 4e. The expectations below were read out of the
    /// pre-change dump (<c>.superpowers/sdd/perf-mp-readspan/logs/tokens-before.txt</c>),
    /// not guessed; the full before/after dump diff is byte-identical.
    /// <para>
    /// A <c>null</c> message means "type only": the one payload that trips a
    /// framework exception (<c>IndexOutOfRangeException</c> from a fixext whose
    /// tag byte is missing) carries a localized runtime message, so asserting
    /// its text here would fail on a non-English host for reasons unrelated to
    /// this change.
    /// </para>
    /// </summary>
    [Test]
    public async Task MalformedPayloads_KeepTheirExactDiagnostics()
    {
        var expectations = new (string Name, byte[] Payload, string Exception, string? Message)[]
        {
            (
                "unknown-byte-c1",
                new byte[] { 0xC1 },
                "FormatException",
                "Unknown MsgPack byte 0xC1 at offset 1"
            ),
            (
                "truncated-int32",
                new byte[] { 0xD2, 0x00, 0x01 },
                "FormatException",
                "Invalid length prefix 4 at offset 1"
            ),
            (
                "truncated-length-bytes-str16",
                new byte[] { 0xDA, 0x01 },
                "FormatException",
                "Expected 2 length bytes at offset 1, only 1 remaining"
            ),
            (
                "hostile-bin32-length",
                new byte[] { 0xC6, 0xFF, 0xFF, 0xFF, 0xFF },
                "FormatException",
                "Invalid length prefix 4294967295 at offset 5"
            ),
            ("truncated-fixext-tag", new byte[] { 0xD6 }, "IndexOutOfRangeException", null),
            (
                "truncated-str8-body",
                new byte[] { 0xD9, 0x0A, 0x61, 0x62, 0x63 },
                "FormatException",
                "Invalid length prefix 10 at offset 2"
            ),
        };

        foreach (var (name, payload, expectedType, expectedMessage) in expectations)
        {
            var (type, message) = FailureOfSpanPath(payload);
            await Assert.That(type).IsEqualTo(expectedType).Because(name);
            if (expectedMessage is not null)
                await Assert.That(message).IsEqualTo(expectedMessage).Because(name);
        }

        var (depthType, depthMessage) = FailureOfSpanPath(NestedOverflowPayload());
        await Assert.That(depthType).IsEqualTo("FormatException");
        await Assert.That(depthMessage).IsEqualTo("Maximum depth of 64 exceeded");
    }

    /// <summary>
    /// The reader really does have two token paths: if someone deleted one of
    /// them, the equivalence tests above would compare a path with itself and
    /// silently pass. This asserts the structure they depend on.
    /// </summary>
    [Test]
    public async Task ReaderHasBothTokenPaths()
    {
        var type = typeof(MsgPackReader);
        var spanPath = type.GetMethod(
            "ReadSpan",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        var sequencePath = type.GetMethod(
            "ReadSequential",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        await Assert.That(spanPath).IsNotNull();
        await Assert.That(sequencePath).IsNotNull();
        await Assert.That(spanPath!.ReturnType).IsEqualTo(typeof(bool));
        await Assert.That(sequencePath!.ReturnType).IsEqualTo(typeof(bool));
    }

    // ── helpers ──

    private static async Task AssertTokenStreamsMatchAsync(
        string label,
        byte[] payload,
        List<MsgPackTokenSnapshot> expected,
        List<MsgPackTokenSnapshot> actual
    )
    {
        await Assert
            .That(actual.Count)
            .IsEqualTo(expected.Count)
            .Because($"{label}: token count ({payload.Length}-byte payload)");

        for (int i = 0; i < expected.Count; i++)
        {
            await Assert
                .That(actual[i].ToString())
                .IsEqualTo(expected[i].ToString())
                .Because($"{label}: token {i} of {expected.Count}");
        }

        if (expected.Count > 0)
            await Assert
                .That(expected[^1].Consumed)
                .IsEqualTo((long)payload.Length)
                .Because($"{label}: the last token consumed the whole payload");
    }

    private static (string Type, string Message) FailureOfSpanPath(byte[] payload)
    {
        var reader = new MsgPackReader(payload.AsSpan());
        try
        {
            while (reader.Read()) { }
        }
        catch (Exception ex)
        {
            return (
                ex.GetType().Name,
                string.IsNullOrEmpty(ex.Message) ? "<no message>" : ex.Message
            );
        }
        finally
        {
            reader.Dispose();
        }
        return ("<no throw>", "<no throw>");
    }

    private static byte[] NestedOverflowPayload()
    {
        var bytes = new byte[66];
        Array.Fill(bytes, (byte)0x91, 0, 65);
        bytes[65] = 0x00;
        return bytes;
    }
}
