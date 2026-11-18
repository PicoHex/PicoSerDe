using Microsoft.CodeAnalysis;
using PicoIni.Gen;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// <c>[IniKey]</c> is a COMPILE-TIME constant, so a key the INI grammar cannot carry has to be
/// answered at generation time — it is not a runtime accident waiting for the right document.
/// <para>
/// <b>The ablation this closes.</b> <c>IniWriter.WriteKey</c> has no charset guard, and none is
/// possible there: by the time the writer is called the bytes are already committed to a wire shape.
/// Measured on the pre-fix tree, three hostile keys were accepted with <b>no diagnostic and no
/// exception</b> and produced a document that does not read back:
/// </para>
/// <list type="bullet">
///   <item><c>[IniKey("n\nline")]</c> wrote <c>n⏎line = 8</c> — our OWN reader throws
///         <c>FormatException</c> on the resulting file, so the member is lost;</item>
///   <item><c>[IniKey("[sec]")]</c> wrote <c>[sec] = 8</c> — a SECTION HEADER, not a key: the
///         member silently moves to a different (invented) section;</item>
///   <item><c>[IniKey("a=b")]</c> wrote <c>a=b = 8</c> — the key is truncated to <c>a</c> by every
///         INI reader, so the member reads back as a different (never declared) key.</item>
/// </list>
/// <para>
/// The five characters are a FORMAT boundary (<c>=</c> ends the key, <c>[</c>/<c>]</c> delimit a
/// section header, CR/LF end the line — <c>PicoIni/README.md</c>, Limits), so the verdict is ⛔: no
/// INI reader could recover those bytes, and no emitter change can make the format hold them. The
/// way out is the KEY (rename the member, or give <c>[IniKey]</c> a plain name) — the member's type
/// is not the problem, which is why the message must not send the reader to another format.
/// </para>
/// <para>
/// Reported as <c>PICOSERDE008</c> (Warning, <c>Location.None</c> — the drop is decided from the
/// <c>TypeInfo</c> the transform produced, which holds strings, not symbols), and the member is
/// <b>not emitted</b>: a generated serializer that cannot write the key must not pretend to.
/// </para>
/// </summary>
public class IniKeyCharsetDiagnosticTests
{
    private const string Id = "PICOSERDE008";

    /// <summary>The member under test; asserted ABSENT from the generated code.</summary>
    private const string Hostile = "Hostile";

    /// <summary>The healthy sibling; asserted PRESENT, so "not emitted" is about the one member.</summary>
    private const string Healthy = "TextField";

    /// <summary>
    /// A model whose SECOND member carries <paramref name="keySourceLiteral"/> — already a C#
    /// string literal (quotes included), so each test can write the escape it means.
    /// </summary>
    private static string Source(string keySourceLiteral) =>
        $$"""
            using PicoIni;

            [PicoIniSerializable]
            public partial class ProbeDoc
            {
                public string TextField { get; set; } = "";

                [IniKey({{keySourceLiteral}})]
                public int Hostile { get; set; }
            }
            """;

    // ── the three hostile keys, one test each ──────────────────────────────────────

    /// <summary>A RAW NEWLINE inside the key ends the line early: the emitted <c>n⏎line = 8</c>
    /// makes our own reader throw <c>FormatException</c>.</summary>
    [Test]
    public async Task Ini_KeyWithNewline_ReportsDiagnostic_AndDropsTheMember() =>
        await AssertHostile("\"n\\nline\"", "LF");

    /// <summary>A SECTION DELIMITER turns <c>[sec] = 8</c> into a section header — the member
    /// silently changes meaning instead of being written.</summary>
    [Test]
    public async Task Ini_KeyWithSectionBrackets_ReportsDiagnostic_AndDropsTheMember() =>
        await AssertHostile("\"[sec]\"", "'[', ']'");

    /// <summary>The <c>=</c> that ends a key cuts <c>a=b = 8</c> down to the key <c>a</c>, so the
    /// member reads back under a name nobody declared.</summary>
    [Test]
    public async Task Ini_KeyWithEquals_ReportsDiagnostic_AndDropsTheMember() =>
        await AssertHostile("\"a=b\"", "'='");

    private static async Task AssertHostile(string keySourceLiteral, string expectedCharNames)
    {
        var run = SilentDropDiagnosticTests.RunGenerator(
            new IniSerializerGenerator(),
            Source(keySourceLiteral)
        );
        var diagnostics = run.Diagnostics.ToArray();
        var generated = string.Join(
            "\n",
            run.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );

        var mine = diagnostics.Where(d => d.Id == Id).ToArray();

        await Assert
            .That(mine.Length)
            .IsEqualTo(1)
            .Because(
                $"the key {keySourceLiteral} cannot be written by INI, so exactly one {Id} must be "
                    + "reported for that member; ids seen: "
                    + string.Join(", ", diagnostics.Select(d => d.Id))
                    + " || MESSAGES: " + string.Join(" || ", diagnostics.Select(d => d.ToString()))
                    + " || GENERATED: " + generated
            );

        await Assert
            .That(mine[0].Severity)
            .IsEqualTo(DiagnosticSeverity.Warning)
            .Because("a dropped member is a warning in the build log — the same contract as "
                + "PICOSERDE003/004/005/006/007");

        await Assert
            .That(mine[0].Location.Kind)
            .IsEqualTo(LocationKind.None)
            .Because(
                "the drop is decided in GenerateAll from the TypeInfo (strings, no symbols), so "
                    + "Location.None is the documented channel for PICOSERDE001/006/007 — the "
                    + "message carries the member and the key instead"
            );

        await Assert
            .That(mine[0].GetMessage())
            .Contains(Hostile)
            .Because("the reader has to find the member from the build log");

        await Assert
            .That(mine[0].GetMessage())
            .Contains(expectedCharNames)
            .Because($"the message must name WHICH reserved character it found ({expectedCharNames})");

        await Assert
            .That(generated)
            .DoesNotContain(Hostile)
            .Because(
                "a serializer that cannot write the key must not be emitted for it — emitting it "
                    + "would keep producing the corrupt document the diagnostic just refused"
            );

        await Assert
            .That(generated)
            .Contains(Healthy)
            .Because("dropping the hostile member must not touch the rest of the type");
    }

    // ── the boundary the guard must not cross ──────────────────────────────────────

    /// <summary>
    /// A dotted key is text INI carries unchanged — <c>a.b</c> is not a path, not an escape and
    /// not structure, so the new guard must leave it completely alone. Pinned here as the
    /// "<c>PICOSERDE008</c> is about five characters, not about 'unusual' keys" half: a rule that
    /// fired on every punctuated key would break real configuration files.
    /// </summary>
    [Test]
    public async Task Ini_KeyWithDot_IsNotReported_AndIsStillEmitted()
    {
        var run = SilentDropDiagnosticTests.RunGenerator(
            new IniSerializerGenerator(),
            Source("\"a.b\"")
        );

        await Assert
            .That(run.Diagnostics.Select(d => d.Id).ToArray())
            .DoesNotContain(Id)
            .Because("a dot is not one of the five reserved characters");

        var generated = string.Join(
            "\n",
            run.Results.SelectMany(r => r.GeneratedSources).Select(g => g.SourceText.ToString())
        );

        await Assert.That(generated).Contains("a.b").Because("the member keeps its declared key");
        await Assert.That(generated.Contains(Hostile)).IsEqualTo(true);
    }
}
