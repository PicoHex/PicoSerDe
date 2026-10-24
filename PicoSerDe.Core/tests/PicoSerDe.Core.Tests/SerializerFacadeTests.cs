namespace PicoSerDe.Core.Tests;

// Fresh marker to isolate static registry state.
internal readonly struct FacadeFmt { }

[NotInParallel]
public class SerializerFacadeTests
{
    [Test]
    public async Task Facade_RegistersAndSerializes()
    {
        SerDelegate<int> handler = static (writer, value, _) =>
        {
            var span = writer.GetSpan(4);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
            writer.Advance(4);
        };
        SerializerFacade<FacadeFmt>.Register(handler);
        var bytes = SerializerFacade<FacadeFmt>.SerializeToUtf8Bytes(42);
        await Assert
            .That(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes))
            .IsEqualTo(42);
    }

    [Test]
    public async Task Facade_RegisterDelegatePair_Deserializes()
    {
        SerializerFacade<FacadeFmt>.Register(
            static (writer, value, _) =>
            {
                var span = writer.GetSpan(4);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
                writer.Advance(4);
            },
            static (data, _) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data)
        );
        var payload = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, 99);
        var result = SerializerFacade<FacadeFmt>.Deserialize<int>(payload);
        await Assert.That(result).IsEqualTo(99);
    }

    [Test]
    public async Task Facade_OptionsReachHandler()
    {
        SerRegistry<FacadeFmt, int>.Handler = null;
        DesRegistry<FacadeFmt, int>.Deserializer = null;
        SerOptions? captured = null;
        SerializerFacade<FacadeFmt>.Register<int>((writer, value, options) => captured = options);
        var opts = new SerOptions();
        SerializerFacade<FacadeFmt>.Serialize(new ArrayBufferWriter<byte>(), 1, opts);
        await Assert.That(captured).IsEqualTo(opts);
    }

    [Test]
    public async Task Facade_OptionsAwareInterfacePair_ForwardsOptionsAndValue()
    {
        SerRegistry<FacadeFmt, int>.Handler = null;
        DesRegistry<FacadeFmt, int>.Deserializer = null;

        var probe = new OptionsAwareSerProbe();
        SerializerFacade<FacadeFmt>.Register<int>(probe, new FacadeDes());

        var opts = new SerOptions();
        var buffer = new ArrayBufferWriter<byte>();
        SerializerFacade<FacadeFmt>.Serialize(buffer, 5, opts);

        // The compat path must detect IOptionsSerializer<T> and forward the
        // per-call options — historically they were silently dropped.
        await Assert.That(ReferenceEquals(probe.Captured, opts)).IsTrue();
        await Assert.That(probe.Captured).IsNotNull();
        await Assert.That(probe.SeenValue).IsEqualTo(5);
        await Assert
            .That(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenSpan))
            .IsEqualTo(5);
    }

    [Test]
    public async Task Facade_PlainInterfacePair_StillWritesValue()
    {
        SerRegistry<FacadeFmt, int>.Handler = null;
        DesRegistry<FacadeFmt, int>.Deserializer = null;

        SerializerFacade<FacadeFmt>.Register(new FacadeSer(), new FacadeDes());

        var buffer = new ArrayBufferWriter<byte>();
        SerializerFacade<FacadeFmt>.Serialize(buffer, 7, new SerOptions());

        await Assert
            .That(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer.WrittenSpan))
            .IsEqualTo(7);
    }

    /// <summary>
    /// Probe implementing both interfaces: the facade must route through the
    /// option-aware overload and hand it the caller's <see cref="SerOptions"/>.
    /// </summary>
    private sealed class OptionsAwareSerProbe : ISerializer<int>, IOptionsSerializer<int>
    {
        public SerOptions? Captured;
        public int SeenValue;

        public void Serialize(IBufferWriter<byte> writer, int value)
        {
            // Must not be reached: the facade prefers the options-aware overload.
            SeenValue = -1;
        }

        public void Serialize(IBufferWriter<byte> writer, int value, SerOptions? options)
        {
            Captured = options;
            SeenValue = value;
            var span = writer.GetSpan(4);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
            writer.Advance(4);
        }
    }

    private readonly struct FacadeSer : ISerializer<int>
    {
        public void Serialize(IBufferWriter<byte> writer, int value)
        {
            var span = writer.GetSpan(4);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
            writer.Advance(4);
        }
    }

    private readonly struct FacadeDes : IDeserializer<int>
    {
        public int Deserialize(ReadOnlySpan<byte> data) =>
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data);
    }
}
