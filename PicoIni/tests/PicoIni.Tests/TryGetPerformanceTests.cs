namespace PicoIni.Tests;

public class TryGetPerformanceTests
{
    [Test]
    public async Task TryGetInt32_ParsesZero()
    {
        var data = "Value = 0"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetInt32(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetInt32_ParsesNegative()
    {
        var data = "Value = -42"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetInt32(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(-42);
    }

    [Test]
    public async Task TryGetInt32_ParsesMaxValue()
    {
        var data = "Value = 2147483647"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetInt32(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(2147483647);
    }

    [Test]
    public async Task TryGetInt32_ParsesMinValue()
    {
        var data = "Value = -2147483648"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetInt32(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(-2147483648);
    }

    [Test]
    public async Task TryGetInt32_ReturnsFalseForNonNumeric()
    {
        var data = "Value = hello"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetInt32(out _)).IsFalse();
    }

    [Test]
    public async Task TryGetInt64_ParsesLargeValue()
    {
        var data = "Value = 9223372036854775807"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetInt64(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(9223372036854775807L);
    }

    [Test]
    public async Task TryGetFloat64_ParsesFraction()
    {
        var data = "Value = 3.14"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetFloat64(out var v)).IsTrue();
        await Assert.That(v).IsEqualTo(3.14);
    }

    [Test]
    public async Task TryGetFloat64_ParsesScientific()
    {
        var data = "Value = 1.5e10"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetFloat64(out var v)).IsTrue();
        await Assert.That(v).IsGreaterThan(1e9);
    }

    [Test]
    public async Task TryGetBool_ParsesTrue()
    {
        var data = "Value = true"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetBool(out var v)).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task TryGetBool_ParsesFalse()
    {
        var data = "Value = false"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetBool(out var v)).IsTrue();
        await Assert.That(v).IsFalse();
    }

    [Test]
    public async Task TryGetBool_ReturnsFalseForNonBool()
    {
        var data = "Value = maybe"u8;
        var reader = new IniReader(data);
        reader.Read();
        await Assert.That(reader.TryGetBool(out _)).IsFalse();
    }
}
