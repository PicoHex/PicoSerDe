// Shared test asset — linked into every test project that pins a multi-line expectation
// written as a SOURCE BLOCK LITERAL (the same mechanism as shared/CrossValidationModels.cs,
// shared/BenchProtocol.cs, shared/Gen/*.cs: <Compile Include="..\shared\TestText.cs" />).
// It is NOT compiled into any shipping assembly: shared/TextHelpers.cs is the production file
// that PicoSerDe.Core links, and this one must never become part of a package's public API.

namespace PicoSerDe.TestSupport;

/// <summary>
/// The line-ending-neutral comparison for tests whose <b>expected value is a block literal in
/// the source file</b>.
/// <para>
/// <b>The trap this removes.</b> A <c>"""…"""</c> literal does not contain <c>\n</c>. It
/// contains whatever line ending the checkout put in the file: <c>\r\n</c> when
/// <c>core.autocrlf</c> is on (this repository: yes, and no <c>.gitattributes</c>), <c>\n</c>
/// on an LF checkout. A test that compares such a block against a value it built itself —
/// <c>string.Join("\n", …)</c> is the usual shape — is therefore <b>green in the author's
/// worktree and red on every CRLF checkout</b>, and the failure message is useless: the two
/// sides print identically because the only difference is an invisible <c>\r</c> after each
/// line. That is not a hypothetical — it cost two separate debugging rounds in this
/// repository (<c>JsonReaderSyntaxCornerGuardTests.EverySyntaxCornerDump_MatchesThePinnedExpectation</c>,
/// <c>JsonColdThrowTests.ColdThrowMessages_AreFrozen</c>), and each time it was patched at the
/// one call site (<c>TrimEnd('\r')</c>, <c>ReplaceLineEndings("\n")</c>) instead of being
/// removed as a class.
/// </para>
/// <para>
/// <b>How to use it.</b> Uniform both sides, never one of them:
/// <code>
/// var actual = string.Join("\n", …);
/// await Assert.That(TestText.Uniform(actual))
///     .IsEqualTo(TestText.Uniform(Expected))
///     .Because(TestText.Explain(actual, Expected));
/// </code>
/// <see cref="Uniform"/> is idempotent and returns the input instance unchanged when it carries
/// no <c>\r</c>, so the LF checkout pays nothing.
/// </para>
/// <para>
/// <b>What it must not do.</b> Uniforming must never swallow a real difference — a helper that
/// normalised whitespace, trimmed lines or lower-cased text would turn every pinned-block test
/// into a test that passes for the wrong reason. Only <c>\r\n</c> and a lone <c>\r</c> are
/// rewritten; everything else, including a trailing newline that only one side has, still
/// fails (see <c>TestTextUniformTests</c>, contract ③).
/// </para>
/// </summary>
public static class TestText
{
    /// <summary>
    /// The one text form two sides are compared in: <c>\r\n</c> and a lone <c>\r</c> both become
    /// <c>\n</c>. A string with no <c>\r</c> is returned as-is (no copy, no allocation).
    /// </summary>
    public static string Uniform(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOf('\r') < 0)
            return text;
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>
    /// True when the two sides are the same text once line endings are uniformed — i.e. the
    /// comparison a block-literal expectation actually means.
    /// </summary>
    public static bool Equal(string actual, string expected) =>
        string.Equals(Uniform(actual), Uniform(expected), StringComparison.Ordinal);

    /// <summary>
    /// The first line on which the two sides differ <b>after</b> line-ending uniforming, as a
    /// two-line report, or <see langword="null"/> when they do not differ. Both lines are shown
    /// with the invisible characters escaped (<c>\r</c>, <c>\n</c>, <c>\t</c>), so a failure can
    /// never again print two sides that look identical.
    /// </summary>
    public static string? FirstDifference(string actual, string expected)
    {
        var actualLines = Uniform(actual).Split('\n');
        var expectedLines = Uniform(expected).Split('\n');
        var shared = Math.Min(actualLines.Length, expectedLines.Length);

        for (var i = 0; i < shared; i++)
        {
            if (string.Equals(actualLines[i], expectedLines[i], StringComparison.Ordinal))
                continue;
            return $"line {i + 1} of {expectedLines.Length} differs"
                + $"\n  expected: {Show(expectedLines[i])}"
                + $"\n  actual  : {Show(actualLines[i])}";
        }

        if (actualLines.Length == expectedLines.Length)
            return null;

        var longer = actualLines.Length > expectedLines.Length ? actualLines : expectedLines;
        return $"the two sides have different line counts: expected {expectedLines.Length}, "
            + $"actual {actualLines.Length}; the first unpaired line is {Show(longer[shared])}";
    }

    /// <summary>
    /// The <c>Because(…)</c> text for a <see cref="Uniform"/>ed comparison: the first difference
    /// when the assertion is about to fail, and an explicit "the line endings were not the
    /// problem" when it is not (so a red never sends the next reader down this road again).
    /// </summary>
    public static string Explain(string actual, string expected) =>
        FirstDifference(actual, expected)
        ?? "identical once line endings are uniformed — the failure is above, not the line endings";

    /// <summary>One line with its invisible characters spelled out.</summary>
    private static string Show(string line) =>
        "\""
        + line.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
        + "\"";
}
