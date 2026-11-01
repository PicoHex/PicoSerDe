// Assertion-level coverage for the attribute lookups the C3 review found without one
// (NIT N4). The review named FOUR — `GetYamlKey`, `GetYamlDateTimeFormat`,
// `GetYamlConverter` and `HasMsgPackIgnore`; re-checking the tree for the follow-up found
// the YAML gap is FIVE wide, `HasYamlIgnore` and `HasYamlCamelCase` included, so all six
// are covered here rather than the named four.
//
// Method: every lookup gets a PAIR of symbols — one carrying the attribute, one not — and
// the test asserts the generated serializer's behaviour differs in exactly the way that
// lookup decides. That is the shape the review asked for ("有该属性 / 无该属性 两个符号,
// 断言返回值正确") with the return value observed through the artifact it drives: the
// lookup is `internal` to the generator assembly and there is no InternalsVisibleTo, so a
// test that called `AttributeLookup.GetString(...)` directly would have to widen the
// generator's surface. The pair makes the assertion two-sided: a lookup that stops
// matching turns the "present" half red, and one that starts over-matching turns the
// "absent" half red.
//
// What these tests do NOT cover: a same-named attribute declared in a FOREIGN namespace
// (the namespace-filter argument). That is deliberately elsewhere — it needs Roslyn
// symbols that the compiler would reject, and the C3 follow-up report carries the
// byte-identical before/after emission for it.

namespace PicoSerDe.Integration.Tests;

// ── [YamlKey] — the custom key name ──────────────────────────────────────────────

public sealed class YamlKeyPresent
{
    [YamlKey("app_name")]
    public string Name { get; set; } = "";
}

public sealed class YamlKeyAbsent
{
    public string Name { get; set; } = "";
}

public class YamlKeyLookupTests
{
    [Test]
    public async Task YamlKey_is_used_when_present_and_the_member_name_when_absent()
    {
        var with = YamlSerializer.Serialize(new YamlKeyPresent { Name = "v" });
        var without = YamlSerializer.Serialize(new YamlKeyAbsent { Name = "v" });

        await Assert.That(with).Contains("app_name:");
        await Assert.That(with).DoesNotContain("Name:");

        await Assert.That(without).Contains("Name:");
        await Assert.That(without).DoesNotContain("app_name:");
    }

    [Test]
    public async Task YamlKey_is_read_back_from_the_custom_key()
    {
        var back = YamlSerializer.Deserialize<YamlKeyPresent>(
            Encoding.UTF8.GetBytes("app_name: v\n")
        );

        await Assert.That(back!.Name).IsEqualTo("v");
    }
}

// ── [YamlDateTimeFormat] — the per-member date format ────────────────────────────

public sealed class YamlDateFormatPresent
{
    [YamlDateTimeFormat("yyyy/MM/dd")]
    public DateTime When { get; set; }
}

public sealed class YamlDateFormatAbsent
{
    public DateTime When { get; set; }
}

public class YamlDateTimeFormatLookupTests
{
    private static readonly DateTime When = new(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    [Test]
    public async Task YamlDateTimeFormat_is_used_when_present_and_the_default_when_absent()
    {
        var with = YamlSerializer.Serialize(new YamlDateFormatPresent { When = When });
        var without = YamlSerializer.Serialize(new YamlDateFormatAbsent { When = When });

        // The custom format is unambiguous: no default YAML rendering produces "2024/03/04".
        await Assert.That(with).Contains("2024/03/04");
        await Assert.That(without).DoesNotContain("2024/03/04");
        await Assert.That(without).Contains("2024-03-04");
    }
}

// ── [YamlConverter] — the per-member converter type ──────────────────────────────

public sealed class YamlConverterPresent
{
    [YamlConverter(typeof(BracketYamlConverter))]
    public string Name { get; set; } = "";
}

public sealed class YamlConverterAbsent
{
    public string Name { get; set; } = "";
}

/// <summary>Writes <c>[value]</c>: a shape the default string writer never produces.</summary>
public sealed class BracketYamlConverter : IYamlConverter<string>
{
    public void Write(IBufferWriter<byte> writer, string value) =>
        writer.Write(Encoding.UTF8.GetBytes($"[{value}]"));

