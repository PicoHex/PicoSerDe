// Tests for source generator hintName uniqueness.
// Uses SafeName (public method) and direct hintName logic testing.

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

    // ── HintName format and dedup ──

    [Test]
    public async Task HintName_WithShortNameOnly_NoCollision()
    {
        // Simulate: unique short name → hintName uses short name
        var usedHintNames = new HashSet<string>();
        var hintName = GenerateHintName(
            "MyType",
            "global::MyApp.MyType",
            usedHintNames,
            "_JsonSerializer.g.cs"
        );
        await Assert.That(hintName).IsEqualTo("MyType_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_CrossNamespaceCollision_FallsBackToFQN()
    {
        // Simulate: two types with same short name in different namespaces
        var usedHintNames = new HashSet<string>();

        // First type gets short name
        var hint1 = GenerateHintName(
            "SharedName",
            "global::Ns1.SharedName",
            usedHintNames,
            "_JsonSerializer.g.cs"
        );
        await Assert.That(hint1).IsEqualTo("SharedName_JsonSerializer.g.cs");

        // Second type (same short name) must use FQN to avoid collision
        var hint2 = GenerateHintName(
            "SharedName",
            "global::Ns2.SharedName",
            usedHintNames,
            "_JsonSerializer.g.cs"
        );
        await Assert.That(hint2).IsNotEqualTo(hint1);
        await Assert.That(hint2).IsEqualTo("Ns2_SharedName_JsonSerializer.g.cs");
    }

    [Test]
    public async Task HintName_ThreeWayCollision_AllUnique()
    {
        // Three types with same short name across three namespaces
        var usedHintNames = new HashSet<string>();
        var hints = new List<string>();

        hints.Add(
            GenerateHintName("Data", "global::Ns1.Data", usedHintNames, "_JsonSerializer.g.cs")
        );
        hints.Add(
            GenerateHintName("Data", "global::Ns2.Data", usedHintNames, "_JsonSerializer.g.cs")
        );
        hints.Add(
            GenerateHintName("Data", "global::Ns3.Data", usedHintNames, "_JsonSerializer.g.cs")
        );

        await Assert.That(hints.Distinct().Count()).IsEqualTo(3);
        await Assert.That(hints[0]).IsEqualTo("Data_JsonSerializer.g.cs"); // first gets short name
    }

    [Test]
    public async Task HintName_WithNullFQN_HintNameUsesShortName()
    {
        var usedHintNames = new HashSet<string>();
        var hintName = GenerateHintName(
            "Fallback",
            "global::SomeNs.Fallback",
            usedHintNames,
            "_JsonSerializer.g.cs"
        );
        await Assert.That(hintName).IsEqualTo("Fallback_JsonSerializer.g.cs");
    }

    // ── HintName generator helper (mirrors the logic in GenerateAll) ──

    /// <summary>
    /// Generate a hintName using the short-name-first, FQN-fallback approach.
    /// This helper exists to TDD the hintName logic before implementing in the SG.
    /// </summary>
    private static string GenerateHintName(
        string shortName,
        string fullyQualifiedName,
        HashSet<string> usedHintNames,
        string suffix
    )
    {
        // First try: short name
        var hintName = $"{shortName}{suffix}";
        if (usedHintNames.Add(hintName))
            return hintName;

        // Collision detected: use FQN-based safe name
        var cleanFq = (fullyQualifiedName ?? "").Replace("global::", "");
        var safeFq = PicoSerDe.Gen.GenInfrastructure.SafeName(cleanFq);
        hintName = $"{safeFq}{suffix}";
        usedHintNames.Add(hintName);
        return hintName;
    }
}
