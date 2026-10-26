namespace PicoToml.Tests;

// R2 Task 0 / fix round 1 — the same spin as D-9, reached by a different input:
// a table header whose name is NOT in the model.
//
// Every emitted TOML deserializer dispatches a table header through
//     if (r.TokenType == TokenType.ObjectStart) {
//         var tbl = r.TablePath;                        // pure getter, no advance
//         if (TextHelpers.Eq(tbl, "V")) { … }           // ← the only Read() lives here
//         else if (TextHelpers.Eq(tbl, "W")) { … }
//         … guard … continue;
//     }
// "Table name not in the model" therefore issues no Read() at all: the token stays
// `ObjectStart`, `BytesConsumed` stays put, and the `continue` re-dispatches the very
// same token for ever. Real TOML files are full of such headers (`[tool.ruff]`,
// `[build-system]`), so "model in, real file in" was an unconditional hang.
//
// The same shape exists for `[[…]]` array-of-tables headers, where the polymorphic
// emitter even put the `continue` OUTSIDE the `Eq(TablePath, …)` chain: any
// `[[Unknown]]` header spun there (found by the tests below, not by the review).
//
// The fix is a *progress* guard (not the D-9 "reader is exhausted" guard): each branch
// records BytesConsumed on entry and, if `continue` would not have advanced the reader,
// issues exactly one Read() (and stops if that fails). The array-of-tables element
// loops get a local no-progress guard as well, so none of these loops depends on the
// reader clearing its token any more (verified by disabling that clearing: no hang).
//
// Every operation still runs on a background thread with a bounded wait, so a
// regression is reported as a timeout instead of hanging the runner.
public class UnknownTableItems
{
    public string Name { get; set; } = string.Empty;
}

public class UnknownTableListModel
{
    public string Title { get; set; } = string.Empty;
    public List<UnknownTableItems> Items { get; set; } = new();
}

[PicoSerializable]
[PicoDerivedType(typeof(UnknownTablePolyDerived), "poly_dict")]
public abstract class UnknownTablePolyBase { }

public class UnknownTablePolyDerived : UnknownTablePolyBase
{
    // A dict member is what makes the polymorphic deserializer emit the
    // `ObjectStart` branch that C-1 was found in ...
    public Dictionary<string, int> Map { get; set; } = new();

    // ... and an object list makes it emit the `ArrayStart` (`[[…]]`) branch.
    public List<UnknownTableItems> Items { get; set; } = new();
}

