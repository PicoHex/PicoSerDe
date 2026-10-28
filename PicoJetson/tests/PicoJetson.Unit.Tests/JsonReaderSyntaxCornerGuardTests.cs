namespace PicoJetson.Tests;

/// <summary>
/// <b>The token-start guard</b> (plan §3.5: with behaviour unchanged by design, the red has to come from a
/// negative control): every case of <see cref="JsonSyntaxCornerDump"/> is dumped and compared with the
/// <b>pinned expectation</b> measured before the token-start consolidation.
/// <para>
/// <b>What it pins.</b> The comment arm, the comma rules (leading, doubled, trailing, comma-at-EOF, comma
/// before a closing bracket), the BOM in both modes, whitespace-only input, and a segment boundary under
/// each of those — i.e. exactly the decisions <c>ReadCore</c> makes before it dispatches one byte, plus the
/// token stream and terminal state that follow. <see cref="JsonReaderFusedEquivalenceTests"/> cannot see
/// any of it: its corpus is well-formed, single-segment and default-option.
/// </para>
/// <para>
/// <b>How the expectation was produced.</b> <see cref="JsonSyntaxCornerDump.Dump"/> was compiled unchanged
/// into an out-of-repo console harness against the <i>pre-change</i> <c>JsonReader</c> (commit
/// <c>59a6dd2</c>), run over the whole corpus, and its output pasted verbatim below; the same harness was
/// then re-run against the changed reader and the two outputs diffed byte-for-byte. The pasted text is
/// therefore a measurement, not an authored expectation.
/// </para>
/// </summary>
public class JsonReaderSyntaxCornerGuardTests
{
    /// <summary>One line per case: <c>key =&gt; dump</c>. Must stay in lock step with the corpus.</summary>
    private const string Pinned = """
line-comment-before-root/Span/opts=comments/final=1/seg=0 => 1:ObjectStart@6/1/0-0-0/;2:PropertyName@10/1/6-7-8/61;3:Int32@11/1/10-10-11/31;4:ObjectEnd@12/0/11-10-11/31;||needs=0 depth=0 consumed=12
line-comment-before-root-seq/Sequence/opts=comments/final=1/seg=0 => 1:ObjectStart@6/1/0--1--1/;2:PropertyName@10/1/6--1--1/61;3:Int32@11/1/10--1--1/31;4:ObjectEnd@12/0/11--1--1/31;||needs=0 depth=0 consumed=12
line-comment-crlf/Span/opts=comments/final=1/seg=0 => 1:Int32@7/0/0-6-7/31;||needs=0 depth=0 consumed=7
line-comment-no-newline-eof/Span/opts=comments/final=1/seg=0 => 1:Int32@1/0/0-0-1/31;||needs=0 depth=0 consumed=12
line-comment-no-newline-nonfinal/Sequence/opts=comments/final=0/seg=0 => ||needs=1 depth=0 consumed=0
line-comment-after-value/Span/opts=comments/final=1/seg=0 => 1:Int32@1/0/0-0-1/31;2:Int32@7/0/1-6-7/32;||needs=0 depth=0 consumed=7
line-comment-after-comma/Span/opts=comments/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;THROW FormatException: Unexpected byte 0x2F at offset 3;||needs=0 depth=1 consumed=3
block-comment-before-root/Span/opts=comments/final=1/seg=0 => 1:Int32@8/0/0-7-8/31;||needs=0 depth=0 consumed=8
block-comment-before-root-seq/Sequence/opts=comments/final=1/seg=0 => 1:Int32@8/0/0--1--1/31;||needs=0 depth=0 consumed=8
block-comment-multiline/Span/opts=comments/final=1/seg=0 => 1:ObjectStart@1/1/0-0-0/;2:PropertyName@16/1/1-13-14/6B;3:Int32@17/1/16-16-17/31;4:ObjectEnd@18/0/17-16-17/31;||needs=0 depth=0 consumed=18
block-comment-stars/Span/opts=comments/final=1/seg=0 => 1:Int32@9/0/0-8-9/31;||needs=0 depth=0 consumed=9
block-comment-nested-ish/Span/opts=comments/final=1/seg=0 => 1:Int32@6/0/0-5-6/31;||needs=0 depth=0 consumed=6
block-comment-unterminated/Span/opts=comments/final=1/seg=0 => THROW FormatException: Unterminated block comment at offset 4;||needs=0 depth=0 consumed=4
block-comment-unterminated-nonfinal/Sequence/opts=comments/final=0/seg=0 => ||needs=1 depth=0 consumed=4
block-comment-after-comma/Span/opts=comments/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;THROW FormatException: Unexpected byte 0x2F at offset 3;||needs=0 depth=1 consumed=3
slash-alone-skip/Span/opts=comments/final=1/seg=0 => THROW FormatException: Unexpected byte 0x2F at offset 0;||needs=0 depth=0 consumed=1
slash-alone-default/Span/opts=default/final=1/seg=0 => THROW FormatException: Unexpected byte 0x2F at offset 0;||needs=0 depth=0 consumed=0
slash-then-eof-skip/Span/opts=comments/final=1/seg=0 => ||needs=0 depth=0 consumed=1
slash-then-eof-nonfinal/Sequence/opts=comments/final=0/seg=0 => ||needs=1 depth=0 consumed=0
comma-legal-array/Span/opts=default/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;3:Int32@4/1/2-3-4/32;4:ArrayEnd@5/0/4-3-4/32;||needs=0 depth=0 consumed=5
comma-ws-array/Span/opts=default/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@3/1/1-2-3/31;3:Int32@7/1/3-6-7/32;4:ArrayEnd@9/0/7-6-7/32;||needs=0 depth=0 consumed=9
comma-legal-object/Span/opts=default/final=1/seg=0 => 1:ObjectStart@1/1/0-0-0/;2:PropertyName@5/1/1-2-3/61;3:Int32@6/1/5-5-6/31;4:PropertyName@11/1/6-8-9/62;5:Int32@12/1/11-11-12/32;6:ObjectEnd@13/0/12-11-12/32;||needs=0 depth=0 consumed=13
comma-leading-array/Span/opts=default/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;THROW FormatException: Unexpected comma at offset 1;||needs=0 depth=1 consumed=1
comma-leading-object/Span/opts=default/final=1/seg=0 => 1:ObjectStart@1/1/0-0-0/;THROW FormatException: Unexpected comma at offset 1;||needs=0 depth=1 consumed=1
comma-doubled/Span/opts=default/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;THROW FormatException: Unexpected byte 0x2C at offset 3;||needs=0 depth=1 consumed=3
comma-trailing-array/Span/opts=default/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;3:Int32@4/1/2-3-4/32;THROW FormatException: Trailing comma before closing bracket;||needs=0 depth=1 consumed=5
comma-trailing-object/Span/opts=default/final=1/seg=0 => 1:ObjectStart@1/1/0-0-0/;2:PropertyName@5/1/1-2-3/61;3:Int32@6/1/5-5-6/31;THROW FormatException: Trailing comma before closing bracket;||needs=0 depth=1 consumed=7
comma-trailing-allowed/Span/opts=trailing/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;3:Int32@4/1/2-3-4/32;4:ArrayEnd@6/0/4-3-4/32;||needs=0 depth=0 consumed=6
comma-trailing-allowed-object/Span/opts=trailing/final=1/seg=0 => 1:ObjectStart@1/1/0-0-0/;2:PropertyName@5/1/1-2-3/61;3:Int32@6/1/5-5-6/31;4:ObjectEnd@8/0/6-5-6/31;||needs=0 depth=0 consumed=8
comma-trailing-ws-allowed/Span/opts=trailing/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;3:ArrayEnd@5/0/2-1-2/31;||needs=0 depth=0 consumed=5
comma-trailing-ws-disallowed/Span/opts=default/final=1/seg=0 => 1:ArrayStart@1/1/0-0-0/;2:Int32@2/1/1-1-2/31;THROW FormatException: Trailing comma before closing bracket;||needs=0 depth=1 consumed=4
comma-at-eof/Span/opts=default/final=1/seg=0 => 1:Int32@1/0/0-0-1/31;THROW FormatException: Trailing comma at end of document;||needs=0 depth=0 consumed=2
comma-at-eof-allowed/Span/opts=trailing/final=1/seg=0 => 1:Int32@1/0/0-0-1/31;||needs=0 depth=0 consumed=2
comma-at-eof-nonfinal/Sequence/opts=default/final=0/seg=0 => ||needs=1 depth=0 consumed=0
comma-at-eof-nonfinal-allowed/Sequence/opts=trailing/final=0/seg=0 => ||needs=1 depth=0 consumed=0
comma-only/Span/opts=default/final=1/seg=0 => THROW FormatException: Unexpected comma at offset 0;||needs=0 depth=0 consumed=0
comma-then-eof-after-ws/Span/opts=default/final=1/seg=0 => 1:Int32@1/0/0-0-1/31;THROW FormatException: Trailing comma at end of document;||needs=0 depth=0 consumed=4
bom-object/Span/opts=default/final=1/seg=0 => 1:ObjectStart@1/1/0-0-0/;2:PropertyName@5/1/1-2-3/61;3:Int32@6/1/5-5-6/31;4:ObjectEnd@7/0/6-5-6/31;||needs=0 depth=0 consumed=7
bom-object-seq/Sequence/opts=default/final=1/seg=0 => 1:ObjectStart@4/1/0--1--1/;2:PropertyName@8/1/4--1--1/61;3:Int32@9/1/8--1--1/31;4:ObjectEnd@10/0/9--1--1/31;||needs=0 depth=0 consumed=10
bom-object-seq-nonfinal/Sequence/opts=default/final=0/seg=0 => 1:ObjectStart@4/1/0--1--1/;2:PropertyName@8/1/4--1--1/61;3:Int32@9/1/8--1--1/31;4:ObjectEnd@10/0/9--1--1/31;||needs=1 depth=0 consumed=10
bom-only/Span/opts=default/final=1/seg=0 => ||needs=0 depth=0 consumed=0
bom-only-seq/Sequence/opts=default/final=1/seg=0 => ||needs=0 depth=0 consumed=3
bom-then-comment/Sequence/opts=comments/final=1/seg=0 => 1:Int32@9/0/0--1--1/31;||needs=0 depth=0 consumed=9
not-bom/Sequence/opts=default/final=1/seg=0 => THROW FormatException: Unexpected byte 0xEF at offset 3;||needs=0 depth=0 consumed=3
ws-heavy/Span/opts=default/final=1/seg=0 => 1:ObjectStart@6/1/0-0-0/;2:PropertyName@14/1/6-9-10/61;3:ArrayStart@17/2/14-9-10/61;4:Int32@19/2/17-18-19/31;5:Int32@24/2/19-23-24/32;6:ArrayEnd@26/1/24-23-24/32;7:ObjectEnd@29/0/26-23-24/32;||needs=0 depth=0 consumed=34
ws-only/Span/opts=default/final=1/seg=0 => ||needs=0 depth=0 consumed=5
ws-only-nonfinal/Sequence/opts=default/final=0/seg=0 => ||needs=1 depth=0 consumed=5
ws-before-comment/Span/opts=comments/final=1/seg=0 => 1:Int32@9/0/0-8-9/31;||needs=0 depth=0 consumed=9
seg-comma-split/Sequence/opts=default/final=1/seg=2 => 1:ArrayStart@1/1/0--1--1/;2:Int32@2/1/1--1--1/31;3:Int32@4/1/2--1--1/32;4:Int32@6/1/4--1--1/33;5:ArrayEnd@7/0/6--1--1/33;||needs=0 depth=0 consumed=7
seg-comma-split-3/Sequence/opts=default/final=1/seg=3 => 1:ArrayStart@1/1/0--1--1/;2:Int32@2/1/1--1--1/31;3:Int32@4/1/2--1--1/32;4:Int32@6/1/4--1--1/33;5:ArrayEnd@7/0/6--1--1/33;||needs=0 depth=0 consumed=7
seg-comment-split/Sequence/opts=comments/final=1/seg=2 => 1:ArrayStart@1/1/0--1--1/;2:Int32@2/1/1--1--1/31;THROW FormatException: Unexpected byte 0x2F at offset 3;||needs=0 depth=1 consumed=3
seg-line-comment-split/Sequence/opts=comments/final=1/seg=2 => 1:ArrayStart@6/1/0--1--1/;2:Int32@7/1/6--1--1/31;3:Int32@9/1/7--1--1/32;4:ArrayEnd@10/0/9--1--1/32;||needs=0 depth=0 consumed=10
seg-trailing-allowed/Sequence/opts=trailing/final=1/seg=3 => 1:ArrayStart@1/1/0--1--1/;2:Int32@2/1/1--1--1/31;3:Int32@4/1/2--1--1/32;4:ArrayEnd@6/0/4--1--1/32;||needs=0 depth=0 consumed=6
seg-ws-split/Sequence/opts=default/final=1/seg=3 => 1:ArrayStart@1/1/0--1--1/;2:Int32@3/1/1--1--1/31;3:Int32@7/1/3--1--1/32;4:ArrayEnd@9/0/7--1--1/32;||needs=0 depth=0 consumed=9
seg-bom-split/Sequence/opts=default/final=1/seg=1 => 1:ArrayStart@4/1/0--1--1/;2:Int32@5/1/4--1--1/31;3:Int32@7/1/5--1--1/32;4:ArrayEnd@8/0/7--1--1/32;||needs=0 depth=0 consumed=8
seg-bom-split-2/Sequence/opts=default/final=1/seg=2 => 1:ArrayStart@4/1/0--1--1/;2:Int32@5/1/4--1--1/31;3:Int32@7/1/5--1--1/32;4:ArrayEnd@8/0/7--1--1/32;||needs=0 depth=0 consumed=8
""";

