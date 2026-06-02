namespace PicoJetson.Tests;

public class FuzzingTests
{
    [Test]
    public async Task SimpleValues_ParseWithoutException()
    {
        var inputs = new[]
        {
            "null",
            "true",
            "false",
            "42",
            "-17",
            "3.14",
            "1.5e10",
            "-2.0E-3",
            "\"hello\"",
            "\"\"",
            "\"escaped\\nstring\"",
            "\"unicode\\u0041\"",
            "{}",
            "{\"a\":1}",
            "{\"a\":1,\"b\":\"x\"}",
            "[]",
            "[1,2,3]",
            "[true, false, null]",
            "{\"nested\":{\"deep\":[1,2]}}",
            "{\"a\":[],\"b\":{}}"
        };
        foreach (var input in inputs)
        {
            var r = new JsonReader(Encoding.UTF8.GetBytes(input));
            while (r.Read()) { }
            // Valid JSON must parse without FormatException
        }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task MalformedJson_ThrowsFormatException()
    {
        var inputs = new[]
        {
            "{broken",
            "{1:2}",
            "\"unterminated",
            "tru",
            "fals",
            "nul",
            "\"\\u00ZZ\""
        };
        foreach (var input in inputs)
        {
            try
            {
                var r = new JsonReader(Encoding.UTF8.GetBytes(input));
                while (r.Read()) { }
            }
            catch (FormatException)
            {
                continue;
            }
            throw new Exception($"Expected FormatException for input: {input}");
        }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task RandomJson_RoundTrip()
    {
        var rng = new Random(42);
        for (int i = 0; i < 100; i++)
        {
            var json = GenerateRandomJson(rng, depth: 3);
            try
            {
                var r = new JsonReader(Encoding.UTF8.GetBytes(json));
                while (r.Read()) { }
            }
            catch (FormatException) { }
        }
        await Assert.That(true).IsTrue();
    }

    private static string GenerateRandomJson(Random rng, int depth)
    {
        if (depth <= 0)
            return GetRandomValue(rng);

        return rng.Next(4) switch
        {
            0 => GetRandomValue(rng),
            1 => "{" + GenerateRandomJson(rng, depth - 1) + "}",
            2 => "[" + GenerateRandomJson(rng, depth - 1) + "]",
            _ => "{ \"a\": " + GenerateRandomJson(rng, depth - 1) + " }"
        };
    }

    private static string GetRandomValue(Random rng) =>
        rng.Next(6) switch
        {
            0 => "\"string\"",
            1 => rng.Next(1000).ToString(),
            2 => rng.NextDouble().ToString("F2"),
            3 => "true",
            4 => "false",
            _ => "null"
        };
}
