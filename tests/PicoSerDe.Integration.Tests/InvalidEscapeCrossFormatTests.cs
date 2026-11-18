namespace PicoSerDe.Integration.Tests;

/// <summary>Scalar member shared by both formats' halves.</summary>
public class EscapeCrossFormatDoc
{
    public string? A { get; set; }
}

/// <summary>
/// One rule, two formats: an escape sequence a format does not define is a wire error, not a
/// backslash to keep. RFC 8259 §7 lists the JSON escapes; YAML 1.2 §5.7 lists its own, and
/// neither contains <c>\q</c>. The JSON path has refused it for a long time
/// (<c>README.md:370</c>, <c>JsonStringScanTests</c>); the YAML path used to drop the backslash
/// and hand back <c>xqy</c>, so the same input was invalid in one format and "valid" in the
/// other. These two tests are the cross-format half of <c>YamlReaderStrictnessTests</c>.
/// </summary>
public class InvalidEscapeCrossFormatTests
{
    [Test]
    public async Task BackslashQ_IsRejectedByJsonAndYamlAlike()
    {
        await Assert
            .That(() =>
                JsonSerializer.Deserialize<EscapeCrossFormatDoc>(
                    Encoding.UTF8.GetBytes("{\"A\":\"x\\qy\"}")
                )
            )
            .Throws<FormatException>()
            .Because("RFC 8259 §7 has no \\q");

        await Assert
            .That(() =>
                YamlSerializer.Deserialize<EscapeCrossFormatDoc>(
                    Encoding.UTF8.GetBytes("A: \"x\\qy\"\n")
                )
            )
            .Throws<FormatException>()
            .Because("YAML 1.2 §5.7 has no \\q either — the same input is the same error");
    }

    /// <summary>
    /// The other direction, so "both throw" cannot be satisfied by throwing at everything: an
    /// escape BOTH grammars define decodes to the same character on both paths.
    /// </summary>
    [Test]
    public async Task BackslashT_IsDecodedByJsonAndYamlAlike()
    {
        var fromJson = JsonSerializer.Deserialize<EscapeCrossFormatDoc>(
            Encoding.UTF8.GetBytes("{\"A\":\"x\\ty\"}")
        );
        var fromYaml = YamlSerializer.Deserialize<EscapeCrossFormatDoc>(
            Encoding.UTF8.GetBytes("A: \"x\\ty\"\n")
        );

        await Assert.That(fromJson!.A).IsEqualTo("x\ty");
        await Assert.That(fromYaml!.A).IsEqualTo("x\ty");
    }
}
