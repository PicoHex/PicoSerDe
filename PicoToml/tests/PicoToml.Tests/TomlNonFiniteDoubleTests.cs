namespace PicoToml.Tests;

// ── Fixtures: non-finite doubles ─────────────────────────────────────────
// TOML 1.0 has no NaN and no Infinity (the spec's float literals are decimal
// forms plus `inf`/`+inf`/`-inf`/`nan`, and this writer's grammar has no spelling
// for them either), so a non-finite double is a value the format cannot carry.
// TomlWriter therefore REFUSES to write one -- but until now nothing pinned that
// refusal, so deleting the guard left every test green while the wire said
// `V = NaN`, a document `tomllib` rejects.
//
// The refusal lives at TWO sites, one per way a double reaches the wire, and
// each site has its own fences here because deleting either one alone is
// invisible to the other:
//   A. TomlWriter.cs:225-229  WriteKeyValue(ReadOnlySpan<byte>, double)
//      -> what a `double` MEMBER writes:  V = 1.5
//   B. TomlWriter.cs:372-376  WriteArrayValue(double)
//      -> what a `double` ELEMENT writes: V = [1.5]

/// <summary>A DTO whose <c>double</c> member writes through site A.</summary>
public class TomlNonFiniteMemberDoc
{
    public string Name { get; set; } = "x";
    public double V { get; set; }
}

/// <summary>A DTO whose <c>List&lt;double&gt;</c> element writes through site B.</summary>
public class TomlNonFiniteListDoc
{
    public string Name { get; set; } = "x";
    public List<double> V { get; set; } = new();
}

public class TomlNonFiniteDoubleTests
{
    /// <summary>
    /// The refusal text, verbatim from <c>TomlWriter</c>. Measured, not assumed: it
    /// names no member (the writer knows the value, not which member it belongs to —
    /// <c>ParamName</c> is <c>value</c> and the key has not been written yet, so the
    /// exception cannot attribute the failure to a field).
    /// </summary>
    private const string Refusal = "NaN and Infinity cannot be written as TOML";

    // ── Site A — the member path ─────────────────────────────────────────

    [Test]
    public async Task MemberValue_NaN_Throws()
    {
        var (output, type, message, paramName) = WriteMember(double.NaN);
        // Nothing reaches the wire: the guard fires before WriteKey().
        await Assert.That(output).IsEqualTo(string.Empty);
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
        await Assert.That(paramName).IsEqualTo("value");
    }

    [Test]
    public async Task MemberValue_PositiveInfinity_Throws()
    {
        var (output, type, message, paramName) = WriteMember(double.PositiveInfinity);
        await Assert.That(output).IsEqualTo(string.Empty);
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
        await Assert.That(paramName).IsEqualTo("value");
    }

    [Test]
    public async Task MemberValue_NegativeInfinity_Throws()
    {
        var (output, type, message, paramName) = WriteMember(double.NegativeInfinity);
        await Assert.That(output).IsEqualTo(string.Empty);
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
        await Assert.That(paramName).IsEqualTo("value");
    }

    /// <summary>
    /// The control for the three above: the guard refuses the non-finite values
    /// WITHOUT refusing the finite ones. Without this, a guard that threw on every
    /// double would keep these fences green.
    /// </summary>
    [Test]
    public async Task MemberValue_FiniteDouble_IsWritten()
    {
        var (output, type, message, paramName) = WriteMember(1.5);
        await Assert.That(output).IsEqualTo("V = 1.5\n");
        await Assert.That(type).IsEqualTo("<no throw>").Because(message);
        await Assert.That(paramName).IsEqualTo(string.Empty);
    }

    // ── Site B — the array-element path ──────────────────────────────────

    [Test]
    public async Task ArrayElement_NaN_Throws()
    {
        var (output, type, message, paramName) = WriteArrayElement(double.NaN);
        // The guard sits before ArrayBeforeValue(), so the array's own prefix is the
        // last byte written: `V = [` and not one byte of the value. Asserted exactly,
        // because the difference is the whole point -- with the guard gone the same
        // document continues as `V = [NaN]`.
        await Assert.That(output).IsEqualTo("V = [");
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
        await Assert.That(paramName).IsEqualTo("value");
    }

