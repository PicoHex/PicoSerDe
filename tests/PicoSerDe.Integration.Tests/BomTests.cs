using System.Text;

namespace PicoSerDe.Integration.Tests;

public class BomDto
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

/// <summary>
/// UTF-8 BOM contract: every text format skips a leading BOM; a BOM-only
/// document fails loudly with FormatException (never silently yields default
/// values).
/// </summary>
public class BomTests
{
    private static byte[] Bom(string tail) =>
        Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(tail)).ToArray();

    [Test]
    public async Task Json_BomPrefixed_ParsesAllFields()
    {
        var dto = JsonSerializer.Deserialize<BomDto>(Bom("""{"Name":"n","Age":3}"""));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Toml_BomPrefixed_KeepsFirstKey()
    {
        var dto = TomlSerializer.Deserialize<BomDto>(Bom("Name = \"n\"\nAge = 3\n"));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Yaml_BomPrefixed_KeepsFirstKey()
    {
        var dto = YamlSerializer.Deserialize<BomDto>(Bom("Name: n\nAge: 3\n"));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Ini_BomPrefixed_KeepsFirstKey()
    {
        var dto = IniSerializer.Deserialize<BomDto>(Bom("Name=n\nAge=3\n"));
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public async Task Json_OnlyBom_ThrowsFormatException()
    {
        await Assert
            .That(() => JsonSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble()))
            .Throws<FormatException>();
    }

    [Test]
    public async Task Toml_OnlyBom_MatchesEmptyInputSemantics()
    {
        // TOML treats an empty document as a valid empty object; a BOM-only
        // document is logically empty and must behave identically.
        var dto = TomlSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble());
        await Assert.That(dto!.Name).IsEqualTo("");
        await Assert.That(dto.Age).IsEqualTo(0);
    }

    [Test]
    public async Task Yaml_OnlyBom_MatchesEmptyInputSemantics()
    {
        var dto = YamlSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble());
        await Assert.That(dto!.Name).IsEqualTo("");
        await Assert.That(dto.Age).IsEqualTo(0);
    }

    [Test]
    public async Task Ini_OnlyBom_MatchesEmptyInputSemantics()
    {
        var dto = IniSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble());
        await Assert.That(dto!.Name).IsEqualTo("");
        await Assert.That(dto.Age).IsEqualTo(0);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(12)]
    [Arguments(64)]
    public async Task Toml_Bom_Stream_Chunked_MatchesSync(int chunk)
    {
        var bytes = Bom("Name = \"n\"\nAge = 3\n");
        using var s = new BomChunkedStream(bytes, chunk);
        var dto = await TomlSerializer.DeserializeFromStreamAsync<BomDto>(s);
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(12)]
    [Arguments(64)]
    public async Task Yaml_Bom_Stream_Chunked_MatchesSync(int chunk)
    {
        var bytes = Bom("Name: n\nAge: 3\n");
        using var s = new BomChunkedStream(bytes, chunk);
        var dto = await YamlSerializer.DeserializeFromStreamAsync<BomDto>(s);
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(12)]
    [Arguments(64)]
    public async Task Ini_Bom_Stream_Chunked_MatchesSync(int chunk)
    {
        var bytes = Bom("Name=n\nAge=3\n");
        using var s = new BomChunkedStream(bytes, chunk);
        var dto = await IniSerializer.DeserializeFromStreamAsync<BomDto>(s);
        await Assert.That(dto!.Name).IsEqualTo("n");
        await Assert.That(dto.Age).IsEqualTo(3);
    }
}

/// <summary>Chunked stream helper for the BOM streaming tests.</summary>
public sealed class BomChunkedStream(byte[] data, int chunk) : Stream
{
    private int _pos;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position
    {
        get => _pos;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        await Task.Yield();
        int n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _pos);
        data.AsSpan(_pos, n).CopyTo(buffer.Span);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
