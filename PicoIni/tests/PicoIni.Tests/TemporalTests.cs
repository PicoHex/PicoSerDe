namespace PicoIni.Tests;

public class TimeSpanModel
{
    public TimeSpan Duration { get; set; }
}

public class TemporalTests
{
    private readonly struct TimeSpanIniSerializer : ISerializer<TimeSpanModel>
    {
        public void Serialize(IBufferWriter<byte> writer, TimeSpanModel value)
        {
            var iw = new IniWriter(writer);
            iw.WriteKeyValue("Duration", value.Duration.ToString());
        }
    }

    private readonly struct TimeSpanIniDeserializer : IDeserializer<TimeSpanModel>
    {
        public TimeSpanModel Deserialize(ReadOnlySpan<byte> data)
        {
            var reader = new IniReader(data);
            var obj = new TimeSpanModel();
            while (reader.Read())
            {
                if (reader.TokenType == IniTokenType.Key && reader.Key.SequenceEqual("Duration"u8))
                    obj.Duration = TimeSpan.Parse(Encoding.UTF8.GetString(reader.ValueSpan));
            }
            return obj;
        }
    }

    [Test]
    public async Task TimeSpan_ManualRoundTrip_Works()
    {
        IniSerializer.Register(new TimeSpanIniSerializer(), new TimeSpanIniDeserializer());
        var original = new TimeSpanModel { Duration = TimeSpan.FromSeconds(30) };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<TimeSpanModel>(bytes);
        await Assert.That(result?.Duration).IsEqualTo(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task TimeSpan_GeneratedRoundTrip_Works()
    {
        var original = new TimeSpanModel { Duration = TimeSpan.FromSeconds(30) };
        var ini = IniSerializer.Serialize(original);
        var bytes = Encoding.UTF8.GetBytes(ini);
        var result = IniSerializer.Deserialize<TimeSpanModel>(bytes);
        await Assert.That(result?.Duration).IsEqualTo(TimeSpan.FromSeconds(30));
    }
}
