namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The <b>usage</b> guard for the fused-read work — the one the round of 2026-10-28 did not have.
/// <para>
/// <b>What went wrong without it.</b> T11 (the MessagePack fused-element emission) was never executed, and
/// nothing said so: the plan's closing table listed "T1, T1b, T2, T3, T5, T6, T7+T8, T9, T10, T12, T13, T14,
/// T15 | merged", T11 was absorbed into that sentence, and the 135 equivalence guards in
/// <c>MsgPackReaderFusedEquivalenceTests</c> only ever proved that a member is <b>correct</b> — never that
/// anybody <b>calls</b> it. "Implemented, never called, never mentioned" was a state no test could see.
/// </para>
/// <para>
/// <b>The rule this file enforces.</b> Every member declared on <see cref="IFusedTokenReader"/> — read as text
/// from <c>PicoSerDe.Core/src/ITokenReader.cs</c>, so a <b>newly added</b> member enters the check by itself —
/// must, <b>for every format that ever pursued emission</b>, either be <b>called</b> in that format's generated
/// baseline or be <b>registered</b> in <c>docs/fused-emission-registry.md</c> as <c>不用</c> with a reason.
/// Implement a member and do neither ⇒ these tests go red, and the failure text says which file to edit and
/// the exact row to add.
/// </para>
/// <para>
/// <b>Why the baseline is the target.</b> The generated baseline under
/// <c>tests/FormatCapabilities.Probe/generated-baseline/</c> is what the probe project actually compiled — the
/// same surface <c>GeneratedCodeConcreteReaderGuardTests</c> (T10) reads. Checking the emitter source instead
/// would answer "what the next build will emit", which is not the question: the question is whether the code
/// that ships calls the member.
/// </para>
/// <para>
/// <b>Scope, and why it is not five formats.</b> Only MessagePack (T6–T11) and JSON (T14/T15) ever pursued
/// fused emission; TOML/YAML/INI have no emission work and no measurement behind one, so they get the opposite
/// assertion (<see cref="FormatsThatNeverPursuedEmission_CallNoFusedRead"/>) rather than fifteen empty registry
/// rows. If a third format starts emitting, add it to <see cref="EmittingFormats"/> and register its members.
/// </para>
/// </summary>
public class FusedMemberEmissionGuardTests
{
    /// <summary>Named in every failure message: the file the next person has to edit.</summary>
    const string RegistryRelativePath = "docs/fused-emission-registry.md";

    /// <summary>Where the member list comes from — the contract, not a copy of it in this test.</summary>
    const string ContractRelativePath = "PicoSerDe.Core/src/ITokenReader.cs";

    /// <summary>The registry's two values, spelled exactly as the table spells them.</summary>
    const string Emitted = "已发射";

    const string NotUsed = "不用";

    /// <summary>
    /// Formats that pursued fused emission: their members must be emitted or registered. The third column is a
    /// floor on the baseline's file count <b>as it stands today</b> — without it, a moved or emptied directory
    /// makes every "calls it 0 times" claim true for the wrong reason. Regenerating a baseline that legitimately
    /// grows is fine; a baseline that <b>shrinks</b> is the case this floor exists for.
    /// </summary>
    static readonly (string Format, string BaselineFolder, int MinimumFiles)[] EmittingFormats =
    [
        ("MsgPack", "PicoMsgPack.Gen", 20),
        ("JSON", "PicoJetson.Gen", 20),
    ];

    /// <summary>Formats that never did: one fused call appearing in their baseline is a silent change.</summary>
    static readonly (
        string Format,
        string BaselineFolder,
        int MinimumFiles
    )[] NeverEmittingFormats =
    [
        ("TOML", "PicoToml.Gen", 20),
        ("YAML", "PicoYaml.Gen", 20),
        ("INI", "PicoIni.Gen", 16),
    ];

    /// <summary>The five methods declared today; a parse that returns fewer is a broken parse, not a pass.</summary>
    const int MinimumFusedMembers = 5;

