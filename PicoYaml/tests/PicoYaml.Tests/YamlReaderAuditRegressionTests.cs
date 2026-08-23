namespace PicoYaml.Tests;

/// <summary>Audit BUG-01 regression tests: top-level scalars/flow structures and
/// orphan lines must not crash with ArgumentOutOfRangeException — they either
/// parse or throw FormatException.</summary>
public class YamlReaderAuditRegressionTests
{
    [Test]
    [Arguments("hello")]
    [Arguments("12345")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("~")]
    [Arguments("[1, 2]")]
    [Arguments("[2147483648]")]
    public async Task TopLevelScalarOrFlow_DoesNotCrash(string input)
    {
        var data = System.Text.Encoding.UTF8.GetBytes(input);
        var crashed = false;
        try
        {
            var reader = new YamlReader(data);
            while (reader.Read()) { }
        }
        catch (FormatException)
        {
            // acceptable: parse or FormatException, never AOORE
        }
        catch (ArgumentOutOfRangeException)
        {
            crashed = true; // audit BUG-01: must never crash
        }
        await Assert.That(crashed).IsFalse();
    }

    [Test]
    public async Task OrphanLineInMapping_ThrowsFormatException()
    {
        var data = "key: value\norphan\n"u8;
        var threw = false;
        try
        {
            var reader = new YamlReader(data);
            while (reader.Read()) { }
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task ValidMapping_StillParses()
    {
        var reader = new YamlReader("a: 1\n"u8);
        await Assert.That(reader.Read()).IsTrue();
    }
}
