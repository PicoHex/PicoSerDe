namespace PicoIni.Tests;

public class TryGetPerformanceTests
{
    [Test]
    public async Task TryGetInt32_ParsesZero()
    {
        var data = "Value = 0"u8.ToArray();
        int v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read(); // PropertyName
            reader.Read(); // value
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetInt32_ParsesNegative()
    {
        var data = "Value = -42"u8.ToArray();
        int v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(-42);
    }

    [Test]
    public async Task TryGetInt32_ParsesMaxValue()
    {
        var data = "Value = 2147483647"u8.ToArray();
        int v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(2147483647);
    }

    [Test]
    public async Task TryGetInt32_ParsesMinValue()
    {
        var data = "Value = -2147483648"u8.ToArray();
        int v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(-2147483648);
    }

    [Test]
    public async Task TryGetInt32_ReturnsFalseForNonNumeric()
    {
        var data = "Value = hello"u8.ToArray();
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetInt32(out _);
        }
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryGetInt64_ParsesLargeValue()
    {
        var data = "Value = 9223372036854775807"u8.ToArray();
        long v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetInt64(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(9223372036854775807L);
    }

    [Test]
    public async Task TryGetFloat64_ParsesFraction()
    {
        var data = "Value = 3.14"u8.ToArray();
        double v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetFloat64(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(3.14);
    }

    [Test]
    public async Task TryGetFloat64_ParsesScientific()
    {
        var data = "Value = 1.5e10"u8.ToArray();
        double v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetFloat64(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsGreaterThan(1e9);
    }

    [Test]
    public async Task TryGetBool_ParsesTrue()
    {
        var data = "Value = true"u8.ToArray();
        bool v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetBool(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task TryGetBool_ParsesFalse()
    {
        var data = "Value = false"u8.ToArray();
        bool v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetBool(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsFalse();
    }

    [Test]
    public async Task TryGetBool_ReturnsFalseForNonBool()
    {
        var data = "Value = maybe"u8.ToArray();
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetBool(out _);
        }
        await Assert.That(ok).IsFalse();
    }
}
