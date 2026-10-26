using System.Text.RegularExpressions;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// I-6 white area 8: the probe's <c>CELL-OBS</c> lines had NO gate at all.
/// <para>
/// The 70 scored cells are checked in as <c>baseline-cells.txt</c>; the two OBSERVATION
/// shapes (S14 <c>List&lt;Dto?&gt;</c>, S15 the scalar family) were deliberately kept out of
/// that file — they are not capability rows — and the price of keeping them out was that
/// nothing compared what the probe measures today with what spec §3 claims. Two of the ten
/// rows had already rotted before this test existed: <c>S14 × yaml</c> said MISMATCH long
/// after R2 Task 4 stopped dropping the member, and <c>S15 × ini</c> said MISMATCH after the
/// default-DateTime read was fixed. A measurement nobody re-reads is a measurement that
/// silently becomes a lie, which is the same failure mode I-4 exists to prevent for the
/// matrix.
/// </para>
/// <para>
/// The gate has two halves, and each one is welded to the document it claims to guard. One
/// half is the checked-in <c>baseline-observations.txt</c> (the probe's recording — refresh it
/// with the command in the probe's own header). The other half is <b>spec §3's observation
/// table itself</b>: this test PARSES
/// <c>docs/superpowers/specs/2026-10-25-format-capabilities-design.md</c> and reads the
/// <c>实测 verdict</c> column out of the markdown, instead of restating the table in a fixture
/// here. Editing the document is therefore a way to make the gate red, which is the whole
/// point. (The first version of this gate compared the recording with a hand-copied
/// transcription of §3 living in this file, so the document itself stayed unguarded: rot §3
/// alone and everything was still green. A gate that guards a copy of the thing it names
/// guards nothing — the two rotted rows that motivated this test are exactly that failure.)
/// </para>
/// <para>
/// <b>Known limitation, stated instead of implied</b>: the gate compares the RECORDING with
/// the spec, it does not run the probe (the probe project is not part of
/// <c>PicoSerDe.slnx</c> and building it from a test would make the suite depend on a
/// <c>dotnet build</c>). A behaviour change that nobody re-measures is therefore still
/// possible — but it can no longer be <i>silent</i>, because the recording is now a checked-in
/// file whose diff is visible in every review.
/// </para>
/// </summary>
public class ProbeObservationGateTests
{
    /// <summary>
    /// Spec §3 is the source of truth for the observation verdicts, so the gate reads the file
    /// rather than a transcription of it.
    /// </summary>
    private const string SpecDocument =
        "docs/superpowers/specs/2026-10-25-format-capabilities-design.md";

    /// <summary>The two observation shapes — measurements, not capability rows (spec §3).</summary>
    private static readonly string[] ObservationShapes = ["S14", "S15"];

    private static readonly string[] ObservationFormats =
    [
        "json",
        "msgpack",
        "toml",
        "yaml",
        "ini",
    ];

    /// <summary>
    /// Two observation shapes × five formats. Spelled out as a number so that a row dropping
    /// out of EITHER side (document or recording) is a red test, not a quietly thinner table.
    /// </summary>
    private const int ObservationCellCount = 10;

    /// <summary>
    /// The only row shape the gate accepts: <c>| S14 × yaml | `OK` | …</c>. The §3 fact table
    /// (<c>| S14 `List&lt;Dto?&gt;` … |</c>) and §6's target matrix (<c>| S14/S15 |</c>) do not
    /// match, which is how the parse stays on the observation table.
    /// </summary>
    private static readonly Regex ObservationRow = new(
        "^(?<shape>S\\d+) × (?<tag>[a-z]+)$",
        RegexOptions.CultureInvariant
    );

