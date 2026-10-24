using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// Review I-23: these tests used to end in <c>Assert.That(true).IsTrue()</c> with a
/// bare <c>catch (FormatException) { }</c>, so they passed no matter what the reader
/// did. Every assertion below is value-based:
/// <list type="bullet">
/// <item>valid inputs must produce an exact first token and token count;</item>
/// <item>the random round-trip only asserts on JSON the generator itself produced,
/// and compares the parsed values against the generated model.</item>
/// </list>
/// </summary>
public class FuzzingTests
{
    private static List<TokenType> ReadAllTokens(string json)
    {
        var reader = new JsonReader(Encoding.UTF8.GetBytes(json));
        var tokens = new List<TokenType>();
        while (reader.Read())
            tokens.Add(reader.TokenType);
        return tokens;
    }

    [Test]
    public async Task SimpleValues_ProduceExactFirstTokenAndCount()
    {
        var cases = new (string Json, TokenType FirstToken, int TokenCount)[]
        {
            ("null", TokenType.Null, 1),
            ("true", TokenType.Bool, 1),
            ("false", TokenType.Bool, 1),
            ("42", TokenType.Int32, 1),
            ("-17", TokenType.Int32, 1),
            ("3.14", TokenType.Float64, 1),
            ("1.5e10", TokenType.Float64, 1),
            ("-2.0E-3", TokenType.Float64, 1),
            ("\"hello\"", TokenType.String, 1),
            ("\"\"", TokenType.String, 1),
            ("\"escaped\\nstring\"", TokenType.String, 1),
            ("\"unicode\\u0041\"", TokenType.String, 1),
            ("{}", TokenType.ObjectStart, 2),
            ("{\"a\":1}", TokenType.ObjectStart, 4),
            ("{\"a\":1,\"b\":\"x\"}", TokenType.ObjectStart, 6),
            ("[]", TokenType.ArrayStart, 2),
            ("[1,2,3]", TokenType.ArrayStart, 5),
            ("[true, false, null]", TokenType.ArrayStart, 5),
            ("{\"nested\":{\"deep\":[1,2]}}", TokenType.ObjectStart, 10),
            ("{\"a\":[],\"b\":{}}", TokenType.ObjectStart, 8),
        };

        foreach (var (json, firstToken, tokenCount) in cases)
        {
            var tokens = ReadAllTokens(json);
            await Assert.That(tokens.Count).IsGreaterThan(0).Because($"input: {json}");
            var actualFirst = tokens.Count > 0 ? tokens[0] : TokenType.None;
            await Assert.That(actualFirst).IsEqualTo(firstToken).Because($"input: {json}");
            await Assert.That(tokens.Count).IsEqualTo(tokenCount).Because($"input: {json}");
        }
    }

    [Test]
    public void MalformedJson_ThrowsFormatException()
    {
        var inputs = new[]
        {
            "{broken",
            "{1:2}",
            "\"unterminated",
            "tru",
            "fals",
            "nul",
            "\"\\u00ZZ\"",
        };
        foreach (var input in inputs)
        {
            var json = input;
            Assert.Throws<FormatException>(() => ReadAllTokens(json));
        }
    }

