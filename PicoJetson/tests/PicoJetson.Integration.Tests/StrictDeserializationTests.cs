namespace PicoJetson.Tests;

// ── Model types used by StrictDeserializationTests ──

public class StrictModel
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public bool Active { get; set; }
    public Guid Id { get; set; }
    public DateTime When { get; set; }
    public decimal Price { get; set; }
    public double Score { get; set; }
    public List<int> Numbers { get; set; } = new();
    public int[] Fixed { get; set; } = [];
    public Dictionary<string, int> Map { get; set; } = new();
    public List<string?> MaybeStrings { get; set; } = new();
}

public class StrictRequiredModel
{
    public required int Id { get; set; }
    public required string Name { get; set; }
    public int Optional { get; set; }
}

public class StrictNested
{
    public string Title { get; set; } = "";
    public StrictInner Inner { get; set; } = new();
}

public class StrictInner
{
    public string Key { get; set; } = "";
}

/// <summary>
/// C2 regression tests: deserialization must reject malformed or
/// wrongly-typed input loudly instead of silently producing defaults.
/// </summary>
public class StrictDeserializationTests
{
    private static bool ThrowsFormat(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (Exception ex)
            when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            return true;
        }
    }

    [Test]
    public async Task Deserialize_ScalarIntoObject_Throws()
    {
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<StrictModel>("5"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_NullIntoObject_ReturnsNull()
    {
        var m = JsonSerializer.Deserialize<StrictModel>("null"u8);
        await Assert.That(m).IsNull();
    }

    [Test]
    public async Task Deserialize_TrailingGarbage_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<StrictModel>("{\"Age\":1} {\"Age\":2}"u8)
                )
            )
            .IsTrue();
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<StrictModel>("{\"Age\":1} 5"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_StringIntoInt_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() => JsonSerializer.Deserialize<StrictModel>("{\"Age\":\"abc\"}"u8))
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_NumberIntoString_Throws()
    {
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<StrictModel>("{\"Name\":5}"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_StringIntoBool_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<StrictModel>("{\"Active\":\"yes\"}"u8)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_InvalidGuid_Throws()
    {
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<StrictModel>("{\"Id\":\"zzz\"}"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_InvalidDateTime_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() => JsonSerializer.Deserialize<StrictModel>("{\"When\":\"zzz\"}"u8))
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_InvalidDecimal_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() => JsonSerializer.Deserialize<StrictModel>("{\"Price\":\"zzz\"}"u8))
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_NumberOutOfIntRange_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<StrictModel>("{\"Age\":9999999999}"u8)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_ListElementWrongType_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<StrictModel>("{\"Numbers\":[1,\"x\"]}"u8)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_ArrayNullElementInIntArray_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<StrictModel>("{\"Fixed\":[1,null]}"u8)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_TopLevelIntArrayWithNull_Throws()
    {
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<int[]>("[1,null,3]"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_TopLevelStringArrayWithNull_YieldsNullElement()
    {
        var arr = JsonSerializer.Deserialize<string[]>("[null,\"a\"]"u8);
        await Assert.That(arr).Count().IsEqualTo(2);
        await Assert.That(arr![0]).IsNull();
        await Assert.That(arr[1]).IsEqualTo("a");
    }

    [Test]
    public async Task Deserialize_ListOfStringsWithNull_YieldsNullElement()
    {
        var m = JsonSerializer.Deserialize<StrictModel>("{\"MaybeStrings\":[null,\"a\"]}"u8);
        await Assert.That(m!.MaybeStrings).Count().IsEqualTo(2);
        await Assert.That(m.MaybeStrings[0]).IsNull();
        await Assert.That(m.MaybeStrings[1]).IsEqualTo("a");
    }

    [Test]
    public async Task Deserialize_TopLevelScalarIntoArray_Throws()
    {
        await Assert.That(ThrowsFormat(() => JsonSerializer.Deserialize<int[]>("5"u8))).IsTrue();
    }

    [Test]
    public async Task Deserialize_TopLevelObjectIntoArray_Throws()
    {
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<int[]>("{\"a\":1}"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_ArrayWithTrailingComma_NoDuplicateElement()
    {
        var opts = new JsonOptions { AllowTrailingCommas = true };
        var arr = JsonSerializer.Deserialize<int[]>("[1,2,]"u8, opts);
        await Assert.That(arr).Count().IsEqualTo(2);
        await Assert.That(arr![0]).IsEqualTo(1);
        await Assert.That(arr[1]).IsEqualTo(2);
    }

    [Test]
    public async Task Deserialize_ArrayWithTrailingGarbage_Throws()
    {
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<int[]>("[1,2] [3]"u8)))
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_MissingRequiredMember_Throws()
    {
        await Assert
            .That(ThrowsFormat(() => JsonSerializer.Deserialize<StrictRequiredModel>("{}"u8)))
            .IsTrue();
        await Assert
            .That(
                ThrowsFormat(() => JsonSerializer.Deserialize<StrictRequiredModel>("{\"Id\":1}"u8))
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_AllRequiredMembersPresent_Succeeds()
    {
        var m = JsonSerializer.Deserialize<StrictRequiredModel>("{\"Id\":7,\"Name\":\"x\"}"u8);
        await Assert.That(m!.Id).IsEqualTo(7);
        await Assert.That(m.Name).IsEqualTo("x");
    }

    [Test]
    public async Task Deserialize_NestedObjectWrongType_Throws()
    {
        await Assert
            .That(
                ThrowsFormat(() =>
                    JsonSerializer.Deserialize<StrictNested>("{\"Title\":\"t\",\"Inner\":5}"u8)
                )
            )
            .IsTrue();
    }

    [Test]
    public async Task Deserialize_ValidInput_StillWorks()
    {
        var m = JsonSerializer.Deserialize<StrictModel>(
            """{"Name":"n","Age":3,"Active":true,"Id":"00112233-4455-6677-8899-aabbccddeeff","When":"2024-01-02T03:04:05Z","Price":1.5,"Score":2.5,"Numbers":[1,2],"Fixed":[9],"Map":{"k":1},"MaybeStrings":["a"]}"""u8
        );
        await Assert.That(m!.Name).IsEqualTo("n");
        await Assert.That(m.Age).IsEqualTo(3);
        await Assert.That(m.Numbers).Count().IsEqualTo(2);
        await Assert.That(m.Map["k"]).IsEqualTo(1);
    }
}
