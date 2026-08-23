namespace PicoToml.Tests;

/// <summary>Audit BUG-03 regression tests: control characters or unterminated
/// strings must throw FormatException, never ArgumentOutOfRangeException.</summary>
public class TomlReaderAuditRegressionTests
{
    [Test]
    [Arguments("\x00")]
    [Arguments("\x01\x02")]
    [Arguments("\"a\x01b\"")]
    [Arguments("\"unterminated")]
    public async Task ControlCharsOrUnterminatedString_ThrowFormatException(string input)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(input);
        var threw = false;
        try
        {
            var reader = new TomlReader(data);
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
        var reader = new TomlReader("a = 1\n"u8);
        await Assert.That(reader.Read()).IsTrue();
    }
}
