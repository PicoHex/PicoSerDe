namespace PicoIni.Tests;

/// <summary>Audit BUG-02 regression tests: malformed lines must throw
/// FormatException, never ArgumentOutOfRangeException.</summary>
public class IniReaderAuditRegressionTests
{
    [Test]
    [Arguments("x")]
    [Arguments("]")]
    [Arguments("{")]
    [Arguments("}")]
    [Arguments("\"unterminated")]
    public async Task MalformedLine_ThrowsFormatException_NotArgumentOutOfRange(string line)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(line);
        var threw = false;
        try
        {
            var reader = new IniReader(data);
            while (reader.Read()) { }
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ValidLine_StillParses()
    {
        var reader = new IniReader("k=v\n"u8);
        await Assert.That(reader.Read()).IsTrue();
    }
}
