using System.Globalization;
using PicoBench;

namespace PicoBench.Protocol.Tests;

// The protocol text is *protocol* text, not prose: `FormatCell`'s output is pasted verbatim
// into README tables, quoted in release notes and read back by tools, so it has to be one
// string, not one string per machine. It was not: every field went through the ambient
// culture, and CI said so — the linux-x64 leg printed
//     "0.250 (0.240–0.310 ±0.030) 12 % w1.2s/17b"
// where windows printed "12%", because a `P0` format specifier inserts a space before the
// percent sign when the culture asks for one. A fr-FR developer would have gotten "0,250"
// out of the same code and archived it.
//
// These tests therefore assert the property the protocol actually needs — "the same string
// under every culture" — against the invariant culture (the linux-x64 CI ambient, whose
// percentage spelling is "12 %") and against cultures that really do use a comma decimal
// separator and a space before the percent sign. The expectation is a pinned literal, never
// a string produced under some culture: a baseline built under CultureInfo.InvariantCulture
// would have *agreed* with the CI failure, because .NET's invariant culture is one of the
// cultures that spaces the percent sign.
//
// [NotInParallel]: these tests move process-wide state (Console.Out) and culture on their own
// async flow. Serialising the class keeps a sibling test from printing into the captured
// writer; every culture and writer is restored in a finally, so nothing outlives the test.
[NotInParallel]
public class ProtocolCultureTests
{
    // "" is the invariant culture — the one a LANG=C.UTF-8 Linux runner reports, and the one
    // that produced the failure in the CI log. The rest use a decimal comma; the first three
    // of those also put a space before the percent sign.
    private static readonly string[] Cultures =
    [
        "",
        "fr-FR",
        "de-DE",
        "ru-RU",
        "tr-TR",
        "ar-SA",
        "hi-IN",
    ];

    private const string PublishedCell = "0.250 (0.240–0.310 ±0.030) 12% w1.2s/17b";
    private const string UnreadableCpuCell = "1.000 (0.900–1.400 ±0.200) n/a w⚠2.5s/31b";
    private const string RefusedCell = "UNSTABLE — not published";

    // ── The property the protocol needs: one cell, every culture ─────────────────────

    [Test]
    public async Task A_published_cell_is_the_same_text_in_every_culture()
    {
        var sample = Published(otherCpuFraction: 0.12, warmupMilliseconds: 1234, bursts: 17);

        // The cell is pinned character for character by
        // A_published_cell_carries_dispersion_the_measured_cpu_and_the_warmup_audit under the
        // host's own culture; what this test adds is that the same string comes out of all of
        // them, including the one the Linux runner runs under.
        foreach (var name in Cultures)
        {
            var cell = InCulture(NewCulture(name), () => BenchProtocol.FormatCell(sample));
            await Assert
                .That(cell)
                .IsEqualTo(PublishedCell)
                .Because($"{Describe(name)} rendered {cell}");
        }
    }

    [Test]
    public async Task A_cell_with_an_unreadable_cpu_is_the_same_text_in_every_culture()
    {
        // The NaN branch prints "n/a" — the sibling of the `P0` branch, and the one that runs
        // on macOS, where system-wide CPU accounting does not exist.
        var sample = Published(
            otherCpuFraction: double.NaN,
            warmupMilliseconds: 2500,
            bursts: 31,
            settled: false,
            medianUs: 1.0,
            minUs: 0.9,
            maxUs: 1.4,
            stdDevUs: 0.2
        );

        foreach (var name in Cultures)
        {
            var cell = InCulture(NewCulture(name), () => BenchProtocol.FormatCell(sample));
            await Assert
                .That(cell)
                .IsEqualTo(UnreadableCpuCell)
                .Because($"{Describe(name)} rendered {cell}");
        }
    }

    [Test]
    public async Task A_refused_cell_is_the_same_text_in_every_culture()
    {
        // No number at all — but still a string that must not acquire a culture, because its
        // em dash and wording are matched by tools and quoted by the README.
        var refused = ProtocolSample.Unstable("PicoYaml");

        foreach (var name in Cultures)
        {
            var cell = InCulture(NewCulture(name), () => BenchProtocol.FormatCell(refused));
            await Assert
                .That(cell)
                .IsEqualTo(RefusedCell)
                .Because($"{Describe(name)} rendered {cell}");
        }
    }