    /// <summary>
    /// The gate is only as good as its parse: if the table moves or is reworded, the parser
    /// would find nothing and every assertion below would pass vacuously over an empty set.
    /// This test is the one that fails in that world, and it also pins the shape/format
    /// vocabulary the probe measures.
    /// </summary>
    [Test]
    public async Task The_gate_parses_the_observation_table_out_of_the_spec_document()
    {
        var spec = SpecObservations();

        await Assert
            .That(spec.Length)
            .IsEqualTo(ObservationCellCount)
            .Because(
                $"{SpecDocument} must hold 2 observation shapes x 5 formats = "
                    + $"{ObservationCellCount} rows shaped `| Sxx × format | `VERDICT` | …`; "
                    + $"the parse found {spec.Length}. If this is 0, the table moved and the "
                    + "rest of this class is green over nothing"
            );

        await Assert
            .That(spec.Select(r => (r.Shape, r.Tag)).Distinct().Count())
            .IsEqualTo(ObservationCellCount)
            .Because("a duplicated row would let one cell be gated twice and another not at all");

        foreach (var (shape, tag, verdict, _) in spec)
        {
            await Assert
                .That(ObservationShapes.Contains(shape))
                .IsTrue()
                .Because($"{shape} is not an observation shape — spec §3 measures S14 and S15");
            await Assert
                .That(ObservationFormats.Contains(tag))
                .IsTrue()
                .Because($"{tag} is not one of the five formats the probe drives");
            await Assert
                .That(verdict)
                .IsIn("OK", "MISMATCH", "TIMEOUT", "SKIPPED_AFTER_TIMEOUT")
                .Because(
                    $"{shape} x {tag}: spec §3's verdict column must hold a probe verdict, "
                        + $"found '{verdict}'"
                );
        }
    }

    /// <summary>
    /// The recording exists, holds exactly the ten observation cells, and each of them is a
    /// well-formed line with a verdict. Ten is pinned: a shape or a format that drops out of
    /// the probe must be a visible edit here, not a quietly shorter file.
    /// </summary>
    [Test]
    public async Task The_observation_recording_covers_every_observation_cell()
    {
        var lines = ObservationLines();

        await Assert
            .That(lines.Length)
            .IsEqualTo(ObservationCellCount)
            .Because(
                "the probe measures S14/S15 x five formats = "
                    + $"{ObservationCellCount} observations; the recording has {lines.Length}"
            );

        foreach (var line in lines)
        {
            var cells = line.Split('|');
            await Assert
                .That(cells.Length)
                .IsGreaterThan(4)
                .Because($"a CELL-OBS line needs format/shape/verdict/wire columns: {line}");
            await Assert
                .That(cells[3])
                .IsIn("OK", "MISMATCH", "TIMEOUT", "SKIPPED_AFTER_TIMEOUT")
                .Because($"unknown verdict in the recording: {line}");
        }
    }

    /// <summary>
    /// Every observation cell must still be OUT of the 70 scored cells. This is the other half
    /// of the contract the observation design rests on: S14/S15 are measurements, not
    /// capability rows, and a shape id that leaks into the model would score a cell R1 never
    /// measured.
    /// </summary>
    [Test]
    public async Task Observations_stay_outside_the_seventy_scored_cells()
    {
        var cells = RepositoryFile("tests/FormatCapabilities.Probe/baseline-cells.txt")
            .Split('\n')
            .Where(l => l.StartsWith("CELL|", StringComparison.Ordinal))
            .ToArray();

        await Assert
            .That(cells.Length)
            .IsEqualTo(70)
            .Because("the scored matrix is 14 shapes x 5 formats — 70 cells, no more, no fewer");

        await Assert
            .That(
                cells.Count(l =>
                    l.Contains("|S14|", StringComparison.Ordinal)
                    || l.Contains("|S15|", StringComparison.Ordinal)
                )
            )
            .IsEqualTo(0)
            .Because(
                "S14/S15 are observations (spec §3): scoring them in baseline-cells.txt would "
                    + "turn a measurement into a capability claim"
            );

        foreach (var (shape, _, _, _) in SpecObservations())
        {
            await Assert
                .That(PicoSerDe.Gen.FormatCapabilities.AllShapeIds.Contains(shape))
                .IsFalse()
                .Because(
                    $"{shape} must stay unknown to the capability model: AllShapeIds stops at S13 "
                        + "(the probe asserts the same thing at run time)"
                );
        }
    }

