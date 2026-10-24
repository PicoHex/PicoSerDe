// TDD (F5 fix round 2, problem B): the YAML inner object helper did not treat a
// nullable value-type member at all. `ym.WriteNumber(value.Optional)` with `int?`
// does not compile (CS1503: cannot convert from 'int?' to 'int'), so EVERY document
// with a list of objects carrying an `int?` member failed to build — on main too,
// not only on the F5 branch. The inner path must use the same null handling as a
// top-level DTO member: omit the key when the value is null, write the underlying
// value otherwise.
namespace PicoYaml.Tests;

public class YamlNullableMemberRow
{
    public string Name { get; set; } = "";
    public int? Optional { get; set; }
    public YamlNullableMemberKind? Kind { get; set; }
}

public enum YamlNullableMemberKind
{
    A,
    B,
}

public class YamlNullableMemberRowHolder
{
    public List<YamlNullableMemberRow> Rows { get; set; } = new();
    public int Count { get; set; }
}

public class PlainObjectNullableMemberTests
{
    [Test]
    public async Task ObjectListWithNullableValueMembers_RoundTrips()
    {
        var value = new YamlNullableMemberRowHolder
        {
            Rows =
            {
                new()
                {
                    Name = "with",
                    Optional = 7,
                    Kind = YamlNullableMemberKind.B,
                },
                new() { Name = "without" },
            },
            Count = 3,
        };

        var yaml = YamlSerializer.Serialize(value);
        await Assert.That(yaml).Contains("Optional: 7").Because("yaml=" + yaml);
        await Assert.That(yaml).Contains("Kind: B").Because("yaml=" + yaml);
        await Assert
            .That(yaml.Split("Optional").Length - 1)
            .IsEqualTo(1)
            .Because("a null member is omitted, not written; yaml=" + yaml);

        var back = YamlSerializer.Deserialize<YamlNullableMemberRowHolder>(
            Encoding.UTF8.GetBytes(yaml)
        );
        await Assert.That(back!.Count).IsEqualTo(3);
        await Assert.That(back.Rows.Count).IsEqualTo(2).Because("yaml=" + yaml);
        await Assert.That(back.Rows[0].Optional).IsEqualTo(7);
        await Assert.That(back.Rows[0].Kind).IsEqualTo(YamlNullableMemberKind.B);
        await Assert.That(back.Rows[1].Optional).IsNull();
        await Assert.That(back.Rows[1].Kind).IsNull();
    }

    /// <summary>
    /// The top-level nullable member handling the inner path now mirrors: a null is
    /// omitted and an explicit value is written as the underlying scalar.
    /// </summary>
    [Test]
    public async Task TopLevelNullableMembers_StillRoundTrip()
    {
        var yaml = YamlSerializer.Serialize(new YamlNullableMemberRow { Name = "x", Optional = 9 });
        await Assert.That(yaml).Contains("Optional: 9").Because("yaml=" + yaml);
        var back = YamlSerializer.Deserialize<YamlNullableMemberRow>(Encoding.UTF8.GetBytes(yaml));
        await Assert.That(back!.Optional).IsEqualTo(9);

        var omitted = YamlSerializer.Serialize(new YamlNullableMemberRow { Name = "y" });
        await Assert.That(omitted).DoesNotContain("Optional").Because("yaml=" + omitted);
        await Assert.That(omitted).DoesNotContain("Kind").Because("yaml=" + omitted);
        var back2 = YamlSerializer.Deserialize<YamlNullableMemberRow>(
            Encoding.UTF8.GetBytes(omitted)
        );
        await Assert.That(back2!.Optional).IsNull();
        await Assert.That(back2.Kind).IsNull();
    }
}
