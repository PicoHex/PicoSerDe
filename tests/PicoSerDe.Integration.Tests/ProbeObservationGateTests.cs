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
/// The gate has two halves. The checked-in <c>baseline-observations.txt</c> is the probe's
/// recording (refresh it with the command in the probe's own header), and this fixture is
/// spec §3's table spelled out. Both must agree — so refreshing the recording without
/// touching the spec fails here, and editing the spec without re-measuring fails too.
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
    /// <summary>shapeId → (format → the verdict spec §3 records). Written out, never derived.</summary>
    private static readonly (string Shape, string Tag, string Verdict, string Why)[] SpecSection3 =
    [
        // S14 — an object list with a null ELEMENT.
        ("S14", "json", "OK", "JSON has a null literal; the element round-trips."),
        ("S14", "msgpack", "OK", "nil element; the element round-trips."),
        ("S14", "toml", "MISMATCH", "TOML has no null literal, so the whole member is dropped."),
        ("S14", "yaml", "OK", "R2 Task 4 wrote `- null`, so the member is no longer dropped."),
        (
            "S14",
            "ini",
            "MISMATCH",
            "an object list inside a member is outside INI's convention (same cause as S2)."
        ),
        // S15 — the scalar family (float/decimal/DateTime/TimeSpan/Guid/enum?).
        (
            "S15",
            "json",
            "MISMATCH",
            "no NaN literal: the boundary value throws ArgumentException."
        ),
        ("S15", "msgpack", "OK", "every member survives, NaN included."),
        ("S15", "toml", "MISMATCH", "the NaN boundary throws (debt: TOML has `nan`)."),
        ("S15", "yaml", "MISMATCH", "the NaN boundary throws (debt: YAML has `.nan`)."),
        ("S15", "ini", "OK", "every member survives, including a default DateTime."),
    ];

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
            .IsEqualTo(SpecSection3.Length)
            .Because(
                "the probe measures S14/S15 x five formats = 10 observations; the recording has "
                    + $"{lines.Length}"
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

        foreach (var (shape, _, _, _) in SpecSection3)
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
    /// Spec §3's recorded verdict for every observation, checked against the recording. This
    /// is the assertion that would have caught both rotted rows.
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

        foreach (var (shape, tag, verdict, why) in SpecSection3)
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
                    $"{shape} x {tag} disagrees with spec §3: recorded '{recorded}', spec says "
                        + $"'{verdict}' ({why}). Either re-measure the probe and refresh "
                        + "baseline-observations.txt, or correct spec §3 — never only one of the "
                        + "two: that is exactly how the two rotted rows survived a whole round"
                );
        }
    }

    /// <summary>
    /// No orphan rows: every recorded cell has a spec §3 row. Without this the fixture could
    /// cover a subset and still look complete.
    /// </summary>
    [Test]
    public async Task The_recording_has_no_cell_spec_section3_does_not_describe()
    {
        var lines = ObservationLines();

        foreach (var line in lines)
        {
            var cells = line.Split('|');
            await Assert
                .That(SpecSection3.Any(s => s.Shape == cells[2] && s.Tag == cells[1]))
                .IsTrue()
                .Because(
                    $"the recording measures {cells[2]} x {cells[1]}, which spec §3 does not "
                        + "describe — add the row to the fixture (and to spec §3) or stop "
                        + "measuring it"
                );
        }
    }

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
