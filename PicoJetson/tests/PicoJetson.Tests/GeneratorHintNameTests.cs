// Tests for source generator hintName uniqueness.
// Uses SafeName (public method) and direct hintName logic testing.
// After refactoring: hintName always uses FQN (SafeName(FullyQualifiedName)),
// matching the Inner Helper naming strategy. No two-step short-then-FQN fallback.

using PicoJetson.Gen;

namespace PicoJetson.Tests;

public sealed class GeneratorHintNameTests
{
    // ── SafeName correctness ──

    [Test]
    public async Task SafeName_TopLevelType_RemovesGlobalPrefix()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("global::System.String");
        await Assert.That(result).IsEqualTo("System_String");
    }

    [Test]
    public async Task SafeName_ReplacesDotsWithUnderscores()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("MyApp.Models.UserDto");
        await Assert.That(result).IsEqualTo("MyApp_Models_UserDto");
    }

    [Test]
    public async Task SafeName_CrossNamespaceSameShortName_Unique()
    {
        var safe1 = PicoSerDe.Gen.GenInfrastructure.SafeName("global::Ns1.SharedName");
        var safe2 = PicoSerDe.Gen.GenInfrastructure.SafeName("global::Ns2.SharedName");
        await Assert.That(safe1).IsNotEqualTo(safe2);
    }

    [Test]
    public async Task SafeName_EmptyInput_ReturnsEmpty()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("");
        await Assert.That(result).IsEqualTo("");
    }

    [Test]
    public async Task SafeName_AlreadyClean_NoChange()
    {
        var result = PicoSerDe.Gen.GenInfrastructure.SafeName("SimpleName");
        await Assert.That(result).IsEqualTo("SimpleName");
    }

    // ── HintName: always uses FQN, zero collision risk ──

    [Test]
    public async Task HintName_AlwaysUsesFQN_EvenWhenNoCollision()
    {
        // Single unique type → still uses FQN, not short name
        var hintName = GenerateHintName("global::MyApp.MyType", "_JsonSerializer.g.cs");
        await Assert.That(hintName).IsEqualTo("MyApp_MyType_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_CrossNamespaceSameShortName_EveryTypeUsesFQN()
    {
        // Two types with same short name in different namespaces —
        // both use FQN, no collision, no "first gets short name" special case
        var hint1 = GenerateHintName("global::Ns1.SharedName", "_JsonSerializer.g.cs");
        var hint2 = GenerateHintName("global::Ns2.SharedName", "_JsonSerializer.g.cs");

        await Assert.That(hint1).IsEqualTo("Ns1_SharedName_JsonSerializer.g.cs");
        await Assert.That(hint2).IsEqualTo("Ns2_SharedName_JsonSerializer.g.cs");
        await Assert.That(hint1).IsNotEqualTo(hint2);
    }

    [Test]
    public async Task HintName_ThreeSameNamedTypes_AllHaveUniqueFQNNames()
    {
        // Three types with same short name across three namespaces
        var hints = new[]
        {
            GenerateHintName("global::Ns1.Data", "_JsonSerializer.g.cs"),
            GenerateHintName("global::Ns2.Data", "_JsonSerializer.g.cs"),
            GenerateHintName("global::Ns3.Data", "_JsonSerializer.g.cs"),
        };

        await Assert.That(hints.Distinct().Count()).IsEqualTo(3);
        // None uses short name — all are FQN-based
        await Assert.That(hints[0]).IsEqualTo("Ns1_Data_JsonSerializer.g.cs");
        await Assert.That(hints[1]).IsEqualTo("Ns2_Data_JsonSerializer.g.cs");
        await Assert.That(hints[2]).IsEqualTo("Ns3_Data_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_GlobalNamespaceType_NoExtraUnderscore()
    {
        // Type with no namespace (global::) should not produce a leading underscore
        var hintName = GenerateHintName("global::GlobalType", "_JsonSerializer.g.cs");
        await Assert.That(hintName).IsEqualTo("GlobalType_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_NestedNamespace_DotsBecomeUnderscores()
    {
        var hintName = GenerateHintName(
            "global::MyCompany.MyApp.Core.Models.UserProfile",
            "_JsonSerializer.g.cs"
        );
        await Assert
            .That(hintName)
            .IsEqualTo("MyCompany_MyApp_Core_Models_UserProfile_JsonSerializer.g.cs");
    }

    // ── HintName generator helper (mirrors the new FQN-only logic in GenerateAll) ──

    /// <summary>
    /// Generate a hintName using the FQN-based approach.
    /// Always uses SafeName(FullyQualifiedName) — no short-name-fallback.
    /// </summary>
    private static string GenerateHintName(string fullyQualifiedName, string suffix)
    {
        var cleanFq = (fullyQualifiedName ?? "").Replace("global::", "");
        var safeFq = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanFq);
        return $"{safeFq}{suffix}";
    }
}
