# 流式根因修复 Plan 1（WP5 + WP1 + WP3）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 JSON 流式（含顶层对象数组）、`DeserializeAsyncEnumerable` 数组模式、BOM 处理在任意分块边界下与同步路径结果一致，并修复生成器发现列表与长数字缓冲缺陷。

**Architecture:** 生成代码级"快照/回退"（`JsonReader.BytesConsumed` + 新增 `RewindTo`）+ 流式发射路径传播 `NeedMoreData`；数组模式改为"有界累积缓冲 + `JsonReader` span 原始切片"；BOM 在 reader 入口统一跳过。

**Tech Stack:** C# 13 / .NET 10、Roslyn 增量源生成器、TUnit (Microsoft Testing Platform)、PicoSerDe 自身栈（零反射、AOT-first）。

**Spec:** `docs/superpowers/specs/2026-09-17-streaming-root-cause-fix-design.md`

## Global Constraints

- 分支：`fix/streaming-root-cause`；每个 Task 一次提交（提交前对改动 `.cs` 跑 CSharpier）。
- 测试运行（本仓库可靠路径，勿用 `--nologo`）：
  `dotnet run --project <test-project> -p:PublishAot=false -c Debug`
- 每步 TDD：先写测试 → 运行确认**红**（失败原因正确）→ 最小实现 → 运行**绿** → 全量回归。
- 严格语义不变：解析错误一律 `FormatException`；流式与同步对同一输入必须同结果或同异常类型。
- 禁止静默丢字段；禁止使用 `System.Reflection`；生成代码必须 AOT/trim 友好、空性注解正确（Integration 项目开启 `WarningsAsErrors=CS8619;CS8620;CS8625`）。
- 新增测试文件命名与位置：
  - 跨格式：`tests/PicoSerDe.Integration.Tests/<Name>Tests.cs`
  - JSON：`PicoJetson/tests/PicoJetson.Integration.Tests/<Name>Tests.cs`
  - Core/reader 单元：`PicoJetson/tests/PicoJetson.Unit.Tests/<Name>Tests.cs`
- 现有 9 个测试工程共 1330 测试必须保持全绿。

---

## Task 1: BOM 统一处理（WP5.1）

**Files:**
- Create: `tests/PicoSerDe.Integration.Tests/BomTests.cs`
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs`（两个 span ctor + 两个 seq ctor + `Read()`）
- Modify: `PicoToml/src/PicoToml/TomlReader.cs`、`PicoYaml/src/PicoYaml/YamlReader.cs`、`PicoIni/src/PicoIni/IniReader.cs`（同类位置）

**Interfaces:**
- Produces: BOM 契约 —— `EF BB BF` 前缀在所有文本格式入口被跳过；仅 BOM 输入 → `FormatException`；seq 模式下 BOM 跨块安全。

- [ ] **Step 1: 写失败测试**

```csharp
// tests/PicoSerDe.Integration.Tests/BomTests.cs
namespace PicoSerDe.Integration.Tests;

public class BomDto
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class BomTests
{
    private static byte[] Bom(string tail) =>
        Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(tail)).ToArray();

    [Test]
    public void Json_BomPrefixed_ParsesAllFields()
    {
        var dto = JsonSerializer.Deserialize<BomDto>(Bom("""{"Name":"n","Age":3}"""));
        Assert.That(dto!.Name).IsEqualTo("n");
        Assert.That(dto.Age).IsEqualTo(3);
    }

    [Test]
    public void Toml_BomPrefixed_KeepsFirstKey()
    {
        var dto = TomlSerializer.Deserialize<BomDto>(Bom("Name = \"n\"\nAge = 3\n"));
        Assert.That(dto!.Name).IsEqualTo("n");
    }

    [Test]
    public void Yaml_BomPrefixed_KeepsFirstKey()
    {
        var dto = YamlSerializer.Deserialize<BomDto>(Bom("Name: n\nAge: 3\n"));
        Assert.That(dto!.Name).IsEqualTo("n");
    }

    [Test]
    public void Ini_BomPrefixed_KeepsFirstKey()
    {
        var dto = IniSerializer.Deserialize<BomDto>(Bom("Name=n\nAge=3\n"));
        Assert.That(dto!.Name).IsEqualTo("n");
    }

    [Test]
    public void Json_OnlyBom_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => JsonSerializer.Deserialize<BomDto>(Encoding.UTF8.GetPreamble()));
    }
}
```

- [ ] **Step 2: 运行确认红**

Run: `dotnet run --project tests/PicoSerDe.Integration.Tests/PicoSerDe.Integration.Tests.csproj -p:PublishAot=false -c Debug`
Expected: JSON 用例 `FormatException: Unexpected byte 0xEF`；TOML/YAML/INI 用例 `Name` 为空字符串。

- [ ] **Step 3: 最小实现**

JSON `JsonReader`（span ctor）：
```csharp
// 在 _data = data; 之前
if (
    data.Length >= 3
    && data[0] == 0xEF
    && data[1] == 0xBB
    && data[2] == 0xBF
)
    data = data[3..];
