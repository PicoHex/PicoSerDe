using PicoIni;
using PicoJetson;
using PicoToml;
using PicoYaml;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// SafeName collisions: two distinct fully-qualified names (CollisionNs.Sub.Inner
/// and CollisionNs.Sub_Inner) normalize to the same generated file name. The
/// generators must skip the second type with PICOSERDE002 instead of throwing
/// (which would drop every generated file in the compilation).
/// </summary>
public class HintNameCollisionTests
{
    [Test]
    public async Task CollidingTypes_DoNotKillTheGenerators()
    {
        int ok = 0,
            dropped = 0;
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
            }
        )
        {
            try
            {
                act();
                ok++;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No serializer"))
            {
                // The skipped type fails loudly (PICOSERDE002 documented it).
                dropped++;
            }
        }

        await Assert.That(ok + dropped).IsEqualTo(8);
        await Assert.That(ok).IsGreaterThan(0);
    }
}
