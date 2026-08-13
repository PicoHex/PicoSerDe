namespace PicoIni.Tests;

[PicoSerializable]
public class StrictIniModel
{
    public int Count { get; set; }
    public bool Flag { get; set; }
}

/// <summary>C2 regression: wrongly-typed INI values must throw loudly.</summary>
public class IniStrictTests
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
        var ini = "Count=abc\n"u8.ToArray();
        await Assert
            .That(ThrowsFormat(() => IniSerializer.Deserialize<StrictIniModel>(ini)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_StringIntoBool_Throws()
    {
        var ini = "Flag=maybe\n"u8.ToArray();
        await Assert
            .That(ThrowsFormat(() => IniSerializer.Deserialize<StrictIniModel>(ini)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_ValidInput_StillWorks()
    {
        var ini = "Count=3\nFlag=true\n"u8.ToArray();
        var m = IniSerializer.Deserialize<StrictIniModel>(ini);
        await Assert.That(m!.Count).IsEqualTo(3);
        await Assert.That(m.Flag).IsTrue();
    }
}
