namespace PicoJetson.Tests;

public class MatrixModel
{
    public List<List<int>> Rows { get; set; } = new();
}

public class NestedStringListModel
{
    public List<List<string>> Groups { get; set; } = new();
}

public class NestedListTests
{
    [Test]
    public async Task SerializeDeserialize_NestedListOfInt_Roundtrips()
    {
        var model = new MatrixModel
        {
            Rows = new List<List<int>>
            {
                new() { 1, 2, 3 },
                new() { 4, 5, 6 },
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<MatrixModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Rows).Count().IsEqualTo(2);
        await Assert.That(result.Rows[0]).Count().IsEqualTo(3);
        await Assert.That(result.Rows[0][0]).IsEqualTo(1);
        await Assert.That(result.Rows[0][2]).IsEqualTo(3);
        await Assert.That(result.Rows[1]).Count().IsEqualTo(3);
        await Assert.That(result.Rows[1][1]).IsEqualTo(5);
    }

    [Test]
    public async Task SerializeDeserialize_NestedStringList_Roundtrips()
    {
        var model = new NestedStringListModel
        {
            Groups = new List<List<string>>
            {
                new() { "a", "b" },
                new() { "c", "d", "e" },
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<NestedStringListModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Groups).Count().IsEqualTo(2);
        await Assert.That(result.Groups[0]).Count().IsEqualTo(2);
        await Assert.That(result.Groups[0][0]).IsEqualTo("a");
        await Assert.That(result.Groups[1]).Count().IsEqualTo(3);
        await Assert.That(result.Groups[1][2]).IsEqualTo("e");
    }

    [Test]
    public async Task SerializeDeserialize_NestedIntList_Empty_Roundtrips()
    {
        var model = new MatrixModel { Rows = new List<List<int>>() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model);
        var result = JsonSerializer.Deserialize<MatrixModel>(bytes);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Rows).Count().IsEqualTo(0);
    }
}
