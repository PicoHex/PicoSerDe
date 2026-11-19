namespace PicoSerDe.Core;

/// <summary>
/// Streaming deserialization delegate over a format reader. The partially
/// built result from the previous chunk is passed back in via
/// <paramref name="partial"/> so state survives chunk boundaries.
/// <para>
/// The delegate returns <see cref="ReadStatus.Success"/> when the value is
/// complete, <see cref="ReadStatus.NeedMoreData"/> when the current chunk ends
/// inside a token/section (the runner refills and resumes from the reader's
/// mark), or <see cref="ReadStatus.EndOfInput"/> when the document holds no
/// value. <typeparamref name="T"/> is constrained to <c>notnull</c> because the
/// partial/result parameters model absence with <c>T?</c>.
/// </para>
/// </summary>
public delegate ReadStatus StreamingFunc<TReader, T>(ref TReader reader, T? partial, out T? result)
    where TReader : allows ref struct
    where T : notnull;

/// <summary>
/// Per-chunk streaming step: constructs the format reader from the chunk,
/// invokes the streaming delegate, and reports the next state on NeedMoreData.
/// </summary>
public delegate ReadStatus StreamStep<TReader, TState, T>(
    ReadOnlySequence<byte> buffer,
    bool isFinalBlock,
    TState state,
    SerOptions? options,
    StreamingFunc<TReader, T> func,
    T? partial,
    out T? result,
    out TState nextState,
    out SequencePosition advanceTo
)
    where TReader : allows ref struct
    where TState : struct
    where T : notnull;

/// <summary>
/// Shared PipeReader streaming loop (state-export strategy). Each format
/// supplies its reader construction + delegate invocation via a step method.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who is on this loop — and why the callers read like copies of each other.</b>
/// Four formats implement <see cref="StreamingFunc{TReader,T}"/>: INI, TOML and YAML
/// (<c>Step&lt;T&gt;</c> in each facade, byte-identical once the reader and state names are
/// replaced) and JSON.
/// Each keeps its own delegate slot (<c>StreamingCache&lt;T&gt;</c>), its own <c>Step&lt;T&gt;</c>
/// and its own <c>DeserializeFromStreamAsync</c> dispatch. That is a deliberate copy, not an
/// un-refactored one: the five formats hold <b>four different streaming policies</b>, so one
/// generic Core type would have to carry a per-format switch —
/// </para>
/// <list type="bullet">
/// <item><description>no delegate registered: INI/TOML/YAML buffer the stream and take the
/// synchronous path, JSON throws <see cref="System.InvalidOperationException"/>;</description></item>
/// <item><description>options reaching the reader: INI/TOML/YAML ignore <see cref="SerOptions"/>
/// (their options only affect <i>writing</i>), while JSON must thread it — <c>MaxDepth</c> and
/// <c>PropertyNameCaseInsensitive</c> change how it reads;</description></item>
/// <item><description>the options parameter itself: two overloads (INI/TOML/YAML) versus one
/// optional argument (JSON).</description></item>
/// </list>
/// <para>
/// <b>MessagePack is not on this loop at all, and it is not an unfinished migration.</b>
/// <c>MsgPackSerializer.StreamingFunc&lt;T&gt;</c> — a nested, public, two-parameter delegate —
/// takes no <c>partial</c> value, so nothing survives a chunk boundary;
/// <c>DeserializeStreamingCore</c> accumulates the stream and re-parses it from the start on
/// every attempt. A different algorithm on a different contract, not a missing parameter.
/// </para>
/// <para>
/// <b>What merging the three text facades into one generic dispatcher costs</b> — measured on a
/// working prototype (built clean, public API byte-identical, full suite green): the three facades
/// go 344 → 295 lines (−49) and a new internal Core file of about 90 lines appears (a dispatch
/// generic over four type parameters, plus a factory abstraction that has to reach back into each
/// reader's state struct to read <c>Position</c>) — a net <i>increase</i>. The floor
/// is the public surface: about 62 of each facade's 112 lines are signatures, XML docs, the format
/// marker and <c>ContentType</c>, which no generic type can emit because the public API is per
/// format. What stays shareable is about 50 lines each.
/// </para>
/// <para>
/// <b>One subtlety the copies share.</b> Each <c>Step&lt;T&gt;</c> calls <c>reader.Dispose()</c>
/// after the delegate returns, so a delegate that throws on malformed input skips it: the
/// reader's <c>ArrayPool</c> rentals and its chunk copy are handed to the GC instead of the pool.
/// Nothing unmanaged leaks, but the pool's fast path degrades under repeated parse failures.
/// </para>
/// </remarks>
public static class StreamingRunner
{
    public static ValueTask<T> RunAsync<TReader, TState, T>(
        Stream stream,
        StreamingFunc<TReader, T> func,
        SerOptions? options,
        StreamStep<TReader, TState, T> step,
        CancellationToken ct
    )
        where TReader : allows ref struct
        where TState : struct
        where T : notnull => RunAsync(stream, func, options, step, null, ct);

