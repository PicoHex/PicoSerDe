// `YamlOptions.Indented` gives a block sequence one level for its dash and TWO for the
// content under it (`YamlWriter.WriteStartSequenceBlock`), so a list of MAPPINGS reads back
// as the list of mappings it was written from. `IndentedTests` pinned the scalar list
// (`\n  - a`) and its round trip, and nothing pinned the mapping item at all — the ablation
// run showed what that costs: balancing those two `_depth += 2`/`-= 2` back to one level
// (`? 1 : 1`) kept every test in this project green while the writer emitted
//
//     Items:
//         -
//         Name: a      <- the member sits in the dash's own column
//         N: 1
//
// whose items our own reader silently dropped (Items=0, no diagnostic) and which PyYAML
// refused with a ParserError. "The suite is green" therefore said nothing about these
// bytes; three separate claims are pinned here so it does:
//
//   A. the document round-trips with BOTH elements (structural: a dropped item shows up here)
//   B. the exact bytes: the member lines sit "  " INSIDE the dash line, no loose match
//   C. another implementation (PyYAML) parses the emitted bytes into the expected structure
//      — our reader agreeing with our writer is not evidence that the bytes are YAML
//
// The compact (`Indented = false`) layout is pinned too: the indented path's second level
// must not leak into the default one.
using System.Diagnostics;

namespace PicoYaml.Tests;

public class IndentedListOfMappingsTests
{
    /// <summary>Serializer output, with the line endings a text comparison expects.</summary>
    private static string Text(string yaml) => yaml.Replace("\r\n", "\n");

    private static IndentedListHolder Holder() =>
        new()
        {
            Top = 1,
            Items = [new IndentedListItem { Name = "a", N = 1 }, new IndentedListItem { Name = "b", N = 2 }],
            Tail = 9,
        };

    private static string HolderText() =>
        Text(YamlSerializer.Serialize(Holder(), new YamlOptions { Indented = true }));

    /// <summary>The bytes B pins, spelled out so the expectation reads like the document.</summary>
    private const string ExpectedIndentedHolder =
        "Top: 1\n"
        + "Items:\n"
        + "    -\n"
        + "      Name: a\n"
        + "      N: 1\n"
        + "    -\n"
        + "      Name: b\n"
        + "      N: 2\n"
        + "Tail: 9\n";

    private const string ExpectedCompactHolder =
        "Top: 1\n"
        + "Items:\n"
        + "  -\n"
        + "    Name: a\n"
        + "    N: 1\n"
        + "  -\n"
        + "    Name: b\n"
        + "    N: 2\n"
        + "Tail: 9\n";

    // ── A: both elements survive the trip ─────────────────────────────────────────

