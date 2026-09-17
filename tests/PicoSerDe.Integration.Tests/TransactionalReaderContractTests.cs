using System.Buffers;
using PicoIni;
using PicoJetson;
using PicoToml;
using PicoYaml;

namespace PicoSerDe.Integration.Tests;

/// <summary>
/// Shared transactional-reader contract (PicoSerDe.Core.ITransactionalTokenReader):
/// - Mark() snapshots position + parser state;
/// - RewindToMark() restores them exactly;
/// - a Read() that reports NeedsMoreData leaves the reader unchanged.
/// This contract is what makes member-level streaming resume possible for the
/// text formats (YAML indent stacks, INI section context, TOML arrays).
/// </summary>
public class TransactionalReaderContractTests
{
    private static bool Implements<TReader>(TReader reader)
        where TReader : ITransactionalTokenReader, allows ref struct
    {
        _ = reader.TokenStart;
        return true;
    }

    [Test]
    public async Task AllTextReaders_Implement_TransactionalContract()
    {
        var json = Implements(new JsonReader("{}"u8));
        var toml = Implements(new TomlReader("a = 1"u8));
        var yaml = Implements(new YamlReader("a: 1"u8));
        var ini = Implements(new IniReader("a=1"u8));
        await Assert.That(json && toml && yaml && ini).IsTrue();
    }

    private static (List<string> First, List<string> Second) YamlMarkReplay()
    {
        var data = "A: 1\nB:\n  C: 2\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        var first = new List<string>();
        var second = new List<string>();
        var r = new YamlReader(seq);
        r.Read(); // prime
        r.Mark();
        while (r.Read() && first.Count < 4)
            first.Add(r.TokenType.ToString());
        r.RewindToMark();
        while (r.Read() && second.Count < 4)
            second.Add(r.TokenType.ToString());
        r.Dispose();
        return (first, second);
    }

    [Test]
    public async Task Yaml_MarkAndRewind_ReplaysSameTokenStream()
    {
        var (first, second) = YamlMarkReplay();
        await Assert.That(first.Count).IsGreaterThan(0);
        await Assert.That(second).IsEquivalentTo(first);
    }

    private static (List<string> First, List<string> Second) IniMarkReplay()
    {
        var data = "A=1\nB=2\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        var first = new List<string>();
        var second = new List<string>();
        var r = new IniReader(seq);
        r.Read(); // prime
        r.Mark();
        while (r.Read() && first.Count < 2)
            first.Add(r.TokenType.ToString());
        r.RewindToMark();
        while (r.Read() && second.Count < 2)
            second.Add(r.TokenType.ToString());
        r.Dispose();
        return (first, second);
    }

    [Test]
    public async Task Ini_MarkAndRewind_ReplaysSameTokenStream()
    {
        var (first, second) = IniMarkReplay();
        await Assert.That(first.Count).IsGreaterThan(0);
        await Assert.That(second).IsEquivalentTo(first);
    }

    private static (List<string> First, List<string> Second) TomlMarkReplay()
    {
        var data = "A = 1\nB = \"x\"\n"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        var first = new List<string>();
        var second = new List<string>();
        var r = new TomlReader(seq);
        r.Read(); // prime
        r.Mark();
        while (r.Read() && first.Count < 2)
            first.Add(r.TokenType.ToString());
        r.RewindToMark();
        while (r.Read() && second.Count < 2)
            second.Add(r.TokenType.ToString());
        r.Dispose();
        return (first, second);
    }

    [Test]
    public async Task Toml_MarkAndRewind_ReplaysSameTokenStream()
    {
        var (first, second) = TomlMarkReplay();
        await Assert.That(first.Count).IsGreaterThan(0);
        await Assert.That(second).IsEquivalentTo(first);
    }

    private static long FailedReadConsumed<TReader>(ref TReader r)
        where TReader : ITransactionalTokenReader, allows ref struct
    {
        while (r.Read()) { }
        return r.BytesConsumed;
    }

    [Test]
    public async Task NeedsMoreData_DoesNotAdvanceReader()
    {
        // A non-final chunk that ends mid-line must leave the reader where the
        // failed read started (position + state unchanged).
        long consumed;
        {
            var r = new TomlReader("Key = "u8, isFinalBlock: false);
            r.Read();
            long before = r.BytesConsumed;
            r.Read(); // no value in the buffer: NeedsMoreData
            consumed = r.BytesConsumed - before;
            r.Dispose();
        }
        await Assert.That(consumed).IsEqualTo(0);
    }
}