    [Test]
    public async Task ArrayElement_PositiveInfinity_Throws()
    {
        var (output, type, message, paramName) = WriteArrayElement(double.PositiveInfinity);
        await Assert.That(output).IsEqualTo("V = [");
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
        await Assert.That(paramName).IsEqualTo("value");
    }

    [Test]
    public async Task ArrayElement_NegativeInfinity_Throws()
    {
        var (output, type, message, paramName) = WriteArrayElement(double.NegativeInfinity);
        await Assert.That(output).IsEqualTo("V = [");
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
        await Assert.That(paramName).IsEqualTo("value");
    }

    /// <summary>The array path's control — see <see cref="MemberValue_FiniteDouble_IsWritten"/>.</summary>
    [Test]
    public async Task ArrayElement_FiniteDouble_IsWritten()
    {
        var (output, type, message, paramName) = WriteArrayElement(1.5);
        await Assert.That(output).IsEqualTo("V = [1.5]\n");
        await Assert.That(type).IsEqualTo("<no throw>").Because(message);
        await Assert.That(paramName).IsEqualTo(string.Empty);
    }

    // ── The public path: Serialize<T> reaches the same two sites ─────────
    // A raw-writer fence does not prove the guards sit on the path real callers
    // take. These two drive TomlSerializer, so the generated serializer's own
    // delegation (`tw.WriteKeyValue("V"u8, v.V)` for a member, `tw.WriteArrayValue`
    // for a list element) is pinned as well.

    [Test]
    public async Task Serialize_DoubleMemberNaN_Throws()
    {
        var (_, type, message) = Serialize(new TomlNonFiniteMemberDoc { V = double.NaN });
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
    }

    [Test]
    public async Task Serialize_DoubleListWithInfinity_Throws()
    {
        var doc = new TomlNonFiniteListDoc();
        doc.V.Add(1.5);
        doc.V.Add(double.PositiveInfinity);
        var (_, type, message) = Serialize(doc);
        await Assert.That(type).IsEqualTo("ArgumentException");
        await Assert.That(message).Contains(Refusal);
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    // TomlWriter is a ref struct, so it cannot be captured by the lambdas that
    // Assert.That(...).Throws<T>() needs: every capture is an explicit try/catch
    // returning what happened (the shape the MsgPack reader fences already use).

    private static (string Output, string Type, string Message, string ParamName) WriteMember(
        double value
    )
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        var writer = new TomlWriter(buffer);
        var type = "<no throw>";
        var message = "<no throw>";
        var paramName = string.Empty;
        try
        {
            writer.WriteKeyValue("V"u8, value);
        }
        catch (Exception ex)
        {
            (type, message, paramName) = Describe(ex);
        }
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), type, message, paramName);
    }

    private static (string Output, string Type, string Message, string ParamName) WriteArrayElement(
        double value
    )
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        var writer = new TomlWriter(buffer);
        var type = "<no throw>";
        var message = "<no throw>";
        var paramName = string.Empty;
        try
        {
            writer.WriteStartArray("V"u8);
            writer.WriteArrayValue(value);
            writer.WriteEndArray();
        }
        catch (Exception ex)
        {
            (type, message, paramName) = Describe(ex);
        }
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), type, message, paramName);
    }

    private static (string Output, string Type, string Message) Serialize(object doc)
    {
        var output = "<no throw>";
        try
        {
            // Each fixture goes through its own generated serializer.
            output = doc switch
            {
                TomlNonFiniteMemberDoc member => Encoding.UTF8.GetString(
                    TomlSerializer.SerializeToUtf8Bytes(member)
                ),
                TomlNonFiniteListDoc list => Encoding.UTF8.GetString(
                    TomlSerializer.SerializeToUtf8Bytes(list)
                ),
                _ => throw new InvalidOperationException("unexpected fixture"),
            };
            return (output, "<no throw>", "<no throw>");
        }
        catch (Exception ex)
        {
            var (type, message, _) = Describe(ex);
            return (output, type, message);
        }
    }

    private static (string Type, string Message, string ParamName) Describe(Exception ex) =>
        (
            ex.GetType().Name,
            string.IsNullOrEmpty(ex.Message) ? "<no message>" : ex.Message,
            ex is ArgumentException argument ? argument.ParamName ?? string.Empty : string.Empty
        );
}
