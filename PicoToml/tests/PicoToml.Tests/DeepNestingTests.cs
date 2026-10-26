using System.Text;

namespace PicoToml.Tests;

/// <summary>
/// Deep object nesting (3+ levels) used to be a loud <c>NotSupportedException</c> on read:
/// the section emitter wrote the second level as a TOP-LEVEL header (<c>[A]</c> under
/// <c>[Parent]</c>), so the reader could not tell which table a member belonged to and refused
/// rather than silently losing the values (review follow-up).
/// <para>
/// R2 Task 5 removed the reason for the refusal: a section written from inside a table now
/// carries its ancestors (<c>[Parent.L2]</c>, <c>[Parent.L2.L3]</c>) and each helper dispatches
/// ITS children, which is the same mechanism a recursive member needs. TOML holds any depth
/// (spec D3), so the fixture now pins the ROUND TRIP instead of the exception.
/// </para>
/// </summary>
public class DeepNestingTests
{
    [Test]
    public async Task ThreeLevelNesting_RoundTrips()
    {
        var dto = new TomlDeepL1 { L2 = new TomlDeepL2 { L3 = new TomlDeepL3 { X = 42 } } };
        var text = TomlSerializer.Serialize(dto);

        // Every section carries its ancestors: a bare `[L2]`/`[L3]` would declare a top-level
        // table and the member would come back as an empty (default) instance.
        await Assert.That(text).Contains("[L2]").Because(text);
        await Assert.That(text).Contains("[L2.L3]").Because(text);

        var back = TomlSerializer.Deserialize<TomlDeepL1>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.L2).IsNotNull();
        await Assert.That(back.L2.L3).IsNotNull();
        await Assert.That(back.L2.L3!.X).IsEqualTo(42);
    }

    /// <summary>
    /// The other half: a FOURTH level, to prove the dispatch is per level rather than a fixed
    /// depth table. Nothing in the emitter knows "three" — each helper asks the next one.
    /// </summary>
    [Test]
    public async Task FiveLevelNesting_RoundTrips()
    {
        var dto = new TomlDeepL1
        {
            L2 = new TomlDeepL2
            {
                L3 = new TomlDeepL3
                {
                    X = 7,
                    L4 = new TomlDeepL4 { Y = "deep" },
                },
            },
        };

        var back = TomlSerializer.Deserialize<TomlDeepL1>(
            Encoding.UTF8.GetBytes(TomlSerializer.Serialize(dto))
        );

        await Assert.That(back!.L2.L3!.X).IsEqualTo(7);
        await Assert.That(back.L2.L3.L4).IsNotNull();
        await Assert.That(back.L2.L3.L4!.Y).IsEqualTo("deep");
    }
}

internal sealed class TomlDeepL1
{
    public TomlDeepL2 L2 { get; set; } = new();
}

internal sealed class TomlDeepL2
{
    public TomlDeepL3 L3 { get; set; } = new();
}

internal sealed class TomlDeepL3
{
    public int X { get; set; }

    public TomlDeepL4 L4 { get; set; } = new();
}

internal sealed class TomlDeepL4
{
    public string Y { get; set; } = "";
}
