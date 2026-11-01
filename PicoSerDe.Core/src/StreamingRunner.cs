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
    public static async ValueTask<T> RunAsync<TReader, TState, T>(
        Stream stream,
        StreamingFunc<TReader, T> func,
        SerOptions? options,
        StreamStep<TReader, TState, T> step,
        CancellationToken ct
    )
        where TReader : allows ref struct
        where TState : struct
        where T : notnull
    {
        var pipe = PipeReader.Create(stream);
        var state = default(TState);
        T? result = default;
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
                pipe.AdvanceTo(r.Buffer.End);
                return result!;
            }
            if (status == ReadStatus.NeedMoreData)
            {
                if (r.IsCompleted)
                    throw new FormatException("Unexpected end of stream while parsing.");
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
}
