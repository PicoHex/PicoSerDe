namespace PicoToml.Tests;

// R2 Task 0 / D-9: an empty table ("[V]" with no key under it) made the generated
// deserializer spin forever.
//
// The loop shape in every emitted TOML deserializer is
//
//     r.Read();
//     while (true) {
//         if (PropertyName) { …; if (!r.Read()) break; continue; }
//         if (ObjectStart)  { …;                     continue; }   // ← no advance
//         if (!r.Read()) break;
//     }
//
// The `ObjectStart` branch advanced nothing, and `TomlReader` kept re-reporting the
// previous token when `Read()` failed at EOF, so the loop kept re-dispatching the same
// `ObjectStart` for ever. `[V]` + EOF hangs; `[V]` + a following `[W]` hangs too (the
// dict loop's lookahead leaves the same stale token); `[V]` followed by `a = 7` did not
// hang, because the token left over there is a `PropertyName`, i.e. one the guarded
// branch owns.
//
// These tests are the contract from the R2 plan (W2's measured matrix; all six "HANG"
// rows reproduced before the fix). Every operation runs on its own thread with a bounded
// wait: a regression must be reported as a timeout, never by hanging the test runner.
//
// Models live at namespace level: the source generator skips nested types.
public sealed class EmptyTableDictionaries
{
    public Dictionary<string, int> V { get; set; } = new();
    public Dictionary<string, int> W { get; set; } = new();
}

/// <summary>
/// The D-9 matrix: six inputs that used to hang and two that must keep working.
/// </summary>
public class TomlEmptyTableHangTests
{
    /// <summary>Nothing may take longer than this, or it is a hang (D-9), not a slow test.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    // ── the six HANG rows ────────────────────────────────────────────────────────

