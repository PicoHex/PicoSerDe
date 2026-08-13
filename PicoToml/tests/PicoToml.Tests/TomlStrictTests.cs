namespace PicoToml.Tests;

[PicoSerializable]
public class StrictTomlModel
{
    public int Count { get; set; }
    public bool Flag { get; set; }
}

/// <summary>C2 regression: wrongly-typed TOML values must throw loudly.</summary>
public class TomlStrictTests
{
    private static bool ThrowsFormat(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }

    [Test]
    public async Task Deserialize_StringIntoInt_Throws()
    {
        var toml = "Count = \"abc\"\n"u8.ToArray();
        await Assert
            .That(ThrowsFormat(() => TomlSerializer.Deserialize<StrictTomlModel>(toml)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_StringIntoBool_Throws()
    {
        var toml = "Flag = \"maybe\"\n"u8.ToArray();
        await Assert
            .That(ThrowsFormat(() => TomlSerializer.Deserialize<StrictTomlModel>(toml)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_ValidInput_StillWorks()
    {
        var toml = "Count = 3\nFlag = true\n"u8.ToArray();
        var m = TomlSerializer.Deserialize<StrictTomlModel>(toml);
        await Assert.That(m!.Count).IsEqualTo(3);
        await Assert.That(m.Flag).IsTrue();
    }
}