    /// <summary>
    /// The rule itself. Runs over (every declared member) × (every emitting format) and collects <b>all</b>
    /// violations before failing, so one run tells the whole story instead of the first red line only.
    /// </summary>
    [Test]
    public async Task EveryFusedMember_IsEmittedOrRegisteredAsNotUsed()
    {
        var members = FusedMembers();
        await Assert
            .That(members.Count)
            .IsGreaterThanOrEqualTo(MinimumFusedMembers)
            .Because(
                $"reading {ContractRelativePath} found only {members.Count} method(s) on IFusedTokenReader "
                    + $"({string.Join(", ", members)}) — the parse is broken, so this guard is checking nothing"
            );

        var registry = Registry();
        var problems = new List<string>();

        foreach (var (format, folder, minimumFiles) in EmittingFormats)
        {
            var files = BaselineFiles(folder);
            await Assert
                .That(files.Length)
                .IsGreaterThanOrEqualTo(minimumFiles)
                .Because(
                    $"{folder} holds only {files.Length} *.g.cs file(s); an empty or mislocated baseline "
                        + "would make every \"calls it 0 times\" claim below true for the wrong reason"
                );

            foreach (var member in members)
            {
                var callSites = CallSites(files, member);
                var row = registry.FirstOrDefault(r => r.Format == format && r.Member == member);

                if (row is null)
                {
                    if (callSites.Count == 0)
                        problems.Add(
                            $"{format}: `{member}` is declared on IFusedTokenReader, its generated baseline "
                                + $"calls it 0 times, and it is NOT registered as `{NotUsed}`. Either make the "
                                + $"generator emit it, or add this row to {RegistryRelativePath}: "
                                + $"`| {format} | {member} | {NotUsed} | <why this format does not use it> |`"
                        );
                    continue;
                }

                if (row.Status == NotUsed && callSites.Count > 0)
                    problems.Add(
                        $"{RegistryRelativePath}:{row.Line} registers {format}'s `{member}` as `{NotUsed}`, but "
                            + $"the baseline calls it at {Describe(callSites)} — the row is stale (the emission "
                            + $"landed: change it to `{Emitted}`) or the baseline was regenerated without "
                            + "updating the row"
                    );

                if (row.Status == Emitted && callSites.Count == 0)
                    problems.Add(
                        $"{RegistryRelativePath}:{row.Line} registers {format}'s `{member}` as `{Emitted}`, but "
                            + "the baseline calls it 0 times — the emission was reverted (register it as "
                            + $"`{NotUsed}` with the reason) or the baseline was not regenerated"
                    );

                if (row.Status == NotUsed && row.Reason.Length == 0)
                    problems.Add(
                        $"{RegistryRelativePath}:{row.Line} registers {format}'s `{member}` as `{NotUsed}` with "
                            + "an empty reason column — \"not used\" has to carry a checkable reason"
                    );
            }
        }

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? "every fused member is either emitted by a generator or registered as `不用` + reason"
                    : "a fused member is neither emitted nor registered as `不用` (or the registry disagrees "
                        + $"with the baseline). The registry is {RegistryRelativePath} — registration means one "
                        + $"row: `| <format> | <member> | {NotUsed} | <reason> |`\n  - "
                        + string.Join("\n  - ", problems)
            );
    }

    /// <summary>
    /// A row that names a typo'd member, an unlisted format or an unknown status guards <b>nothing</b> — it
    /// would sit in the table looking like a decision while the real member stays unregistered. This is the
    /// (cheap) test that makes the registry a ledger instead of prose.
    /// </summary>
    [Test]
    public async Task RegistryRows_OnlyNameRealMembersRealFormatsAndRealStatuses()
    {
        var members = FusedMembers();
        var formats = EmittingFormats.Select(f => f.Format).ToArray();
        var problems = new List<string>();

        foreach (var row in Registry())
        {
            if (!members.Contains(row.Member, StringComparer.Ordinal))
                problems.Add(
                    $"{RegistryRelativePath}:{row.Line}: `{row.Member}` is not a member of IFusedTokenReader "
                        + $"(declared today: {string.Join(", ", members)}) — a misspelled row registers nothing"
                );

            if (!formats.Contains(row.Format, StringComparer.Ordinal))
                problems.Add(
                    $"{RegistryRelativePath}:{row.Line}: format `{row.Format}` is not checked by this guard "
                        + $"(checked: {string.Join(", ", formats)}) — add it to EmittingFormats in this test if "
                        + "that format now emits fused reads"
                );

            if (row.Status is not (Emitted or NotUsed))
                problems.Add(
                    $"{RegistryRelativePath}:{row.Line}: status `{row.Status}` is neither `{Emitted}` nor "
                        + $"`{NotUsed}` — only those two values are read"
                );
        }

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? "every registry row names a real member, a checked format and a real status"
                    : $"the registry has rows that guard nothing:\n  - {string.Join("\n  - ", problems)}"
            );
    }

    /// <summary>
    /// The other direction: a format that never pursued emission must stay at zero fused calls. If one of these
    /// starts emitting, that is a real change of strategy — it belongs in <see cref="EmittingFormats"/> and, for
    /// each of its members, in a registry row.
    /// </summary>
    [Test]
    public async Task FormatsThatNeverPursuedEmission_CallNoFusedRead()
    {
        var members = FusedMembers();
        var problems = new List<string>();

        foreach (var (format, folder, minimumFiles) in NeverEmittingFormats)
        {
            var files = BaselineFiles(folder);
            await Assert
                .That(files.Length)
                .IsGreaterThanOrEqualTo(minimumFiles)
                .Because(
                    $"{folder} holds only {files.Length} *.g.cs file(s); an empty baseline would make this "
                        + "assertion true for the wrong reason"
                );

            foreach (var member in members)
            {
                var callSites = CallSites(files, member);
                if (callSites.Count > 0)
                    problems.Add(
                        $"{format}'s generated baseline calls `{member}` at {Describe(callSites)} — this format "
                            + "never pursued fused emission, so either the emission is new (add "
                            + $"`(\"{format}\", \"{folder}\", {files.Length})` to EmittingFormats and register every member "
                            + $"it in {RegistryRelativePath}) or it emitted by accident"
                    );
            }
        }

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? "TOML/YAML/INI still call no fused member"
                    : $"a format that never pursued emission now emits one:\n  - {string.Join("\n  - ", problems)}"
            );
    }

    // ── the three inputs: the contract, the baselines, the registry ──

    /// <summary>
    /// The members of <c>IFusedTokenReader</c>, read from the interface body as text. Read rather than listed
    /// here on purpose: a member added to the interface (a new fused read) must enter this check <b>without</b>
    /// anyone remembering to update the test — that is the whole point of the guard.
    /// <para>
    /// Properties (<c>bool HasCountedCollections { get; }</c>) are skipped by construction: only a declaration
    /// with a parameter list, <c>bool Name(</c>, is a fused read.
    /// </para>
    /// </summary>
    static List<string> FusedMembers()
    {
        var text = File.ReadAllText(Path.Combine(TestRepo.Root(), ContractRelativePath));
        var start = text.IndexOf("public interface IFusedTokenReader", StringComparison.Ordinal);
        if (start < 0)
            return [];

        // The interface body ends at the first line that is a lone closing brace.
        var end = text.IndexOf("\n}", start, StringComparison.Ordinal);
        var body = end < 0 ? text[start..] : text[start..end];

        var names = new List<string>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("bool ", StringComparison.Ordinal))
                continue;

            var paren = line.IndexOf('(');
            if (paren < 0)
                continue;

            var name = line["bool ".Length..paren].Trim();
            if (name.Length == 0 || !name.All(char.IsLetterOrDigit))
                continue;

            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }

        return names;
    }

    /// <summary>Every <c>*.g.cs</c> under one format's baseline directory.</summary>
    static string[] BaselineFiles(string folder)
    {
        var directory = Path.Combine(
            TestRepo.Root(),
            "tests",
            "FormatCapabilities.Probe",
            "generated-baseline",
            folder
        );
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.g.cs", SearchOption.AllDirectories)
            : [];
    }

    /// <summary>
    /// Where one member is called in a set of baseline files, as <c>file:line</c>. The open paren is part of the
    /// needle, so <c>TryReadInt32(</c> does not match the unrelated <c>TryReadInt32Array(</c>/<c>TryReadInt32List(</c>
    /// bulk helpers.
    /// </summary>
    static List<string> CallSites(string[] files, string member)
    {
        var needle = member + "(";
        var sites = new List<string>();

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(needle, StringComparison.Ordinal))
                    sites.Add($"{Path.GetFileName(file)}:{i + 1}");
        }

        return sites;
    }

    /// <summary>First few call sites, for a failure message that does not paste 20 file names.</summary>
    static string Describe(List<string> sites) =>
        sites.Count <= 3
            ? string.Join(", ", sites)
            : string.Join(", ", sites.Take(3)) + $", … ({sites.Count} sites)";

    /// <summary>
    /// One row of the registry table. A reference type on purpose: <c>FirstOrDefault</c> has to answer
    /// <see langword="null"/> for "no row at all", which a record struct cannot do (it would answer the
    /// all-empty row), and "no row" is exactly the case this guard is about.
    /// </summary>
    sealed record RegistryRow(string Format, string Member, string Status, string Reason, int Line);

    /// <summary>
    /// Parses the first table of <c>docs/fused-emission-registry.md</c>: cells are
    /// <c>| format | member | status | reason |</c>. Anything that is not a table row — prose, the header, the
    /// <c>|---|</c> separator — is skipped, so the file can stay readable.
    /// </summary>
    static List<RegistryRow> Registry()
    {
        var path = Path.Combine(TestRepo.Root(), RegistryRelativePath);
        var rows = new List<RegistryRow>();
        if (!File.Exists(path))
            return rows;

        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith('|'))
                continue;

            var cells = line.Split('|');
            if (cells.Length < 5)
                continue;

            var format = cells[1].Trim();
            if (
                format.Length == 0
                || format == "格式"
                || format.StartsWith("---", StringComparison.Ordinal)
            )
                continue;

            rows.Add(
                new RegistryRow(format, cells[2].Trim(), cells[3].Trim(), cells[4].Trim(), i + 1)
            );
        }

        return rows;
    }
}