```
seq ctor + `Read()`：新增字段 `private bool _bomChecked;`
- 恢复构造器（带 `JsonReaderState`）中 `_bomChecked = true;`；
- `Read()` 开头（`SkipWhitespace()` 之前）：
```csharp
if (!_bomChecked)
{
    if (_isSequence && _seqReader.Remaining < 3 && !_isFinalBlock)
    {
        _needsMoreData = true;
        return false;
    }
    if (_seqReader.Remaining >= 3
        && _seqReader.Sequence.Slice(_seqReader.Position, 3).ToArray() is [0xEF, 0xBB, 0xBF])
        _seqReader.Advance(3);
    _bomChecked = true;
}
```
TOML/YAML/INI：span ctor 同样切片；seq 模式在其 `Read()` 入口做同样检查（各 reader 的 `ReadSeq` 前），恢复构造器置 `_bomChecked = true`。仅 BOM 输入在跳过 3 字节后 `IsAtEnd` → 已有"未预期结束"错误路径（JSON 为生成的 `Expected a JSON object ... EndOfInput`，需确认是 `FormatException`）。

- [ ] **Step 4: 运行确认绿 + 回归**

Run: 同 Step 2；然后 `dotnet run --project PicoJetson/tests/PicoJetson.Integration.Tests/PicoJetson.Integration.Tests.csproj -p:PublishAot=false -c Debug`
Expected: 新增 5 测试通过；既有测试不回退。

- [ ] **Step 5: 提交**

```bash
csharpier format PicoJetson/src/PicoJetson/JsonReader.cs PicoToml/src/PicoToml/TomlReader.cs PicoYaml/src/PicoYaml/YamlReader.cs PicoIni/src/PicoIni/IniReader.cs tests/PicoSerDe.Integration.Tests/BomTests.cs
git add -A && git commit -m "fix(readers): skip UTF-8 BOM consistently across JSON/TOML/YAML/INI"
```

---

## Task 2: 删除 `JsonOptions` 悬空注释（WP5.2）

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonOptions.cs`（文件尾部 3 行悬挂 doc comment）

- [ ] **Step 1: 删除注释块**（`/// <summary>Thread-local current options...` 到 `/// <remarks>Public so SG-generated...`）
- [ ] **Step 2: 构建确认无变化**：`dotnet build PicoJetson/src/PicoJetson/PicoJetson.csproj -v q` → 0 warning
- [ ] **Step 3: 提交** `docs: remove stale ThreadStatic options doc comment`

---

## Task 3: SG 发现列表补全（WP5.3）

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs:154`（`IsCandidate`）
- Create: `PicoJetson/tests/PicoJetson.Integration.Tests/DiscoveryTests.cs`

**Interfaces:**
- Produces：`JsonSerializer.SerializeLines<T>` / `DeserializeLines<T>` / `DeserializeFromStreamAsync<T>` / `DeserializeAsyncEnumerable<T>` 单独使用即可触发 SG 生成。

- [ ] **Step 1: 写失败测试**

```csharp
// PicoJetson/tests/PicoJetson.Integration.Tests/DiscoveryTests.cs
namespace PicoJetson.Tests;

