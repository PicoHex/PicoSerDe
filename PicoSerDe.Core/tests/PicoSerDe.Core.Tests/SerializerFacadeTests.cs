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
    public async Task Facade_InterfacePair_WrapsWithoutOptions()
    {
        SerRegistry<FacadeFmt, int>.Handler = null;
        DesRegistry<FacadeFmt, int>.Deserializer = null;
        SerOptions? captured = null;
        SerializerFacade<FacadeFmt>.Register(
            new FacadeSer(),
            new FacadeDes()
        );
        SerializerFacade<FacadeFmt>.Serialize(new ArrayBufferWriter<byte>(), 5, new SerOptions());
        await Assert.That(captured).IsNull(); // hand-written impls do not receive options
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
