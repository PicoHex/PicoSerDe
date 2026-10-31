using System.Text.RegularExpressions;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// A doc that points at code has to point at something that will still be there next week.
/// <para>
/// <b>What went wrong without it.</b> <c>api/PENDING-CHANGES.md</c> and
/// <c>docs/fused-emission-registry.md</c> identified members of <c>JsonReader.cs</c> by line number:
/// <c>JsonReader.cs:758</c> for <c>HasCountedCollections</c>, <c>:792</c> for <c>TryReadInt32</c>,
/// <c>:862</c>/<c>:905</c>/<c>:919</c> for the other three fused reads. One commit inserted 33 lines in that
/// file (<c>a5c0dfb</c>, the strict float-literal guard) and every one of those six pointers began naming a
/// blank line, a lone <c>{</c>, a <c>default:</c> label and a <c>return false;</c> comment — while the whole
/// suite stayed green, because a line number in prose is not a value any test can compare. "Silently stale" is
/// the same failure mode <see cref="FusedMemberEmissionGuardTests"/> exists for, one level up: the doc is the
/// artifact, so the doc gets the guard.
/// </para>
/// <para>
/// <b>The rule, in two halves.</b> The two <b>ledgers</b> — the files whose whole job is to say where a thing
/// is — name it by <b>symbol</b> (<c>JsonReader.TryReadInt32</c>) or by the <b>baseline entry</b> itself
/// (<c>M:MsgPackWriter.WriteString(...)</c>), never by line: a member name is found with one <c>grep</c> and
/// survives any insertion above it. Everywhere else a line pointer is legitimate (a paragraph may need to
/// say "this range of that file"), so it is <b>checked</b> instead of forbidden: it has to resolve to a file
/// in the tree, it has to be inside that file, and the line it lands on has to look like an anchor rather
/// than a blank line, a lone brace or a bare statement.
/// </para>
/// <para>
/// <b>What this cannot do, stated instead of implied.</b> The checker does not know <i>which</i> member a
/// pointer meant — the docs name the symbol in the sentence before the pointer as often as next to it, so
/// deriving it mechanically would produce false reds (measured: <c>AGENTS.md:59</c> names
/// <c>YamlNestedListDiagnosticTests.Yaml_NestedNullableMember…</c> and then points at
/// <c>NestedNullableElementTests.cs:85</c>, a different file on purpose). So it catches the two failure modes
/// the review actually found — a pointer past the end of the file, and a pointer that landed on a line which
/// is blank / a brace / a statement — which is five of the six, and the sixth (a <i>range</i> whose first line
/// is a doc comment) is exactly why the ledgers are then required to cite symbols
/// (<see cref="LedgerDocs_PointAtSymbolsAndEntriesNeverAtLineNumbers"/>) and why those citations are required
/// to be real (<see cref="SymbolCitationsInTheLedgers_NameSomethingThatExists"/>).
/// </para>
/// </summary>
public class DocPointerGuardTests
{
    /// <summary>The queue of public API additions — a ledger, so it cites, it does not point.</summary>
    private const string PendingLedger = "api/PENDING-CHANGES.md";

    /// <summary>The fused-emission registry — the other ledger, guarded by the same rules.</summary>
    private const string Registry = "docs/fused-emission-registry.md";

    private static readonly string[] Ledgers = [PendingLedger, Registry];

    /// <summary>
    /// Docs that describe <b>the tree as it was on a date</b>: a plan, a spec, a release note, an audit
    /// report, the probe evidence of one round. A pointer there is a pointer into a snapshot, so a line that
    /// moved since is <i>expected</i> and is verified at the commit or tag the doc names — not here. Adding a
    /// folder to this list is the one way to opt a doc out; the alternative (checking them) would demand
    /// editing history every time a refactor moves a line.
    /// </summary>
    private static readonly string[] DatedSnapshotFolders =
    [
        "docs/release-notes/",
        "docs/superpowers/",
        "docs/r2/",
    ];

