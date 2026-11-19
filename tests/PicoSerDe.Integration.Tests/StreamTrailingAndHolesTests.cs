using System.Text;

namespace PicoSerDe.Integration.Tests;

public class HoleDoc
{
    public int N { get; set; }
}

public class HoleTagDoc
{
    public string Tail { get; set; } = "";
    public int N { get; set; }
}

/// <summary>Value-type DTO: no streaming delegate, so it takes the generator's direct path.</summary>
public struct HoleStructDoc
{
    public int N { get; set; }
}

/// <summary>
/// A stream that hands out <c>chunk</c> bytes per read and <b>honours cancellation</b>.
/// <para>
/// The cancellation is not decoration: one test below asserts that a broken parser fails
/// <i>fast</i>. A migration that turns the array-mode truncation check into a spin would otherwise
/// read a completed <see cref="MemoryStream"/> for ever (its <c>ReadAsync</c> ignores the token),
/// and the whole run would hang instead of reporting one red test.
/// </para>
/// </summary>
public sealed class HoleChunkedStream(byte[] data, int chunk, CancellationToken ct) : Stream
{
    private int _pos;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position
    {
        get => _pos;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        int n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        int n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _pos);
        data.AsSpan(_pos, n).CopyTo(buffer.Span);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}

/// <summary>
/// The holes the ablation wave-3 C run found on the streaming path, plus the defect underneath two
/// of them: a stream entry point silently accepted trailing data after the document while its
/// byte/span entry point rejected it, in direct contradiction of the README's "streaming shares the
/// semantics" promise. Two formats had that defect — <b>JSON</b> (<c>{"N":1} 7</c>) and
/// <b>MessagePack</b> (a second value, or one stray byte, behind the root container).
/// <para>
/// Holes 1–5 and 6d had <b>guarding code and no assertion</b>: each one is green on today's tree and
/// goes red the moment its guard is removed (the mutation evidence is in
/// <c>.superpowers/sdd/fix-stream/report.md</c>). Hole 6 was the JSON defect and hole 7 is its
/// MessagePack twin; both are red on the pre-fix tree by themselves.
/// </para>
/// <para>
/// <b>Why the four text formats are covered by three different mechanisms, on purpose.</b> The
/// verdict is one for all of them — a document followed by junk is a <see cref="FormatException"/>
/// on both paths — but the arbiter is per format, and the tests below pin <i>which</i> one fires:
/// JSON and MessagePack end their document at a byte, so the shared byte gate in
/// <see cref="PicoSerDe.Core.StreamingRunner"/> (and its MessagePack twin) decides; TOML/YAML/INI
/// have no closing byte and their own lexer decides, exactly as it does on the synchronous path.
/// Handing the byte gate to those three was tried and is wrong: their readers stop counting at the
/// last token, so a legal trailing comment starts the "junk" — <c>Hole6d</c> is the test that keeps
/// that from being re-introduced.
/// </para>
/// </summary>
public class StreamTrailingAndHolesTests
{
    private const string GateMessage = "Unexpected data after the document at offset ";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static MemoryStream Ms(string s) => new(Utf8(s));

    // ── Hole 1: JsonSerializer.cs:286 — JSONL empty-line skip had no assertion ──

    /// <summary>
    /// Blank lines between JSONL records are skipped, not parsed as empty documents. Removing the
    /// <c>if (nlPos &lt;= lineStart) continue;</c> guard feeds a zero-length line to the
    /// deserializer (FormatException) instead of yielding two records.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task Hole1_Jsonl_BlankLines_AreSkipped(CancellationToken ct)
    {
        var got = new List<int>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<HoleDoc>(
                Ms("\n\n{\"N\":1}\n\n{\"N\":2}\n\n"),
                ct: ct
            )
        )
            got.Add(item!.N);