public class DiscoveryLineOnly { public int V { get; set; } }
public class DiscoveryStreamOnly { public string S { get; set; } = ""; }
public class DiscoveryEnumOnly { public int N { get; set; } }

public class DiscoveryTests
{
    [Test]
    public void SerializeLines_OnlyUsage_Generates()
    {
        var bytes = JsonSerializer.SerializeLines(new[] { new DiscoveryLineOnly { V = 7 } });
        Assert.That(Encoding.UTF8.GetString(bytes)).Contains("\"V\":7");
    }

    [Test]
    public void DeserializeLines_OnlyUsage_Generates()
    {
        var items = JsonSerializer.DeserializeLines<DiscoveryEnumOnly>("{\"N\":1}\n{\"N\":2}\n"u8);
        Assert.That(items.Length).IsEqualTo(2);
    }

    [Test]
    public async Task DeserializeFromStreamAsync_OnlyUsage_Generates()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("{\"S\":\"x\"}"));
        var dto = await JsonSerializer.DeserializeFromStreamAsync<DiscoveryStreamOnly>(ms);
        Assert.That(dto!.S).IsEqualTo("x");
    }
}
```

- [ ] **Step 2: 运行确认红** —— 三个用例均 `InvalidOperationException: No serializer/deserializer registered for ...`
- [ ] **Step 3: 实现** —— `IsCandidate` 方法名集合扩为：
  `"Serialize" or "SerializeToUtf8Bytes" or "Deserialize" or "SerializeLines" or "DeserializeLines" or "DeserializeFromStreamAsync" or "DeserializeAsyncEnumerable"`
  （`Transform` 已经用 `IMethodSymbol.TypeArguments[0]`，无需改动；确认 `DeserializeAsyncEnumerable` 生成的类型同时也注册 streaming delegate）
- [ ] **Step 4: 运行确认绿 + 回归**
- [ ] **Step 5: 提交** `fix(gen): trigger generation for SerializeLines/DeserializeLines/streaming entry points`

---

## Task 4: BUG-11 长数字缓冲增长（WP5.4）

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs`（`ReadNumberSeq`，L1274-1368）
- Create: `PicoJetson/tests/PicoJetson.Integration.Tests/NumberBufferTests.cs`

**Interfaces:**
- Produces：seq 模式下任意长度数字 token 不再 `IndexOutOfRangeException`；超 `double`/`long` 范围时按既有语义（`FormatException` 或分类为可解析类型）。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoJetson.Tests;

public class NumberDto { public double Value { get; set; } }

public class NumberBufferTests
{
    [Test]
    public async Task LongNumber_SplitAcrossChunks_ParsesWithoutIndexOutOfRange()
    {
        JsonSerializer.Serialize(new NumberDto()); // 触发发现
        var payload = "{\"Value\":" + new string('9', 60) + "}";
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var dto = await JsonSerializer.DeserializeFromStreamAsync<NumberDto>(s);
        Assert.That(dto!.Value).IsGreaterThan(1e59);
    }
}
```

- [ ] **Step 2: 运行确认红** —— `IndexOutOfRangeException`
- [ ] **Step 3: 实现** —— `ReadNumberSeq` 中每次写入前检查并扩容：
```csharp
if (di == buf.Length)
{
    var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
    buf.AsSpan(0, di).CopyTo(bigger);
    TrackBuffer(bigger);          // 旧 buf 仍由 Dispose 归还，勿提前归还（BUG-10 教训）
    buf = bigger;
}
```
（对 4 处 `buf[di++]` 写入点统一处理；优先抽局部函数 `Append(byte)`。）
- [ ] **Step 4: 运行确认绿 + 回归**（含 `TotalPoolReturns == TotalTrackedBuffers` 锁定的既有测试）
- [ ] **Step 5: 提交** `fix(json): grow seq number buffer instead of overflowing (BUG-11)`

---

## Task 5: `JsonReader.RewindTo` API + 单元测试（WP1.1）

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs`
- Create: `PicoJetson/tests/PicoJetson.Unit.Tests/RewindToTests.cs`

