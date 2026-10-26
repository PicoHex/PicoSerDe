using System.Text;

namespace PicoToml.Tests;

// Models at namespace level (SG skips nested types)
public class SectionOrderAddress
{
    public string City { get; set; } = "";
}

public class SectionOrderChild
{
    // Declaration order matters: A (an object → a sub-table) comes before Z (a
    // scalar), so the inner helper has to run the same two-pass emission.
    public SectionOrderAddress A { get; set; } = new();
    public string Z { get; set; } = "";
}

public class SectionOrderParent
{
    public SectionOrderChild Parent { get; set; } = new();
}

public class SectionOrderDictFirst
{
    // A dictionary is written as a [Map] section too.
    public Dictionary<string, int> Map { get; set; } = new();
    public string Title { get; set; } = "";
}

public class SectionOrderItem
{
    public string Name { get; set; } = "";
}

public class SectionOrderListFirst
{
    // An object list is written as [[Items]] array-of-tables sections.
    public List<SectionOrderItem> Items { get; set; } = new();
    public string Title { get; set; } = "";
}

public class SectionOrderOther
{
    public int X { get; set; }
}

public class SectionOrderDottedRoot
{
    [TomlKey("a")]
    public StrictDottedInner A { get; set; } = new();
    public SectionOrderOther Other { get; set; } = new();
}

[PicoDerivedType(typeof(SectionOrderPolyDerived), "derived")]
public class SectionOrderPolyBase
{
    public string Kind { get; set; } = "";
}

public class SectionOrderPolyDerived : SectionOrderPolyBase
{
    public SectionOrderAddress Address { get; set; } = new();
    public string Name { get; set; } = "";
}

public class SectionOrderOrdered
{
    // Both members that follow the table are declared *after* it: emitted in
    // declaration order they land inside [Address] and are lost on read-back.
    public SectionOrderAddress Address { get; set; } = new();
    public string Name { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// C-11: a TOML table header ends every key/value pair that follows it, so a
/// scalar declared after a nested object used to be emitted inside the child
/// table and silently lost on the way back.
/// </summary>
public class TomlSectionOrderTests
{
    [Test]
    public async Task ObjectBeforeScalar_RoundTripsAllMembers()
    {
        var text = TomlSerializer.Serialize(
            new SectionOrderOrdered
            {
                Address = new SectionOrderAddress { City = "X" },
                Name = "parsed",
                Count = 7,
            }
        );
        var back = TomlSerializer.Deserialize<SectionOrderOrdered>(Encoding.UTF8.GetBytes(text))!;
        await Assert.That(back.Address.City).IsEqualTo("X");
        // "" / 0 are the initial values: only a genuine read-back passes.
        await Assert.That(back.Name).IsEqualTo("parsed");
        await Assert.That(back.Count).IsEqualTo(7);
    }

    [Test]
    public async Task ObjectBeforeScalar_EmitsScalarsBeforeTheTable()
    {
        var text = TomlSerializer.Serialize(
            new SectionOrderOrdered
            {
                Address = new SectionOrderAddress { City = "X" },
                Name = "n",
                Count = 1,
            }
        );
        await Assert.That(text).IsEqualTo("Name = \"n\"\nCount = 1\n[Address]\nCity = \"X\"\n");
    }

    [Test]
    public async Task DictBeforeScalar_RoundTripsAllMembers()
    {
        var text = TomlSerializer.Serialize(
            new SectionOrderDictFirst { Map = { ["k"] = 1 }, Title = "parsed" }
        );
        await Assert.That(text).IsEqualTo("Title = \"parsed\"\n[Map]\nk = 1\n");

        var back = TomlSerializer.Deserialize<SectionOrderDictFirst>(Encoding.UTF8.GetBytes(text))!;
        await Assert.That(back.Title).IsEqualTo("parsed");
        await Assert.That(back.Map["k"]).IsEqualTo(1);
    }

    [Test]
    public async Task ObjectListBeforeScalar_RoundTripsAllMembers()
    {
        var text = TomlSerializer.Serialize(
            new SectionOrderListFirst
            {
                Items = [new SectionOrderItem { Name = "i" }],
                Title = "parsed",
            }
        );
        await Assert.That(text).IsEqualTo("Title = \"parsed\"\n[[Items]]\nName = \"i\"\n");

        var back = TomlSerializer.Deserialize<SectionOrderListFirst>(Encoding.UTF8.GetBytes(text))!;
        await Assert.That(back.Title).IsEqualTo("parsed");
        await Assert.That(back.Items.Count).IsEqualTo(1);
        await Assert.That(back.Items[0].Name).IsEqualTo("i");
    }

    [Test]
    public async Task PolyDerived_ObjectBeforeScalar_EmitsScalarsBeforeTheTable()
    {
        // R2 Task 6: the discriminator key is QUOTED now. `$` is not in TOML 1.0's
        // bare-key grammar (`[A-Za-z0-9_-]+`), so `$type = "derived"` was not TOML at
        // all — tomllib rejects it. NeedsKeyQuoting used to be a six-character blacklist
        // that never held `$`; asking the grammar instead (spec D-9b) fixed this one for
        // free. The reader still accepts the bare form (canonical write, lenient read),
        // which is why the hand-written payloads in PolymorphicTests keep working.
        // The polymorphic serializer walks the derived type's members: it needs
        // the same "sections last" ordering.
        var text = TomlSerializer.Serialize<SectionOrderPolyBase>(
            new SectionOrderPolyDerived
            {
                Address = new SectionOrderAddress { City = "X" },
                Name = "n",
            }
        );
        await Assert
            .That(text)
            .IsEqualTo(
                "\"$type\" = \"derived\"\nKind = \"\"\nName = \"n\"\n[Address]\nCity = \"X\"\n"
            );
    }

    [Test]
    public async Task InnerHelper_ObjectBeforeScalar_EmitsScalarFirst()
    {
        // Second-level table: A (object) is declared before Z (scalar), so the
        // inner helper must emit Z first — otherwise Z is swallowed by the
        // sub-table header.
        //
        // R2 Task 5 changed the header from `[A]` to `[Parent.A]`: a section written from
        // inside a table has to carry its ancestors, or it declares a TOP-LEVEL table and
        // `Parent.A` comes back empty (the same dotted-path rule that lets a recursive
        // member read back at all). The round trip below is what proves the new form is the
        // right one - the old text could not be read back into the model it came from.
        var value = new SectionOrderParent
        {
            Parent = new SectionOrderChild
            {
                A = new SectionOrderAddress { City = "Y" },
                Z = "kept",
            },
        };
        var text = TomlSerializer.Serialize(value);
        await Assert.That(text).IsEqualTo("[Parent]\nZ = \"kept\"\n[Parent.A]\nCity = \"Y\"\n");

        var back = TomlSerializer.Deserialize<SectionOrderParent>(Encoding.UTF8.GetBytes(text))!;
        await Assert.That(back.Parent.Z).IsEqualTo("kept");
        await Assert.That(back.Parent.A.City).IsEqualTo("Y");
    }

    [Test]
    public async Task InnerHelper_StopsAtTheNextSection()
    {
        // The inner helper for 'a' must stop at the [Other] header instead of
        // consuming the following section's members.
        var back = TomlSerializer.Deserialize<SectionOrderDottedRoot>(
            Encoding.UTF8.GetBytes("a.b = 2\n[Other]\nX = 1\n")
        )!;
        await Assert.That(back.A.B).IsEqualTo(2);
        await Assert.That(back.Other.X).IsEqualTo(1);
    }
}
