Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine(
    "PicoYaml vs YamlDotNet — typed POCO comparison, CoreCLR only (one table per execution mode)"
);
Console.WriteLine($"Runtime: {Environment.Version} | OS: {Environment.OSVersion}");
Console.WriteLine(new string('=', 78));
Console.WriteLine();

var simple = new SimplePoco { Name = "Hello World", Age = 42 };
var simpleBytes = YamlSerializer.SerializeToUtf8Bytes(simple);
var simpleStr = YamlSerializer.Serialize(simple);

var nested = new NestedPoco
{
    Id = 1,
    Name = "Alice",
    Address = new NestedAddress
    {
        Street = "123 Main",
        City = "SF",
        Zip = "94105",
    },
    Tags = new List<string> { "dev", "bench" },
};
var nestedBytes = YamlSerializer.SerializeToUtf8Bytes(nested);
var nestedStr = YamlSerializer.Serialize(nested);

var collection = new CollectionPoco
{
    Scores = Enumerable.Range(0, 100).ToList(),
    Metadata = new() { ["source"] = "benchmark" },
};
var collectionBytes = YamlSerializer.SerializeToUtf8Bytes(collection);
var collectionStr = YamlSerializer.Serialize(collection);

if (BenchProtocol.Mode == ProtocolMode.Jit)
{
    // TABLE — the equivalent comparison, and it exists only here.
    //
    // PicoYaml.Serialize / Deserialize<T>  ↔  YamlDotNet's Serializer.Serialize /
    // Deserializer.Deserialize<T>: one library call each, same input text, same POCO out. Both
    // libraries are reflection-free on our side and reflection-based on theirs, which is a
    // disclosed implementation difference, not a difference in the operation being timed.
    // The builders are constructed once in YamlDotNetTypedArms, outside every delegate.
    //
    // Roles are positional and enforced by BenchRoleGuard: ours = PicoYaml, theirs = YamlDotNet.
    var yamlDotNetRoundTrip = YamlDotNetTypedArms.Deserialize<SimplePoco>(simpleStr);
    Console.WriteLine(
        $"[equiv] YamlDotNet round-trips our text = {yamlDotNetRoundTrip.Name}"
            + $"/{yamlDotNetRoundTrip.Age} (one call each side; input text pre-encoded"
            + " outside the delegates)"
    );
    Console.WriteLine();

    var table = new ProtocolTable("PicoYaml", "YamlDotNet¹", equivalent: true);

    table.Compare(
        "Simple — SerializeToString",
        () => YamlSerializer.Serialize(simple),
        () => YamlDotNetTypedArms.Serialize(simple)
    );
    table.Compare(
        "Simple — Deserialize←string",
        () => YamlSerializer.Deserialize<SimplePoco>(simpleBytes),
        () => YamlDotNetTypedArms.Deserialize<SimplePoco>(simpleStr)
    );
    table.Compare(
        "Nested — SerializeToString",
        () => YamlSerializer.Serialize(nested),
        () => YamlDotNetTypedArms.Serialize(nested)
    );
    table.Compare(
        "Nested — Deserialize←string",
        () => YamlSerializer.Deserialize<NestedPoco>(nestedBytes),
        () => YamlDotNetTypedArms.Deserialize<NestedPoco>(nestedStr)
    );
    table.Compare(
        "Collection — SerializeToString",
        () => YamlSerializer.Serialize(collection),
        () => YamlDotNetTypedArms.Serialize(collection)
    );
    table.Compare(
        "Collection — Deserialize←string",
        () => YamlSerializer.Deserialize<CollectionPoco>(collectionBytes),
        () => YamlDotNetTypedArms.Deserialize<CollectionPoco>(collectionStr)
    );

    table.Print();
    Console.WriteLine();
    Console.WriteLine(
        "¹ YamlDotNet 18.1.0 (theirs): typed POCO API, one call per row. Both builders are built"
            + " once, outside the measured delegates — rebuilding them per call would time the"
            + " builder instead of the operation (that mistake measures ~80-120 µs/op where these"
            + " rows measure 6-20 µs/op). This table exists in CoreCLR runs only; under NativeAOT"
            + " the same two calls do not work at all (see YamlDotNetTypedArms.cs and the AOT"
            + " table's own note)."
    );
    Console.WriteLine(
        "   Input shape is NOT identical on the deserialize rows, and the difference is in the"
            + " competitor's favour: our Deserialize<T>(byte[]) is handed UTF-8 bytes that were"
            + " encoded outside the delegate, so it pays the UTF-8 decode inside the timed region,"
            + " while YamlDotNet's Deserialize<T>(string) is handed a string that was built outside"
            + " it. Both sides do one library call on the same text, but our deserialize columns"
            + " carry one extra decode that theirs does not, which can only understate us."
    );
}
else
{
    // TABLE — AOT: no equivalent arm can exist, and the reason is printed instead of a number.
    //
    // NON-EQUIVALENT in AOT: YamlDotNet's typed POCO API is unusable in this process —
    // Deserialize<T> throws (the POCO's parameterless constructor is trimmed away) and
    // Serialize does not throw at all but writes `{}`. Timing `{}` and calling it YamlDotNet's
    // serialization would be worse than having no arm: it would measure a serializer that wrote
    // nothing. What remains is a self-benchmark of our own two operations, absolute timings only.
    Console.WriteLine(
        "NON-EQUIVALENT in AOT: YamlDotNet 18.1.0's typed POCO API cannot run in this process."
    );
    Console.WriteLine(
        "  Deserialize<T>(text) → YamlDotNet.Core.YamlException: Exception during deserialization"
            + " ← InvalidOperationException: Failed to create an instance of type 'SimplePoco'."
            + " ← MissingMethodException: No parameterless constructor defined for type"
            + " 'SimplePoco'."
    );
    Console.WriteLine(
        "  Serialize(poco) → no exception, and the document is `{}`: a silent empty result."
            + " Its node DOM still parses under AOT, but a DOM walk is not a typed POCO operation,"
            + " so no like-for-like arm exists here."
    );
    Console.WriteLine();

    var table = new ProtocolTable("PicoYaml", theirsLabel: null, equivalent: false);

    table.Measure("Simple — Serialize", () => YamlSerializer.SerializeToUtf8Bytes(simple));
    table.Measure(
        "Simple — Deserialize",
        () => YamlSerializer.Deserialize<SimplePoco>(simpleBytes)
    );
    table.Measure("Nested — Serialize", () => YamlSerializer.SerializeToUtf8Bytes(nested));
    table.Measure(
        "Nested — Deserialize",
        () => YamlSerializer.Deserialize<NestedPoco>(nestedBytes)
    );
    table.Measure("Collection — Serialize", () => YamlSerializer.SerializeToUtf8Bytes(collection));
    table.Measure(
        "Collection — Deserialize",
        () => YamlSerializer.Deserialize<CollectionPoco>(collectionBytes)
    );

    table.Print();
    Console.WriteLine();
    Console.WriteLine(
        "No competitor column in this table: constructing one would mean timing `{}`, and the"
            + " CoreCLR table (which does have the equivalent arm) is a separate run of this same"
            + " program — `dotnet run -c Release`, without PublishAot."
    );
}

Console.WriteLine(new string('=', 78));

Environment.ExitCode = BenchProtocol.ExitCode;