    public string Read(ref YamlReader reader) =>
        Encoding.UTF8.GetString(reader.ValueSpan).Trim('[', ']');
}

public class YamlConverterLookupTests
{
    [Test]
    public async Task YamlConverter_is_used_when_present_and_skipped_when_absent()
    {
        var with = YamlSerializer.Serialize(new YamlConverterPresent { Name = "v" });
        var without = YamlSerializer.Serialize(new YamlConverterAbsent { Name = "v" });

        await Assert.That(with).Contains("[v]");
        await Assert.That(without).Contains("Name: v");
        await Assert.That(without).DoesNotContain("[v]");
    }
}

// ── [YamlCamelCase] — a TYPE-level flag (the "does this symbol carry it" form) ───

[YamlCamelCase]
public sealed class YamlCamelCasePresent
{
    public string ProductName { get; set; } = "";
}

public sealed class YamlCamelCaseAbsent
{
    public string ProductName { get; set; } = "";
}

public class YamlCamelCaseLookupTests
{
    [Test]
    public async Task YamlCamelCase_is_honoured_when_present_and_ignored_when_absent()
    {
        var with = YamlSerializer.Serialize(new YamlCamelCasePresent { ProductName = "v" });
        var without = YamlSerializer.Serialize(new YamlCamelCaseAbsent { ProductName = "v" });

        await Assert.That(with).Contains("productName:");
        await Assert.That(with).DoesNotContain("ProductName:");

        await Assert.That(without).Contains("ProductName:");
        await Assert.That(without).DoesNotContain("productName:");
    }
}

// ── [YamlIgnore] — a TYPE-level flag on a member ────────────────────────────────

public sealed class YamlIgnorePresent
{
    public string Shown { get; set; } = "";

    [YamlIgnore]
    public string Hidden { get; set; } = "";
}

public sealed class YamlIgnoreAbsent
{
    public string Shown { get; set; } = "";

    public string Hidden { get; set; } = "";
}

public class YamlIgnoreLookupTests
{
    [Test]
    public async Task YamlIgnore_drops_the_member_only_when_it_is_present()
    {
        var with = YamlSerializer.Serialize(new YamlIgnorePresent { Shown = "s", Hidden = "h" });
        var without = YamlSerializer.Serialize(new YamlIgnoreAbsent { Shown = "s", Hidden = "h" });

        await Assert.That(with).Contains("Shown:");
        await Assert.That(with).DoesNotContain("Hidden:");

        await Assert.That(without).Contains("Hidden:");
    }
}

// ── [MsgPackIgnore] — the one MessagePack flag, on a member ─────────────────────

public sealed class MsgPackIgnorePresent
{
    public string Shown { get; set; } = "";

    [MsgPackIgnore]
    public string Hidden { get; set; } = "";
}

public sealed class MsgPackIgnoreAbsent
{
    public string Shown { get; set; } = "";

    public string Hidden { get; set; } = "";
}

public class MsgPackIgnoreLookupTests
{
    // MessagePack keys are integer indices, so the marker has to be a VALUE: assert on the
    // serialised payload, which is a map whose entry count and contents move when a member
    // is dropped.
    [Test]
    public async Task MsgPackIgnore_drops_the_member_only_when_it_is_present()
    {
        var with = Encoding.UTF8.GetString(
            MsgPackSerializer.SerializeToUtf8Bytes(
                new MsgPackIgnorePresent { Shown = "shown-marker", Hidden = "hidden-marker" }
            )
        );
        var without = Encoding.UTF8.GetString(
            MsgPackSerializer.SerializeToUtf8Bytes(
                new MsgPackIgnoreAbsent { Shown = "shown-marker", Hidden = "hidden-marker" }
            )
        );

        await Assert.That(with).Contains("shown-marker");
        await Assert.That(with).DoesNotContain("hidden-marker");

        await Assert.That(without).Contains("hidden-marker");
    }
}
