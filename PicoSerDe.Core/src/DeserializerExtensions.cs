namespace PicoSerDe.Core;

public static class DeserializerExtensions
{
    extension<T>(IDeserializer<T> deserializer)
    {
        public T Deserialize(byte[] data)
        {
            return deserializer.Deserialize(data.AsSpan());
        }

        public T Deserialize(string data)
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            return deserializer.Deserialize(bytes.AsSpan());
        }

        public T DeserializeFromStream(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return deserializer.Deserialize(ms.ToArray().AsSpan());
        }

        public async ValueTask<T> DeserializeFromStreamAsync(Stream stream,
            CancellationToken ct = default
        )
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            return deserializer.Deserialize(ms.ToArray().AsSpan());
        }

        public async ValueTask<T> DeserializeFromPipeAsync(PipeReader reader,
            CancellationToken ct = default
        )
        {
            ReadResult result;
            do
            {
                ct.ThrowIfCancellationRequested();
                result = await reader.ReadAsync(ct);
            } while (!result.IsCompleted && result.Buffer.Length == 0);

            var buffer = result.Buffer;
            byte[] data;

            if (buffer.IsSingleSegment)
            {
                data = buffer.FirstSpan.ToArray();
            }
            else
            {
                data = new byte[buffer.Length];
                buffer.CopyTo(data);
            }

            reader.AdvanceTo(buffer.End);
            return deserializer.Deserialize(data);
        }
    }
}
