namespace PicoYaml.Tests;

[PicoSerializable]
public class StrictYamlModel
{
    public int Count { get; set; }
    public bool Flag { get; set; }
}

/// <summary>C2 regression: wrongly-typed YAML values must throw loudly.</summary>
public class YamlStrictTests
{
    private static bool ThrowsFormat(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (Exception ex)
            when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return true;
        }
    }

    [Test]
    public async Task Deserialize_StringIntoInt_Throws()
    {
        var yaml = "Count: abc\n"u8.ToArray();
        await Assert
            .That(ThrowsFormat(() => YamlSerializer.Deserialize<StrictYamlModel>(yaml)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_StringIntoBool_Throws()
    {
        var yaml = "Flag: maybe\n"u8.ToArray();
        await Assert
            .That(ThrowsFormat(() => YamlSerializer.Deserialize<StrictYamlModel>(yaml)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_ValidInput_StillWorks()
    {
        var yaml = "Count: 3\nFlag: true\n"u8.ToArray();
        var m = YamlSerializer.Deserialize<StrictYamlModel>(yaml);
        await Assert.That(m!.Count).IsEqualTo(3);
        await Assert.That(m.Flag).IsTrue();
    }
}
