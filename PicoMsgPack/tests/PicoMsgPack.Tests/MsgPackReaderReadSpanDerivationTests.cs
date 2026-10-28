using System.Runtime.CompilerServices;

namespace PicoMsgPack.Tests;

/// <summary>
/// Task 4e drift guard: <c>ReadSpan</c> is not a hand-written twin of
/// <c>ReadSequential</c>, it is <c>ReadSequential</c> with a fixed, listed set of
/// textual substitutions applied (the rules live in
/// <c>PicoMsgPack/tools/readspan/derive_readspan.py</c>). That claim is the only
/// reason two ~200-line copies of the tokenizer are allowed to exist, so it is
/// asserted here on every <c>dotnet test</c> instead of living only in a script
/// and a report.
/// <para>
/// The test reads <c>MsgPackReader.cs</c>, extracts both method bodies and demands
/// <c>Derive(ReadSequential) == ReadSpan</c>. The Python tooling is the printable,
/// diff-producing version of the same check; Python is not installed everywhere,
/// so the rules are re-implemented below — and the two implementations being
/// independent is intentional: they cross-check each other.
/// </para>
/// <para>
/// What it catches: any edit to one body that the rules do not produce on their
/// own — a new token arm added to only one path, a changed token type / length /
/// helper in only one path, a re-ordered statement, a dropped guard. What it does
/// <b>not</b> judge is whether the two paths are <i>behaviourally</i> equivalent:
/// change both bodies the same way and this test passes (correctly — that is a
/// deliberate change, not drift). Behaviour is
/// <see cref="MsgPackReaderSpanSequenceEquivalenceTests"/>' job.
/// </para>
/// </summary>
public class MsgPackReaderReadSpanDerivationTests
{
    private const string SequentialSignature = "private bool ReadSequential()";
    private const string SpanSignature = "private bool ReadSpan()";
    private const string ProvenanceMarker = "Mechanically derived from the original Read() body by";
    private const string NoDifference = "<identical>";

    [Test]
    public async Task ReadSpan_IsDerivedFromReadSequential_andHasNotDrifted()
    {
        var source = Normalize(File.ReadAllText(ResolveSourcePath(TestFile())));
        var sequential = MethodBody(source, SequentialSignature);
        var (span, hadProvenance) = StripProvenance(MethodBody(source, SpanSignature));

        await Assert
            .That(hadProvenance)
            .IsTrue()
            .Because(
                "ReadSpan must keep the provenance comment that points at "
                    + "PicoMsgPack/tools/readspan/derive_readspan.py; without it the next reader has no "
                    + "way to know where the second tokenizer came from"
            );
        await Assert
            .That(span.Length)
            .IsGreaterThan(4000)
            .Because("the extracted body is the whole tokenizer, not an empty match");
        await Assert
            .That(sequential.Length)
            .IsGreaterThan(4000)
            .Because("the extracted body is the whole tokenizer, not an empty match");

        var derived = Derive(sequential);

        await Assert
            .That(derived == sequential)
            .IsFalse()
            .Because("the rules must actually change something, or the comparison is vacuous");

        var difference = FirstDifference(span, derived);

        await Assert
            .That(difference == NoDifference)
            .IsTrue()
            .Because(
                difference == NoDifference
                    ? "ReadSpan is derive(ReadSequential), as claimed"
                    : "ReadSpan must be exactly derive(ReadSequential): edit ReadSequential (and the rule list "
                        + "in PicoMsgPack/tools/readspan/derive_readspan.py), then regenerate with "
                        + "`python PicoMsgPack/tools/readspan/derive_readspan.py --write` — never patch "
                        + "ReadSpan by hand. "
                        + difference
            );
    }