**Interfaces:**
- Produces:
```csharp
public long BytesConsumed { get; }        // 已存在
public void RewindTo(long consumedOffset); // 新增
// span：_position = (int)offset；seq：_seqReader.Rewind(_seqReader.Consumed - offset)
```
- 约束：`0 <= offset <= BytesConsumed`；非法值抛 `ArgumentOutOfRangeException`。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoJetson.Tests;

public class RewindToTests
{
    [Test]
    public void RewindTo_ReReadsFromEarlierToken()
    {
        var r = new JsonReader("{\"A\":1}"u8);
        Assert.That(r.Read(), Is.True);                     // {
        long snap = r.BytesConsumed;                        // after '{'
        Assert.That(r.Read(), Is.True);                     // A
        Assert.That(r.TokenType).IsEqualTo(TokenType.PropertyName);
        r.RewindTo(snap);
        Assert.That(r.Read(), Is.True);                     // A again
        Assert.That(r.TokenType).IsEqualTo(TokenType.PropertyName);
        Assert.That(Encoding.UTF8.GetString(r.GetStringRaw())).IsEqualTo("A");
    }
}
```

- [ ] **Step 2: 运行确认红**（编译错误：无 `RewindTo`）
- [ ] **Step 3: 实现**（见 Interfaces）
- [ ] **Step 4: 运行确认绿**
- [ ] **Step 5: 提交** `feat(json): add JsonReader.RewindTo for streaming resume`

---

## Task 6: 流式发射路径传播 `NeedMoreData`（WP1.2）

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`
- Create: `PicoJetson/tests/PicoJetson.Integration.Tests/StreamingChunkMatrixTests.cs`

**Interfaces:**
- Produces：生成的流式委托遵守
```csharp
long __snap = reader.BytesConsumed;                 // 每个可重读单元入口（属性/元素）
...
if (reader.NeedsMoreData) { reader.RewindTo(__snap); return ReadStatus.NeedMoreData; }
```
- 覆盖发射点：`EmitStreamingDeserializer` 属性循环内的集合/字典/嵌套对象；`EmitPolyStreamingDeserializer`；`EmitNestedListDeserialize` 流式调用点。

- [ ] **Step 1: 写失败测试（矩阵骨架）**

