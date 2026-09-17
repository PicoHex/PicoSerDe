using PicoIni;
using PicoJetson;
using PicoMsgPack;
using PicoToml;
using PicoYaml;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// SafeName collisions: two distinct fully-qualified names (CollisionNs.Sub.Inner
/// and CollisionNs.Sub_Inner) normalize to the same generated file/class name.
/// Generated names must be collision-free in every format — both types keep
/// working serializers, and the holder that nests both round-trips each member
/// independently (no cross-wired helper).
/// </summary>
public class HintNameCollisionTests
{
    [Test]
    public async Task CollidingTypes_AllFormats_HaveWorkingSerializers()
    {
        int ok = 0;
        foreach (
            var act in new Func<object?>[]
            {
                () => JsonSerializer.Serialize(new global::CollisionNs.Sub.Inner { V = 1 }),
                () => JsonSerializer.Serialize(new global::CollisionNs.Sub_Inner { W = 2 }),
                () => TomlSerializer.Serialize(new global::CollisionNs.Sub.Inner { V = 1 }),
                () => TomlSerializer.Serialize(new global::CollisionNs.Sub_Inner { W = 2 }),
                () => YamlSerializer.Serialize(new global::CollisionNs.Sub.Inner { V = 1 }),
                () => YamlSerializer.Serialize(new global::CollisionNs.Sub_Inner { W = 2 }),
                () => IniSerializer.Serialize(new global::CollisionNs.Sub.Inner { V = 1 }),
                () => IniSerializer.Serialize(new global::CollisionNs.Sub_Inner { W = 2 }),
                () =>
                    MsgPackSerializer.SerializeToUtf8Bytes(
                        new global::CollisionNs.Sub.Inner { V = 1 }
                    ),
                () =>
                    MsgPackSerializer.SerializeToUtf8Bytes(
                        new global::CollisionNs.Sub_Inner { W = 2 }
                    ),
            }
        )
        {
            act();
            ok++;
        }

        await Assert.That(ok).IsEqualTo(10);
    }

    [Test]
    public async Task CollidingTypes_NestedInOneDto_RoundTripIndependently()
    {
        var holder = new global::CollisionNs.SubHolder
        {
            A = new global::CollisionNs.Sub.Inner { V = 7 },
            B = new global::CollisionNs.Sub_Inner { W = 9 },
        };

        var json = JsonSerializer.Serialize(holder);
        var back = JsonSerializer.Deserialize<global::CollisionNs.SubHolder>(
            System.Text.Encoding.UTF8.GetBytes(json)
        );

        await Assert.That(back!.A?.V).IsEqualTo(7);
        await Assert.That(back.B?.W).IsEqualTo(9);

        var toml = TomlSerializer.Serialize(holder);
        var tomlBack = TomlSerializer.Deserialize<global::CollisionNs.SubHolder>(
            System.Text.Encoding.UTF8.GetBytes(toml)
        );
        await Assert.That(tomlBack!.A?.V).IsEqualTo(7);
        await Assert.That(tomlBack.B?.W).IsEqualTo(9);

        var yaml = YamlSerializer.Serialize(holder);
        var yamlBack = YamlSerializer.Deserialize<global::CollisionNs.SubHolder>(
            System.Text.Encoding.UTF8.GetBytes(yaml)
        );
        await Assert.That(yamlBack!.A?.V).IsEqualTo(7);
        await Assert.That(yamlBack.B?.W).IsEqualTo(9);
    }
}