    /// <summary>
    /// The structural half of the same claim, kept separate so a failure says
    /// <i>which</i> kind of drift happened: the derivation equality above fails on
    /// any textual difference, these fail only when a mode-branched helper or an
    /// <c>_isSequence</c> test has leaked into the span path (or the span access
    /// pattern has leaked into the sequence path).
    /// </summary>
    [Test]
    public async Task SpanPath_UsesNoSequenceStateMachineHelpers()
    {
        var source = Normalize(File.ReadAllText(ResolveSourcePath(TestFile())));
        var span = MethodBody(source, SpanSignature);
        var sequential = MethodBody(source, SequentialSignature);

        foreach (
            var forbidden in new[]
            {
                "_isSequence",
                "IsAtEnd()",
                "PeekByte()",
                "AdvanceByte()",
                "ReadBytes(",
                "ReadByteLen(",
                "ReadString(",
            }
        )
        {
            await Assert
                .That(span.Contains(forbidden, StringComparison.Ordinal))
                .IsFalse()
                .Because($"ReadSpan is the span path: it must not contain `{forbidden}`");
        }

        foreach (
            var expected in new[] { "_isSequence", "IsAtEnd()", "PeekByte()", "AdvanceByte()" }
        )
        {
            await Assert
                .That(sequential.Contains(expected, StringComparison.Ordinal))
                .IsTrue()
                .Because(
                    $"ReadSequential is the sequence path: it must still contain `{expected}`"
                );
        }

        await Assert
            .That(span.Contains("_data[_position]", StringComparison.Ordinal))
            .IsTrue()
            .Because("the span path reads the buffer directly");
    }

    // ── derivation rules (mirror of derive_readspan.py SUBSTITUTIONS / HELPER_RENAMES) ──

    private static readonly (string Before, string After, int? Count, string Label)[] Rules =
    [
        // 1. The trailing-garbage verdict is span-mode only, and its throw moves
        //    out of line (same exception type, same message).
        (
            Lines(
                "            if (_depth == 0 && !_isSequence && _position < _data.Length)",
                "                throw new FormatException(",
                "                    $\"Unexpected data after the document at offset {_position}\"",
                "                );",
                ""
            ),
            Lines(
                "            if (_depth == 0 && _position < _data.Length)",
                "                throw TrailingGarbage(_position);",
                ""
            ),
            1,
            "#1 trailing-garbage verdict"
        ),
        // 2. `IsAtEnd()` is `_position >= _data.Length` for a span-mode buffer.
        ("if (IsAtEnd())", "if (_position >= _data.Length)", 1, "#2 end-of-buffer test"),
        // 3. The streaming partial-token guard is gated on `!_isFinalBlock &&
        //    _isSequence`: unreachable in span mode.
        (
            Lines(
                "        // Streaming sequence mode: refuse to tokenize a partial value whose",
                "        // payload bytes have not fully arrived yet.",
                "        if (!_isFinalBlock && _isSequence)",
                "        {",
                "            int tokenLength = PeekTokenLength();",
                "            if (tokenLength < 0 || _seqReader.Remaining < tokenLength)",
                "            {",
                "                _needsMoreData = true;",
                "                return false;",
                "            }",
                "        }",
                "",
                ""
            ),
            "",
            1,
            "#3 streaming partial-token guard"
        ),
        // 4. Tag byte: read, then advance (the buffer was checked to be non-empty).
        (
            Lines("        var b = PeekByte();", "        AdvanceByte(); // consume tag byte", ""),
            Lines(
                "        var b = _data[_position];",
                "        _position++; // consume tag byte",
                ""
            ),
            1,
            "#4 tag byte"
        ),
        // 5. Extension tag byte: same two statements, so a truncated fixext still
        //    throws from the read before `_position` moves. The count is not pinned
        //    to today's 8 arms: a new extension arm written with this shape is
        //    covered without editing this list (the Python generator does pin it,
        //    as a safety net while it rewrites the file).
        (
            Lines(
                "                        _tag = PeekByte();",
                "                        AdvanceByte();",
                ""
            ),
            Lines(
                "                        _tag = _data[_position];",
                "                        _position++;",
                ""
            ),
            null,
            "#5 extension tag byte"
        ),
        // 6. Unknown tag byte: the throw moves out of line, same message.
        (
            Lines(
                "                        throw new FormatException(",
                "                            $\"Unknown MsgPack byte 0x{b:X2} at offset {BytesConsumed}\"",
                "                        );",
                ""
            ),
            Lines("                        throw UnknownByte(b, _position);", ""),
            1,
            "#6 unknown tag byte"
        ),
    ];

    private static readonly (string Before, string After)[] HelperRenames =
    [
        ("ReadBytes(", "ReadBytesSpan("),
        ("ReadByteLen(", "ReadByteLenSpan("),
        ("ReadString(", "ReadStringSpan("),
    ];

    /// <summary>Apply the rule list to a <c>ReadSequential</c> body.</summary>
    private static string Derive(string sequentialBody)
    {
        var body = Replace(
            sequentialBody,
            "    " + SequentialSignature,
            "    " + SpanSignature,
            1,
            "signature"
        );

        foreach (var (before, after, count, label) in Rules)
            body = Replace(body, before, after, count, label);

        foreach (var (before, after) in HelperRenames)
            body = body.Replace(before, after, StringComparison.Ordinal);

        return body;
    }