```csharp
// PicoJetson/tests/PicoJetson.Integration.Tests/StreamingChunkMatrixTests.cs
namespace PicoJetson.Tests;

public class StreamRow { public int Id { get; set; } public string Name { get; set; } = ""; }
public class StreamDoc
{
    public string Scalar { get; set; } = "";
    public List<int> Numbers { get; set; } = new();
    public List<StreamRow> Rows { get; set; } = new();
    public Dictionary<string, int> Map { get; set; } = new();
    public StreamInner? Nested { get; set; }
    public List<List<int>> Grid { get; set; } = new();
    public string Tail { get; set; } = "";
}
public class StreamInner { public string Name { get; set; } = ""; public int N { get; set; } }

public sealed class ChunkedReadStream(byte[] data, int chunk) : Stream
{
    private int _pos;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int n) { int k = Math.Min(Math.Min(n, chunk), data.Length - _pos); Array.Copy(data, _pos, b, o, k); _pos += k; return k; }
    public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default)
    { await Task.Yield(); int k = Math.Min(Math.Min(b.Length, chunk), data.Length - _pos); data.AsSpan(_pos, k).CopyTo(b.Span); _pos += k; return k; }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
}

public class StreamingChunkMatrixTests
{
    private static StreamDoc MakeDoc() => new()
    {
        Scalar = "head",
        Numbers = { 1, 2, 3 },
        Rows = { new StreamRow { Id = 1, Name = "a" }, new StreamRow { Id = 2, Name = "bb" } },
        Map = { ["k1"] = 1, ["k2"] = 2 },
        Nested = new StreamInner { Name = "in", N = 7 },
        Grid = { new List<int> { 1, 2 }, new List<int> { 3 } },
        Tail = "END",
    };

    private static void AssertSame(StreamDoc expected, StreamDoc actual)
    {
        Assert.That(actual.Scalar).IsEqualTo(expected.Scalar);
        Assert.That(actual.Numbers).IsEquivalentTo(expected.Numbers);
        Assert.That(actual.Rows.Select(r => r.Id + ":" + r.Name)).IsEquivalentTo(expected.Rows.Select(r => r.Id + ":" + r.Name));
        Assert.That(actual.Map).IsEquivalentTo(expected.Map);
        Assert.That(actual.Nested?.Name).IsEqualTo(expected.Nested?.Name);
        Assert.That(actual.Nested?.N).IsEqualTo(expected.Nested?.N);
        Assert.That(actual.Grid.Select(g => string.Join(",", g))).IsEquivalentTo(expected.Grid.Select(g => string.Join(",", g)));
        Assert.That(actual.Tail).IsEqualTo(expected.Tail);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(5)] [Arguments(8)] [Arguments(16)] [Arguments(4096)]
    public async Task Json_Stream_MatchesSync_ForChunkSize(int chunk)
    {
        var doc = MakeDoc();
        var json = JsonSerializer.Serialize(doc);
        using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(json), chunk);
        var streamed = await JsonSerializer.DeserializeFromStreamAsync<StreamDoc>(s);
        AssertSame(doc, streamed!);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Json_Stream_RealFileStream_2000Rows()
    {
        var doc = new StreamDoc { Tail = "END" };
        for (int i = 0; i < 2000; i++) doc.Rows.Add(new StreamRow { Id = i, Name = "n" + i });
        var json = JsonSerializer.Serialize(doc);
        var path = Path.Combine(Path.GetTempPath(), "picoserde-plan1.json");
        await File.WriteAllTextAsync(path, json);
        try
        {
            using var fs = File.OpenRead(path);
            var back = await JsonSerializer.DeserializeFromStreamAsync<StreamDoc>(fs);
            Assert.That(back!.Rows.Count).IsEqualTo(2000);
            Assert.That(back.Tail).IsEqualTo("END");
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 2: 运行确认红** —— 多数 chunk 下 `Rows/Numbers/Nested/Tail` 缺失或异常
- [ ] **Step 3: 实现（生成器）**
  1. 在流式属性循环（`EmitStreamingDeserializer`）每轮取 `__snap`；
  2. 集合/字典/嵌套对象/嵌套列表的读取块结束后、返回 `Success` 前插入：
     `if (reader.NeedsMoreData) { reader.RewindTo(__snap); return ReadStatus.NeedMoreData; }`
  3. 泛型抽成生成器私有助手 `AppendNeedMoreDataRewind(StringBuilder sb, string indent, string snapVar)`，避免每个发射点手写；
  4. 只改流式发射路径；span 路径（`EmitDeserializer`）禁止改动。
- [ ] **Step 4: 运行确认绿 + 全量回归**（JSON Unit/Integration/Functional + 跨格式）
- [ ] **Step 5: 提交** `fix(json-gen): propagate NeedMoreData with snapshot/rewind in streaming delegates`

---

## Task 7: 顶层对象数组流式（WP1.3）

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`（顶层 `T[]` 流式发射）
- Modify/Create 同 Task 6 测试文件追加用例

**Interfaces:**
- Produces：`DeserializeFromStreamAsync<StreamRow[]>` 在任意分块下返回完整数组；无死循环；失败元素回退到元素起点，已解析元素保存在 `partial`/`StreamState`。

- [ ] **Step 1: 写失败测试（追加到 Task 6 文件）**

```csharp
[Test]
[Timeout(30_000)]
[Arguments(1)] [Arguments(3)] [Arguments(8)] [Arguments(4096)]
public async Task Json_Stream_ObjectArray_MatchesSync(int chunk)
{
    var rows = new[] { new StreamRow { Id = 1, Name = "a" }, new StreamRow { Id = 2, Name = "bb" }, new StreamRow { Id = 3, Name = "ccc" } };
    var json = JsonSerializer.Serialize(rows);
    using var s = new ChunkedReadStream(Encoding.UTF8.GetBytes(json), chunk);
    var back = await JsonSerializer.DeserializeFromStreamAsync<StreamRow[]>(s);
    Assert.That(back!.Select(r => r.Id + ":" + r.Name)).IsEquivalentTo(rows.Select(r => r.Id + ":" + r.Name));
}
```

