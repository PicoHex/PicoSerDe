namespace PicoJetson.Tests;

public class NamingPolicyTests
{
    [Test]
    public async Task SnakeCase_SimpleName_ConvertsCorrectly()
    {
        var policy = JsonNamingPolicy.SnakeCaseLower;
        var result = policy.ConvertName("FirstName");
        await Assert.That(result).IsEqualTo("first_name");
    }

    [Test]
    public async Task SnakeCase_AcronymPrefix_ConvertsCorrectly()
    {
        var policy = JsonNamingPolicy.SnakeCaseLower;
        var result = policy.ConvertName("XMLParser");
        await Assert.That(result).IsEqualTo("xml_parser");
    }

    [Test]
    public async Task SnakeCase_AcronymMidWord_ConvertsCorrectly()
    {
        var policy = JsonNamingPolicy.SnakeCaseLower;
        var result = policy.ConvertName("ParseXML");
        await Assert.That(result).IsEqualTo("parse_xml");
    }

    [Test]
    public async Task SnakeCase_MultipleAcronyms_ConvertsCorrectly()
    {
        var policy = JsonNamingPolicy.SnakeCaseLower;
        var result = policy.ConvertName("HTTPResponseCode");
        await Assert.That(result).IsEqualTo("http_response_code");
    }

    [Test]
    public async Task KebabCase_SimpleName_ConvertsCorrectly()
    {
        var policy = JsonNamingPolicy.KebabCaseLower;
        var result = policy.ConvertName("FirstName");
        await Assert.That(result).IsEqualTo("first-name");
    }

    [Test]
    public async Task KebabCase_AcronymPrefix_ConvertsCorrectly()
    {
        var policy = JsonNamingPolicy.KebabCaseLower;
        var result = policy.ConvertName("XMLParser");
        await Assert.That(result).IsEqualTo("xml-parser");
    }

    [Test]
    public async Task KebabCase_MultipleAcronyms_ConvertsCorrectly()
    {
        var policy = JsonNamingPolicy.KebabCaseLower;
        var result = policy.ConvertName("HTTPResponseCode");
        await Assert.That(result).IsEqualTo("http-response-code");
    }

    [Test]
    public async Task PropertyNameCaseInsensitive_DefaultIsCaseInsensitive()
    {
        // When option is not set (default), matching is case-insensitive
        var result = TextHelpers.Eq("FullName"u8, "fullname"u8);
        await Assert.That(result).IsTrue();

        // Explicit true: also case-insensitive
        result = TextHelpers.Eq("FullName"u8, "fullname"u8, false);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task PropertyNameCaseInsensitive_WhenFalse_MatchIsCaseSensitive()
    {
        // When caseSensitive=true (i.e., PropertyNameCaseInsensitive=false), match is exact
        var result = TextHelpers.Eq("FullName"u8, "fullname"u8, caseSensitive: true);
        await Assert.That(result).IsFalse();

        // Same case matches
        result = TextHelpers.Eq("FullName"u8, "FullName"u8, caseSensitive: true);
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task PropertyNameCaseInsensitive_RoundTrip_WithOption()
    {
        // Round-trip with PropertyNameCaseInsensitive=true should work
        var model = ComplexModelFactory.Create();
        var opts = new JsonOptions { PropertyNameCaseInsensitive = true };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var back = JsonSerializer.Deserialize<ComplexModel>(bytes, opts);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task SnakeCase_Serialize_ProducesSnakeCaseKeys()
    {
        // SnakeCase serialization only affects output keys.
        // Deserialization matches against compiled property names (PascalCase).
        var model = ComplexModelFactory.Create();
        var opts = new JsonOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, opts);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        // Simple property 'Bool' should become 'bool'
        await Assert.That(text).Contains("\"bool\"");
        // Complex property 'DateTime' should become 'date_time'
        await Assert.That(text).Contains("\"date_time\"");
        // Property 'String' should become 'string'
        await Assert.That(text).Contains("\"string\"");
    }
}
