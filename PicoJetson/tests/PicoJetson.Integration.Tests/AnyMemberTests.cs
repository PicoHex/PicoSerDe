namespace PicoJetson.Tests;

// Regression models for the `object?` MEMBER (not dict-value, not list-element) position.
// The any-value emit chain already existed for Dictionary<string, object?> values
// (EmitAnyValueSerialize/Deserialize at the element/dict-value arms) but the two member
// switches — EmitDeserializeValue / EmitMemberScalarWrite — and the constructor-parameter
// switch had no `case "any"`. The write side therefore fell into `default:`, wrote the
// property NAME and then not one byte of value (the JSON writer then refuses to close the
// object: "Cannot close object: property name was written with no value"), and the read
// side fell into `default:` too, leaving the member silently at default/null.

public sealed record ObjectMemberRecord(object? Result);

public sealed class ObjectMemberClass
{
    public object? Result { get; set; }
}

public class AnyMemberTests
{
    [Test]
    public async Task ObjectMember_String_IsWritten_NotAnEmptyBlock()
    {
        var json = JsonSerializer.Serialize(new ObjectMemberRecord("boom"));
        await Assert.That(json).IsEqualTo("{\"Result\":\"boom\"}");
    }

    [Test]
    public async Task ObjectMember_Null_IsWrittenAsNull()
    {
        var json = JsonSerializer.Serialize(new ObjectMemberRecord(null));
        await Assert.That(json).IsEqualTo("{\"Result\":null}");
    }

    [Test]
    public async Task ObjectMember_String_ReadsBackAsString_NotNull()
    {
        var back = JsonSerializer.Deserialize<ObjectMemberRecord>("""{"Result":"boom"}"""u8);
        await Assert.That(back!.Result).IsNotNull();
        await Assert.That(back.Result).IsEqualTo("boom");
    }

    [Test]
    public async Task ObjectMember_RoundTrips_String()
    {
        var back = JsonSerializer.Deserialize<ObjectMemberRecord>(
            JsonSerializer.SerializeToUtf8Bytes(new ObjectMemberRecord("boom"))
        );
        await Assert.That(back!.Result).IsEqualTo("boom");
    }

    [Test]
    public async Task ObjectMember_RoundTrips_Integer()
    {
        var back = JsonSerializer.Deserialize<ObjectMemberRecord>(
            JsonSerializer.SerializeToUtf8Bytes(new ObjectMemberRecord(42))
        );
        // The any-value chain is long/double based (UnsignedIntegerTests.AnyValue_UInt64BeyondInt64_ThrowsOnRead).
        await Assert.That(back!.Result).IsEqualTo(42L);
    }

    [Test]
    public async Task ObjectMember_RoundTrips_Boolean()
    {
        var back = JsonSerializer.Deserialize<ObjectMemberRecord>(
            JsonSerializer.SerializeToUtf8Bytes(new ObjectMemberRecord(true))
        );
        await Assert.That((bool)back!.Result!).IsTrue();
    }

    [Test]
    public async Task ObjectMember_RoundTrips_NestedObject()
    {
        var payload = new Dictionary<string, object?> { ["k"] = "v", ["n"] = 1L };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ObjectMemberRecord(payload));
        await Assert
            .That(Encoding.UTF8.GetString(bytes))
            .IsEqualTo("{\"Result\":{\"k\":\"v\",\"n\":1}}");

        var back = JsonSerializer.Deserialize<ObjectMemberRecord>(bytes);
        var map = back!.Result as Dictionary<string, object?>;
        await Assert.That(map).IsNotNull();
        await Assert.That(map!["k"]).IsEqualTo("v");
        await Assert.That(map["n"]).IsEqualTo(1L);
    }

    [Test]
    public async Task ObjectMember_RoundTrips_List()
    {
        var payload = new List<object?> { "a", 2L, false };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ObjectMemberRecord(payload));
        await Assert.That(Encoding.UTF8.GetString(bytes)).IsEqualTo("{\"Result\":[\"a\",2,false]}");

        var back = JsonSerializer.Deserialize<ObjectMemberRecord>(bytes);
        var list = back!.Result as List<object?>;
        await Assert.That(list).IsNotNull();
        await Assert.That(list!).Count().IsEqualTo(3);
        await Assert.That(list[0]).IsEqualTo("a");
        await Assert.That(list[1]).IsEqualTo(2L);
        await Assert.That((bool)list[2]!).IsFalse();
    }

    // ── Settable member (EmitMemberScalarWrite / EmitDeserializeValue), not the ctor path ──

    [Test]
    public async Task ObjectProperty_String_IsWrittenAndReadBack()
    {
        var json = JsonSerializer.Serialize(new ObjectMemberClass { Result = "boom" });
        await Assert.That(json).IsEqualTo("{\"Result\":\"boom\"}");

        var back = JsonSerializer.Deserialize<ObjectMemberClass>(Encoding.UTF8.GetBytes(json));
        await Assert.That(back!.Result).IsEqualTo("boom");
    }

    [Test]
    public async Task ObjectProperty_Null_WritesAndReadsNull()
    {
        var json = JsonSerializer.Serialize(new ObjectMemberClass());
        await Assert.That(json).IsEqualTo("{\"Result\":null}");

        var back = JsonSerializer.Deserialize<ObjectMemberClass>("""{"Result":null}"""u8);
        await Assert.That(back!.Result).IsNull();
    }
}