    [Test]
    public async Task RandomJson_RoundTrip()
    {
        var rng = new Random(42);
        for (var i = 0; i < 100; i++)
        {
            // The generator emits structurally valid JSON plus the model it was
            // rendered from, so every assertion below has a concrete expected value.
            var value = GenerateRandomValue(rng, depth: 3);
            var json = Render(value);

            var tokens = ReadAllTokens(json); // throws FormatException if the generator emitted broken JSON
            await Assert.That(tokens.Count).IsEqualTo(CountTokens(value)).Because($"json: {json}");

            var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(json));
            await AssertSameValue(value, doc.RootElement);
        }
    }

    // ── Random JSON model: rendered to JSON, then compared against the parsed DOM ──

    private abstract record JsonValue;

    private sealed record JsonNumber(int Value) : JsonValue;

    private sealed record JsonString(string Value) : JsonValue;

    private sealed record JsonBool(bool Value) : JsonValue;

    private sealed record JsonNull : JsonValue;

    private sealed record JsonArray(IReadOnlyList<JsonValue> Items) : JsonValue;

    private sealed record JsonObject(IReadOnlyList<KeyValuePair<string, JsonValue>> Props)
        : JsonValue;

    private static JsonValue GenerateRandomValue(Random rng, int depth)
    {
        if (depth <= 0)
            return GenerateScalar(rng);

        switch (rng.Next(4))
        {
            case 0:
            {
                var count = rng.Next(1, 4);
                var props = new List<KeyValuePair<string, JsonValue>>(count);
                for (var i = 0; i < count; i++)
                    props.Add(new($"k{i}", GenerateRandomValue(rng, depth - 1)));
                return new JsonObject(props);
            }
            case 1:
            {
                var count = rng.Next(0, 4);
                var items = new List<JsonValue>(count);
                for (var i = 0; i < count; i++)
                    items.Add(GenerateRandomValue(rng, depth - 1));
                return new JsonArray(items);
            }
            default:
                return GenerateScalar(rng);
        }
    }

    private static JsonValue GenerateScalar(Random rng) =>
        rng.Next(4) switch
        {
            0 => new JsonNumber(rng.Next(-1000, 1000)),
            1 => new JsonString($"s{rng.Next(10)}"),
            2 => new JsonBool(rng.Next(2) == 0),
            _ => new JsonNull(),
        };

    private static string Render(JsonValue value) =>
        value switch
        {
            JsonNumber n => n.Value.ToString(CultureInfo.InvariantCulture),
            JsonString s => $"\"{s.Value}\"",
            JsonBool b => b.Value ? "true" : "false",
            JsonNull => "null",
            JsonArray a => "[" + string.Join(",", a.Items.Select(Render)) + "]",
            JsonObject o => "{"
                + string.Join(",", o.Props.Select(p => $"\"{p.Key}\":{Render(p.Value)}"))
                + "}",
            _ => throw new InvalidOperationException($"unhandled model node {value}"),
        };

    private static int CountTokens(JsonValue value) =>
        value switch
        {
            JsonArray a => 2 + a.Items.Sum(CountTokens),
            JsonObject o => 2 + o.Props.Sum(p => 1 + CountTokens(p.Value)),
            _ => 1,
        };

    private static async Task AssertSameValue(JsonValue expected, PicoElement actual)
    {
        switch (expected)
        {
            case JsonNumber n:
                await Assert.That(actual.ValueKind).IsEqualTo(PicoValueKind.Number);
                await Assert.That(actual.GetInt32()).IsEqualTo(n.Value);
                break;
            case JsonString s:
                await Assert.That(actual.ValueKind).IsEqualTo(PicoValueKind.String);
                await Assert.That(actual.GetString()).IsEqualTo(s.Value);
                break;
            case JsonBool b:
                await Assert
                    .That(actual.ValueKind)
                    .IsEqualTo(b.Value ? PicoValueKind.True : PicoValueKind.False);
                await Assert.That(actual.GetBoolean()).IsEqualTo(b.Value);
                break;
            case JsonNull:
                await Assert.That(actual.ValueKind).IsEqualTo(PicoValueKind.Null);
                break;
            case JsonArray a:
                await Assert.That(actual.ValueKind).IsEqualTo(PicoValueKind.Array);
                await Assert.That(actual.GetArrayLength()).IsEqualTo(a.Items.Count);
                for (var i = 0; i < a.Items.Count; i++)
                    await AssertSameValue(a.Items[i], actual[i]);
                break;
            case JsonObject o:
                await Assert.That(actual.ValueKind).IsEqualTo(PicoValueKind.Object);
                foreach (var (key, child) in o.Props)
                    await AssertSameValue(child, actual[key]);
                break;
            default:
                throw new InvalidOperationException($"unhandled model node {expected}");
        }
    }
}