public class TomlUnknownTableHangTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    /// <summary>Prefix of every polymorphic payload below.</summary>
    private const string DiscriminatorLine = "$type = \"poly_dict\"\n";

    // ── the C-1 matrix: headers the model has no section for ─────────────────────

    [Test]
    [Arguments("[Unknown]\n")]
    [Arguments("[Unknown]")]
    [Arguments("[Unknown]\nb = 1\n")]
    [Arguments("[V]\n[Unknown]\nb = 1\n")]
    [Arguments("[tool.ruff]\nline-length = 88\n")]
    [Arguments("[build-system]\nrequires = [\"setuptools\"]\n")]
    public async Task UnknownTable_IsIgnoredInsteadOfHanging(string input)
    {
        var model = await Run(
            () => TomlSerializer.Deserialize<EmptyTableDictionaries>(bytes(input))!,
            Escape(input)
        );

        // Unknown sections are silently ignored (unchanged product behaviour): they
        // must not populate the model and must not hang.
        await Assert.That(model.V.Count).IsEqualTo(0);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The lookahead trap again, now with an unknown section in the middle: the
    /// progress guard must advance past <c>[Unknown]</c> without swallowing <c>[W]</c>.
    /// </summary>
    [Test]
    public async Task UnknownTableBetweenKnownTables_KeepsBothSections()
    {
        var model = await Run(
            () =>
                TomlSerializer.Deserialize<EmptyTableDictionaries>(
                    bytes("[V]\na = 7\n[Unknown]\nb = 1\n[W]\nc = 2\n")
                )!,
            "[V] a=7 [Unknown] b=1 [W] c=2"
        );

        await Assert.That(model.V.Count).IsEqualTo(1);
        await Assert.That(model.V["a"]).IsEqualTo(7);
        await Assert.That(model.W.Count).IsEqualTo(1);
        await Assert.That(model.W["c"]).IsEqualTo(2);
    }

    /// <summary>The streaming entry point emits the same branch and needs its own guard.</summary>
    [Test]
    public async Task UnknownTableThroughStreamingEntryPoint_DoesNotHang()
    {
        var model = await Run(
            () =>
                TomlSerializer
                    .DeserializeFromStreamAsync<EmptyTableDictionaries>(
                        new MemoryStream(bytes("[V]\n[Unknown]\nb = 1\n"))
                    )
                    .AsTask()
                    .GetAwaiter()
                    .GetResult(),
            "[V] [Unknown] b=1 via DeserializeFromStreamAsync"
        );

        await Assert.That(model.V.Count).IsEqualTo(0);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    /// <summary>The polymorphic deserializer has its own <c>ObjectStart</c> loop (C-1, third site).</summary>
    [Test]
    public async Task UnknownTableInPolymorphicPayload_DoesNotHang()
    {
        var result = await Run(
            () =>
                TomlSerializer.Deserialize<UnknownTablePolyBase>(
                    bytes("$type = \"poly_dict\"\n[Unknown]\nb = 1\n")
                )!,
            "$type=poly_dict [Unknown] b=1"
        );

        await Assert.That(result).IsTypeOf<UnknownTablePolyDerived>();
        await Assert.That(((UnknownTablePolyDerived)result).Map.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The polymorphic deserializer's own <c>[[…]]</c> branch (the third place fix round 1
    /// had to guard). One empty element for a known table, none for an unknown one.
    /// </summary>
    [Test]
    [Arguments("[[Items]]\n", 1)]
    [Arguments("[[Unknown]]\n", 0)]
    public async Task EmptyArrayOfTablesInPolymorphicPayload_TerminatesWithinBudget(
        string body,
        int expectedCount
    )
    {
        var result = await Run(
            () =>
                TomlSerializer.Deserialize<UnknownTablePolyBase>(bytes(DiscriminatorLine + body))!,
            "poly " + Escape(body)
        );

        await Assert.That(result).IsTypeOf<UnknownTablePolyDerived>();
        await Assert.That(((UnknownTablePolyDerived)result).Items.Count).IsEqualTo(expectedCount);
    }

    // ── I-3: empty array-of-tables, the loops that only stopped via the reader ───

    /// <summary>
    /// <c>[[Items]]</c> with nothing under it: the inner element loop stops because the
    /// reader reports a different token (D-9's <c>FailRead</c>), and since fix round 1 it
    /// also carries a local no-progress guard (it would otherwise grow the list unboundedly
    /// — an OOM, much harder to diagnose than a timeout). Both are pinned here: what
    /// changed in round 1 is that the loop no longer *relies* on the reader alone.
    /// </summary>
    [Test]
    [Arguments("[[Items]]\n", 1)]
    [Arguments("[[Items]]\n[[Items]]\n", 2)]
    [Arguments("[[Unknown]]\n", 0)]
    // Two elements, not one: the element loop does not re-check which table a
    // subsequent `[[…]]` header names, so the foreign header starts (and terminates)
    // a second, empty element. That is pre-existing behaviour, out of scope here —
    // what this test pins is that the loop terminates and stops growing.
    [Arguments("[[Items]]\nName = \"a\"\n[[Unknown]]\n", 2)]
    public async Task EmptyArrayOfTables_TerminatesWithinBudget(string input, int expectedCount)
    {
        var model = await Run(
            () => TomlSerializer.Deserialize<UnknownTableListModel>(bytes(input))!,
            Escape(input)
        );

        await Assert.That(model.Items.Count).IsEqualTo(expectedCount);
    }

    [Test]
    [Arguments("[[Items]]\n", 1)]
    [Arguments("[[Unknown]]\n", 0)]
    public async Task EmptyArrayOfTablesThroughStreamingEntryPoint_TerminatesWithinBudget(
        string input,
        int expectedCount
    )
    {
        var model = await Run(
            () =>
                TomlSerializer
                    .DeserializeFromStreamAsync<UnknownTableListModel>(
                        new MemoryStream(bytes(input))
                    )
                    .AsTask()
                    .GetAwaiter()
                    .GetResult(),
            Escape(input) + " via DeserializeFromStreamAsync"
        );

        await Assert.That(model.Items.Count).IsEqualTo(expectedCount);
    }

    // ── bounded execution ───────────────────────────────────────────────────────

    private static byte[] bytes(string input) => Encoding.UTF8.GetBytes(input);

    private static Task<T> Run<T>(Func<T> operation, string label) =>
        BoundedWait.Run(operation, label, Budget);

    private static string Escape(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}

/// <summary>
/// Runs an operation on its own background thread and fails with an explicit
/// <see cref="TimeoutException"/> when it does not return in time, so a hang shows up as
/// a failing test instead of a stuck runner. The abandoned thread is a background thread
/// (it cannot keep the process alive) and the event is deliberately not disposed on the
/// timeout path — the worker may still call <c>Set()</c> from a foreign thread.
/// </summary>
internal static class BoundedWait
{
    public static Task<T> Run<T>(Func<T> operation, string label, TimeSpan budget)
    {
        T? result = default;
        Exception? error = null;
        var done = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            try
            {
                result = operation();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        })
        {
            IsBackground = true,
            Name = "toml-hang-guard",
        };
        worker.Start();

        var finished = done.Wait(budget);
        if (finished)
            done.Dispose();

        if (!finished)
            throw new TimeoutException(
                $"Deserialize did not return within {budget.TotalSeconds}s for {label} "
                    + "— the reader loop is spinning (R2/D-9 family)."
            );
        if (error is not null)
            throw new InvalidOperationException($"Deserialize threw for {label}", error);

        return Task.FromResult(result!);
    }
}