    /// <summary>Dated documents that are not in a dated folder (<c>docs/bug-audit-report-2026-08-16.md</c>).</summary>
    private const string DatedSnapshotFileNamePrefix = "bug-audit-";

    /// <summary>
    /// Directories that are not the tree: build output, package cache, local process ledgers and — the one
    /// that matters here — other worktrees, each of which carries a full copy of these very docs.
    /// </summary>
    private static readonly string[] NotTheTree = ["bin", "obj", "artifacts", "TestResults"];

    /// <summary>
    /// How many docs the scan has to <b>read</b>. Today it reads the root <c>README.md</c>, <c>AGENTS.md</c>,
    /// <c>api/PENDING-CHANGES.md</c>, <c>docs/**</c> and every sub-<c>README.md</c> — comfortably more than this.
    /// The floor is deliberately a count of <i>docs</i> and not of pointers: converting the remaining pointers to
    /// symbol names is a legitimate edit that would take the pointer count to zero, while <c>docs/</c> going
    /// missing or the discovery rule breaking is not.
    /// </summary>
    private const int MinimumDocsRead = 10;

    /// <summary>
    /// The ledgers cite at least this many <c>Type.Member</c> symbols that resolve to a file. Same reason:
    /// a citation syntax change must not turn <see cref="SymbolCitationsInTheLedgers_NameSomethingThatExists"/>
    /// into a test of nothing.
    /// </summary>
    private const int MinimumSymbolCitations = 6;

    /// <summary>
    /// <c>&lt;file&gt;.cs:758</c>, <c>&lt;file&gt;.cs:792-797</c>, <c>&lt;file&gt;.public.txt:83</c> — the shape
    /// both ledgers used. The path part is optional-length so a bare <c>JsonReader.cs:27</c> matches too.
    /// </summary>
    private static readonly Regex LinePointer = new(
        @"(?<path>[A-Za-z0-9_./\-]*[A-Za-z0-9_]+\.(?:cs|txt)):"
            + @"(?<start>[0-9]+)(?:[ \t]*[-\u2013][ \t]*(?<end>[0-9]+))?",
        RegexOptions.CultureInvariant
    );

    /// <summary>
    /// The shorthand a cell used after it had already named the file: <c>（:758）</c> and <c>`:8`</c>. Only
    /// accepted behind a backtick or an opening bracket, which is why the copyright year in a code span
    /// (<c>2026.09:14</c>) is not a pointer.
    /// </summary>
    private static readonly Regex BareLinePointer = new(
        @"(?<=[`\uFF08(]):[0-9]+(?:[ \t]*[-\u2013][ \t]*[0-9]+)?",
        RegexOptions.CultureInvariant
    );

    /// <summary>
    /// What a pointer must <b>not</b> land on: a control-flow line. A blank line, a lone brace and a
    /// <c>return</c>/<c>default:</c> statement are the four shapes all six stale pointers landed on, and none
    /// of them is ever what a doc means to cite.
    /// </summary>
    private static readonly Regex StatementLine = new(
        @"^(?:default|break|continue|return|throw|goto|else|yield|case|await|do|using|get|set)\b",
        RegexOptions.CultureInvariant
    );

    /// <summary>A backticked <c>Owner.Member</c> — the citation form the ledgers are required to use.</summary>
    private static readonly Regex SymbolCitation = new(
        @"`(?<owner>[A-Za-z_][A-Za-z0-9_]*)\.(?<member>[A-Za-z_][A-Za-z0-9_]*)`",
        RegexOptions.CultureInvariant
    );

    /// <summary>
    /// File extensions, so <c>`MsgPackReader.cs`</c> is read as a file name and not as a citation of a
    /// member called <c>cs</c>.
    /// </summary>
    private static readonly string[] FileExtensions =
    [
        "cs",
        "txt",
        "md",
        "ps1",
        "json",
        "props",
        "targets",
        "csproj",
        "slnx",
        "yml",
        "g",
    ];

