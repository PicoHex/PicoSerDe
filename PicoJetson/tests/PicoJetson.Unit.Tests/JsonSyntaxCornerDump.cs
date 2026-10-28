using System.Text;

namespace PicoJetson.Tests;

/// <summary>
/// The <b>syntax-corner</b> corpus and dump fixture for <see cref="JsonReader"/>'s token-start state
/// machine (<c>ReadCore</c>): comments, commas (leading / doubled / trailing), BOM, whitespace layout and
/// multi-segment sequences — the JSON grammar edges the fused-read guard
/// (<see cref="JsonReaderFusedEquivalenceTests"/> / <see cref="JsonSpanTokenDump"/>) does not carry,
/// because it walks well-formed documents with default options over one segment.
/// <para>
/// <b>Why a pinned dump instead of a dual walk.</b> The fused reads have a reference to compare against
/// (the literal <c>Read() &amp;&amp; TryGetX()</c> combination). A token-start consolidation has none: it
/// rewrites the reference itself. So the guard here is a <b>pinned expectation</b> — the dump of the
/// behaviour measured <i>before</i> the change — and the negative controls are what prove it bites: any
/// slip in the comment check, the comma check or the dispatch changes a line and turns the test red.
/// </para>
/// <para>
/// Static and test-framework free, like the other dumps: the same code runs inside the pinned test and
/// inside the out-of-repo harness that produced the expectations, so a diff can only come from the reader.
/// </para>
/// </summary>
public static class JsonSyntaxCornerDump
{
    /// <summary>Which reader mode a case is walked in.</summary>
    public enum Mode
    {
        /// <summary><c>new JsonReader(ReadOnlySpan&lt;byte&gt;)</c> — the strict, non-resumable path.</summary>
        Span = 0,

        /// <summary><c>new JsonReader(ReadOnlySequence&lt;byte&gt;)</c> — the streaming path.</summary>
        Sequence = 1,
    }

    /// <summary>One case: a payload, the options variant, the mode, the block flag and the segmentation.</summary>
    public readonly record struct SyntaxCase(
        string Name,
        byte[] Payload,
        int Options,
        Mode Mode,
        bool IsFinal,
        int Segment
    );

    // ── option variants (one integer keeps the pinned key short) ──

    /// <summary>No options: comments are <c>Disallow</c>, trailing commas are rejected.</summary>
    public const int OptDefault = 0;

    /// <summary><c>ReadCommentHandling = Skip</c>.</summary>
    public const int OptComments = 1;

    /// <summary><c>AllowTrailingCommas = true</c>.</summary>
    public const int OptTrailing = 2;

    /// <summary>Both of the above.</summary>
    public const int OptBoth = 3;