    /// <summary>
    /// The loop with the <b>trailing-data gate</b>: <paramref name="consumedOf"/> reports how many
    /// bytes of the current chunk the format's reader consumed, which is what tells the loop where
    /// the document ended and the junk began.
    /// <para>
    /// <b>Why the gate lives on the loop and not in a generated delegate.</b> Three formats had
    /// already agreed with the synchronous path by accident — TOML/YAML/INI only reach
    /// <c>Success</c> once their lexer has scanned to the end of the stream, so a trailing line
    /// fails in their own lexer (<c>Invalid TOML line: expected '='.</c>, <c>Invalid YAML mapping
    /// line: expected ':'.</c>) on both paths. JSON is the one text format whose document has a
    /// closing byte: its delegate returns <c>Success</c> at <c>}</c> and the loop used to discard
    /// everything behind it, so <c>{"N":1} 7</c> threw <c>Unexpected data after the document at
    /// offset 13</c> on the direct path and returned a materialized document on the streaming path.
    /// Putting the gate on the loop fixes that <b>once</b>: a per-format copy would be the next
    /// place where the two paths can drift apart.
    /// </para>
    /// <para>
    /// <b>Only a format whose reader ends the document at a byte passes a count.</b> Today that is
    /// JSON alone, and the reason is measured rather than assumed. The gate compares the reader's
    /// consumed count against raw bytes, so it can only be as good as the gap between <i>consumed</i>
    /// and <i>validated</i>. For JSON the two coincide: the reader stops on the closing byte, and
    /// everything after it is either JSON whitespace or junk — the exact statement the synchronous
    /// path's <c>if (reader.Read()) throw</c> makes. TOML/YAML/INI are not shaped that way: their
    /// <c>Read()</c> rewinds <c>_position</c> to the token start whenever it then scans trailing
    /// ignorable text to end-of-file, so at <c>Success</c> the consumed count stops <b>at the end of
    /// the last token</b> and a trailing comment — legal, skippable, already validated by the lexer —
    /// sits in that gap. Wiring the byte gate to them rejected <c>N = 1\n# note\n</c>, which the
    /// synchronous path accepts: not hypothetical, it is what happened when this overload was first
    /// threaded through all four facades. `Hole6d` in
    /// <c>tests/PicoSerDe.Integration.Tests/StreamTrailingAndHolesTests.cs</c> pins it and goes red
    /// the moment a format that does not qualify passes a count. Their skippable set is grammar, not
    /// bytes, so their arbiter stays their own lexer — which is why they call the five-argument
    /// overload above, and why their parity with the synchronous path is a fact about the lexer
    /// rather than about this gate.
    /// </para>
    /// <para>
    /// <b>The judgment is the synchronous path's.</b> There, the trailing check is
    /// <c>if (reader.Read()) throw</c> executed after the value: it throws when another token
    /// follows the document and stays quiet when only whitespace remains. Here the same statement is
    /// made against bytes instead of tokens — after <c>Success</c>, any byte past the consumed part
    /// that is not JSON whitespace (space, tab, CR, LF) is trailing data.
    /// </para>
    /// <para>
    /// <b>The one stated difference.</b> The message is the same text
    /// (<c>Unexpected data after the document at offset N</c>) and an offset, but <c>N</c> points at
    /// the <b>first byte</b> of the trailing data, while the synchronous path reports its offset
    /// after the offending token was consumed (<c>{"N":1} 7</c> — streaming 8, direct 9). Pointing at
    /// the start is the more useful number, so the difference is documented instead of emulated.
    /// </para>
    /// <para>
    /// <b>It drains.</b> A value that completes before the stream does (chunk boundaries are the
    /// normal case for this API) is only provably the last one at end of stream, so the loop keeps
    /// reading. Every format on this loop already needs the end of the stream before it reports
    /// <c>Success</c> — their <c>NeedsMoreData</c>/<c>Success</c> split is bounded by
    /// <c>isFinalBlock</c> — so draining costs JSON nothing it was not already paying elsewhere and
    /// asks no format to change. A caller who wants <i>one value out of a longer stream</i> wants
    /// <c>DeserializeAsyncEnumerable</c>, which is the API that exists for it; silently dropping the
    /// rest of a stream was the defect, not the feature.
    /// </para>
    /// <para>
    /// <b>Why an internal overload.</b> <see cref="RunAsync{TReader,TState,T}(Stream,StreamingFunc{TReader,T},SerOptions?,StreamStep{TReader,TState,T},CancellationToken)"/>
    /// is public API, and the consumed count is not derivable from the public
    /// <see cref="StreamStep{TReader,TState,T}"/> result — its <c>advanceTo</c> is the reader's
    /// <b>resume</b> position (INI rewinds it to the start of the section it is inside), so the
    /// count has to come from the reader's own state. Rather than widen a public delegate (and the
    /// API-diff gate) for a fix that belongs to one format, the count arrives as a second, internal
    /// entry point; <c>InternalsVisibleTo</c> already covers PicoJetson/PicoToml/PicoYaml/PicoIni, so
    /// a format that grows a closing byte can adopt it without an API change (PicoJetson is the one
    /// that does today).
    /// </para>
    /// <para>
    /// <b>MessagePack is the same defect in a different loop.</b> It does not run here —
    /// <c>MsgPackSerializer.DeserializeStreamingCore</c> re-parses an accumulator — but its reader
    /// carries the identical split: span mode closes the root container with
    /// <c>if (_position &lt; _data.Length) throw TrailingGarbage</c>, and sequence mode deliberately
    /// skips that check (<c>MsgPackReader.ReadSequential</c>, "Sequence mode keeps the streaming
    /// semantics untouched"), so a second value or a stray byte behind the document was rejected by
    /// <c>Deserialize</c> and silently dropped by <c>DeserializeFromStreamAsync</c>. Its own loop now
    /// makes the same byte comparison. The two gates are written up in one place on purpose: the
    /// rule is single — a stream entry point rejects exactly what the byte entry point rejects — and
    /// the next format with a closing byte owes it too.
    /// </para>
    /// </summary>
    internal static async ValueTask<T> RunAsync<TReader, TState, T>(
        Stream stream,
        StreamingFunc<TReader, T> func,
        SerOptions? options,
        StreamStep<TReader, TState, T> step,
        Func<TState, long>? consumedOf,
        CancellationToken ct
    )
        where TReader : allows ref struct
        where TState : struct
        where T : notnull
    {
        var pipe = PipeReader.Create(stream);
        var state = default(TState);
        T? result = default;
        // Absolute offset of the current chunk's first byte: the pipe hands out only what has not
        // been consumed yet, so this is the sum of every byte released to `pipe.AdvanceTo` so far.
        long chunkStart = 0;
        while (true)
        {
            var r = await pipe.ReadAsync(ct);
            var status = step(
                r.Buffer,
                r.IsCompleted,
                state,
                options,
                func,
                result,
                out result,
                out state,
                out var advanceTo
            );
            if (status == ReadStatus.Success)
            {
                if (consumedOf is null)
                {
                    pipe.AdvanceTo(r.Buffer.End);
                    return result!;
                }
                long end = Math.Clamp(consumedOf(state), 0, r.Buffer.Length);
                long hit = IndexOfNonWhitespace(r.Buffer.Slice(end));
                if (hit >= 0)
                    throw new FormatException(
                        $"Unexpected data after the document at offset {chunkStart + end + hit}."
                    );
                bool completed = r.IsCompleted;
                pipe.AdvanceTo(r.Buffer.End);
                chunkStart += r.Buffer.Length;
                while (!completed)
                {
                    var tail = await pipe.ReadAsync(ct);
                    hit = IndexOfNonWhitespace(tail.Buffer);
                    if (hit >= 0)
                        throw new FormatException(
                            $"Unexpected data after the document at offset {chunkStart + hit}."
                        );
                    chunkStart += tail.Buffer.Length;
                    completed = tail.IsCompleted;
                    pipe.AdvanceTo(tail.Buffer.End);
                }
                return result!;
            }
            if (status == ReadStatus.NeedMoreData)
            {
                if (r.IsCompleted)
                    throw new FormatException("Unexpected end of stream while parsing.");
                chunkStart += r.Buffer.Slice(r.Buffer.Start, advanceTo).Length;
                pipe.AdvanceTo(advanceTo, r.Buffer.End);
                continue;
            }
            if (status == ReadStatus.EndOfInput)
                throw new FormatException(
                    "Unexpected end of input: the document contains no value."
                );
            throw new FormatException($"Unexpected parser state: {status}.");
        }
    }

    /// <summary>
    /// Offset of the first byte that is neither JSON whitespace (space, tab, CR, LF) nor absent, or
    /// <c>-1</c> when the sequence holds nothing but whitespace. Deliberately <b>not</b>
    /// <c>byte &lt;= 32</c>: a vertical tab or a form feed is not whitespace to the JSON reader, and
    /// the streaming paths must reject exactly what the synchronous readers reject.
    /// </summary>
    private static long IndexOfNonWhitespace(ReadOnlySequence<byte> data)
    {
        long offset = 0;
        foreach (var segment in data)
        {
            var span = segment.Span;
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (b != (byte)' ' && b != (byte)'\t' && b != (byte)'\r' && b != (byte)'\n')
                    return offset + i;
            }
            offset += span.Length;
        }
        return -1;
    }
}
