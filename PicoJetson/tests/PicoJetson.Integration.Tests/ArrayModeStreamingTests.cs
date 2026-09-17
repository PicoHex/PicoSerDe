using System.Text;

namespace PicoJetson.Tests;

public class ArrayModeItem
{
    public int V { get; set; }
    public string S { get; set; } = "";
}

/// <summary>
/// Array-mode DeserializeAsyncEnumerable contract: chunked streams must yield
/// the same elements as the synchronous array deserializer, for any chunk size.
/// </summary>
public class ArrayModeStreamingTests
{
    private const string Payload =
        "[{\"V\":1,\"S\":\"x] } y\"},{\"V\":2,\"S\":\"{\\\"k\\\":[1,2]}\"},{\"V\":3,\"S\":\"z\"}]";

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    [Arguments(8)]
    [Arguments(4096)]
    public async Task ArrayMode_Chunked_MatchesSync(int chunk, CancellationToken ct)
    {
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(Payload), chunk);
        var got = new List<ArrayModeItem>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<ArrayModeItem>(
                s,
                topLevelValues: false,
                options: null,
                ct: ct
            )
        )
            got.Add(item!);

        await Assert
            .That(got.Select(i => i.V + ":" + i.S))
            .IsEquivalentTo(new[] { "1:x] } y", "2:{\"k\":[1,2]}", "3:z" });
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task ArrayMode_EmptyArray_YieldsNothing(int chunk, CancellationToken ct)
    {
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes("[]"), chunk);
        var got = new List<ArrayModeItem>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<ArrayModeItem>(
                s,
                topLevelValues: false,
                options: null,
                ct: ct
            )
        )
            got.Add(item!);

        await Assert.That(got.Count).IsEqualTo(0);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(64)]
    public async Task ArrayMode_ElementLargerThanBuffer_GrowsAndParses(
        int chunk,
        CancellationToken ct
    )
    {
        var big = new string('x', 20_000);
        var payload = "[{\"V\":1,\"S\":\"" + big + "\"},{\"V\":2,\"S\":\"tail\"}]";
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(payload), chunk);
        var got = new List<ArrayModeItem>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<ArrayModeItem>(
                s,
                topLevelValues: false,
                options: null,
                ct: ct
            )
        )
            got.Add(item!);

        await Assert.That(got.Count).IsEqualTo(2);
        await Assert.That(got[0]!.S.Length).IsEqualTo(20_000);
        await Assert.That(got[1]!.S).IsEqualTo("tail");
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task ArrayMode_LeadingComma_Throws(int chunk, CancellationToken ct)
    {
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes("[,1]"), chunk);
        Exception? error = null;
        try
        {
            await foreach (
                var _ in JsonSerializer.DeserializeAsyncEnumerable<ArrayModeItem>(
                    s,
                    topLevelValues: false,
                    options: null,
                    ct: ct
                )
            ) { }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await Assert.That(error).IsTypeOf<FormatException>();
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task ArrayMode_PrettyPrinted_WithWhitespace_Parses(int chunk, CancellationToken ct)
    {
        var payload =
            "[\n  {\n    \"V\": 1,\n    \"S\": \"a\"\n  },\n  {\"V\": 2, \"S\": \"b\"}\n]\n";
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(payload), chunk);
        var got = new List<ArrayModeItem>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<ArrayModeItem>(
                s,
                topLevelValues: false,
                options: null,
                ct: ct
            )
        )
            got.Add(item!);

        await Assert.That(got.Select(i => i.V + ":" + i.S)).IsEquivalentTo(new[] { "1:a", "2:b" });
    }

    [Test]
    [Timeout(30_000)]
    public async Task ArrayMode_NonArrayRoot_Throws(CancellationToken ct)
    {
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes("{\"V\":1}"), 1);
        Exception? error = null;
        try
        {
            await foreach (
                var _ in JsonSerializer.DeserializeAsyncEnumerable<ArrayModeItem>(
                    s,
                    topLevelValues: false,
                    options: null,
                    ct: ct
                )
            ) { }
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await Assert.That(error).IsTypeOf<FormatException>();
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)]
    [Arguments(3)]
    public async Task ArrayMode_TrailingComma_WithOption_Parses(int chunk, CancellationToken ct)
    {
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes("[{\"V\":1},]"), chunk);
        var got = new List<ArrayModeItem>();
        await foreach (
            var item in JsonSerializer.DeserializeAsyncEnumerable<ArrayModeItem>(
                s,
                topLevelValues: false,
                options: new JsonOptions { AllowTrailingCommas = true },
                ct: ct
            )
        )
            got.Add(item!);

        await Assert.That(got.Count).IsEqualTo(1);
    }
}
