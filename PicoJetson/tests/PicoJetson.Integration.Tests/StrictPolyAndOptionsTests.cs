namespace PicoJetson.Tests
{
    // ── H3: polymorphic null serialization ──

    [PicoSerializable]
    [PicoPolymorphic]
    [PicoDerivedType(typeof(NullPolyCat), "cat")]
    public abstract class NullPolyAnimal
    {
        public string? Name { get; set; }
    }

    [PicoSerializable]
    public class NullPolyCat : NullPolyAnimal { }

    // ── M2: same short name in different namespaces ──

    namespace PolyNsA
    {
        [PicoSerializable]
        [PicoDerivedType(typeof(Entry), "a")]
        public abstract class Base
        {
            public int V { get; set; }
        }

        [PicoSerializable]
        public class Entry : Base { }
    }

    namespace PolyNsB
    {
        [PicoSerializable]
        [PicoDerivedType(typeof(Entry), "b")]
        public abstract class Base
        {
            public int V { get; set; }
        }

        [PicoSerializable]
        public class Entry : Base { }
    }

    // ── M3: [JsonConstructor] with an unsupported parameter type ──

    public class CtorFallbackModel
    {
        public int A { get; set; }

        public CtorFallbackModel() { }

        [JsonConstructor]
        public CtorFallbackModel(int a, Func<int> unsupported)
        {
            A = a;
        }
    }

    // ── M5: inner helper honors UnmappedMemberHandling.Disallow ──

    public class DisallowOuter
    {
        public string Title { get; set; } = "";
        public DisallowInner Inner { get; set; } = new();
    }

    public class DisallowInner
    {
        public string Key { get; set; } = "";
    }

    // ── H4: WhenWritingDefault ──

    public class DefaultOmittingModel
    {
        public int Count { get; set; }
        public int? Opt { get; set; }
        public string? Note { get; set; }
        public Guid Id { get; set; }
    }

    // ── H5: nullable ctor params + JSON null (poly + plain records) ──

    [PicoSerializable]
    [PicoDerivedType(typeof(PolyCtorEvent), "polyCtorEvent")]
    public abstract record PolyCtorEventBase;

    public sealed record PolyCtorEvent(
        string? ProviderName = null,
        string? ModelId = null,
        string? ApiKey = null,
        string? BaseUrl = null,
        int? Retries = null
    ) : PolyCtorEventBase;

    public sealed record PlainCtorRecord(
        string? ProviderName = null,
        string? ModelId = null,
        string? ApiKey = null,
        string? BaseUrl = null,
        int? Retries = null
    );

    public sealed record NonNullableCtorRecord(string Name, int Age);

    public class StrictPolyAndOptionsTests
    {
        // ── H3 ──

        [Test]
        public async Task Poly_SerializeNullBase_WritesJsonNull()
        {
            NullPolyAnimal? nil = null;
            var json = JsonSerializer.Serialize(nil);
            await Assert.That(json).IsEqualTo("null");
            var back = JsonSerializer.Deserialize<NullPolyAnimal>("null"u8);
            await Assert.That(back).IsNull();
        }

        [Test]
        public async Task Poly_NullBase_DoesNotEmitInvalidJson()
        {
            NullPolyAnimal? nil = null;
            var json = JsonSerializer.Serialize(nil);
            await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
        }

        [Test]
        public async Task Poly_CtorRecordNullableParams_JsonNull_RoundTrips()
        {
            PolyCtorEventBase ev = new PolyCtorEvent(
                ProviderName: "anthropic",
                ModelId: "claude-4"
            );
            var json = JsonSerializer.Serialize(ev);
            var back = JsonSerializer.Deserialize<PolyCtorEventBase>(Encoding.UTF8.GetBytes(json));
            await Assert.That(back).IsTypeOf<PolyCtorEvent>();
            var e = (PolyCtorEvent)back!;
            await Assert.That(e.ProviderName).IsEqualTo("anthropic");
            await Assert.That(e.ModelId).IsEqualTo("claude-4");
            await Assert.That(e.ApiKey).IsNull();
            await Assert.That(e.BaseUrl).IsNull();
            await Assert.That(e.Retries).IsNull();
        }

        [Test]
        public async Task Poly_CtorRecordNullableParams_OldPersistedJsonWithNull_Replays()
        {
            var json =
                """{"$type":"polyCtorEvent","ProviderName":"anthropic","ModelId":"claude-4","ApiKey":null,"BaseUrl":null,"Retries":null}"""u8;
            var back = JsonSerializer.Deserialize<PolyCtorEventBase>(json);
            await Assert.That(back).IsTypeOf<PolyCtorEvent>();
            var e = (PolyCtorEvent)back!;
            await Assert.That(e.ApiKey).IsNull();
            await Assert.That(e.BaseUrl).IsNull();
            await Assert.That(e.Retries).IsNull();
        }

        [Test]
        public async Task Plain_CtorRecordNullableParams_JsonNull_RoundTrips()
        {
            var dto = new PlainCtorRecord(ProviderName: "anthropic", ModelId: "claude-4");
            var json = JsonSerializer.Serialize(dto);
            var back = JsonSerializer.Deserialize<PlainCtorRecord>(Encoding.UTF8.GetBytes(json));
            await Assert.That(back!.ApiKey).IsNull();
            await Assert.That(back.BaseUrl).IsNull();
            await Assert.That(back.Retries).IsNull();
        }

        [Test]
        public async Task CtorRecord_NonNullableParam_JsonNull_StillThrows()
        {
            var threw = false;
            try
            {
                JsonSerializer.Deserialize<NonNullableCtorRecord>("""{"Name":null,"Age":1}"""u8);
            }
            catch (FormatException)
            {
                threw = true;
            }
            await Assert.That(threw).IsTrue();
        }

        // ── M2 ──

        [Test]
        public async Task Poly_SameShortNameDerivedTypesInDifferentNamespaces_Roundtrip()
        {
            PolyNsA.Base a = new PolyNsA.Entry { V = 1 };
            var jsonA = JsonSerializer.Serialize(a);
            var backA = JsonSerializer.Deserialize<PolyNsA.Base>(Encoding.UTF8.GetBytes(jsonA));
            await Assert.That(backA).IsTypeOf<PolyNsA.Entry>();
            await Assert.That(backA.V).IsEqualTo(1);

            PolyNsB.Base b = new PolyNsB.Entry { V = 2 };
            var jsonB = JsonSerializer.Serialize(b);
            var backB = JsonSerializer.Deserialize<PolyNsB.Base>(Encoding.UTF8.GetBytes(jsonB));
            await Assert.That(backB).IsTypeOf<PolyNsB.Entry>();
            await Assert.That(backB.V).IsEqualTo(2);
        }

        // ── M3 ──

        [Test]
        public async Task Ctor_UnsupportedParamType_FallsBackToParameterlessCtor()
        {
            var m = JsonSerializer.Deserialize<CtorFallbackModel>("{\"A\":7}"u8);
            await Assert.That(m!.A).IsEqualTo(7);
        }

        // ── M5 ──

        [Test]
        public async Task InnerHelper_DisallowUnmappedMembers_Throws()
        {
            var opts = new JsonOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            var threw = false;
            try
            {
                JsonSerializer.Deserialize<DisallowOuter>(
                    "{\"Title\":\"t\",\"Inner\":{\"Key\":\"k\",\"Bogus\":1}}"u8,
                    opts
                );
            }
            catch (FormatException)
            {
                threw = true;
            }
            await Assert.That(threw).IsTrue();
        }

        [Test]
        public async Task InnerHelper_UnknownNestedProperty_SkipDefault_StillWorks()
        {
            var m = JsonSerializer.Deserialize<DisallowOuter>(
                "{\"Title\":\"t\",\"Inner\":{\"Key\":\"k\",\"Bogus\":1}}"u8
            );
            await Assert.That(m!.Inner.Key).IsEqualTo("k");
        }

        // ── H4 ──

        [Test]
        public async Task WhenWritingDefault_OmitsDefaultValueTypes()
        {
            var m = new DefaultOmittingModel
            {
                Count = 0,
                Opt = 0,
                Note = null,
                Id = default,
            };
            var json = JsonSerializer.Serialize(
                m,
                new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault }
            );
            await Assert.That(json).IsEqualTo("{}");
        }

        [Test]
        public async Task WhenWritingDefault_KeepsNonDefaultValues()
        {
            var m = new DefaultOmittingModel
            {
                Count = 3,
                Opt = 0,
                Note = "x",
                Id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            };
            var json = JsonSerializer.Serialize(
                m,
                new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault }
            );
            await Assert.That(json).Contains("\"Count\":3");
            await Assert.That(json).Contains("\"Note\":\"x\"");
            await Assert.That(json).DoesNotContain("\"Opt\"");
            await Assert.That(json).Contains("\"Id\":\"00112233-4455-6677-8899-aabbccddeeff\"");
        }

        [Test]
        public async Task WhenWritingDefault_NullableWithDefaultValue_IsOmitted()
        {
            var m = new DefaultOmittingModel
            {
                Count = 1,
                Opt = 0,
                Note = null,
                Id = default,
            };
            var json = JsonSerializer.Serialize(
                m,
                new JsonOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault }
            );
            await Assert.That(json).Contains("\"Count\":1");
            await Assert.That(json).DoesNotContain("\"Opt\"");
            await Assert.That(json).DoesNotContain("\"Note\"");
            await Assert.That(json).DoesNotContain("\"Id\"");
        }

        // ── C1 (SG-generated streaming, chunked input) ──

        private sealed class ChunkedStream : Stream
        {
            private readonly byte[] _data;
            private readonly int _chunk;
            private int _pos;

            public ChunkedStream(byte[] data, int chunk)
            {
                _data = data;
                _chunk = chunk;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _data.Length;
            public override long Position
            {
                get => _pos;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_pos >= _data.Length)
                    return 0;
                int n = Math.Min(Math.Min(count, _chunk), _data.Length - _pos);
                Array.Copy(_data, _pos, buffer, offset, n);
                _pos += n;
                return n;
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken ct = default
            )
            {
                if (_pos >= _data.Length)
                    return 0;
                int n = Math.Min(Math.Min(buffer.Length, _chunk), _data.Length - _pos);
                _data.AsSpan(_pos, n).CopyTo(buffer.Span);
                _pos += n;
                return n;
            }

            public override void Flush() { }

            public override long Seek(long offset, SeekOrigin origin) =>
                throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }

        [Test]
        public async Task Streaming_ObjectSplitAcrossChunks_PreservesAllValues()
        {
            var payload = "{\"Name\":\"Alice\",\"Age\":30,\"Opt\":7}"u8.ToArray();
            var stream = new ChunkedStream(payload, 5);
            var m = await JsonSerializer.DeserializeFromStreamAsync<StrictModel>(stream);
            await Assert.That(m.Name).IsEqualTo("Alice");
            await Assert.That(m.Age).IsEqualTo(30);
        }

        [Test]
        public async Task Streaming_IntArraySplitAcrossChunks_PreservesAllElements()
        {
            var payload = "[10,20,30]"u8.ToArray();
            var stream = new ChunkedStream(payload, 3);
            var arr = await JsonSerializer.DeserializeFromStreamAsync<int[]>(stream);
            await Assert.That(arr).Count().IsEqualTo(3);
            await Assert.That(arr[0]).IsEqualTo(10);
            await Assert.That(arr[1]).IsEqualTo(20);
            await Assert.That(arr[2]).IsEqualTo(30);
        }

        [Test]
        public async Task Streaming_PolySplitAcrossChunks_DispatchesCorrectly()
        {
            var payload = "{\"$type\":\"cat\",\"Name\":\"tom\"}"u8.ToArray();
            var stream = new ChunkedStream(payload, 4);
            var a = await JsonSerializer.DeserializeFromStreamAsync<NullPolyAnimal>(stream);
            await Assert.That(a).IsTypeOf<NullPolyCat>();
            await Assert.That(a.Name).IsEqualTo("tom");
        }
    }
}