    [Test]
    [Arguments("[V]\n")]
    [Arguments("[V]\r\n")]
    [Arguments("[V]\n\n")]
    [Arguments("[V]\n# comment\n")]
    public async Task EmptyTableFollowedByEndOfDocument_ReturnsEmptyDictionary(string input)
    {
        var model = await DeserializeWithinBudget(Encoding.UTF8.GetBytes(input));

        await Assert.That(model.V.Count).IsEqualTo(0);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("[V]")]
    public async Task EmptyTableWithoutTrailingNewline_ReturnsEmptyDictionary(string input)
    {
        var model = await DeserializeWithinBudget(Encoding.UTF8.GetBytes(input));

        await Assert.That(model.V.Count).IsEqualTo(0);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("[V]\na = 7\n[W]\n")]
    public async Task EmptyTableAfterPopulatedTable_ReturnsWithEmptySecondTable(string input)
    {
        var model = await DeserializeWithinBudget(Encoding.UTF8.GetBytes(input));

        await Assert.That(model.V.Count).IsEqualTo(1);
        await Assert.That(model.V["a"]).IsEqualTo(7);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    // ── the two regression anchors: they pass today and must keep passing ─────────

    [Test]
    [Arguments("[V]\na = 7\n")]
    public async Task PopulatedTable_StillParses(string input)
    {
        var model = await DeserializeWithinBudget(Encoding.UTF8.GetBytes(input));

        await Assert.That(model.V.Count).IsEqualTo(1);
        await Assert.That(model.V["a"]).IsEqualTo(7);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("")]
    public async Task EmptyDocument_StillReturnsDefaultInstance(string input)
    {
        var model = await DeserializeWithinBudget(Encoding.UTF8.GetBytes(input));

        await Assert.That(model.V.Count).IsEqualTo(0);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    // ── guards for the two ways a "fix" can be wrong ─────────────────────────────

    /// <summary>
    /// The lookahead trap: a section's dict loop stops on the NEXT table header, so a
    /// blind <c>Read()</c> added to the <c>ObjectStart</c> branch would swallow that
    /// header and silently drop <c>[W]</c>'s contents instead of hanging.
    /// </summary>
    [Test]
    public async Task TwoPopulatedTables_BothKeepTheirKeys()
    {
        var model = await DeserializeWithinBudget(
            Encoding.UTF8.GetBytes("[V]\na = 7\n[W]\nb = 2\n")
        );

        await Assert.That(model.V.Count).IsEqualTo(1);
        await Assert.That(model.V["a"]).IsEqualTo(7);
        await Assert.That(model.W.Count).IsEqualTo(1);
        await Assert.That(model.W["b"]).IsEqualTo(2);
    }

    /// <summary>
    /// The round trip this defect is about: writing an empty dictionary emits a bare
    /// <c>[V]</c> table, and reading that back must give the empty dictionary, not a hang.
    /// (R2 Task 2/3 make the writer emit more of these.)
    /// </summary>
    [Test]
    public async Task EmptyDictionary_RoundTripsThroughItsOwnWireForm()
    {
        var bytes = TomlSerializer.SerializeToUtf8Bytes(new EmptyTableDictionaries());
        await Assert.That(Encoding.UTF8.GetString(bytes)).IsEqualTo("[V]\n[W]\n");

        var model = await DeserializeWithinBudget(bytes);
        await Assert.That(model.V.Count).IsEqualTo(0);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The same document through the streaming entry point (<c>DeserializeFromStreamAsync</c>
    /// uses the generated per-chunk delegate over a resumable reader). It hung on the very
    /// same input, and it is the path a future capability row may use, so it is pinned here
    /// even though the defect was found on the buffered path.
    /// </summary>
    [Test]
    public async Task EmptyTableThroughStreamingEntryPoint_ReturnsEmptyDictionary()
    {
        var bytes = Encoding.UTF8.GetBytes("[V]\n");
        var model = await RunWithinBudget(
            () =>
                TomlSerializer
                    .DeserializeFromStreamAsync<EmptyTableDictionaries>(new MemoryStream(bytes))
                    .AsTask()
                    .GetAwaiter()
                    .GetResult(),
            "[V]\\n via DeserializeFromStreamAsync"
        );

        await Assert.That(model.V.Count).IsEqualTo(0);
        await Assert.That(model.W.Count).IsEqualTo(0);
    }

    // ── bounded execution ───────────────────────────────────────────────────────

    /// <summary>Deserializes <paramref name="bytes"/> with the buffered entry point.</summary>
    private static Task<EmptyTableDictionaries> DeserializeWithinBudget(byte[] bytes) =>
        RunWithinBudget(
            () => TomlSerializer.Deserialize<EmptyTableDictionaries>(bytes)!,
            Escape(Encoding.UTF8.GetString(bytes))
        );

    /// <summary>
    /// Runs <paramref name="operation"/> on a dedicated background thread and fails with an
    /// explicit <see cref="TimeoutException"/> if it has not returned within
    /// <see cref="Budget"/>. The abandoned thread is a background thread, so it cannot keep
    /// the test process alive; the event is deliberately not disposed on that path (the
    /// worker may still call <c>Set()</c>, and an exception on a foreign thread would kill
    /// the run instead of failing one test).
    /// </summary>
    private static Task<EmptyTableDictionaries> RunWithinBudget(
        Func<EmptyTableDictionaries> operation,
        string label
    )
    {
        EmptyTableDictionaries? result = null;
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
            Name = "toml-empty-table",
        };
        worker.Start();

        var finished = done.Wait(Budget);
        if (finished)
            done.Dispose();

        if (!finished)
            throw new TimeoutException(
                $"Deserialize did not return within {Budget.TotalSeconds}s for {label} "
                    + "— the reader is spinning (D-9)."
            );
        if (error is not null)
            throw new InvalidOperationException($"Deserialize threw for {label}", error);

        return Task.FromResult(result!);
    }

    /// <summary>Keeps control characters visible in failure messages.</summary>
    private static string Escape(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
