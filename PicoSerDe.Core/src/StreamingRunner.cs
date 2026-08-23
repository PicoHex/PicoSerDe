namespace PicoSerDe.Core;

/// <summary>
/// Streaming deserialization delegate over a format reader. The partially
/// built result from the previous chunk is passed back in via
/// <paramref name="partial"/> so state survives chunk boundaries.
/// </summary>
public delegate ReadStatus StreamingFunc<TReader, T>(
    ref TReader reader,
    T? partial,
    out T? result
)
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
            throw new FormatException("Unexpected parser state.");
        }
    }
}
