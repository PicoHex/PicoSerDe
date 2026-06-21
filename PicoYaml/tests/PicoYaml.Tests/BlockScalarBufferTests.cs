namespace PicoYaml.Tests;

public class BlockScalarBufferTests
{
    private static string ReadBlockScalar(ReadOnlySpan<byte> yaml, int skipReads = 0)
    {
        var reader = new YamlReader(yaml);
        for (int i = 0; i < skipReads; i++)
            reader.Read();
        reader.Read();
        return Encoding.UTF8.GetString(reader.ValueSpan);
    }

    [Test]
    public async Task BlockScalar_Literal_ReadsCorrectly()
    {
        // |  default chomp keeps final line break
        var yaml = "key: |\n  line1\n  line2\n"u8;
        var value = ReadBlockScalar(yaml);
        await Assert.That(value).IsEqualTo("line1\nline2\n");
    }

    [Test]
    public async Task BlockScalar_Folded_ReadsCorrectly()
    {
        // > default chomp keeps final line break
        var yaml = "key: >\n  folded\n  text\n"u8;
        var value = ReadBlockScalar(yaml);
        await Assert.That(value).IsEqualTo("folded text\n");
    }

    [Test]
    public async Task BlockScalar_AfterManyReads_DataStillValid()
    {
        // Fill 8 RentBuf slots + wrap around, THEN read block scalar
        var yaml =
            "a: 1\nb: 2\nc: 3\nd: 4\ne: 5\nf: 6\ng: 7\nh: 8\ni: 9\nkey: |\n  preserved\nnext: done\n"u8;
        var value = ReadBlockScalar(yaml, skipReads: 9);
        await Assert.That(value).IsEqualTo("preserved\n");
    }

    [Test]
    public async Task BlockScalar_StripChomp()
    {
        // |− = no trailing newline
        var yaml = "key: |-\n  line1\n  line2\n"u8;
        var value = ReadBlockScalar(yaml);
        await Assert.That(value).IsEqualTo("line1\nline2");
    }

    [Test]
    public async Task BlockScalar_KeepChomp()
    {
        // |+ = keeps trailing newline and empty lines (extra newline from clip + keep)
        var yaml = "key: |+\n  line1\n  line2\n"u8;
        var value = ReadBlockScalar(yaml);
        await Assert.That(value).IsEqualTo("line1\nline2\n\n");
    }

    [Test]
    public async Task BlockScalar_FollowedByNextKey_SyncPreserved()
    {
        var yaml = "key: |\n  text\nnext: done\n"u8;
        var reader = new YamlReader(yaml);
        reader.Read(); // key
        var value = Encoding.UTF8.GetString(reader.ValueSpan);
        reader.Read(); // next
        var nextKey = Encoding.UTF8.GetString(reader.KeySpan);
        var nextValue = Encoding.UTF8.GetString(reader.ValueSpan);
        await Assert.That(value).IsEqualTo("text\n");
        await Assert.That(nextKey).IsEqualTo("next");
        await Assert.That(nextValue).IsEqualTo("done");
    }
}