        await Assert.That(got).IsEquivalentTo(new[] { 1, 2 });
    }

    // ── Hole 2: JsonSerializer.cs:265 — no trailing newline on the last JSONL record ──

    /// <summary>
    /// The final record of a JSONL stream does not need a terminating newline: the accumulator is
    /// flushed at end of stream. Dropping that flush (line 265) yields one record instead of two.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task Hole2_Jsonl_LastRecordWithoutNewline_IsYielded(CancellationToken ct)
    {
        var got = new List<int>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<HoleDoc>(
                Ms("{\"N\":1}\n{\"N\":2}"),
                ct: ct
            )
        )
            got.Add(item!.N);

        await Assert.That(got).IsEquivalentTo(new[] { 1, 2 });
    }

    /// <summary>
    /// The same, one byte per read: the flush must survive a chunk boundary that ends exactly on
    /// the last record.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task Hole2b_Jsonl_LastRecordWithoutNewline_Chunked(CancellationToken ct)
    {
        var got = new List<int>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<HoleDoc>(
                new HoleChunkedStream(Utf8("{\"N\":1}\n{\"N\":2}"), 1, ct),
                ct: ct
            )
        )
            got.Add(item!.N);

        await Assert.That(got).IsEquivalentTo(new[] { 1, 2 });
    }

    // ── Hole 3: JsonSerializer.cs:386 — array mode must reject a truncated array ──

    /// <summary>
    /// <c>[{…},{…}</c> — an array whose closing <c>]</c> never arrives — is a malformed document,
    /// not an empty tail. Without the <c>if (done) throw</c> guard the loop re-enters
    /// <c>FillAsync</c> on a completed stream for ever (the mutation recorded in the report), so
    /// this test is also the wall that keeps that regression at 30 s instead of a hung run.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(4096)]
    public async Task Hole3_ArrayMode_TruncatedArray_Throws(int chunk, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        Exception? error = null;
        try
        {
            await foreach (
                var _ in JsonSerializer.DeserializeAsyncEnumerable<HoleDoc>(
                    new HoleChunkedStream(Utf8("[{\"N\":1},{\"N\":2}"), chunk, cts.Token),
                    topLevelValues: false,
                    options: null,
                    ct: cts.Token
                )
            ) { }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await Assert
            .That(error)
            .IsTypeOf<FormatException>()
            .Because(
                $"truncated array must be a FormatException, got {error?.GetType().Name ?? "<none>"}"
            );
    }

    // ── Hole 4: StreamingRunner.cs:105 — the CancellationToken must reach the pipe ──

    /// <summary>
    /// A token that is already cancelled must abort before any parse happens — for <b>every</b>
    /// format that reads a stream, whichever loop it uses (<see cref="PicoSerDe.Core.StreamingRunner"/>
    /// for JSON/TOML/YAML/INI, <c>MsgPackSerializer.DeserializeStreamingCore</c> for MessagePack).
    /// Replacing <c>pipe.ReadAsync(ct)</c> with <c>pipe.ReadAsync(CancellationToken.None)</c> makes
    /// the call succeed (or hang) instead of throwing.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments("json")]
    [Arguments("toml")]
    [Arguments("yaml")]
    [Arguments("ini")]
    [Arguments("msgpack")]
    public async Task Hole4_Streaming_CancelledToken_Throws(string format, CancellationToken ct)
    {
        _ = ct;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Exception? error = null;
        try
        {
            _ = format switch
            {
                "json" => await JsonSerializer.DeserializeFromStreamAsync<HoleDoc>(
                    Ms("{\"N\":1}"),
                    null,
                    cts.Token
                ),
                "toml" => await TomlSerializer.DeserializeFromStreamAsync<HoleDoc>(
                    Ms("N = 1\n"),
                    cts.Token
                ),
                "yaml" => await YamlSerializer.DeserializeFromStreamAsync<HoleDoc>(
                    Ms("N: 1\n"),
                    cts.Token
                ),
                "ini" => await IniSerializer.DeserializeFromStreamAsync<HoleDoc>(
                    Ms("N = 1\n"),
                    cts.Token
                ),
                "msgpack" => await MsgPackSerializer.DeserializeFromStreamAsync<HoleDoc>(
                    new MemoryStream(MsgPackSerializer.SerializeToUtf8Bytes(new HoleDoc { N = 1 })),
                    cts.Token
                ),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
            };
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await Assert
            .That(error)
            .IsAssignableTo<OperationCanceledException>()
            .Because(
                $"{format}: an already-cancelled token must abort, got {error?.GetType().Name ?? "<success>"}"
            );
    }

    // ── Hole 5: JsonSerializerGenerator.cs:3115 — trailing data on the value-type branch ──

    /// <summary>
    /// Struct DTOs have no streaming delegate and take the generator's <b>direct</b> read path,
    /// whose trailing-data check is a separate emission from the streaming-wrapper branch. This is
    /// the only test that pins that second emission: delete it and the sync path silently ignores
    /// the stray value.
    /// </summary>
    [Test]
    public async Task Hole5_StructDto_TrailingData_Throws()
    {
        await Assert
            .That(() => JsonSerializer.Deserialize<HoleStructDoc>(Utf8("{\"N\":1} 7")))
            .Throws<FormatException>();
    }

    // ── Hole 6: the JSON defect — streaming must reject trailing data like the sync path ──

    public static IEnumerable<(string Name, string Json, int Chunk)> TrailingDocs() =>
        [
            ("object-then-number", "{\"N\":1} 7", 4096),
            ("object-then-object", "{\"N\":1}{\"N\":2}", 4096),
            ("multiline-then-number", "{\"N\":1}\n7", 4096),
            ("two-property-then-number", "{\"Tail\":\"END\",\"N\":1} 7", 4096),
            ("garbage-byte", "{\"N\":1} x", 4096),
            // Chunk boundaries: the tail must be seen however the bytes are delivered.
            ("object-then-number", "{\"N\":1} 7", 3),
            ("object-then-number", "{\"N\":1} 7", 9),
            ("object-then-number", "{\"N\":1} 7", 10),
            ("object-then-object", "{\"N\":1}{\"N\":2}", 1),
            ("two-property-then-number", "{\"Tail\":\"END\",\"N\":1} 7", 2),
        ];

    /// <summary>
    /// <b>The defect.</b> <c>{"Tail":"END","N":1} 7</c> is a <see cref="FormatException"/> on the
    /// synchronous path (<c>Unexpected data after the document at offset 22</c>) and was silently
    /// accepted on the streaming path, which returned the first document and discarded the rest —
    /// the README promises the two paths share their semantics.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [MethodDataSource(nameof(TrailingDocs))]
    public async Task Hole6_Streaming_TrailingData_Throws(
        string name,
        string json,
        int chunk,
        CancellationToken ct
    )
    {
        // The synchronous path is the reference: whatever it does, streaming must do.
        Exception? syncError = null;
        try
        {
            _ = JsonSerializer.Deserialize<HoleTagDoc>(Utf8(json));
        }
        catch (Exception ex)
        {
            syncError = ex;
        }
        await Assert
            .That(syncError)
            .IsTypeOf<FormatException>()
            .Because($"{name}: the synchronous reference itself must reject this document");

        Exception? streamError = null;
        try
        {
            _ = await JsonSerializer.DeserializeFromStreamAsync<HoleTagDoc>(
                new HoleChunkedStream(Utf8(json), chunk, ct),
                null,
                ct
            );
        }
        catch (Exception ex)
        {
            streamError = ex;
        }

        await Assert
            .That(streamError)
            .IsTypeOf<FormatException>()
            .Because(
                $"{name} (chunk={chunk}): streaming accepted trailing data the sync path rejects "
                    + $"(got {streamError?.GetType().Name ?? "<no exception, value returned>"})"
            );
    }

    /// <summary>
    /// The other half of hole 6: rejecting trailing data must not reject a document that merely
    /// ends in whitespace, which is what a real file looks like.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments("{\"N\":1}")]
    [Arguments("{\"N\":1}\n")]
    [Arguments("{\"N\":1}\r\n\t \n")]
    [Arguments("  {\"N\":1}  ")]
    public async Task Hole6b_Streaming_WhitespaceTail_IsAccepted(string json, CancellationToken ct)
    {
        var doc = await JsonSerializer.DeserializeFromStreamAsync<HoleTagDoc>(Ms(json), null, ct);
        await Assert.That(doc.N).IsEqualTo(1);
    }

    /// <summary>
    /// A third statement about hole 6, and the one that pins <b>where</b> the rejection comes from:
    /// JSON's trailing-data check is the shared gate in <see cref="PicoSerDe.Core.StreamingRunner"/>,
    /// so the message is the gate's and the offset is the first byte the document did not claim.
    /// <para>
    /// The offset must also be <b>chunk-invariant</b>: it is assembled from a running total of the
    /// bytes released to the pipe, so an off-by-one in that bookkeeping that still throws would be
    /// invisible to every other test here. <c>{"N":1} 7</c> ends at byte 7, so the stray <c>7</c> is
    /// byte 8 however the bytes arrive — and the synchronous path says 9, because it reports after
    /// consuming the offending token (a stated, documented difference).
    /// </para>
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments(4096)]
    [Arguments(11)]
    [Arguments(9)]
    [Arguments(8)]
    [Arguments(3)]
    [Arguments(1)]
    public async Task Hole6e_Json_TrailingData_IsTheSharedGate_AtAChunkInvariantOffset(
        int chunk,
        CancellationToken ct
    )
    {
        Exception? streamError = null;
        try
        {
            _ = await JsonSerializer.DeserializeFromStreamAsync<HoleTagDoc>(
                new HoleChunkedStream(Utf8("{\"N\":1} 7"), chunk, ct),
                null,
                ct
            );
        }
        catch (Exception ex)
        {
            streamError = ex;
        }

        await Assert
            .That((streamError as FormatException)?.Message)
            .IsEqualTo(GateMessage + "8.")
            .Because(
                $"chunk={chunk}: the shared gate must fire, at the first trailing byte (got {streamError?.Message ?? "<no exception>"})"
            );
    }

    /// <summary>
    /// Cross-format counterpart: TOML, YAML and INI go through the same
    /// <see cref="PicoSerDe.Core.StreamingRunner"/>, so whatever the trailing-data gate does it must
    /// do to them the same way the synchronous path does. Each document here is one the format's
    /// own lexer rejects (a bare <c>7</c> is not <c>key = value</c> / <c>key: value</c>), and the
    /// messages are asserted to be the <b>lexer's</b>, not the shared gate's: these three formats
    /// have no closing byte, so "trailing data" is a grammar error there and their lexer is the
    /// arbiter. The shared gate must stay out of their way (see <c>Hole6d</c>).
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments("toml", "N = 1\n7\n", "Invalid TOML line: expected '='.")]
    [Arguments("yaml", "N: 1\n7\n", "Invalid YAML mapping line: expected ':'.")]
    [Arguments("ini", "N = 1\n7\n", "Invalid INI line: expected '='.")]
    public async Task Hole6c_TomlYamlIni_StreamingTrailingData_MatchesSync(
        string format,
        string text,
        string expectedLexerMessage,
        CancellationToken ct
    )
    {
        _ = ct;
        Exception? syncError = null;
        try
        {
            _ = format switch
            {
                "toml" => TomlSerializer.Deserialize<HoleDoc>(Utf8(text)),
                "yaml" => YamlSerializer.Deserialize<HoleDoc>(Utf8(text)),
                "ini" => IniSerializer.Deserialize<HoleDoc>(Utf8(text)),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
            };
        }
        catch (Exception ex)
        {
            syncError = ex;
        }
        await Assert.That(syncError).IsTypeOf<FormatException>();

        Exception? streamError = null;
        try
        {
            _ = format switch
            {
                "toml" => await TomlSerializer.DeserializeFromStreamAsync<HoleDoc>(Ms(text)),
                "yaml" => await YamlSerializer.DeserializeFromStreamAsync<HoleDoc>(Ms(text)),
                "ini" => await IniSerializer.DeserializeFromStreamAsync<HoleDoc>(Ms(text)),
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
            };
        }
        catch (Exception ex)
        {
            streamError = ex;
        }

        await Assert
            .That(streamError)
            .IsTypeOf<FormatException>()
            .Because($"{format}: streaming must reject what the sync path rejects");
        // The verdict is shared; the mechanism is not. If this ever turns into the shared gate's
        // message, the byte gate has been wired to a format that cannot carry it — see Hole6d.
        await Assert
            .That((streamError as FormatException)?.Message)
            .IsEqualTo(expectedLexerMessage)
            .Because(
                $"{format}: the format's own lexer is the arbiter for a trailing line, not the byte gate"
            );
    }

    /// <summary>
    /// The regression the trailing-data gate must not cause: trailing comments and unmapped
    /// members are <b>not</b> trailing data. Their handling must stay identical on both paths.
    /// <para>
    /// This is load-bearing, not decoration. Wiring <c>static s =&gt; s.BytesConsumed</c> into the
    /// PicoToml/PicoYaml/PicoIni facades was tried (the obvious reading of "fix all four at once")
    /// and made exactly these five cases throw <c>Unexpected data after the document at offset
    /// 5/6</c> on the streaming path while the synchronous path returned <c>N = 1</c>: those
    /// readers rewind their position to the last token start before scanning a trailing comment to
    /// end-of-file, so <c>BytesConsumed</c> under-reports the document by exactly the ignorable
    /// tail. Two of these arguments are delivered one byte at a time as well, because a chunk
    /// boundary between the token and the comment is where a "did the document end?" heuristic
    /// would break next.
    /// </para>
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments("toml", "N = 1\n# note\n", 0)]
    [Arguments("toml", "N = 1\n[unknown]\nx = 2\n", 0)]
    [Arguments("yaml", "N: 1\n# note\n", 0)]
    [Arguments("ini", "N = 1\n# note\n", 0)]
    [Arguments("ini", "N = 1\n; note\n", 0)]
    [Arguments("toml", "N = 1\n# note\n", 1)]
    [Arguments("ini", "N = 1\n; note\n", 1)]
    public async Task Hole6d_TomlYamlIni_TrailingCommentOrUnmapped_StillAccepted(
        string format,
        string text,
        int chunk,
        CancellationToken ct
    )
    {
        int sync = format switch
        {
            "toml" => TomlSerializer.Deserialize<HoleDoc>(Utf8(text))!.N,
            "yaml" => YamlSerializer.Deserialize<HoleDoc>(Utf8(text))!.N,
            "ini" => IniSerializer.Deserialize<HoleDoc>(Utf8(text))!.N,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
        Stream stream = chunk == 0 ? Ms(text) : new HoleChunkedStream(Utf8(text), chunk, ct);
        int streaming = format switch
        {
            "toml" => (await TomlSerializer.DeserializeFromStreamAsync<HoleDoc>(stream, ct))!.N,
            "yaml" => (await YamlSerializer.DeserializeFromStreamAsync<HoleDoc>(stream, ct))!.N,
            "ini" => (await IniSerializer.DeserializeFromStreamAsync<HoleDoc>(stream, ct))!.N,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        await Assert.That(streaming).IsEqualTo(sync);
    }

    // ── Hole 7: the same defect in MessagePack's own loop ──

    /// <summary>MessagePack payloads for the two trailing shapes, plus the clean control.</summary>
    private static (byte[] Clean, byte[] SecondValue, byte[] StrayByte) MsgPackPayloads()
    {
        var one = MsgPackSerializer.SerializeToUtf8Bytes(new HoleDoc { N = 1 });
        var second = new byte[one.Length * 2];
        one.CopyTo(second, 0);
        one.CopyTo(second, one.Length);
        var stray = new byte[one.Length + 1];
        one.CopyTo(stray, 0);
        stray[^1] = 0x07; // positive fixint 7: a *valid* MessagePack value behind the document.
        return (one, second, stray);
    }

    /// <summary>
    /// <b>Hole 7.</b> MessagePack has a second streaming loop
    /// (<c>MsgPackSerializer.DeserializeStreamingCore</c> — accumulate, re-parse from the start, so
    /// it never reaches <see cref="PicoSerDe.Core.StreamingRunner"/>), and the same split inside its
    /// reader: span mode closes the root container with
    /// <c>if (_position &lt; _data.Length) throw TrailingGarbage</c>, sequence mode deliberately
    /// skips it. So <c>Deserialize&lt;T&gt;</c> rejected a second value or a stray byte and
    /// <c>DeserializeFromStreamAsync&lt;T&gt;</c> dropped it — the JSON defect, once more.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments(4096, "second-value")]
    [Arguments(4096, "stray-byte")]
    [Arguments(2, "second-value")]
    [Arguments(2, "stray-byte")]
    [Arguments(1, "second-value")]
    [Arguments(1, "stray-byte")]
    public async Task Hole7_MsgPack_StreamingTrailingData_Throws(
        int chunk,
        string shape,
        CancellationToken ct
    )
    {
        var (_, second, stray) = MsgPackPayloads();
        var data = shape == "second-value" ? second : stray;

        // The synchronous reference, and the exact text/offset the stream must reproduce.
        Exception? syncError = null;
        try
        {
            _ = MsgPackSerializer.Deserialize<HoleDoc>(data);
        }
        catch (Exception ex)
        {
            syncError = ex;
        }
        await Assert
            .That(syncError)
            .IsTypeOf<FormatException>()
            .Because($"{shape}: the byte path must reject this payload");

        Exception? streamError = null;
        try
        {
            _ = await MsgPackSerializer.DeserializeFromStreamAsync<HoleDoc>(
                new HoleChunkedStream(data, chunk, ct),
                ct
            );
        }
        catch (Exception ex)
        {
            streamError = ex;
        }

        await Assert
            .That((streamError as FormatException)?.Message)
            .IsEqualTo(syncError!.Message)
            .Because(
                $"{shape} (chunk={chunk}): the stream must reject it with the byte path's message, got "
                    + $"{streamError?.Message ?? "<no exception, value returned>"}"
            );
    }

    /// <summary>
    /// The control for hole 7, and the reason the gate cannot be "any byte at all": a clean
    /// MessagePack document delivered one byte per read must still deserialize. Without this the
    /// drain could reject its own last read and still look like a fix.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    [Arguments(4096)]
    [Arguments(3)]
    [Arguments(2)]
    [Arguments(1)]
    public async Task Hole7b_MsgPack_CleanPayload_Chunked_IsAccepted(
        int chunk,
        CancellationToken ct
    )
    {
        var (clean, _, _) = MsgPackPayloads();
        var doc = await MsgPackSerializer.DeserializeFromStreamAsync<HoleDoc>(
            new HoleChunkedStream(clean, chunk, ct),
            ct
        );
        await Assert.That(doc.N).IsEqualTo(1);
    }
}