    /// <summary>
    /// Two mapping elements in, two out. The failing form this pins dropped BOTH silently
    /// (the list read back as an empty one), so a count is the assertion that fails loudest.
    /// </summary>
    [Test]
    public async Task Indented_ListOfMappings_ReadsBackBothElements()
    {
        var text = HolderText();
        var back = YamlSerializer.Deserialize<IndentedListHolder>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back).IsNotNull().Because("document=" + text);
        await Assert.That(back!.Items.Count).IsEqualTo(2).Because("document=" + text);
        await Assert.That(back.Items[0].Name).IsEqualTo("a");
        await Assert.That(back.Items[0].N).IsEqualTo(1);
        await Assert.That(back.Items[1].Name).IsEqualTo("b");
        await Assert.That(back.Items[1].N).IsEqualTo(2);
        await Assert.That(back.Top).IsEqualTo(1);
        await Assert.That(back.Tail).IsEqualTo(9);
    }

    // ── B: the exact bytes ────────────────────────────────────────────────────────

    /// <summary>
    /// The whole document, byte for byte. Wants <c>"\n    -\n      Name: a"</c>: the dash
    /// one level in from its key, the member two — anything looser (a <c>Contains</c>, a
    /// column-insensitive match) is exactly what let the ablation through.
    /// </summary>
    [Test]
    public async Task Indented_ListOfMappings_PutsEachMemberInsideItsDash()
    {
        var text = HolderText();
        await Assert.That(text).IsEqualTo(ExpectedIndentedHolder).Because("actual=" + text);
    }

    /// <summary>
    /// The same rule one container deeper: the extra level must be added at every depth,
    /// not just under a document root — a writer that got the root right by accident would
    /// still fail here.
    /// </summary>
    [Test]
    public async Task Indented_NestedListOfMappings_KeepsTheSameDashShape()
    {
        var dto = new IndentedNestedHolder { Name = "o", Inner = Holder() };
        var text = Text(YamlSerializer.Serialize(dto, new YamlOptions { Indented = true }));

        const string expected =
            "Name: o\n"
            + "Inner:\n"
            + "  Top: 1\n"
            + "  Items:\n"
            + "    -\n"
            + "      Name: a\n"
            + "      N: 1\n"
            + "    -\n"
            + "      Name: b\n"
            + "      N: 2\n"
            + "  Tail: 9\n";

        await Assert.That(text).IsEqualTo(expected).Because("actual=" + text);
    }

    // ── C: someone else's reader ──────────────────────────────────────────────────

    /// <summary>
    /// PyYAML parses the emitted document into the structure it came from. Our own reader
    /// shares this writer's blind spots, so this is the half that says the bytes are YAML
    /// and not merely "readable by us".
    /// </summary>
    [Test]
    public async Task Indented_ListOfMappings_IsReadBackByPyYAML()
    {
        var text = HolderText();
        var python = await FindPythonWithYaml();

        Skip.Unless(
            python is not null,
            "PyYAML is not installed for any of `python`/`python3` here, so the external "
                + "cross-check cannot run (set it up with `pip install pyyaml`)"
        );

        var path = Path.Combine(
            Path.GetTempPath(),
            "picoyaml-indented-list-" + Guid.NewGuid().ToString("N") + ".yaml"
        );

        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        try
        {
            var (exit, stdout, stderr) = await RunAsync(python!, PyYamlToJson, path);

            await Assert
                .That(exit)
                .IsEqualTo(0)
                .Because("PyYAML rejected our bytes: " + stderr.Trim() + "\n--- document ---\n" + text);

            const string expectedJson =
                "{\"Top\": 1, \"Items\": [{\"Name\": \"a\", \"N\": 1}, "
                + "{\"Name\": \"b\", \"N\": 2}], \"Tail\": 9}";

            await Assert.That(stdout.Trim()).IsEqualTo(expectedJson).Because("document=" + text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The default layout is untouched by the indented path's second level: a compact list
    /// of mappings still puts the dash one level in and the member one level in from THAT.
    /// </summary>
    [Test]
    public async Task Default_ListOfMappings_KeepsTheCompactDashLayout()
    {
        var text = Text(YamlSerializer.Serialize(Holder()));
        await Assert.That(text).IsEqualTo(ExpectedCompactHolder).Because("actual=" + text);
    }

    // ── the external reader plumbing ──────────────────────────────────────────────

    private const string PyYamlToJson =
        "import sys, json, yaml\n"
        + "doc = yaml.safe_load(open(sys.argv[1], encoding='utf-8').read())\n"
        + "print(json.dumps(doc))\n";

    /// <summary>
    /// The first interpreter on PATH that can <c>import yaml</c>, or null. Probed rather than
    /// assumed: `python3` is a stub on some machines (it exits silently), so a launcher that
    /// "exists" is not a launcher that runs.
    /// </summary>
    private static async Task<string?> FindPythonWithYaml()
    {
        foreach (var candidate in new[] { "python", "python3" })
        {
            try
            {
                var (exit, stdout, _) = await RunAsync(candidate, "import yaml; print(yaml.__version__)");
                if (exit == 0 && stdout.Trim().Length > 0)
                    return candidate;
            }
#pragma warning disable CA1031 // a launcher that cannot start is the answer "not here"
            catch (Exception)
#pragma warning restore CA1031
            {
                // Not on PATH (or not executable): try the next candidate.
            }
        }
        return null;
    }

    /// <summary>Runs <c>python -c &lt;script&gt; args...</c> and captures both pipes.</summary>
    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        string python,
        string script,
        params string[] arguments
    )
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // `-c <script>` first, then the script's own argv: the document path.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process =
            Process.Start(startInfo) ?? throw new InvalidOperationException($"`{python}` started no process");

        // Both pipes read before waiting: a full pipe buffer would block the child forever.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdout, await stderr);
    }
}

public sealed class IndentedListHolder
{
    public int Top { get; set; }
    public List<IndentedListItem> Items { get; set; } = [];
    public int Tail { get; set; }
}

public sealed class IndentedListItem
{
    public string Name { get; set; } = "";
    public int N { get; set; }
}

public sealed class IndentedNestedHolder
{
    public string Name { get; set; } = "";
    public IndentedListHolder Inner { get; set; } = new();
}
