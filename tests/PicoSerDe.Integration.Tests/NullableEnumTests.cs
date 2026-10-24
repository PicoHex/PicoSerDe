// Nullable enum members (review C-04): every format must emit a non-nullable
// type argument for Enum.TryParse, otherwise the generated code fails to
// compile with CS0453 ('Level?' must be a non-nullable value type).

namespace PicoSerDe.Integration.Tests;

public enum Level
{
    Low = 1,
    High = 2,
}

public class NullableEnumHolder
{
    public Level? Level { get; set; }
}

public class NullableEnumTests
{
    [Test]
    public async Task Json_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new NullableEnumHolder { Level = Level.High });
        await Assert
            .That(
                JsonSerializer.Deserialize<NullableEnumHolder>(Encoding.UTF8.GetBytes(json))!.Level
            )
            .IsEqualTo(Level.High);
    }

    [Test]
    public async Task OtherFormats_RoundTrip()
    {
        var v = new NullableEnumHolder { Level = Level.High };
        await Assert
            .That(
                TomlSerializer
                    .Deserialize<NullableEnumHolder>(
                        Encoding.UTF8.GetBytes(TomlSerializer.Serialize(v))
                    )!
                    .Level
            )
            .IsEqualTo(Level.High);
        await Assert
            .That(
                YamlSerializer
                    .Deserialize<NullableEnumHolder>(
                        Encoding.UTF8.GetBytes(YamlSerializer.Serialize(v))
                    )!
                    .Level
            )
            .IsEqualTo(Level.High);
        await Assert
            .That(
                IniSerializer
                    .Deserialize<NullableEnumHolder>(
                        Encoding.UTF8.GetBytes(IniSerializer.Serialize(v))
                    )!
                    .Level
            )
            .IsEqualTo(Level.High);
        await Assert
            .That(
                MsgPackSerializer
                    .Deserialize<NullableEnumHolder>(MsgPackSerializer.SerializeToUtf8Bytes(v))!
                    .Level
            )
            .IsEqualTo(Level.High);
    }
}