    /// <summary>
    /// Spec §3's recorded verdict for every observation, checked against the recording — the
    /// two sides being the DOCUMENT and the probe's recording, with no third copy in between.
    /// This is the assertion that would have caught both rotted rows.
    /// </summary>
    [Test]
    public async Task Every_observation_verdict_matches_spec_section3()
    {
        var lines = ObservationLines();
        var byCell = new Dictionary<(string Shape, string Tag), string>();
        foreach (var line in lines)
        {
            var cells = line.Split('|');
            byCell[(cells[2], cells[1])] = cells[3];
        }

        foreach (var (shape, tag, verdict, detail) in SpecObservations())
        {
            if (!byCell.TryGetValue((shape, tag), out var recorded))
            {
                throw new InvalidOperationException(
                    $"the recording has no line for {shape} x {tag}; expected one, because "
                        + "spec §3 records a verdict for it"
                );
            }

            await Assert
                .That(recorded)
                .IsEqualTo(verdict)
                .Because(
                    $"{shape} x {tag} disagrees with spec §3: recorded '{recorded}', §3 says "
                        + $"'{verdict}' (§3's own note: {Excerpt(detail)}). Either re-measure the "
                        + "probe and refresh baseline-observations.txt, or correct spec §3 — "
                        + "never only one of the two: that is exactly how the two rotted rows "
                        + "survived a whole round"
                );
        }
    }

    /// <summary>
    /// No orphan rows: every recorded cell has a spec §3 row. Without this the document could
    /// describe a subset and the other direction would still look complete.
    /// </summary>
    [Test]
    public async Task The_recording_has_no_cell_spec_section3_does_not_describe()
    {
        var spec = SpecObservations();

        foreach (var line in ObservationLines())
        {
            var cells = line.Split('|');
            await Assert
                .That(spec.Any(s => s.Shape == cells[2] && s.Tag == cells[1]))
                .IsTrue()
                .Because(
                    $"the recording measures {cells[2]} x {cells[1]}, which spec §3 does not "
                        + "describe — add the row to spec §3 (the gate reads the document, not a "
                        + "copy of it) or stop measuring it"
                );
        }
    }

    /// <summary>
    /// The observation table of spec §3, parsed out of the markdown: every
    /// <c>| Sxx × format | `VERDICT` | detail |</c> row, in file order. This is the only place
    /// a verdict may come from on the document side.
    /// </summary>
    private static (string Shape, string Tag, string Verdict, string Detail)[] SpecObservations()
    {
        var rows = new List<(string, string, string, string)>();
        foreach (var raw in RepositoryFile(SpecDocument).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("| S", StringComparison.Ordinal))
                continue;

            var columns = line.Split('|');
            if (columns.Length < 4)
                continue;

            var match = ObservationRow.Match(columns[1].Trim());
            if (!match.Success)
                continue;

            rows.Add(
                (
                    match.Groups["shape"].Value,
                    match.Groups["tag"].Value,
                    columns[2].Trim().Trim('`'),
                    columns[3].Trim()
                )
            );
        }

        return rows.ToArray();
    }

    /// <summary>§3's note column, shortened so a failure message stays readable.</summary>
    private static string Excerpt(string detail) =>
        detail.Length <= 220 ? detail : detail[..220] + "…";

    /// <summary>The <c>CELL-OBS</c> lines of the checked-in recording, in file order.</summary>
    private static string[] ObservationLines() =>
        RepositoryFile("tests/FormatCapabilities.Probe/baseline-observations.txt")
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();

    private static string RepositoryFile(string relativePath) =>
        File.ReadAllText(Path.Combine(TestRepo.Root(), relativePath));
}