- [ ] **Step 2: 运行确认红** —— chunk<4096 时**挂死**（`[Timeout]` 判定失败）或丢元素
- [ ] **Step 3: 实现** —— 顶层数组流式委托：元素级快照 + 失败回退 + 部分结果保留
  （列表存于 `partial` 或 `reader.StreamState`，与手写 `IntArrayFunc` 同构；`ReadStatus.Success` 仅在读到 `ArrayEnd` 时返回）
- [ ] **Step 4: 运行确认绿 + 回归**
- [ ] **Step 5: 提交** `fix(json-gen): correct top-level object array streaming resume`

---

## Task 8: `DeserializeAsyncEnumerable` 数组模式重写（WP3）

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonSerializer.cs`（删除 `DeserializeAsyncEnumerableImpl` 数组模式手写解析，替换为有界缓冲 + `JsonReader` 切片）
- Create: `PicoJetson/tests/PicoJetson.Integration.Tests/ArrayModeStreamingTests.cs`

**Interfaces:**
- Produces：`DeserializeAsyncEnumerable<T>(stream, topLevelValues:false, options, ct)` 在任意分块下产出与 `Deserialize<T[]>` 相同的元素序列；缓冲只保留未消费字节（消费后压缩）。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoJetson.Tests;

public class ArrayModeTests
{
    public class Item { public int V { get; set; } public string S { get; set; } = ""; }

    [Test]
    [Timeout(30_000)]
    [Arguments(1)] [Arguments(3)] [Arguments(8)] [Arguments(4096)]
    public async Task ArrayMode_Chunked_MatchesSync(int chunk)
    {
        JsonSerializer.Serialize(new Item()); // 触发元素类型发现
        var payload = "[{\"V\":1,\"S\":\"x] } y\"},{\"V\":2,\"S\":\"{\\\"k\\\":[1,2]}\"},{\"V\":3,\"S\":\"z\"}]";
        var bytes = Encoding.UTF8.GetBytes(payload);
        using var s = new ChunkedReadStream(bytes, chunk);
        var got = new List<Item>();
        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<Item>(s, topLevelValues: false))
            got.Add(item!);
        Assert.That(got.Select(i => i.V + ":" + i.S)).IsEquivalentTo(new[] { "1:x] } y", "2:{\"k\":[1,2]}", "3:z" });
    }
}
```

- [ ] **Step 2: 运行确认红** —— 所有 chunk 均 `FormatException: Unexpected end of stream inside JSON array`
- [ ] **Step 3: 实现**
  - `byte[] buffer` + `head/count`；`ReadAsync` 追加；`done` 标志；
  - 每轮：`new JsonReader(buffer.AsSpan(head, count - head), maxDepth, isFinalBlock: done, options)`（ref struct 只在 await 之间、yield 之前存活）；
  - 首个 token 断言 `ArrayStart`；元素：`start = reader.RawPos` → `Read()` → `ArrayEnd` 结束 → `TrySkip()`（false = 未完成）→ `end = reader.RawPos` → 切片复制为 `byte[]`（跨 `yield` 不能持 span）→ 收集 `List<T?>`；
  - reader 作用域结束后逐个 `yield return`；消费后 `head += consumed`，`head > 32KB` 时压缩到 0；
  - `done && 未完成` → `FormatException`。
- [ ] **Step 4: 运行确认绿 + 回归**
- [ ] **Step 5: 提交** `fix(json): rewrite array-mode DeserializeAsyncEnumerable on JsonReader`

---

## Self-Review

- **Spec coverage**：WP5.1→Task1、WP5.2→Task2、WP5.3→Task3、WP5.4→Task4、WP1.1→Task5、WP1.2→Task6、WP1.3→Task7、WP3→Task8。WP2/WP4 在 Plan 2。
- **占位符扫描**：无 TBD；每个 Task 含测试代码与实现要点。
- **类型一致性**：`RewindTo(long)` / `BytesConsumed` / `ReadStatus.NeedMoreData` / `ChunkedReadStream` 全文一致；`__snap` 约定统一。