    private static Dictionary<string, string> Expected()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Pinned.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            int at = line.IndexOf(" => ", StringComparison.Ordinal);
            map[line[..at]] = line[(at + 4)..];
        }
        return map;
    }

    /// <summary>The guard: every case's dump is unchanged, and the corpus is not degenerate.</summary>
    [Test]
    public async Task EverySyntaxCornerDump_MatchesThePinnedExpectation()
    {
        var expected = Expected();
        var problems = new List<string>();
        var compared = 0;
        foreach (var c in JsonSyntaxCornerDump.Cases())
        {
            var key = JsonSyntaxCornerDump.KeyOf(c);
            if (!expected.TryGetValue(key, out var want))
            {
                problems.Add(
                    $"{key}: no pinned expectation (the corpus grew without its expectation)"
                );
                continue;
            }
            var got = JsonSyntaxCornerDump.Dump(c);
            compared++;
            if (got != want)
                problems.Add($"{key}\n    pinned: {want}\n    actual: {got}");
        }

        // A pinned entry no case produces would silently stop guarding anything.
        var produced = JsonSyntaxCornerDump
            .Cases()
            .Select(c => JsonSyntaxCornerDump.KeyOf(c))
            .ToHashSet();
        problems.AddRange(
            expected
                .Keys.Where(k => !produced.Contains(k))
                .Select(k => $"{k}: pinned but not produced")
        );

        await Assert
            .That(string.Join("\n", problems))
            .IsEqualTo("")
            .Because("pinned syntax corpus");
        await Assert
            .That(compared)
            .IsEqualTo(expected.Count)
            .Because("every pinned case was walked");
        await Assert
            .That(compared)
            .IsGreaterThanOrEqualTo(50)
            .Because("the syntax-corner corpus must stay substantive (measured: 56)");
    }

    /// <summary>
    /// <b>The shared prologue's paused-reader gate, pinned directly.</b> Once a read has paused at a chunk
    /// boundary the reader is sticky: <see cref="JsonReader.Read"/> answers <see langword="false"/> and sets
    /// <c>NeedsMoreData</c> for every further call. Every fused span read shares one prologue
    /// (<c>TryScalarStartSpan</c>), and that prologue must decline in this state instead of scanning bytes
    /// that are, by contract, not readable yet — a <see langword="false"/> from it is what makes the caller
    /// fall back to the reference and see the same pause.
    /// <para>
    /// The payloads put a <b>complete-looking value</b> at the pause point on purpose: with only a truncated
    /// tail there would be nothing for a faulty prologue to serve, so the gate could rot silently.
    /// </para>
    /// </summary>
    [Test]
    public async Task PausedReader_IsNotServedByAnyFusedRead()
    {
        var problems = new List<string>();
        // Each ends inside a token that the reader must treat as incomplete while isFinalBlock is false:
        // a number with no delimiter, a string with no closing quote, an array with an open element.
        string[] payloads = ["[1,999", "[\"ab", "[1,2", "{ \"a\": 7"];
        foreach (var text in payloads)
        {
            var reader = new JsonReader(
                new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(text)),
                maxDepth: 256,
                isFinalBlock: false
            );
            try
            {
                while (reader.Read()) { }
                if (!reader.NeedsMoreData)
                {
                    problems.Add($"{text}: expected a chunk-boundary pause, got end of document");
                    continue;
                }

                long consumed = reader.BytesConsumed;
                bool i32 = reader.TryReadInt32(out _);
                bool i64 = reader.TryReadInt64(out _);
                bool f64 = reader.TryReadFloat64(out _);
                bool b = reader.TryReadBool(out _);
                if (i32 || i64 || f64 || b)
                    problems.Add(
                        $"{text}: a fused read served a paused reader (i32={i32} i64={i64} f64={f64} bool={b})"
                    );
                if (reader.BytesConsumed != consumed)
                    problems.Add($"{text}: the cursor moved {consumed} -> {reader.BytesConsumed}");
            }
            finally
            {
                reader.Dispose();
            }
        }

        await Assert.That(string.Join("; ", problems)).IsEqualTo("").Because("paused readers");
    }

    /// <summary>
    /// The corpus must keep reaching each token-start decision, or the guard would be green for the wrong
    /// reason. Pinned per family so dropping a case is a named failure instead of a silent shrink.
    /// </summary>
    [Test]
    public async Task Corpus_ReachesEveryTokenStartDecision()
    {
        var dumps = JsonSyntaxCornerDump
            .Cases()
            .Select(c => (Case: c, Dump: JsonSyntaxCornerDump.Dump(c)))
            .ToList();
        var all = string.Join("|", dumps.Select(d => d.Dump));

        string[] families =
        [
            "Unexpected byte 0x2F", // a '/' that opens no comment
            "Unterminated block comment", // EOF inside /* */
            "Unexpected comma", // leading comma
            "Unexpected byte 0x2C", // doubled comma — the dispatch's default arm
            "Trailing comma before closing bracket",
            "Trailing comma at end of document",
            "Unexpected byte 0xEF", // a second BOM is not stripped
        ];
        var problems = families.Where(f => !all.Contains(f, StringComparison.Ordinal)).ToList();

        // The behaviour the comma arm must NOT acquire: a comment after a comma is not skipped.
        var afterComma = dumps
            .Where(d => d.Case.Name is "line-comment-after-comma" or "block-comment-after-comma")
            .Select(d => d.Dump);
        if (
            !afterComma.All(d =>
                d.Contains("THROW FormatException: Unexpected byte 0x2F", StringComparison.Ordinal)
            )
        )
            problems.Add("comment-after-comma");

        // Decisions that are only visible as "no throw" must be pinned too.
        int trailingAllowedEndTokens = dumps.Count(d =>
            d.Case.Options is JsonSyntaxCornerDump.OptTrailing or JsonSyntaxCornerDump.OptBoth
            && d.Dump.Contains("ArrayEnd", StringComparison.Ordinal)
        );
        int needsMoreDataCases = dumps.Count(d =>
            d.Dump.EndsWith("needs=1", StringComparison.Ordinal)
            || d.Dump.Contains("needs=1 ", StringComparison.Ordinal)
        );
        int bomStripped = dumps.Count(d =>
            d.Case.Name.StartsWith("bom-object", StringComparison.Ordinal)
            && d.Dump.StartsWith("1:ObjectStart@", StringComparison.Ordinal)
        );
        int multiSegment = dumps.Count(d => d.Case.Segment > 0);

        await Assert
            .That(string.Join(",", problems))
            .IsEqualTo("")
            .Because("token-start decision families");
        await Assert
            .That(trailingAllowedEndTokens)
            .IsGreaterThanOrEqualTo(3)
            .Because("AllowTrailingCommas end tokens");
        await Assert
            .That(needsMoreDataCases)
            .IsGreaterThanOrEqualTo(5)
            .Because("chunk-boundary pauses");
        await Assert
            .That(bomStripped)
            .IsGreaterThanOrEqualTo(2)
            .Because("BOM stripped in both modes");
        await Assert
            .That(multiSegment)
            .IsGreaterThanOrEqualTo(6)
            .Because("multi-segment sequences");
    }
}