    /// <summary>The option names as they appear in the pinned keys.</summary>
    public static string OptionName(int variant) =>
        variant switch
        {
            OptDefault => "default",
            OptComments => "comments",
            OptTrailing => "trailing",
            OptBoth => "both",
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

    private static JsonOptions? OptionsOf(int variant) =>
        variant switch
        {
            OptDefault => null,
            OptComments => new JsonOptions { ReadCommentHandling = JsonCommentHandling.Skip },
            OptTrailing => new JsonOptions { AllowTrailingCommas = true },
            OptBoth => new JsonOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

    /// <summary>
    /// The corpus. Every entry names the state-machine decision it exists to pin: a comment that opens a
    /// token, a comment after a comma (which the comma arm does <b>not</b> re-check — the behaviour the
    /// pinned dump must keep), a comma that is legal, a comma that is not, a BOM in both modes, and the
    /// chunk-boundary pause in each of those.
    /// </summary>
    public static IEnumerable<SyntaxCase> Cases()
    {
        // ── line comments ──
        yield return Case(
            "line-comment-before-root",
            "// c\n{\"a\":1}",
            OptComments,
            Mode.Span,
            true,
            0
        );
        yield return Case(
            "line-comment-before-root-seq",
            "// c\n{\"a\":1}",
            OptComments,
            Mode.Sequence,
            true,
            0
        );
        yield return Case("line-comment-crlf", "// c\r\n1", OptComments, Mode.Span, true, 0);
        yield return Case(
            "line-comment-no-newline-eof",
            "1// trailing",
            OptComments,
            Mode.Span,
            true,
            0
        );
        yield return Case(
            "line-comment-no-newline-nonfinal",
            "1// trailing",
            OptComments,
            Mode.Sequence,
            false,
            0
        );
        yield return Case("line-comment-after-value", "1// c\n2", OptComments, Mode.Span, true, 0);
        yield return Case(
            "line-comment-after-comma",
            "[1,// c\n2]",
            OptComments,
            Mode.Span,
            true,
            0
        );

        // ── block comments ──
        yield return Case("block-comment-before-root", "/* c */1", OptComments, Mode.Span, true, 0);
        yield return Case(
            "block-comment-before-root-seq",
            "/* c */1",
            OptComments,
            Mode.Sequence,
            true,
            0
        );
        yield return Case(
            "block-comment-multiline",
            "{\n/* a\nb */\n\"k\":1}",
            OptComments,
            Mode.Span,
            true,
            0
        );
        yield return Case("block-comment-stars", "/* ** */1", OptComments, Mode.Span, true, 0);
        yield return Case("block-comment-nested-ish", "/*/*/1", OptComments, Mode.Span, true, 0);
        yield return Case("block-comment-unterminated", "/* c", OptComments, Mode.Span, true, 0);
        yield return Case(
            "block-comment-unterminated-nonfinal",
            "/* c",
            OptComments,
            Mode.Sequence,
            false,
            0
        );
        yield return Case(
            "block-comment-after-comma",
            "[1,/* c */2]",
            OptComments,
            Mode.Span,
            true,
            0
        );

        // ── a '/' that opens neither a line nor a block comment ──
        yield return Case("slash-alone-skip", "/ 1", OptComments, Mode.Span, true, 0);
        yield return Case("slash-alone-default", "/ 1", OptDefault, Mode.Span, true, 0);
        yield return Case("slash-then-eof-skip", "/", OptComments, Mode.Span, true, 0);
        yield return Case("slash-then-eof-nonfinal", "/", OptComments, Mode.Sequence, false, 0);

        // ── commas: legal, leading, doubled, trailing ──
        yield return Case("comma-legal-array", "[1,2]", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-ws-array", "[ 1 , 2 ]", OptDefault, Mode.Span, true, 0);
        yield return Case(
            "comma-legal-object",
            "{\"a\":1,\"b\":2}",
            OptDefault,
            Mode.Span,
            true,
            0
        );
        yield return Case("comma-leading-array", "[,1]", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-leading-object", "{,\"a\":1}", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-doubled", "[1,,2]", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-trailing-array", "[1,2,]", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-trailing-object", "{\"a\":1,}", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-trailing-allowed", "[1,2,]", OptTrailing, Mode.Span, true, 0);
        yield return Case(
            "comma-trailing-allowed-object",
            "{\"a\":1,}",
            OptTrailing,
            Mode.Span,
            true,
            0
        );
        yield return Case("comma-trailing-ws-allowed", "[1, ]", OptTrailing, Mode.Span, true, 0);
        yield return Case("comma-trailing-ws-disallowed", "[1, ]", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-at-eof", "1,", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-at-eof-allowed", "1,", OptTrailing, Mode.Span, true, 0);
        yield return Case("comma-at-eof-nonfinal", "1,", OptDefault, Mode.Sequence, false, 0);
        yield return Case(
            "comma-at-eof-nonfinal-allowed",
            "1,",
            OptTrailing,
            Mode.Sequence,
            false,
            0
        );
        yield return Case("comma-only", ",", OptDefault, Mode.Span, true, 0);
        yield return Case("comma-then-eof-after-ws", "1,  ", OptDefault, Mode.Span, true, 0);

        // ── BOM ──
        yield return Case("bom-object", "\uFEFF{\"a\":1}", OptDefault, Mode.Span, true, 0);
        yield return Case("bom-object-seq", "\uFEFF{\"a\":1}", OptDefault, Mode.Sequence, true, 0);
        yield return Case(
            "bom-object-seq-nonfinal",
            "\uFEFF{\"a\":1}",
            OptDefault,
            Mode.Sequence,
            false,
            0
        );
        yield return Case("bom-only", "\uFEFF", OptDefault, Mode.Span, true, 0);
        yield return Case("bom-only-seq", "\uFEFF", OptDefault, Mode.Sequence, true, 0);
        yield return Case("bom-then-comment", "\uFEFF// c\n1", OptComments, Mode.Sequence, true, 0);
        yield return Case("not-bom", "\uFEFF\uFEFF1", OptDefault, Mode.Sequence, true, 0);

        // ── whitespace layout ──
        yield return Case(
            "ws-heavy",
            " \t\r\n {  \"a\"  :  [ 1 ,  2 ]  }  \r\n ",
            OptDefault,
            Mode.Span,
            true,
            0
        );
        yield return Case("ws-only", "  \t\n ", OptDefault, Mode.Span, true, 0);
        yield return Case("ws-only-nonfinal", "  \t\n ", OptDefault, Mode.Sequence, false, 0);
        yield return Case("ws-before-comment", "   // c\n1", OptComments, Mode.Span, true, 0);

        // ── multi-segment sequences (the segment boundary moves under PeekByte) ──
        yield return Case("seg-comma-split", "[1,2,3]", OptDefault, Mode.Sequence, true, 2);
        yield return Case("seg-comma-split-3", "[1,2,3]", OptDefault, Mode.Sequence, true, 3);
        yield return Case("seg-comment-split", "[1,/* c */2]", OptComments, Mode.Sequence, true, 2);
        yield return Case(
            "seg-line-comment-split",
            "// c\n[1,2]",
            OptComments,
            Mode.Sequence,
            true,
            2
        );
        yield return Case("seg-trailing-allowed", "[1,2,]", OptTrailing, Mode.Sequence, true, 3);
        yield return Case("seg-ws-split", "[ 1 , 2 ]", OptDefault, Mode.Sequence, true, 3);
        yield return Case("seg-bom-split", "\uFEFF[1,2]", OptDefault, Mode.Sequence, true, 1);
        yield return Case("seg-bom-split-2", "\uFEFF[1,2]", OptDefault, Mode.Sequence, true, 2);
    }

    /// <summary>The pinned key: every input that selects one walk.</summary>
    public static string KeyOf(in SyntaxCase c) =>
        string.Join(
            "/",
            c.Name,
            c.Mode.ToString(),
            "opts=" + OptionName(c.Options),
            "final=" + (c.IsFinal ? "1" : "0"),
            "seg=" + c.Segment
        );

    /// <summary>
    /// The dump: one line per token (type, consumed, depth, token/value offsets, value bytes) — or the
    /// exception the walk ends with — followed by the terminal state. <c>||</c> separates the token stream
    /// from the terminal state so a wrong token cannot hide behind a coincidentally equal counter.
    /// </summary>
    public static string Dump(in SyntaxCase c)
    {
        var sb = new StringBuilder();
        var reader = Open(c);
        int tokens = 0;
        try
        {
            while (reader.Read())
            {
                if (++tokens > 256)
                {
                    sb.Append("<runaway>");
                    break;
                }
                sb.Append(tokens)
                    .Append(':')
                    .Append(reader.TokenType)
                    .Append('@')
                    .Append(reader.BytesConsumed)
                    .Append('/')
                    .Append(reader.Depth)
                    .Append('/')
                    .Append(reader.TokenStart)
                    .Append('-')
                    .Append(reader.TokenValueStart)
                    .Append('-')
                    .Append(reader.TokenValueEnd)
                    .Append('/')
                    .Append(JsonSpanTokenDump.Hex(reader.ValueSpan))
                    .Append(';');
            }
        }
        catch (Exception ex)
        {
            sb.Append("THROW ")
                .Append(ex.GetType().Name)
                .Append(": ")
                .Append(ex.Message)
                .Append(';');
        }
        finally
        {
            reader.Dispose();
        }

        sb.Append("||needs=")
            .Append(reader.NeedsMoreData ? '1' : '0')
            .Append(" depth=")
            .Append(reader.Depth)
            .Append(" consumed=")
            .Append(reader.BytesConsumed);
        return sb.ToString();
    }

    private static JsonReader Open(in SyntaxCase c)
    {
        if (c.Mode == Mode.Span)
            return new JsonReader(
                c.Payload.AsSpan(),
                maxDepth: 256,
                isFinalBlock: c.IsFinal,
                options: OptionsOf(c.Options)
            );

        return new JsonReader(
            Segmented(c.Payload, c.Segment),
            maxDepth: 256,
            isFinalBlock: c.IsFinal,
            options: OptionsOf(c.Options)
        );
    }

    /// <summary>One segment when <paramref name="segment"/> is 0, otherwise fixed-size chunks.</summary>
    public static ReadOnlySequence<byte> Segmented(byte[] payload, int segment)
    {
        if (segment <= 0)
            return new ReadOnlySequence<byte>(payload);

        var segments = new List<byte[]>();
        for (int at = 0; at < payload.Length; at += segment)
            segments.Add(payload.AsSpan(at, Math.Min(segment, payload.Length - at)).ToArray());
        if (segments.Count == 0)
            segments.Add([]);

        Segment? first = null;
        Segment? last = null;
        foreach (var bytes in segments)
        {
            var next = new Segment(
                bytes,
                first is null ? 0 : last!.RunningIndex + last.Memory.Length
            );
            if (first is null)
                first = next;
            else
                last!.SetNext(next);
            last = next;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    private static SyntaxCase Case(
        string name,
        string payload,
        int options,
        Mode mode,
        bool isFinal,
        int segment
    ) => new(name, Encoding.UTF8.GetBytes(payload), options, mode, isFinal, segment);

    /// <summary>The minimal <see cref="ReadOnlySequenceSegment{T}"/> the chunked cases need.</summary>
    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] bytes, long runningIndex)
        {
            Memory = bytes;
            RunningIndex = runningIndex;
        }

        public void SetNext(Segment next) => Next = next;
    }
}