    // ── The two character-level defects CI reported, asserted as content ─────────────

    [Test]
    public async Task Under_a_comma_decimal_culture_the_cell_still_uses_a_dot_and_a_tight_percent()
    {
        var culture = NewCulture("fr-FR");

        // The premise, asserted rather than assumed: if this culture ever stopped being the
        // one that reproduces the failure, the assertions below would pass for the wrong
        // reason. (This is also what makes the test fail loudly — instead of vacuously
        // passing — on a host built with InvariantGlobalization, where no culture has a
        // decimal comma at all.)
        await Assert.That((0.25).ToString("F3", culture)).IsEqualTo("0,250");
        await Assert.That((0.12).ToString("P0", culture)).IsEqualTo("12 %");

        var sample = Published(otherCpuFraction: 0.12, warmupMilliseconds: 1234, bursts: 17);
        var cell = InCulture(culture, () => BenchProtocol.FormatCell(sample));

        await Assert.That(cell).IsEqualTo(PublishedCell);
        // Decimal point, not the comma this culture would have used.
        await Assert.That(cell).Contains("0.250");
        await Assert.That(cell).DoesNotContain("0,250");
        // Percent sign glued to the number: the exact character difference the CI log showed
        // between "12%" (windows) and "12 %" (linux).
        await Assert.That(cell).Contains("12%");
        await Assert.That(cell).DoesNotContain("12 %");
    }

    [Test]
    public async Task The_banner_prints_its_numbers_in_the_protocol_format_in_every_culture()
    {
        // The banner quotes the same numbers in prose — `N0` iteration floors, `P0`/`F1`
        // limits — and is copied into run logs and job summaries, so it is protocol text too.
        foreach (var name in Cultures)
        {
            var banner = InCulture(NewCulture(name), CaptureBanner);

            foreach (var token in new[] { "20,000", "100,000", "20%", "3.0x" })
                await Assert
                    .That(banner)
                    .Contains(token)
                    .Because($"{Describe(name)} banner rendered: {banner}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static ProtocolSample Published(
        double otherCpuFraction,
        int warmupMilliseconds,
        int bursts,
        bool settled = true,
        double medianUs = 0.25,
        double minUs = 0.24,
        double maxUs = 0.31,
        double stdDevUs = 0.03
    ) =>
        new(medianUs, minUs, maxUs, stdDevUs, 5)
        {
            Label = "PicoYaml",
            Stable = true,
            OtherCpuFraction = otherCpuFraction,
            WarmupMilliseconds = warmupMilliseconds,
            WarmupBursts = bursts,
            WarmupSettled = settled,
        };

    private static CultureInfo NewCulture(string name) => new(name);

    private static string Describe(string name) =>
        name.Length == 0 ? "the invariant culture" : name;

    /// <summary>
    /// Runs <paramref name="produce"/> with the current and current-UI cultures set to
    /// <paramref name="culture"/>, both restored in a <c>finally</c> so no sibling test can
    /// inherit this one's culture.
    /// </summary>
    private static string InCulture(CultureInfo culture, Func<string> produce)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return produce();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>
    /// The banner text, captured rather than printed. <see cref="BenchProtocol.PrintBanner"/>
    /// is once-per-process (that is the point of its flag), so the flag is cleared through
    /// reflection first: the test project links the same source, so this is the same
    /// assembly, and the alternative — a public reset hook — would be product surface bought
    /// for a test. Console is redirected for the length of one call and put back in a
    /// <c>finally</c>; TUnit0055 is suppressed because TUnit's own logging is the writer that
    /// gets put back.
    /// </summary>
    private static string CaptureBanner()
    {
        const string flagName = "_bannerReported";
        var flag =
            typeof(BenchProtocol).GetField(
                flagName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            ) ?? throw new InvalidOperationException($"BenchProtocol.{flagName} is gone");
        flag.SetValue(null, false);

        var original = Console.Out;
        using var capture = new StringWriter();
#pragma warning disable TUnit0055 // see the summary above: restored in the finally below
        try
        {
            Console.SetOut(capture);
            BenchProtocol.PrintBanner();
        }
        finally
        {
            Console.SetOut(original);
        }
#pragma warning restore TUnit0055

        return capture.ToString();
    }
}