    // ── 1. the ledgers: symbol or nothing ───────────────────────────────────────────

    /// <summary>
    /// The two ledgers must never carry a line number again, in either direction: no
    /// <c>&lt;file&gt;.cs:&lt;line&gt;</c> / <c>&lt;file&gt;.public.txt:&lt;line&gt;</c>, and none of the
    /// shorthand <c>（:758）</c> / <c>`:8`</c> forms either. A pointer that is <i>correct today</i> is still a
    /// red test: the failure this rule exists for was six correct pointers becoming six wrong ones in a
    /// commit that touched neither file.
    /// </summary>
    [Test]
    public async Task LedgerDocs_PointAtSymbolsAndEntriesNeverAtLineNumbers()
    {
        var problems = new List<string>();

        foreach (var ledger in Ledgers)
        {
            var path = Path.Combine(TestRepo.Root(), ledger);
            if (!File.Exists(path))
            {
                problems.Add(
                    $"{ledger} does not exist — this guard is reading a ledger that moved"
                );
                continue;
            }

            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in LinePointer.Matches(lines[i]))
                    problems.Add(
                        $"{ledger}:{i + 1}: `{match.Value}` is a line pointer — cite the symbol "
                            + "(`Type.Member`, e.g. `JsonReader.TryReadInt32`) or the baseline entry itself, "
                            + "and let `grep -n` answer \"where\""
                    );

                foreach (Match match in BareLinePointer.Matches(lines[i]))
                    problems.Add(
                        $"{ledger}:{i + 1}: `{match.Value}` is a line pointer in shorthand — the shorthand is "
                            + "the same rot with less to grep; name the member instead"
                    );
            }
        }

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? "both ledgers identify code by symbol and baselines by entry, not by line"
                    : "a ledger points at a line number. `JsonReader.cs` grew by 33 lines in `a5c0dfb` and "
                        + "invalidated six pointers in these two files with no test turning red; a symbol name "
                        + "cannot be invalidated that way\n  - "
                        + string.Join("\n  - ", problems)
            );
    }

    // ── 2. pointers that remain: inside the file, on an anchor ───────────────────────

    /// <summary>
    /// <b>The scanner, pinned to its own shapes.</b> A floor on how many <i>docs</i> were read (below) says the
    /// scan ran; it cannot say the scan still <i>recognises</i> a line pointer, because a doc set with no pointers
    /// left is a legitimate end state. So the shapes are pinned here, on strings, in the forms these docs actually
    /// carried: the pointers that went stale (<c>JsonReader.cs:758</c>, <c>JsonReader.cs:792-797</c>), the baseline
    /// form (<c>api/PicoMsgPack.public.txt:83</c>) and the shorthand a cell used after naming the file
    /// (<c>（:758）</c>, <c>`:8`</c>). A pattern rewritten to match nothing fails here instead of quietly guarding
    /// nothing.
    /// </summary>
    [Test]
    public async Task ThePointerScannersRecogniseTheShapesTheyGuard()
    {
        string[] mustMatchPointer =
        [
            "`JsonReader.cs:27`",
            "`PicoIni/src/PicoIni/IniReader.cs:15`",
            "`JsonReader.cs:792-797`",
            "`JsonReader.cs:577–616`",
            "`api/PicoMsgPack.public.txt:83`",
        ];
        string[] mustNotMatchPointer =
        [
            "`JsonReader.cs`",
            "`docs/fused-emission-registry.md`",
            "`scripts/release.ps1:309-316`",
            "`2026.10.27-review`",
            "v2026.11.0 -> v2026.11.1",
        ];

        var missed = new List<string>();
        foreach (var text in mustMatchPointer)
            if (!LinePointer.IsMatch(text))
                missed.Add(text);

        var spurious = new List<string>();
        foreach (var text in mustNotMatchPointer)
            if (LinePointer.IsMatch(text))
                spurious.Add(text);

        await Assert
            .That(missed)
            .IsEmpty()
            .Because(
                $"the line-pointer pattern no longer recognises {string.Join(", ", missed)} — it would let a "
                    + "stale pointer through in silence"
            );
        await Assert
            .That(spurious)
            .IsEmpty()
            .Because(
                $"the line-pointer pattern now matches {string.Join(", ", spurious)}, which is not a pointer"
            );

        // The range and the shorthand carry the same information as the single line, so they are part of the
        // contract too: a range has to yield both ends, or `792-797` would be checked as line 792 only.
        var range = LinePointer.Match("`JsonReader.cs:792-797`");
        await Assert
            .That($"{range.Groups["start"].Value}-{range.Groups["end"].Value}")
            .IsEqualTo("792-797")
            .Because(
                "a range pointer has to give both ends; checking only the first line would miss the rot"
            );

        await Assert
            .That(BareLinePointer.IsMatch("（:758）"))
            .IsTrue()
            .Because("the shorthand a registry cell used after naming the file is still a pointer");
        await Assert
            .That(BareLinePointer.IsMatch("2026.09:14"))
            .IsFalse()
            .Because(
                "a clock/date in a code span is not a pointer; the shorthand only counts behind a bracket"
            );
    }

    /// <summary>
    /// Every <c>&lt;file&gt;.cs:&lt;line&gt;</c> in a doc that makes claims about <b>today's</b> tree has to
    /// resolve to a file in the tree, sit inside that file, and land on something that could be an anchor.
    /// Collects <b>all</b> violations before failing, so one run tells the whole story.
    /// </summary>
    [Test]
    public async Task RemainingLinePointers_ResolveInsideTheFileAndLandOnAnAnchor()
    {
        var root = TestRepo.Root();
        var codeFiles = CodeFileIndex(root);
        var problems = new List<string>();
        var withPointers = new List<string>();
        var docsRead = 0;
        var pointers = 0;

        foreach (var doc in ScannedDocs(root))
        {
            docsRead++;
            var lines = File.ReadAllLines(Path.Combine(root, doc));
            var found = false;

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in LinePointer.Matches(lines[i]))
                {
                    pointers++;
                    found = true;

                    var documented = match.Groups["path"].Value;
                    var start = int.Parse(match.Groups["start"].Value);
                    var end = match.Groups["end"].Success
                        ? int.Parse(match.Groups["end"].Value)
                        : start;
                    var designation =
                        $"{doc}:{i + 1} -> {documented}:{start}"
                        + (end == start ? string.Empty : $"-{end}");
                    var target = Resolve(root, documented, codeFiles, out var why);
                    if (target is null)
                    {
                        problems.Add($"{designation}: {why}");
                        continue;
                    }

                    var targetLines = File.ReadAllLines(target);
                    if (end < start)
                    {
                        problems.Add(
                            $"{designation}: the range ends before it starts — nothing to look at"
                        );
                        continue;
                    }

                    if (start > targetLines.Length || end > targetLines.Length)
                    {
                        problems.Add(
                            $"{designation}: {documented} has {targetLines.Length} line(s) — the pointer is past "
                                + "its end, so it names nothing"
                        );
                        continue;
                    }

                    var anchor = targetLines[(start - 1)..end];
                    if (!anchor.Any(LooksLikeAnAnchor))
                        problems.Add(
                            $"{designation}: lands on {Describe(anchor)} — not an anchor. The file moved under "
                                + "the pointer (blank line, lone brace and bare statement are the shapes a "
                                + "stale line number lands on); cite the member instead"
                        );
                }
            }

            if (found)
                withPointers.Add(doc);
        }

        await Assert
            .That(docsRead)
            .IsGreaterThanOrEqualTo(MinimumDocsRead)
            .Because(
                $"the scan read only {docsRead} doc(s) — `docs/`, `api/` or a sub-README went missing, so nothing "
                    + "below is being checked"
            );

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? $"{pointers} line pointer(s) in {withPointers.Count} doc(s) "
                        + $"({string.Join(", ", withPointers)}) all resolve to an anchor"
                    : $"a doc points at a line that is not there:\n  - {string.Join("\n  - ", problems)}"
            );
    }

    // ── 3. the citations the ledgers now use have to name something real ─────────────

    /// <summary>
    /// The other half of "cite the symbol": a citation that names a member which does not exist is worse
    /// than a stale line number, because it <i>looks</i> checkable. A member rename that forgets the ledger
    /// therefore fails here — that is the durability the symbol form is supposed to buy.
    /// </summary>
    [Test]
    public async Task SymbolCitationsInTheLedgers_NameSomethingThatExists()
    {
        var root = TestRepo.Root();
        var codeFiles = CodeFileIndex(root);
        var problems = new List<string>();
        var checkedCitations = 0;

        foreach (var ledger in Ledgers)
        {
            var lines = File.ReadAllLines(Path.Combine(root, ledger));

            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in SymbolCitation.Matches(lines[i]))
                {
                    var owner = match.Groups["owner"].Value;
                    var member = match.Groups["member"].Value;

                    // `MsgPackReader.cs` is a file name in a code span, not `MsgPackReader.cs` the member.
                    if (FileExtensions.Contains(member, StringComparer.OrdinalIgnoreCase))
                        continue;

                    // A type is located by its file: `IniReader.HasCountedCollections` -> IniReader.cs.
                    // A namespace-qualified owner (`PicoJetson.JsonReader`) resolves on its last segment.
                    var target =
                        Single(codeFiles, owner + ".cs") ?? Single(codeFiles, member + ".cs");
                    if (target is null)
                        continue; // not a type this repo keeps in a file of its own: nothing to check

                    checkedCitations++;
                    if (
                        !File.ReadAllText(Path.Combine(root, target))
                            .Contains(member, StringComparison.Ordinal)
                    )
                        problems.Add(
                            $"{ledger}:{i + 1}: `{owner}.{member}` is cited, but {target} does not mention "
                                + $"`{member}` — the citation outlived the member (renamed? moved to another file?)"
                        );
                }
            }
        }

        await Assert
            .That(checkedCitations)
            .IsGreaterThanOrEqualTo(MinimumSymbolCitations)
            .Because(
                $"only {checkedCitations} citation(s) resolved to a file — the ledgers stopped citing "
                    + "`Type.Member`, so this guard is checking nothing"
            );

        await Assert
            .That(problems)
            .IsEmpty()
            .Because(
                problems.Count == 0
                    ? $"{checkedCitations} symbol citation(s) in the ledgers all name a member that exists"
                    : $"a ledger cites a member that is not there:\n  - {string.Join("\n  - ", problems)}"
            );
    }

    // ── inputs: the doc set, the code index, one pointer's target ────────────────────

    /// <summary>
    /// Docs that make claims about the tree as it is right now: a README, the agent orientation, and anything
    /// under <c>api/</c> or <c>docs/</c> that is not a dated snapshot. Discovered rather than listed, so a new
    /// doc enters the check by itself.
    /// </summary>
    private static IEnumerable<string> ScannedDocs(string root)
    {
        foreach (var relative in TreeFiles(root, "*.md"))
        {
            if (
                DatedSnapshotFolders.Any(folder =>
                    relative.StartsWith(folder, StringComparison.Ordinal)
                )
            )
                continue;
            if (
                Path.GetFileName(relative)
                    .StartsWith(DatedSnapshotFileNamePrefix, StringComparison.Ordinal)
            )
                continue;

            var name = Path.GetFileName(relative);
            var claimsToday =
                name is "README.md" or "AGENTS.md"
                || relative.StartsWith("api/", StringComparison.Ordinal)
                || relative.StartsWith("docs/", StringComparison.Ordinal);
            if (claimsToday)
                yield return relative;
        }
    }

    /// <summary>
    /// Every file matching <paramref name="pattern"/> under the repository root, as repository-relative
    /// paths, with the directories that are not the tree pruned <b>before</b> they are descended into — a
    /// sibling worktree holds a full copy of the docs and would otherwise be scanned as if it were this tree.
    /// </summary>
    private static IEnumerable<string> TreeFiles(string root, string pattern)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (IsTheTree(Path.GetFileName(child)))
                    pending.Push(child);

            foreach (var file in Directory.EnumerateFiles(directory, pattern))
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
        }
    }

    /// <summary><c>bin</c>, <c>obj</c>, any dot-directory (so a sibling worktree's copy is not scanned), &amp;c.</summary>
    private static bool IsTheTree(string segment) =>
        !segment.StartsWith('.') && !NotTheTree.Contains(segment);

    /// <summary>Every <c>*.cs</c> in the tree, keyed by file name, so a doc may write a bare <c>JsonReader.cs</c>.</summary>
    private static Dictionary<string, List<string>> CodeFileIndex(string root)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var relative in TreeFiles(root, "*.cs"))
        {
            var name = Path.GetFileName(relative);
            if (!index.TryGetValue(name, out var paths))
                index[name] = paths = [];
            paths.Add(relative);
        }

        return index;
    }

    /// <summary>
    /// The one file a doc's path means: the path as written when it is a real relative path, otherwise the
    /// single file in the tree with that file name. Two candidates is a red, not a guess — a doc that names
    /// <c>Reader.cs</c> when four of them exist is not checkable.
    /// </summary>
    private static string? Resolve(
        string root,
        string documented,
        Dictionary<string, List<string>> codeFiles,
        out string why
    )
    {
        if (File.Exists(Path.Combine(root, documented)))
        {
            why = string.Empty;
            return Path.Combine(root, documented);
        }

        if (!codeFiles.TryGetValue(Path.GetFileName(documented), out var candidates))
        {
            why =
                $"{documented} does not exist in the tree and no file is named that — the doc names a file "
                + "that is gone (renamed? deleted?)";
            return null;
        }

        if (candidates.Count > 1)
        {
            why =
                $"{documented} is ambiguous: {candidates.Count} files are named that "
                + $"({string.Join(", ", candidates.Take(4))}…) — write the path from the repository root";
            return null;
        }

        why = string.Empty;
        return Path.Combine(root, candidates[0]);
    }

    /// <summary>The single entry of an index list, or <see langword="null"/> when absent or ambiguous.</summary>
    private static string? Single(Dictionary<string, List<string>> index, string key) =>
        index.TryGetValue(key, out var paths) && paths.Count == 1 ? paths[0] : null;

    /// <summary>
    /// Could a doc anchor on this line? Blank, punctuation-only (a lone <c>{</c>, <c>}</c>, <c>);</c>) and
    /// control-flow statements are the shapes a line pointer drifts onto, and no doc ever means to cite one.
    /// </summary>
    private static bool LooksLikeAnAnchor(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || !trimmed.Any(char.IsLetter))
            return false;
        return !StatementLine.IsMatch(trimmed);
    }

    /// <summary>First non-blank line of what a pointer landed on, for a failure message that is readable.</summary>
    private static string Describe(string[] landed)
    {
        var first = landed.FirstOrDefault(line => line.Trim().Length > 0);
        var shown = first is null
            ? "(every line blank)"
            : $"\"{first.Trim()[..Math.Min(60, first.Trim().Length)]}\"";
        return landed.Length == 1 ? shown : $"{shown} (+{landed.Length - 1} more line(s))";
    }
}
