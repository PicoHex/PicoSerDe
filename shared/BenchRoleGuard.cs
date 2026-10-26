// Shared benchmark role guard — linked into every benchmark project.
//
// PicoBench's signature is
//
//     Benchmark.Compare(string name, string baselineName, Action baselineAction,
//                       string candidateName, Action candidateAction, BenchmarkConfig? config = null)
//
// and ComparisonResult.Speedup is Baseline.Statistics.Avg / Candidate.Statistics.Avg,
// i.e. ">1 means candidate is faster".
//
// The house convention is therefore:
//
//     candidate = PicoSerDe's own action   (the one being argued about)
//     baseline  = the competitor's action
//
// With that convention Speedup reads as "how many times faster we are", and every
// printed label (Pico=…/Competitor=…) sits next to the number it actually describes.
//
// Every call site in this repo used to pass the two actions the other way round. That
// swapped the column labels in all five comparison tables and inverted the meaning of
// every ratio, while still producing plausible-looking numbers — so nothing caught it.
// Guard() runs on every comparison and throws if the roles are inverted, turning a
// silent misreading into an immediate crash.

namespace PicoBench;

/// <summary>
/// Enforces the benchmark role convention:
/// <see cref="ComparisonResult.Candidate"/> holds PicoSerDe's own action and
/// <see cref="ComparisonResult.Baseline"/> holds the competitor's.
/// </summary>
public static class BenchmarkRoles
{
    /// <summary>Every first-party benchmark name starts with this.</summary>
    private const string OwnPrefix = "Pico";

    /// <summary>
    /// Validates the roles of a comparison and returns it unchanged.
    /// </summary>
    /// <param name="comparison">The measured comparison.</param>
    /// <returns>The same instance, so the call can wrap a construction expression.</returns>
    /// <exception cref="InvalidOperationException">
    /// The candidate slot does not hold a Pico* action, or the baseline slot does.
    /// Both actual names are included in the message.
    /// </exception>
    public static ComparisonResult Guard(ComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var candidate = comparison.Candidate.Name;
        var baseline = comparison.Baseline.Name;

        if (IsOwn(candidate) && !IsOwn(baseline))
            return comparison;

        throw new InvalidOperationException(
            $"Benchmark role inversion in comparison '{comparison.Name}': the candidate slot must hold a "
                + $"PicoSerDe action and the baseline slot the competitor, but candidate='{candidate}' "
                + $"and baseline='{baseline}'. Pass the competitor as baselineName/baselineAction "
                + "(arguments 2 and 3) and PicoSerDe as candidateName/candidateAction (arguments 4 and 5) "
                + "of Benchmark.Compare(name, baselineName, baselineAction, candidateName, candidateAction)."
        );
    }

    private static bool IsOwn(string name) => name.StartsWith(OwnPrefix, StringComparison.Ordinal);
}