    private static string Replace(
        string body,
        string before,
        string after,
        int? expectedCount,
        string label
    )
    {
        var found = CountOccurrences(body, before);
        if (found == 0)
            throw new InvalidOperationException(
                $"derivation rule {label} matched nothing in ReadSequential, so the rule no longer "
                    + $"describes the code: {Show(before)}"
            );
        if (expectedCount is not null && found != expectedCount)
            throw new InvalidOperationException(
                $"derivation rule {label} expected {expectedCount} occurrence(s), found {found}: "
                    + Show(before)
            );
        return body.Replace(before, after, StringComparison.Ordinal);
    }

    // ── source-text helpers ──

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(pattern, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += pattern.Length;
        }
        return count;
    }

    /// <summary>The <paramref name="signature"/> line through its closing <c>    }</c> line.</summary>
    private static string MethodBody(string source, string signature)
    {
        var lines = source.Split('\n');
        var start = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i].Trim(), signature, StringComparison.Ordinal))
                continue;
            if (start >= 0)
                throw new InvalidOperationException(
                    $"{signature} appears more than once in MsgPackReader.cs"
                );
            start = i;
        }
        if (start < 0)
            throw new InvalidOperationException($"could not find {signature} in MsgPackReader.cs");

        var end = start + 1;
        while (end < lines.Length && lines[end] != "    }")
            end++;
        if (end == lines.Length)
            throw new InvalidOperationException($"no closing '    }}' for {signature}");

        return string.Join("\n", lines[start..(end + 1)]);
    }

    /// <summary>
    /// Drop the provenance comment block (the marker line plus the <c>//</c> lines
    /// directly below it): rewording documentation must never look like drift.
    /// </summary>
    private static (string Body, bool Found) StripProvenance(string body)
    {
        var lines = body.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(ProvenanceMarker, StringComparison.Ordinal))
                continue;

            var end = i + 1;
            while (
                end < lines.Length
                && lines[end].TrimStart().StartsWith("//", StringComparison.Ordinal)
            )
                end++;

            return (string.Join("\n", lines[..i].Concat(lines[end..])), true);
        }
        return (body, false);
    }

    private static string FirstDifference(string expected, string actual)
    {
        var expectedLines = expected.Split('\n');
        var actualLines = actual.Split('\n');
        var shared = Math.Min(expectedLines.Length, actualLines.Length);

        for (var i = 0; i < shared; i++)
        {
            if (string.Equals(expectedLines[i], actualLines[i], StringComparison.Ordinal))
                continue;
            return $"line {i + 1} of the body differs\n  ReadSpan: {Show(expectedLines[i])}"
                + $"\n  derived : {Show(actualLines[i])}";
        }

        if (expectedLines.Length == actualLines.Length)
            return NoDifference;

        var longer = expectedLines.Length > actualLines.Length ? expectedLines : actualLines;
        return $"the bodies have different lengths: ReadSpan has {expectedLines.Length} lines, "
            + $"derived has {actualLines.Length}; the first unpaired line is "
            + Show(longer[shared]);
    }

    private static string Show(string line) =>
        "\"" + line.Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static string TestFile([CallerFilePath] string path = "") => path;

    /// <summary>
    /// Locates <c>PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs</c>: first relative to
    /// this test file (the compiler bakes in its absolute path), then by walking up
    /// from the test binary and the working directory, so the guard also works from
    /// a published test host that lives outside the checkout.
    /// </summary>
    private static string ResolveSourcePath(string testFile)
    {
        const string relative = "src/PicoMsgPack/MsgPackReader.cs";
        var candidates = new List<string>();

        var testDirectory = string.IsNullOrEmpty(testFile) ? null : Path.GetDirectoryName(testFile);
        if (!string.IsNullOrEmpty(testDirectory))
            candidates.Add(Path.GetFullPath(Path.Combine(testDirectory, "..", "..", relative)));

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = start;
            for (var depth = 0; depth < 12 && !string.IsNullOrEmpty(directory); depth++)
            {
                candidates.Add(Path.Combine(directory, "PicoMsgPack", relative));
                directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            }
        }

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
                return full;
        }

        throw new FileNotFoundException(
            "could not locate PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs; tried:\n  "
                + string.Join("\n  ", candidates)
        );
    }
}
