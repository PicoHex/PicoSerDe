using PicoSerDe.TestSupport;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The guard for <see cref="TestText"/> itself — the comparison helper every "the expectation is
/// a block literal in the source" test uses instead of a raw
/// <c>Assert.That(actual).IsEqualTo(expected)</c>.
/// <para>
/// <b>Why the helper exists.</b> A <c>"""…"""</c> literal does not hold <c>\n</c>: it holds
/// whatever the checkout put in the file, i.e. <c>\r\n</c> on a CRLF checkout and <c>\n</c> on an
/// LF one. An expectation written that way compared against a value the test builds with
/// <c>string.Join("\n", …)</c> is therefore green in the author's worktree (LF) and red on the
/// CRLF checkout — and the failure prints two sides that look character-for-character identical
/// because the only difference is an invisible <c>\r</c> at the end of every line. That happened
/// twice in this repository (<c>JsonReaderSyntaxCornerGuardTests</c>,
/// <c>JsonColdThrowTests</c>), and each time it was patched locally (<c>TrimEnd('\r')</c>,
/// <c>ReplaceLineEndings("\n")</c>) instead of being removed as a class.
/// </para>
/// <para>
/// The three contracts below are the whole helper: <b>①</b> text that already agrees is left
/// alone, <b>②</b> a CRLF side and an LF side compare equal (the point), and <b>③</b> a genuine
/// difference is still a difference — a helper that also ate real differences would turn every
/// pinned-block test into a no-op.
/// </para>
/// </summary>
public class TestTextUniformTests
{
    /// <summary>
    /// ① Nothing to erase: both sides are already LF, so the helper neither rewrites the text
    /// nor reports a difference. This is also the LF-checkout path, where <c>Uniform</c> must
    /// not allocate a copy of every pinned block.
    /// </summary>
    [Test]
    public async Task Uniform_BothSidesLf_IsAPassAndLeavesTheTextAlone()
    {
        const string actual = "alpha\nbeta\ngamma";
        const string expected = "alpha\nbeta\ngamma";

        await Assert.That(TestText.Uniform(actual)).IsEqualTo(actual);
        await Assert.That(TestText.Equal(actual, expected)).IsTrue();
        await Assert.That(TestText.FirstDifference(actual, expected)).IsNull();
    }

    /// <summary>
    /// ② <b>The core.</b> The expectation is a block literal read from a CRLF checkout; the
    /// actual is what <c>string.Join("\n", …)</c> produced. Raw, they differ on every line (the
    /// first assertion states the trap). Through the helper they are the same text, which is
    /// exactly what the two patched tests now rely on.
    /// </summary>
    [Test]
    public async Task Uniform_CrlfAgainstLf_ComparesEqual()
    {
        // What a """…""" literal holds on a CRLF checkout (this repository sets
        // core.autocrlf=true and has no .gitattributes, so that is the default checkout).
        const string fromCrlfCheckout = "alpha\r\nbeta\r\ngamma";
        // What the test itself produces.
        const string joinedWithLf = "alpha\nbeta\ngamma";

        // The trap: raw equality fails, and the failure message shows two identical-looking sides.
        await Assert.That(fromCrlfCheckout).IsNotEqualTo(joinedWithLf);

        await Assert.That(TestText.Uniform(fromCrlfCheckout)).IsEqualTo(joinedWithLf);
        await Assert.That(TestText.Uniform(joinedWithLf)).IsEqualTo(joinedWithLf);
        await Assert.That(TestText.Equal(fromCrlfCheckout, joinedWithLf)).IsTrue();
        await Assert.That(TestText.Equal(joinedWithLf, fromCrlfCheckout)).IsTrue();
        await Assert.That(TestText.FirstDifference(fromCrlfCheckout, joinedWithLf)).IsNull();
    }

    /// <summary>
    /// ③ A real difference survives. Uniforming line endings must not degrade the comparison
    /// into "always equal": the pair below differs in content on line 2, and the helper has to
    /// say so — naming the line and showing both sides with the invisible characters escaped.
    /// </summary>
    [Test]
    public async Task Uniform_RealDifference_IsStillReportedAsADifference()
    {
        const string actual = "alpha\r\nbeta\ngamma";
        const string expected = "alpha\nBETA\ngamma";

        await Assert.That(TestText.Equal(actual, expected)).IsFalse();
        await Assert.That(TestText.Uniform(actual)).IsNotEqualTo(TestText.Uniform(expected));

        var difference = TestText.FirstDifference(actual, expected);
        await Assert.That(difference).IsNotNull();
        await Assert.That(difference!).Contains("line 2");
        await Assert.That(difference!).Contains("beta");
        await Assert.That(difference!).Contains("BETA");
    }

    /// <summary>
    /// A lone <c>\r</c> (old Mac line ending, and what a hand-pasted block can carry) is a line
    /// end too. <c>ReplaceLineEndings</c> handles it; a bare <c>Replace("\r\n", "\n")</c> does
    /// not, which is why the helper does not use the bare form.
    /// </summary>
    [Test]
    public async Task Uniform_LoneCarriageReturn_IsAlsoALineEnd()
    {
        await Assert.That(TestText.Uniform("alpha\rbeta\rgamma")).IsEqualTo("alpha\nbeta\ngamma");
        await Assert.That(TestText.Equal("alpha\rbeta\rgamma", "alpha\nbeta\ngamma")).IsTrue();
    }

    /// <summary>
    /// A block literal adds a trailing newline that a <c>string.Join</c> does not, and that
    /// difference is a real one: the helper must report it rather than trim it away.
    /// </summary>
    [Test]
    public async Task Uniform_TrailingNewlineDifference_IsARealDifference()
    {
        const string withTrailingNewline = "alpha\r\nbeta\r\n";
        const string withoutTrailingNewline = "alpha\nbeta";

        await Assert.That(TestText.Equal(withTrailingNewline, withoutTrailingNewline)).IsFalse();

        var difference = TestText.FirstDifference(withTrailingNewline, withoutTrailingNewline);
        await Assert.That(difference).IsNotNull();
        await Assert.That(difference!).Contains("line count");
    }
}
