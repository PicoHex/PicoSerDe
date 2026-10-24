# PicoSerDe P0 审核修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 2026-10-23 审核报告中的 31 条 Critical，使五种格式不再静默损坏数据、不再生成不可编译代码，并让生成器对自己写出的 payload 可往返。

**Architecture:** 根因优先、共享层优先。`shared/Gen` 的修复（继承合并、匿名类型可空布局、码点校验、可空枚举命名）自动覆盖全部 5 个生成器；单格式修复落在各自的 `*SerializerGenerator.cs` / reader / writer。所有修复走 TDD：先让新增回归测试红（构建红或断言红），再最小实现转绿。

**Tech Stack:** C# 13 / net10.0、Roslyn incremental source generators（netstandard2.0）、TUnit on Microsoft.Testing.Platform、CSharpier 1.3.0。

**Spec:** `docs/review-report-2026-10-23.md`（审核报告与 P0 清单）。

## Global Constraints

- 分支：执行前用 `using-git-worktrees` 建隔离工作区，分支 `fix/review-p0-2026-10`；不得直接改 `main`。
- 每个任务先写失败测试并确认红，再实现；一次提交一个任务。
- 禁止反射：生成代码与运行时都不得引入 `Activator`、`MakeGenericType`、非泛型 `Enum.Parse(Type, ...)`、`dynamic`；泛型 `Enum.Parse<T>` 允许。AOT/trim 兼容是硬约束。
- 生成代码必须 0 warning；`CS8619/CS8620/CS8625` 已设为 error。
- 每个任务提交前跑所属测试项目；收尾跑 `PicoSerDe.slnx` 全量。
- 提交前对改动的 `.cs` 跑 `dotnet csharpier format <files>`。
- 测试命令（不要加 `--nologo`，会触发 “Zero tests ran”）：
  - `dotnet test <proj> -c Debug --no-progress`
  - 定向：`dotnet test <proj> -c Debug --no-progress -- --treenode-filter "/*/*/<Class>/*"`
- 行为契约变化（null 语义、异常类型）必须写进 README 的严格性矩阵（Task 21）。
- 公开 API 变化后重新 seed `api/<PackageId>.public.txt`（`scripts/api-surface dump <dll> --out <baseline>`）。

## Review Focus

审核确认“用户最可能踩到、现有测试抓不到”的 5 类输入，每个都由指定任务钉死：

1. `new { A = (int?)null }` / `new { A = (int?)5 }` —— 匿名类型可空值成员（Task 2）。
2. MsgPack payload 中 `decimal?/DateTime?/Guid?/TimeSpan?/DateOnly?/TimeOnly?` 的 `nil` —— 库自己写出的字节（Task 10）。
3. TOML 中嵌套表出现在标量之前（`[Address]` 后跟 `Name = "n"`）—— 往返丢字段（Task 13）。
4. `record IniRec(string Name, long Age)` 或 `[IniConstructor]` 含 long/bool/Guid/List —— 编译失败（Task 14）。
5. YAML 空对象列表后紧跟兄弟键（`Items:` + `Count: 7`）—— 兄弟键丢失（Task 17）。

---

## Phase A — 共享生成器（一次修，五格式继承）

### Task 1: 非多态继承合并基类属性（C-01）

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs`（`ExtractProperties`，约 930-1360 行）
- Test: `tests/PicoSerDe.Integration.Tests/InheritanceTests.cs`（新建）

**Interfaces:**
- Produces: `ExtractProperties` 返回基类链（最基 → 最派生）与声明成员的合并序列，按 `Name` 去重；派生类同名成员覆盖基类值、位置保留基类声明处（稳定，不随派生成员增减漂移）。
- **与多态合并的重叠**：`GenInfrastructure.cs:1977-2008` 现在靠 `Insert(0)` 把基类属性插到前面并重编号 `IntKey`。Task 1 落地后基类属性已包含在结果里，该 `Insert(0)` 会变成 no-op，最终顺序改为"合并顺序"。必须在该处显式退化为"只取一层基类"或删除该循环，并保留"最基在前"的最终顺序；否则 `PolyInheritanceTests` 的 MsgPack `IntKey` 断言会漂移。
- 回归范围包含 `PicoJetson.Functional.Tests`（`FunctionalTypeMatrix` 的 poly 用例），不能只跑 Integration.Tests。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoSerDe.Integration.Tests;

public class BaseDto { public int A { get; set; } = 1; }
public class MiddleDto : BaseDto { public int B { get; set; } = 2; }
public class DerivedDto : MiddleDto { public int C { get; set; } = 3; }

public class InheritanceTests
{
    [Test]
    public async Task Json_DerivedType_IncludesBaseMembers()
    {
        var json = JsonSerializer.Serialize(new DerivedDto());
        await Assert.That(json).Contains("\"A\":1");
        await Assert.That(json).Contains("\"B\":2");
        await Assert.That(json).Contains("\"C\":3");
    }

    [Test]
    public async Task Json_DerivedType_RoundTripsBaseMembers()
    {
        var back = JsonSerializer.Deserialize<DerivedDto>(
            Encoding.UTF8.GetBytes("{\"A\":10,\"B\":20,\"C\":30}"));
        await Assert.That(back.A).IsEqualTo(10);
        await Assert.That(back.B).IsEqualTo(20);
        await Assert.That(back.C).IsEqualTo(30);
    }

    [Test]
    public async Task Toml_Yaml_Ini_MsgPack_IncludeBaseMembers()
    {
        await Assert.That(TomlSerializer.Serialize(new DerivedDto())).Contains("A = 1");
        await Assert.That(YamlSerializer.Serialize(new DerivedDto())).Contains("A: 1");
        await Assert.That(IniSerializer.Serialize(new DerivedDto())).Contains("A = 1");
        await Assert.That(MsgPackSerializer.Deserialize<DerivedDto>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DerivedDto())).A).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress -- --treenode-filter "/*/*/InheritanceTests/*"`
Expected: FAIL —— JSON 只有 `C`。

- [ ] **Step 3: 实现**

把成员提取体抽成 `ExtractDeclaredProperties(INamedTypeSymbol, ...)`；`ExtractProperties` 先沿 `type.BaseType` 收集链（最基在前），逐级提取并按 `Name` 去重合并（派生覆盖基类值，位置保留基类声明处）；`autoKey` 在合并完成后统一分配。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（3/3）。

- [ ] **Step 5: 回归与提交**

```bash
dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress
dotnet csharpier format shared/Gen/GenInfrastructure.cs tests/PicoSerDe.Integration.Tests/InheritanceTests.cs
git add shared/Gen/GenInfrastructure.cs tests/PicoSerDe.Integration.Tests/InheritanceTests.cs
git commit -m "fix(gen): merge inherited base-type members for all formats"
```

---

### Task 2: 匿名类型可空值成员与 null 集合键（C-26, C-02）

**Files:**
- Modify: `shared/Gen/AnonTypeHandler.cs`（`AnonFieldInfo`、`TypeLayout`、`ComputeFieldLayout`、`BuildAnonTypeInfo`、`GenerateReaderClass`、`GenerateInterceptorClass`）
- Modify: 5 个格式的匿名值发射器（`emitField` 回调实现处）：`JsonSerializerGenerator.EmitJsonValue`（≈6303-6324）、`TomlSerializerGenerator`（≈3220）、`YamlSerializerGenerator`、`IniSerializerGenerator`、`MsgPackSerializerGenerator` 各自的 `Emit*Value`
- Test: `tests/PicoSerDe.Integration.Tests/AnonNullableTests.cs`（新建）

**Interfaces:**
- `AnonFieldInfo` 末尾新增 `bool IsNullable = false`（保持现有按名传参调用点不变）。
- `TypeLayout` 新增 `nullable_<kind>` 条目：`int32 (8,4)`、`int64 (16,8)`、`int16 (4,2)`、`uint16 (4,2)`、`uint32 (8,4)`、`uint64 (16,8)`、`sbyte (2,1)`、`byte (2,1)`、`char (4,2)`、`float32 (8,4)`、`float64 (16,8)`、`boolean (2,1)`、`decimal (24,8)`、`datetime (16,8)`、`dateonly (8,4)`、`timeonly (16,8)`、`timespan (16,8)`、`guid (20,4)`（`Nullable<Guid>` = 16B 值 + 1B hasValue，align 4）；`enum` 不能一律按 `nullable_int32`——底层为 `long/ulong` 时应为 `(16,8)`，按 `INamedTypeSymbol.EnumUnderlyingType` 选 `nullable_int32`/`nullable_int64`。
- `GenerateReaderClass` 对 `nullable_*` 生成 `internal static T? Read_nullable_x(object obj, int offset)`：先 `bool hasValue = Unsafe.Read<bool>(data)`，再 `return hasValue ? Unsafe.Read<T>(data + align) : null;`。
- `GenerateInterceptorClass` 的标量成员（`nullable_*`）：`HasNullLiteral` 格式生成 `if (__v.HasValue) { key; write(__v.Value); } else { key; WriteNull(); }`；无 null 字面量格式生成 `if (__v.HasValue) { key; write(__v.Value); }`（整个成员省略）。
- `GenerateInterceptorClass` 的 list/array/dict 分支：`HasNullLiteral` 格式把 key 提到 `if (__col != null)` 之外并在 else 写 `WriteNull()`；无 null 字面量格式 key 保持在非 null 分支内（null 时整个成员省略）。
- **5 个 `Emit*Value` 必须能处理 `nullable_*`**：`JsonSerializerGenerator.EmitJsonValue` 当前 `_ =>` 会 `WriteString(...)`，`int?=5` 会写成 `"5"`；其它 4 个生成器的同构分支会把 `int?` 直接传给 `WriteKeyValue(..., int)` 造成 CS1503。最小改法：拦截器里先解包（`var __vv = __v.Value;`）再把 `__vv` 交给 `emitField`；或在每处 `Emit*Value` 顶部加 `nullable_*` 分支。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoSerDe.Integration.Tests;

public class AnonNullableTests
{
    [Test]
    public async Task Json_AnonNullableInt_NullIsNull()
        => await Assert.That(JsonSerializer.Serialize(new { A = (int?)null })).IsEqualTo("{\"A\":null}");

    [Test]
    public async Task Json_AnonNullableInt_ValuePreserved()
        => await Assert.That(JsonSerializer.Serialize(new { A = (int?)5 })).IsEqualTo("{\"A\":5}");

    [Test]
    public async Task Json_AnonNullableDecimal_ValuePreserved()
        => await Assert.That(JsonSerializer.Serialize(new { A = (decimal?)1.5m })).IsEqualTo("{\"A\":1.5}");

    [Test]
    public async Task Json_AnonNullableAllKinds_AreNull()
    {
        // 匿名类型会重排字段，逐成员断言。
        var json = JsonSerializer.Serialize(new
        {
            I = (int?)null, L = (long?)null, S = (short?)null, B = (bool?)null,
            D = (double?)null, M = (decimal?)null, T = (DateTime?)null,
            G = (Guid?)null, TS = (TimeSpan?)null, DO = (DateOnly?)null, TO = (TimeOnly?)null,
        });
        foreach (var n in new[] { "I", "L", "S", "B", "D", "M", "T", "G", "TS", "DO", "TO" })
            await Assert.That(json).Contains($"\"{n}\":null");
    }

    [Test]
    public async Task AnonNullableList_NullDoesNotCorruptDocument()
    {
        await Assert.That(JsonSerializer.Serialize(new { A = (List<int>?)null })).IsEqualTo("{\"A\":null}");
        await Assert.That(TomlSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();
        await Assert.That(YamlSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();
        await Assert.That(IniSerializer.Serialize(new { A = (List<int>?)null }).Trim()).IsEmpty();

        // 非 null 的反向：修复不能把正常值也吞掉。
        await Assert.That(JsonSerializer.Serialize(new { A = (int?)5 })).IsEqualTo("{\"A\":5}");
        await Assert.That(TomlSerializer.Serialize(new { A = (int?)5 })).Contains("A = 5");
        await Assert.That(YamlSerializer.Serialize(new { A = (int?)5 })).Contains("A: 5");
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress -- --treenode-filter "/*/*/AnonNullableTests/*"`
Expected: FAIL —— `{"A":0}`、`{"A":1}`、`{"A":65536}`、`{null}`。

- [ ] **Step 3: 实现**

1. `BuildAnonTypeInfo`：`var (tk, isNullable, _) = TypeKindResolver.Resolve(...)`；`isNullable && !p.Type.IsReferenceType` 时 `tk = "nullable_" + tk`。
2. `ComputeFieldLayout` 使用新表；`ElementTypeKind` 同步。
3. `GenerateReaderClass` 生成 nullable 读法；`KindToCSharp("nullable_int32") => "int?"`。
4. `GenerateInterceptorClass`：key 前移；`else` 按 `HasNullLiteral` 发射。
5. `GenerateNestedHelper` 同步可空嵌套对象。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（5/5）。

- [ ] **Step 5: 回归与提交**

```bash
dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress
dotnet csharpier format shared/Gen/AnonTypeHandler.cs tests/PicoSerDe.Integration.Tests/AnonNullableTests.cs
git add shared/Gen/AnonTypeHandler.cs tests/PicoSerDe.Integration.Tests/AnonNullableTests.cs
git commit -m "fix(gen): support nullable value members in anonymous types"
```

---

### Task 3: 码点校验，杜绝非法 UTF-8（C-25）

**Files:**
- Modify: `shared/TextHelpers.cs`（`AppendCodepoint`，约 146-169 行）
- Test: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/TextHelpersCodepointTests.cs`（新建）

**Interfaces:**
- `AppendCodepoint(byte[] buf, int di, int cp)`：`cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)` 时抛 `FormatException($"Invalid Unicode code point U+{cp:X}")`。
- **合法代理对不能被拒绝**：TOML/YAML 允许 `\uD83D\uDE00` 这种"两个 `\uXXXX` 拼一个码点"的写法。调用方（`TomlReader.cs:1344/1351`、`YamlReader.cs:1381/1388/1395`）在把 `\u` 结果交给 `AppendCodepoint` 之前，若读到高位代理（D800–DBFF），必须与紧随其后的低位代理转义合并为 `0x10000 + ((hi-0xD800)<<10) + (lo-0xDC00)`；只有**孤立**代理才抛 `FormatException`。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoSerDe.Core.Tests;

public class TextHelpersCodepointTests
{
    [Test]
    public void RejectsNegative() =>
        Assert.Throws<FormatException>(() => TextHelpers.AppendCodepoint(new byte[8], 0, -1));

    [Test]
    public void RejectsBeyondMax() =>
        Assert.Throws<FormatException>(() => TextHelpers.AppendCodepoint(new byte[8], 0, 0x110000));

    [Test]
    public void RejectsSurrogates() =>
        Assert.Throws<FormatException>(() => TextHelpers.AppendCodepoint(new byte[8], 0, 0xD800));

    [Test]
    public async Task EncodesValid()
    {
        var buf = new byte[8];
        var next = TextHelpers.AppendCodepoint(buf, 0, 0x4E2D);
        await Assert.That(next).IsEqualTo(3);
        await Assert.That(Encoding.UTF8.GetString(buf, 0, 3)).IsEqualTo("中");

        var buf4 = new byte[8];
        var next4 = TextHelpers.AppendCodepoint(buf4, 0, 0x1F600);
        await Assert.That(next4).IsEqualTo(4);
        await Assert.That(Encoding.UTF8.GetString(buf4, 0, 4)).IsEqualTo("\U0001F600");
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests -c Debug --no-progress -- --treenode-filter "/*/*/TextHelpersCodepointTests/*"`
Expected: FAIL —— 未抛异常。

- [ ] **Step 3: 实现**

在 `AppendCodepoint` 首行加范围/代理区守卫并抛 `FormatException`。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（4/4）。

- [ ] **Step 5: 回归与提交**

```bash
dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests -c Debug --no-progress
dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress
dotnet csharpier format shared/TextHelpers.cs PicoSerDe.Core/tests/PicoSerDe.Core.Tests/TextHelpersCodepointTests.cs
git add shared/TextHelpers.cs PicoSerDe.Core/tests/PicoSerDe.Core.Tests/TextHelpersCodepointTests.cs
git commit -m "fix(core): reject invalid Unicode code points in AppendCodepoint"
```

---

## Phase B — JSON

### Task 4: 多态序列化 default 分支与流式 ctor 状态（C-03, C-06）

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`（poly serializer switch ≈4947-4995；poly streaming ≈5280-5375）
- Test: `PicoJetson/tests/PicoJetson.Integration.Tests/PolyContractTests.cs`（新建）

**Interfaces:**
- poly `switch (value)` 末尾新增 default 抛 `FormatException`，消息用生成期已知的基类名拼好（不调用 `GetType()`）。
- **poly 分支不会发射普通 `{Type}StreamingState`**（它只由 `EmitStreamingDeserializer` 在 `JsonSerializerGenerator.cs:4789` 发射）。因此 poly streaming 必须自带独立状态类 `file sealed class {Type}PolyStreamingState`，字段含 `__cp_*`、`__seen_*` 与 `string? Discriminator;`，避免 CS0101。
- `__cp_{i}_{ci}` 声明为 `ref var __cp_... = ref __st.__cp_...;`；`:5197`/`:5242` 的 `reader.StreamState as string` 读写全部替换为 `__st.Discriminator`。
- `RewindTo(__snap)` 回退到 discriminator 之前时，`__st.Discriminator` 已持久化，回退分支不得清空它。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoJetson.Tests;

[PicoDerivedType(typeof(DeclaredLeaf), "leaf")]
public abstract class PolyBase { public int Id { get; set; } }
public class DeclaredLeaf : PolyBase { public string Name { get; set; } = ""; }
public class UndeclaredLeaf : PolyBase { public string Other { get; set; } = ""; }

[PicoDerivedType(typeof(PolyCtorLeaf), "leaf")]
public abstract class PolyNode { }

public class PolyCtorLeaf : PolyNode
{
    [PicoConstructor]
    public PolyCtorLeaf(string label, int count) { Label = label; Count = count; }
    public string Label { get; }
    public int Count { get; }
}

public class PolyContractTests
{
    [Test]
    public async Task UndeclaredDerivedType_Throws()
    {
        await Assert.ThrowsAsync<FormatException>(async () =>
            await Task.Run(() => JsonSerializer.Serialize<PolyBase>(new UndeclaredLeaf())));
    }

    [Test]
    public async Task DeclaredDerivedType_StillWorks()
        => await Assert.That(JsonSerializer.Serialize<PolyBase>(new DeclaredLeaf { Id = 1, Name = "x" }))
            .Contains("\"Name\":\"x\"");

    [Test]
    public async Task PolyStreaming_CtorParamsSurviveChunkBoundary()
    {
        var json = JsonSerializer.Serialize<PolyNode>(new PolyCtorLeaf("label", 7));
        using var stream = new OneByteChunkStream(Encoding.UTF8.GetBytes(json));
        var back = await JsonSerializer.DeserializeFromStreamAsync<PolyNode>(stream);
        await Assert.That(back).IsTypeOf<PolyCtorLeaf>();
        await Assert.That(((PolyCtorLeaf)back).Label).IsEqualTo("label");
        await Assert.That(((PolyCtorLeaf)back).Count).IsEqualTo(7);
    }
}
```

`OneByteChunkStream` 若测试辅助中不存在，在本文件内新增：`ReadAsync` 每次返回 1 字节。

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoJetson/tests/PicoJetson.Integration.Tests -c Debug --no-progress -- --treenode-filter "/*/*/PolyContractTests/*"`
Expected: `UndeclaredDerivedType_Throws` FAIL（输出 `{"$type":}`）；ctor 测试 FAIL。

- [ ] **Step 3: 实现**

1. poly serializer switch 结束前插入 `default: throw new FormatException(...)`。
2. poly streaming 开头 `var __st = ({Type}StreamingState)(reader.StreamState ??= new {Type}StreamingState());`，ctor 临时量改为 `ref var __cp_{i}_{ci} = ref __st.__cp_{i}_{ci};`。
3. `StreamingState` 增加 `public string? Discriminator;`，替换 `string? __disc = reader.StreamState as string;` 与其写回。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（3/3）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoJetson/tests/PicoJetson.Integration.Tests -c Debug --no-progress
dotnet csharpier format PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs PicoJetson/tests/PicoJetson.Integration.Tests/PolyContractTests.cs
git add PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs PicoJetson/tests/PicoJetson.Integration.Tests/PolyContractTests.cs
git commit -m "fix(picojetson): reject undeclared poly types; persist poly ctor state across chunks"
```

---

### Task 5: 可空枚举读取（C-04，5 格式）

**Files:**
- Modify: `shared/Gen/GenInfrastructure.cs`（新增 `public static string MapEnumReadType(string typeFullName) => typeFullName.TrimEnd('?');`）
- Modify: **5 个**生成器的全部枚举解析点：
  - `JsonSerializerGenerator.cs`：`1180`、`1358`、`2821`、`3402`、`4006`、`4224`、`4502`、`6199`
  - `IniSerializerGenerator.cs`：`1774`、`2300`、`2374`
  - `TomlSerializerGenerator.cs`：`2089`、`2560`
  - `MsgPackSerializerGenerator.cs`：`2543`
  - `YamlSerializerGenerator.cs`：`1288`、`2991`（`Enum.Parse<`，需改为 `TryParse` + `FormatException`，否则同样 CS0453）
- Test: `tests/PicoSerDe.Integration.Tests/NullableEnumTests.cs`（新建）

**Interfaces:**
- 所有 `Enum.TryParse<T>`/`Enum.Parse<T>` 使用去 `?` 的类型名；可空枚举先读入非空局部量再赋给字段（`Level` → `Level?` 有隐式提升，无需额外分支）。
- YAML 两处 `Enum.Parse<T>` 改为 `TryParse` + 失败抛 `FormatException`。
- **MsgPack 整数枚举分支**（`MsgPackSerializerGenerator.cs:2563-2566`）的 `(T)(object)__enumInt` 对 `Level?` 会运行时 `InvalidCastException`；改为 `= (Level)__enumInt;`。

- [ ] **Step 1: 写失败测试（构建红）**

```csharp
namespace PicoSerDe.Integration.Tests;

public enum Level { Low = 1, High = 2 }
public class NullableEnumHolder { public Level? Level { get; set; } }

public class NullableEnumTests
{
    [Test]
    public async Task Json_RoundTrips()
    {
        var json = JsonSerializer.Serialize(new NullableEnumHolder { Level = Level.High });
        await Assert.That(JsonSerializer.Deserialize<NullableEnumHolder>(Encoding.UTF8.GetBytes(json)).Level)
            .IsEqualTo(Level.High);
    }

    [Test]
    public async Task OtherFormats_RoundTrip()
    {
        var v = new NullableEnumHolder { Level = Level.High };
        await Assert.That(TomlSerializer.Deserialize<NullableEnumHolder>(
            Encoding.UTF8.GetBytes(TomlSerializer.Serialize(v))).Level).IsEqualTo(Level.High);
        await Assert.That(YamlSerializer.Deserialize<NullableEnumHolder>(
            Encoding.UTF8.GetBytes(YamlSerializer.Serialize(v))).Level).IsEqualTo(Level.High);
        await Assert.That(IniSerializer.Deserialize<NullableEnumHolder>(
            Encoding.UTF8.GetBytes(IniSerializer.Serialize(v))).Level).IsEqualTo(Level.High);
        await Assert.That(MsgPackSerializer.Deserialize<NullableEnumHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(v)).Level).IsEqualTo(Level.High);
    }
}
```

- [ ] **Step 2: 确认红灯（构建失败）**

Run: `dotnet build tests/PicoSerDe.Integration.Tests -c Debug`
Expected: FAIL —— `CS0453: 'Level?' must be a non-nullable value type`。

- [ ] **Step 3: 实现**

`GenInfrastructure.MapEnumReadType`；4 个生成器的枚举读取改用该辅助。

- [ ] **Step 4: 确认绿灯**

Run: `dotnet build tests/PicoSerDe.Integration.Tests -c Debug`，然后 Step 1 的测试命令。
Expected: PASS（2/2）。

- [ ] **Step 5: 提交**

```bash
dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress
dotnet csharpier format shared/Gen/GenInfrastructure.cs PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs tests/PicoSerDe.Integration.Tests/NullableEnumTests.cs
git add -u shared PicoJetson/src PicoIni/src PicoToml/src PicoMsgPack/src tests/PicoSerDe.Integration.Tests/NullableEnumTests.cs
git commit -m "fix(gen): emit non-nullable enum type arguments for Enum.TryParse"
```

---

### Task 6: 流式池化缓冲所有权（C-05）

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs`（`_rb0.._rb7`/`TrackBuffer`/`ReturnBuffer` ≈1660-1735）
- Modify: `PicoJetson/tests/PicoJetson.Unit.Tests/JsonReaderTests.cs:627-641`（**必须**同步：现有 `PeakTrackedBufferCount > 8` 与新模型冲突）
- Test: `PicoJetson/tests/PicoJetson.Unit.Tests/ReaderBufferOwnershipTests.cs`（新建）

**Interfaces:**
- 新增 `private byte[]? _spanBuffer;` 与 `private byte[]? _lastBuffer;`。
- **4 处**（不是 3 处）`_valueSpan` 指向池化缓冲的赋值都要同步 `_spanBuffer = buf;`：`JsonReader.cs:1104`（seq 字符串）、`:1328`（反转义 decoded）、`:1487`（seq 数字）、`:1555`（seq literal）。
- **时序**：`UnescapeIfNeeded` 先 `TrackBuffer(decoded)`（`:1281`）再赋 `_valueSpan`（`:1328`）——必须**先**把源 `buf` 记为 `_spanBuffer`，**再** `TrackBuffer(decoded)`；否则 `ReleaseLastBuffer(keep: decoded)` 会归还仍被 span 使用的源缓冲。
- 新增 `private void ReleaseLastBuffer(byte[]? keep)`：当 `_lastBuffer` 非空且 `!ReferenceEquals(_lastBuffer, keep)` 且 `!ReferenceEquals(_lastBuffer, _spanBuffer)` 时归还并置空。
- `TrackBuffer(byte[] buf)`：先 `ReleaseLastBuffer(buf)`，再 `_buffers.Add(buf); _lastBuffer = buf; _rentedBuffer = buf;`，更新峰值。
- `ReturnBuffer()`：遍历 `_buffers` 归还、清空列表、`_spanBuffer = null; _lastBuffer = null;`。
- 稳态缓冲数 ≤ 2（`_spanBuffer` + 当前 token）。
- **`PeakTrackedBufferCount` 语义变更**：从"8 槽轮转的槽位占用峰值"改为"同一时刻存活（已租未还）的缓冲数峰值"。因此 `JsonReaderTests.cs:637-641` 的 `IsGreaterThan(8)` 必须改为 `IsLessThanOrEqualTo(2)`；`TrackedBufferCount`（Dispose 后为 0）、`LeakedBufferCount`、`TotalPoolReturns`/`TotalTrackedBuffers` 语义不变。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoJetson.Tests;

public class ReaderBufferOwnershipTests
{
    [Test]
    public async Task PeakLiveBuffers_StaysBounded()
    {
        // 12 个字符串属性 = 24 次 Rent；8 槽轮转模型下 _bufCount 单调增长，
        // 修复后（只保留 span 源 + 当前 token）峰值必须 <= 2。
        var sb = new StringBuilder("{");
        for (var i = 1; i <= 12; i++)
            sb.Append(i == 1 ? "" : ",").Append($"\"P{i}\":\"v{i}\"");
        sb.Append('}');
        var reader = new JsonReader(
            new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(sb.ToString())),
            maxDepth: 256,
            isFinalBlock: true);
        while (reader.Read()) { }
        await Assert.That(reader.LeakedBufferCount).IsZero();
        await Assert.That(reader.PeakTrackedBufferCount).IsLessThanOrEqualTo(2);
        reader.Dispose();
        await Assert.That(reader.TrackedBufferCount).IsZero();
        await Assert.That(reader.TotalPoolReturns).IsEqualTo(reader.TotalTrackedBuffers);
    }

    [Test]
    public async Task PropertyNameSpan_SurvivesManyReads()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"P1\":\"v1\",\"P2\":\"v2\",\"P3\":\"v3\",\"P4\":\"v4\",\"P5\":\"v5\",\"P6\":\"v6\"}");
        var reader = new JsonReader(new ReadOnlySequence<byte>(json), maxDepth: 256, isFinalBlock: true);
        try
        {
            await Assert.That(reader.Read()).IsTrue();
            for (var i = 1; i <= 6; i++)
            {
                await Assert.That(reader.Read()).IsTrue();
                var name = reader.GetStringRaw();
                await Assert.That(reader.Read()).IsTrue();
                var probe = ArrayPool<byte>.Shared.Rent(256);
                probe.AsSpan().Fill((byte)'X');
                ArrayPool<byte>.Shared.Return(probe);
                await Assert.That(Encoding.UTF8.GetString(name)).IsEqualTo($"P{i}");
            }
        }
        finally { reader.Dispose(); }
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoJetson/tests/PicoJetson.Unit.Tests -c Debug --no-progress -- --treenode-filter "/*/*/ReaderBufferOwnershipTests/*"`
Expected: `PeakLiveBuffers_StaysBounded` FAIL —— 当前 8 槽轮转模型下 `PeakTrackedBufferCount` 远大于 2。

- [ ] **Step 3: 实现**

按 Interfaces 重构缓冲跟踪。注意 seq 字符串路径在 `_valueSpan = buf.AsSpan(0, di)` 之后立刻进入 `SkipWhitespaceSeq()`，此时 `buf` 可能就是下一个 token 要复用的缓冲，所以 `_spanBuffer` 必须在 `Read()` 返回前一直保持有效，直到下一次 `TrackBuffer` 决定是否归还它。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（2/2）。

- [ ] **Step 5: 回归与提交**

```bash
dotnet test PicoJetson/tests/PicoJetson.Unit.Tests -c Debug --no-progress
dotnet test PicoJetson/tests/PicoJetson.Integration.Tests -c Debug --no-progress
dotnet csharpier format PicoJetson/src/PicoJetson/JsonReader.cs PicoJetson/tests/PicoJetson.Unit.Tests/ReaderBufferOwnershipTests.cs
git add PicoJetson/src/PicoJetson/JsonReader.cs PicoJetson/tests/PicoJetson.Unit.Tests/ReaderBufferOwnershipTests.cs
git commit -m "fix(picojetson): keep rented buffers alive for the span lifetime"
```

---

### Task 7: JSON 字典可空值元素（C-29）

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`（`EmitDictInnerValueDeserialize` ≈1189-1450）
- Test: `PicoJetson/tests/PicoJetson.Integration.Tests/DictNullableValueTests.cs`（新建）

**Interfaces:**
- 元素可空（`ElementIsNullableValue || ElementIsNullableReference`）时先判 `TokenType.Null` 写 `null`，否则走既有分支；`string` 元素同样处理。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoJetson.Tests;

public class DictNullableValueTests
{
    [Test]
    public async Task Int_NullStaysNull() =>
        await Assert.That(JsonSerializer.Deserialize<IntMap>(
            Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}")).Map["b"]).IsNull();

    [Test]
    public async Task Decimal_NullStaysNull() =>
        await Assert.That(JsonSerializer.Deserialize<DecimalMap>(
            Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}")).Map["b"]).IsNull();

    [Test]
    public async Task GuidAndDateTime_NullStayNull()
    {
        await Assert.That(JsonSerializer.Deserialize<GuidMap>(
            Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}")).Map["b"]).IsNull();
        await Assert.That(JsonSerializer.Deserialize<DateTimeMap>(
            Encoding.UTF8.GetBytes("{\"Map\":{\"b\":null}}")).Map["b"]).IsNull();
    }

    public class IntMap { public Dictionary<string, int?> Map { get; set; } = new(); }
    public class DecimalMap { public Dictionary<string, decimal?> Map { get; set; } = new(); }
    public class GuidMap { public Dictionary<string, Guid?> Map { get; set; } = new(); }
    public class DateTimeMap { public Dictionary<string, DateTime?> Map { get; set; } = new(); }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoJetson/tests/PicoJetson.Integration.Tests -c Debug --no-progress -- --treenode-filter "/*/*/DictNullableValueTests/*"`
Expected: FAIL —— 0 / `Guid.Empty` / `DateTime.MinValue`。

- [ ] **Step 3: 实现**

在 `EmitDictInnerValueDeserialize` 的 `switch` 前插入可空判断。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（3/3）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoJetson/tests/PicoJetson.Integration.Tests -c Debug --no-progress
dotnet csharpier format PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs PicoJetson/tests/PicoJetson.Integration.Tests/DictNullableValueTests.cs
git add PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs PicoJetson/tests/PicoJetson.Integration.Tests/DictNullableValueTests.cs
git commit -m "fix(picojetson): preserve null for nullable dictionary values"
```

---

## Phase C — MsgPack

### Task 8: string token 校验与元素类型完整性（C-07, C-08）

**Files:**
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`（属性读 1989-2600；元素读 2772-2990；元素写 1780-1990）
- Test: `PicoMsgPack/tests/PicoMsgPack.Tests/ElementStrictnessTests.cs`（新建）

**Interfaces:**
- string/bytes 读取前断言 `TokenType`，否则 `FormatException`。
- `ReadDeserElemCore`/`WriteSerElemCore` 补齐 `guid/datetime/timespan/dateonly/timeonly/enum/bytes/extension`；所有元素 `TryGet*` 检查返回值。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoMsgPack.Tests;

public class ElementStrictnessTests
{
    [Test]
    public void StringMember_FromIntegerToken_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<StringHolder>(new byte[] { 0x81, 0x00, 0x2A }));

    [Test]
    public void ElementInt_FromStringToken_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<IntListHolder>(
                new byte[] { 0x81, 0x00, 0x92, 0x01, 0xA1, (byte)'x' }));

    [Test]
    public async Task GuidAndDateTimeLists_RoundTrip()
    {
        var id = Guid.NewGuid();
        var at = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var back = MsgPackSerializer.Deserialize<TemporalLists>(
            MsgPackSerializer.SerializeToUtf8Bytes(new TemporalLists { Ids = { id }, Ats = { at } }));
        await Assert.That(back.Ids[0]).IsEqualTo(id);
        await Assert.That(back.Ats[0]).IsEqualTo(at);
    }

    public class StringHolder { public string Name { get; set; } = ""; }
    public class IntListHolder { public List<int> Nums { get; set; } = new(); }
    public class TemporalLists
    {
        public List<Guid> Ids { get; set; } = new();
        public List<DateTime> Ats { get; set; } = new();
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoMsgPack/tests/PicoMsgPack.Tests -c Debug --no-progress -- --treenode-filter "/*/*/ElementStrictnessTests/*"`
Expected: FAIL —— `"*"` / `0` / `Guid.Empty`。

- [ ] **Step 3: 实现**

抽出 `EmitElementRead`，按属性级严格实现；string/bytes 增加 token 断言；`TryGet*` 全部 `if (!...) throw`。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（3/3）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoMsgPack/tests/PicoMsgPack.Tests -c Debug --no-progress
dotnet csharpier format PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs PicoMsgPack/tests/PicoMsgPack.Tests/ElementStrictnessTests.cs
git add PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs PicoMsgPack/tests/PicoMsgPack.Tests/ElementStrictnessTests.cs
git commit -m "fix(picomsgpack): validate element tokens and support temporal element kinds"
```

---

### Task 9: 容器闭合、map 键、UInt64 与长度守卫（C-09, C-10）

**Files:**
- Modify: `PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs`（`TryGetInt64`≈703；`ReadBytes`/`ReadByteLen`≈160-211；`TrySkip`≈869-878）
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`（**span 版**根循环 595-635 **与流式版**根循环 ≈659-730，两处都加闭合校验）
- Test: `PicoMsgPack/tests/PicoMsgPack.Tests/PayloadRobustnessTests.cs`（新建）

**Interfaces:**
- 根循环后 `if (reader.Depth != 0) throw new FormatException("Truncated container ...")`。
- 流式版（≈659-730）在 `result = obj` 返回前加同一校验，但**先判 `NeedsMoreData`**：`if (reader.NeedsMoreData) return ReadStatus.NeedMoreData; if (reader.Depth != 0) throw ...` —— 否则合法分块会被误判为截断。
- map 分支 `if (!reader.TryGetInt32(out __k)) throw new FormatException("Map keys must be integers ...")`。
- `TryGetInt64` 的 8 字节分支区分 `UInt64` 并做 `> long.MaxValue` 判定。
- `ReadByteLen` 返回 `long`；`ReadBytes(long)` 首行校验负值与 `int.MaxValue` 上限。
- **`TrySkip` 不引用 `IncompleteInputException`**（该类型是 `internal`，`PicoSerDe.Core.csproj` 的 `InternalsVisibleTo` 不含 `PicoMsgPack`，直接 catch 会 CS0122）。改为：`public bool TrySkip() { if (NeedsMoreData) return false; Skip(); return true; }` —— 不再裸 `catch`，真实 `FormatException` 照常抛出。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoMsgPack.Tests;

public class PayloadRobustnessTests
{
    [Test]
    public void TruncatedMap_Throws() =>
        Assert.Throws<FormatException>(() => MsgPackSerializer.Deserialize<NameHolder>(
            new byte[] { 0x82, 0x00, 0xA1, (byte)'x', 0x01 }));

    [Test]
    public void StringKeyMap_Throws() =>
        Assert.Throws<FormatException>(() => MsgPackSerializer.Deserialize<IntHolder>(
            new byte[] { 0x81, 0xA1, (byte)'k', 0x01 }));

    [Test]
    public void UInt64OutOfInt64Range_Throws() =>
        Assert.Throws<FormatException>(() => MsgPackSerializer.Deserialize<LongHolder>(
            new byte[] { 0x81, 0x00, 0xCF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }));

    [Test]
    public void NegativeBinLength_ThrowsFormatException() =>
        Assert.Throws<FormatException>(() => MsgPackSerializer.Deserialize<BytesHolder>(
            new byte[] { 0x81, 0x00, 0xC6, 0xFF, 0xFF, 0xFF, 0xFF }));

    public class NameHolder { public string Name { get; set; } = ""; }
    public class IntHolder { public int Value { get; set; } }
    public class LongHolder { public long Value { get; set; } }
    public class BytesHolder { public byte[] Data { get; set; } = []; }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoMsgPack/tests/PicoMsgPack.Tests -c Debug --no-progress -- --treenode-filter "/*/*/PayloadRobustnessTests/*"`
Expected: 4 条 FAIL（无异常 / 部分对象 / `ArgumentOutOfRangeException`）。

- [ ] **Step 3: 实现**

按 Interfaces 修改 reader 与生成器；长度累加改 `long`。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（4/4）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoMsgPack/tests/PicoMsgPack.Tests -c Debug --no-progress
dotnet csharpier format PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs PicoMsgPack/tests/PicoMsgPack.Tests/PayloadRobustnessTests.cs
git add -u PicoMsgPack/src PicoMsgPack/tests/PicoMsgPack.Tests/PayloadRobustnessTests.cs
git commit -m "fix(picomsgpack): validate container closure, map keys and length prefixes"
```

---

### Task 10: MsgPack nullable 语义（C-27, C-28）

**Files:**
- Modify: `PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`（属性读 2315-2580；list/array/dict 读 2587-2630）
- Test: `PicoMsgPack/tests/PicoMsgPack.Tests/NullableSemanticsTests.cs`（新建）

**Interfaces:**
- `decimal/datetime/guid/timespan/dateonly/timeonly/enum` 可空分支首行 `if (TokenType == Null) { target = null; break; }`。
- `list/array/dict`：nil → `target = null`；非 nil 才 `??=` 并读取。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoMsgPack.Tests;

public class NullableSemanticsTests
{
    [Test]
    public async Task TemporalNullable_RoundTripsNull()
    {
        await Assert.That(MsgPackSerializer.Deserialize<DecHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DecHolder())).V).IsNull();
        await Assert.That(MsgPackSerializer.Deserialize<DtHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DtHolder())).V).IsNull();
        await Assert.That(MsgPackSerializer.Deserialize<GuidHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new GuidHolder())).V).IsNull();
        await Assert.That(MsgPackSerializer.Deserialize<TsHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new TsHolder())).V).IsNull();
        await Assert.That(MsgPackSerializer.Deserialize<DoHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DoHolder())).V).IsNull();
        await Assert.That(MsgPackSerializer.Deserialize<ToHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new ToHolder())).V).IsNull();
    }

    [Test]
    public async Task NullableCollections_StayNull()
    {
        await Assert.That(MsgPackSerializer.Deserialize<ListHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new ListHolder())).V).IsNull();
        await Assert.That(MsgPackSerializer.Deserialize<ArrayHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new ArrayHolder())).V).IsNull();
        await Assert.That(MsgPackSerializer.Deserialize<DictHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DictHolder())).V).IsNull();
    }

    [Test]
    public async Task NonNullValues_RoundTrip()
    {
        await Assert.That(MsgPackSerializer.Deserialize<DecHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DecHolder { V = 1.5m })).V).IsEqualTo(1.5m);
        await Assert.That(MsgPackSerializer.Deserialize<ListHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new ListHolder { V = [1, 2] })).V).IsEquivalentTo([1, 2]);
    }

    public class DecHolder { public decimal? V { get; set; } }
    public class DtHolder { public DateTime? V { get; set; } }
    public class GuidHolder { public Guid? V { get; set; } }
    public class TsHolder { public TimeSpan? V { get; set; } }
    public class DoHolder { public DateOnly? V { get; set; } }
    public class ToHolder { public TimeOnly? V { get; set; } }
    public class ListHolder { public List<int>? V { get; set; } }
    public class ArrayHolder { public int[]? V { get; set; } }
    public class DictHolder { public Dictionary<string, int>? V { get; set; } }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoMsgPack/tests/PicoMsgPack.Tests -c Debug --no-progress -- --treenode-filter "/*/*/NullableSemanticsTests/*"`
Expected: 前两条 FAIL（6 个 `FormatException`；3 个空集合）。

- [ ] **Step 3: 实现**

按 Interfaces 增加 nil 分支。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（3/3）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoMsgPack/tests/PicoMsgPack.Tests -c Debug --no-progress
dotnet csharpier format PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs PicoMsgPack/tests/PicoMsgPack.Tests/NullableSemanticsTests.cs
git add PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs PicoMsgPack/tests/PicoMsgPack.Tests/NullableSemanticsTests.cs
git commit -m "fix(picomsgpack): honour nil for temporal, enum and collection members"
```

---

## Phase D — TOML / INI / YAML / 跨格式

### Task 11: decimal/time culture 不变性（C-14）

**Files:**
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`（≈2140-2185）、`PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`（≈943-949）、`PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs`（≈1765-1772）
- Test: 在**既有** `PicoToml/tests/PicoToml.Tests/CultureTests.cs`、`PicoYaml/tests/PicoYaml.Tests/CultureTests.cs` 内**追加方法**；`PicoMsgPack/tests/PicoMsgPack.Tests/CultureTests.cs` 新建。**不要**新建同名类（CS0101）。

**Interfaces:**
- 写侧 `decimal`/`TimeSpan` 用 `ToString(CultureInfo.InvariantCulture)`。
- MsgPack 读侧 `decimal` 去掉 `NumberStyles.Any`，改 `NumberStyles.Number`（禁止千分位）。

- [ ] **Step 1: 写失败测试**

```csharp
// 追加到既有 CultureTests（Toml/Yaml）；MsgPack 新建同类。
// culture 是进程级状态，TUnit 默认并行 → 必须 [NotInParallel]。
[Test]
[NotInParallel]
public async Task Decimal_IsCultureInvariant()
{
    var old = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        var text = TomlSerializer.Serialize(new DecimalDto { Price = 1.5m });
        await Assert.That(text).Contains("1.5");
        await Assert.That(TomlSerializer.Deserialize<DecimalDto>(Encoding.UTF8.GetBytes(text)).Price)
            .IsEqualTo(1.5m);
    }
    finally { CultureInfo.CurrentCulture = old; }
}

public class DecimalDto { public decimal Price { get; set; } }
```

YAML/MsgPack 版本同样 `de-DE` + `1.5m`。

- [ ] **Step 2: 确认红灯**

Run: 三个项目分别 `--treenode-filter "/*/*/CultureTests/*"`。
Expected: TOML/YAML 输出 `1,5` 且回读 15；MsgPack 同样 100 倍。

- [ ] **Step 3: 实现**

按 Interfaces 修改三处生成器。

- [ ] **Step 4: 确认绿灯**

Run: 三个项目分别 `--treenode-filter "/*/*/CultureTests/*"`。Expected: PASS（Toml 2/2、Yaml 3/3、MsgPack 1/1 —— 含各自既有用例）。

- [ ] **Step 5: 提交**

```bash
dotnet csharpier format PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs
git add -u PicoToml/src PicoYaml/src PicoMsgPack/src
git commit -m "fix(gen): write decimal/timespan culture-invariantly in TOML/YAML/MsgPack"
```

---

### Task 12: TOML 值扫描与带引号点号键（C-12, C-13）

**Files:**
- Modify: `PicoToml/src/PicoToml/TomlReader.cs`（`ReadValueSpan`≈1000-1035；`ReadKeyValueSpan`/`TrySplitDottedKey`≈873-935；`TryGet*`≈420-490）
- Test: `PicoToml/tests/PicoToml.Tests/TomlValueStrictnessTests.cs`（新建）

**Interfaces:**
- 裸值扫描在“行首或空白后的 `#`”处截断；`_valueSpan` 不含注释。
- **截断点不止 `ReadValueSpan`**：数组元素扫描（`TomlReader.cs:1090-1100`）与内联表元素扫描（`:1180-1190`）用同一规则，否则 `Tags = [1, 2] # c` 仍抛。
- `TryGet*` 要求 `consumed == _valueSpan.Length`；新增 `TryParseTomlInteger` 支持 `0x/0o/0b/_`。
- 引号键内容不参与点号拆分；偏移使用内容真实起点。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoToml.Tests;

public class TomlValueStrictnessTests
{
    [Test]
    public async Task HexAndUnderscore_ParseExactly()
    {
        var v = TomlSerializer.Deserialize<NumDto>(
            Encoding.UTF8.GetBytes("Hex = 0x1F\nUnder = 1_000"));
        await Assert.That(v.Hex).IsEqualTo(31);
        await Assert.That(v.Under).IsEqualTo(1000);
    }

    [Test]
    public async Task TrailingComment_IsIgnored() =>
        await Assert.That(TomlSerializer.Deserialize<FlagDto>(
            Encoding.UTF8.GetBytes("Flag = true # note")).Flag).IsTrue();

    [Test]
    public async Task QuotedDottedKey_IsSingleKey() =>
        await Assert.That(TomlSerializer.Deserialize<QuotedKeyDto>(
            Encoding.UTF8.GetBytes("\"a.b\" = 1")).Value).IsEqualTo(1);

    [Test]
    public async Task DottedKey_StillSplitsIntoTables()
    {
        var v = TomlSerializer.Deserialize<DottedDto>(Encoding.UTF8.GetBytes("a.b = 2"));
        await Assert.That(v.A.B).IsEqualTo(2);
    }

    public class NumDto { public int Hex { get; set; } public int Under { get; set; } }
    public class FlagDto { public bool Flag { get; set; } }
    public class QuotedKeyDto { [TomlKey("a.b")] public int Value { get; set; } }
    public class Inner { public int B { get; set; } }
    public class DottedDto { [TomlKey("a")] public Inner A { get; set; } = new(); }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoToml/tests/PicoToml.Tests -c Debug --no-progress -- --treenode-filter "/*/*/TomlValueStrictnessTests/*"`
Expected: `0`/`1`、`FormatException`、`Value` 为 0（键被拆成 `"` 与 `.`）。

- [ ] **Step 3: 实现**

按 Interfaces 修改扫描与解析。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（4/4）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoToml/tests/PicoToml.Tests -c Debug --no-progress
dotnet csharpier format PicoToml/src/PicoToml/TomlReader.cs PicoToml/tests/PicoToml.Tests/TomlValueStrictnessTests.cs
git add PicoToml/src/PicoToml/TomlReader.cs PicoToml/tests/PicoToml.Tests/TomlValueStrictnessTests.cs
git commit -m "fix(picotoml): consume whole scalar tokens and stop at inline comments"
```

---

### Task 13: TOML section 顺序导致标量丢失（C-11）

**Files:**
- Modify: `PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs`（顶层发射循环≈784；inner helper 循环≈611）
- Test: `PicoToml/tests/PicoToml.Tests/TomlSectionOrderTests.cs`（新建）

**Interfaces:**
- 顶层顺序：标量与集合先写，`object` 成员（`[Section]`）后写。
- **inner helper 的序列化同样要两轮**：`TomlSerializerGenerator.cs:514` 现在是 `props.OrderBy(x => x.JsonName)`，二级表 `[Parent]` 内若 `A`(object) 排在 `Z`(scalar) 前，`Z` 仍会被吞。改为先标量/集合、后 object，再按原序号稳定排序。
- inner helper 循环加 `TablePath` 守卫；`ReadOnlySpan<byte>` 不能跨 `Read()` 保存，进入时复制 section 名（`byte[]`）。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoToml.Tests;

public class TomlSectionOrderTests
{
    [Test]
    public async Task ObjectBeforeScalar_RoundTripsAllMembers()
    {
        var text = TomlSerializer.Serialize(
            new Ordered { Address = new Address { City = "X" }, Name = "parsed" });
        var back = TomlSerializer.Deserialize<Ordered>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back.Address.City).IsEqualTo("X");
        // 初值是 ""，只有真正读回 "parsed" 才算通过（初值不得与期望值重合）。
        await Assert.That(back.Name).IsEqualTo("parsed");
    }

    public class Address { public string City { get; set; } = ""; }
    public class Ordered
    {
        public Address Address { get; set; } = new();
        public string Name { get; set; } = "";
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoToml/tests/PicoToml.Tests -c Debug --no-progress -- --treenode-filter "/*/*/TomlSectionOrderTests/*"`
Expected: FAIL —— `Name` 为 `""`。

- [ ] **Step 3: 实现**

顶层分两轮发射；inner helper 加 `TablePath` 守卫。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（1/1）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoToml/tests/PicoToml.Tests -c Debug --no-progress
dotnet csharpier format PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs PicoToml/tests/PicoToml.Tests/TomlSectionOrderTests.cs
git add PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs PicoToml/tests/PicoToml.Tests/TomlSectionOrderTests.cs
git commit -m "fix(picotoml): emit scalar members before table sections"
```

---

### Task 14: INI ctor 赋值、字典键与属性文本转义（C-15, C-16, C-17）

**Files:**
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`（`EmitRead` 1495-1860；dict 读 857-918；注释/节名 527-543、615-618）
- Modify: `shared/Gen/GenInfrastructure.cs`（`IsSupportedDictKeyKind` 调用点 1192，INI 侧按 format 过滤）
- Test: `tests/PicoSerDe.Integration.Tests/IniGeneratorCompileTests.cs`（新建；红灯为构建失败）

**Interfaces:**
- `EmitRead` 所有分支统一用 `EmitAssignTarget(...)` 生成赋值左侧（`__cp_N` 或 `target.Prop`）。
- INI 只允许 `string` 字典键：非 string 键在提取期 `skippedUnsupported` + `PICOSERDE004`。
- `GenInfrastructure.IsSupportedDictKeyKind` 是**共享**判定（`:1192`），不能直接改它——否则 JSON 的 `Dictionary<int,string>` 会被顺带丢掉。改成 `IsSupportedDictKeyKind(string kind, string formatTag)`，仅 `formatTag == "ini"` 时限制为 `string`。
- `[IniComment]`/`[IniSection]` 文本走 `EscapeCSharpString`。

- [ ] **Step 1: 写失败测试（构建红）**

```csharp
namespace PicoSerDe.Integration.Tests;

public record IniRec(string Name, long Age);

[IniComment("say \"hi\"")]
public class IniCommentDto { public string A { get; set; } = "a"; }

public class IniDictKeyDto { public Dictionary<int, string> Map { get; set; } = new(); }

public class IniGeneratorCompileTests
{
    [Test]
    public async Task RecordWithLong_RoundTrips()
    {
        var text = IniSerializer.Serialize(new IniRec("a", 5));
        await Assert.That(IniSerializer.Deserialize<IniRec>(Encoding.UTF8.GetBytes(text)).Age).IsEqualTo(5);
    }

    [Test]
    public async Task CommentWithQuotes_Serializes() =>
        await Assert.That(IniSerializer.Serialize(new IniCommentDto())).Contains("say \"hi\"");

    [Test]
    public async Task NonStringDictKey_IsDropped() =>
        await Assert.That(IniSerializer.Serialize(new IniDictKeyDto { Map = { [1] = "x" } }))
            .DoesNotContain("Map");
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet build tests/PicoSerDe.Integration.Tests -c Debug`
Expected: FAIL —— CS1061、CS1003、CS1503。

- [ ] **Step 3: 实现**

按 Interfaces 修改。

- [ ] **Step 4: 确认绿灯**

Run: `dotnet build tests/PicoSerDe.Integration.Tests -c Debug`，然后 Step 1 的测试命令。
Expected: PASS（3/3）。

- [ ] **Step 5: 提交**

```bash
dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress
dotnet csharpier format PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs shared/Gen/GenInfrastructure.cs tests/PicoSerDe.Integration.Tests/IniGeneratorCompileTests.cs
git add -u PicoIni/src shared/Gen tests/PicoSerDe.Integration.Tests/IniGeneratorCompileTests.cs
git commit -m "fix(picoini): assign ctor targets, drop non-string dict keys and escape attribute text"
```

---

### Task 15: INI 严格解析、converter 写路径、引号与注释（C-18, C-19, C-20）

**Files:**
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`（`EmitRead` 1733-1798；`WriteValue` 1310）
- Modify: `PicoIni/src/PicoIni/IniReader.cs`（引号扫描 594-646；值截断 632-639）
- Test: `PicoIni/tests/PicoIni.Tests/IniStrictnessTests.cs`（新建）

**Interfaces:**
- Guid/DateTime/TimeSpan/enum 解析失败 → `FormatException`。
- **不要**把 converter 塞进 `WriteValue`：它是纯表达式构造器，被用在 `sb.Append(` 与 `string.Join(",", ...)` 内部，无法承载 `var __c = ...; var __bw = ...;` 语句序列。
- 新增语句级发射器 `EmitWriteKeyValue(StringBuilder s, string key, PropertyInfo p, string accessor, string ind)`：
  - converter 分支：`var __c = new X(); var __bw = new System.Buffers.ArrayBufferWriter<byte>(); __c.Write(__bw, acc); iw.WriteKeyValue("k"u8, __bw.WrittenSpan);`
  - 普通分支：沿用现有 `WriteValue` 表达式 + `iw.WriteKeyValue(...)`。
  - 列表元素内部仍用 `WriteValue`（converter 列表暂不支持，保持现有行为）。
- 未闭合引号：final → `FormatException`，非 final → `IncompleteInputException`；`#`/`;` 仅在行首或空白后开始注释。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoIni.Tests;

public class IniStrictnessTests
{
    [Test]
    public void InvalidGuid_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<GuidDto>(Encoding.UTF8.GetBytes("Id = not-a-guid")));

    [Test]
    public void InvalidEnum_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<EnumDto>(Encoding.UTF8.GetBytes("Level = Funday")));

    [Test]
    public void UnterminatedQuote_Throws() =>
        Assert.Throws<FormatException>(() =>
            IniSerializer.Deserialize<TextDto>(Encoding.UTF8.GetBytes("A = \"oops")));

    [Test]
    public async Task HashInsideUrl_IsPreserved() =>
        await Assert.That(IniSerializer.Deserialize<TextDto>(
            Encoding.UTF8.GetBytes("A = http://x#frag")).A).IsEqualTo("http://x#frag");

    [Test]
    public async Task Converter_IsUsedOnWriteAndRead()
    {
        var text = IniSerializer.Serialize(new ConvDto { Values = [1, 2, 3] });
        await Assert.That(text).Contains("1|2|3");
        await Assert.That(IniSerializer.Deserialize<ConvDto>(Encoding.UTF8.GetBytes(text)).Values)
            .IsEquivalentTo([1, 2, 3]);
    }

    public class GuidDto { public Guid Id { get; set; } }
    public class EnumDto { public Level Level { get; set; } }
    public enum Level { Low = 1, High = 2 }
    public class TextDto { public string A { get; set; } = ""; }
    public class ConvDto
    {
        [IniConverter(typeof(PipeConverter))]
        public int[] Values { get; set; } = [];
    }
    public sealed class PipeConverter : IIniConverter<int[]>
    {
        public void Write(IBufferWriter<byte> writer, int[] value)
        {
            var s = string.Join("|", value);
            var span = writer.GetSpan(s.Length);
            Encoding.UTF8.GetBytes(s, span);
            writer.Advance(s.Length);
        }
        public int[] Read(ReadOnlySpan<byte> value) =>
            Encoding.UTF8.GetString(value).Split('|').Select(int.Parse).ToArray();
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoIni/tests/PicoIni.Tests -c Debug --no-progress -- --treenode-filter "/*/*/IniStrictnessTests/*"`
Expected: 5 条 FAIL。

- [ ] **Step 3: 实现**

按 Interfaces 修改生成器与 reader。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（5/5）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoIni/tests/PicoIni.Tests -c Debug --no-progress
dotnet csharpier format PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs PicoIni/src/PicoIni/IniReader.cs PicoIni/tests/PicoIni.Tests/IniStrictnessTests.cs
git add -u PicoIni/src PicoIni/tests/PicoIni.Tests/IniStrictnessTests.cs
git commit -m "fix(picoini): fail loudly on invalid values and honour converters on write"
```

---

### Task 16: INI nullable 字典为 null（C-31）

**Files:**
- Modify: `PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs`（dict section 写路径 583-598）
- Test: `PicoIni/tests/PicoIni.Tests/IniNullableDictTests.cs`（新建）

**Interfaces:**
- dict 写循环包在 `EmitNullGuardOpen` 返回的块内；null 时整节省略。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoIni.Tests;

public class IniNullableDictTests
{
    [Test]
    public async Task NullDictionary_IsOmitted() =>
        await Assert.That(IniSerializer.Serialize(new Holder()).Trim()).IsEmpty();

    [Test]
    public async Task NonNullDictionary_StillWrites() =>
        await Assert.That(IniSerializer.Serialize(new Holder { Map = { ["a"] = 1 } })).Contains("[Map]");

    public class Holder { public Dictionary<string, int>? Map { get; set; } }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoIni/tests/PicoIni.Tests -c Debug --no-progress -- --treenode-filter "/*/*/IniNullableDictTests/*"`
Expected: FAIL —— `NullReferenceException`。

- [ ] **Step 3: 实现**

包 `EmitNullGuardOpen`。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（2/2）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoIni/tests/PicoIni.Tests -c Debug --no-progress
dotnet csharpier format PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs PicoIni/tests/PicoIni.Tests/IniNullableDictTests.cs
git add PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs PicoIni/tests/PicoIni.Tests/IniNullableDictTests.cs
git commit -m "fix(picoini): omit null dictionary sections instead of throwing"
```

---

### Task 17: YAML 空对象列表吞兄弟键与元素类型（C-21, C-22）

**Files:**
- Modify: `PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs`（列表反序列化 2637-2700；元素读 switch 3026-3113）
- Test: `PicoYaml/tests/PicoYaml.Tests/YamlListContractTests.cs`（新建）

**Interfaces:**
- 对象元素列表：`YamlReader` **没有 push-back**，必须用 `Mark()/RewindToMark()`（`YamlReader.cs:279-282`）：
  `r.Mark(); if (!r.Read() || r.TokenType != TokenType.ObjectStart) { r.RewindToMark(); return new List<T>(); }`
  不要用"先 Read 再补救"的写法——兄弟键的 `PropertyName` 已被消费。
- 元素读 switch 补齐 `float32/float64/boolean/decimal/guid/datetime/timespan/dateonly/timeonly/enum`，复用属性级标量实现。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoYaml.Tests;

public class YamlListContractTests
{
    [Test]
    public async Task EmptyObjectList_DoesNotSwallowNextProperty()
    {
        var v = YamlSerializer.Deserialize<Holder>(Encoding.UTF8.GetBytes("Items:\nCount: 7"));
        await Assert.That(v.Items).IsEmpty();
        await Assert.That(v.Count).IsEqualTo(7);
    }

    [Test]
    public async Task ScalarElementLists_RoundTrip()
    {
        var value = new Scalars
        {
            Doubles = { 1.5, 2.5 },
            Bools = { true, false },
            Decimals = { 1.5m },
            Guids = { Guid.Parse("11111111-2222-3333-4444-555555555555") },
            Dates = { new DateTime(2024, 1, 2) },
        };
        var back = YamlSerializer.Deserialize<Scalars>(
            Encoding.UTF8.GetBytes(YamlSerializer.Serialize(value)));
        await Assert.That(back.Doubles).IsEquivalentTo([1.5, 2.5]);
        await Assert.That(back.Bools).IsEquivalentTo([true, false]);
        await Assert.That(back.Decimals[0]).IsEqualTo(1.5m);
    }

    public class Item { public string Name { get; set; } = ""; }
    public class Holder
    {
        public List<Item> Items { get; set; } = new();
        public int Count { get; set; }
    }
    public class Scalars
    {
        public List<double> Doubles { get; set; } = new();
        public List<bool> Bools { get; set; } = new();
        public List<decimal> Decimals { get; set; } = new();
        public List<Guid> Guids { get; set; } = new();
        public List<DateTime> Dates { get; set; } = new();
    }
}
```

- [ ] **Step 2: 确认红灯（构建失败）**

Run: `dotnet build PicoYaml/tests/PicoYaml.Tests -c Debug`
Expected: FAIL —— `CS1503: string → double`。

- [ ] **Step 3: 实现**

按 Interfaces 修改。

- [ ] **Step 4: 确认绿灯**

Run: `dotnet build PicoYaml/tests/PicoYaml.Tests -c Debug`，然后 Step 1 的测试命令。
Expected: PASS（2/2）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoYaml/tests/PicoYaml.Tests -c Debug --no-progress
dotnet csharpier format PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs PicoYaml/tests/PicoYaml.Tests/YamlListContractTests.cs
git add PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs PicoYaml/tests/PicoYaml.Tests/YamlListContractTests.cs
git commit -m "fix(picoyaml): stop empty object lists from consuming the next property"
```

---

### Task 18: YAML 行内注释与 stale token（C-23, C-24）

**Files:**
- Modify: `PicoYaml/src/PicoYaml/YamlReader.cs`（值扫描 825-845、1134-1141、1209-1217；`Read()` 失败路径 415-424、531）
- Test: `PicoYaml/tests/PicoYaml.Tests/YamlInlineCommentTests.cs`（新建）

**Interfaces:**
- 值扫描在“行首或空白后的 `#`”处截断（引号内不截断）。
- `Read()` 无 token 时置 `_tokenType = None`；嵌套/字典分支 `!r.Read()` 时 `break`。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoYaml.Tests;

public class YamlInlineCommentTests
{
    [Test]
    public async Task InlineComment_OnString_IsStripped() =>
        await Assert.That(YamlSerializer.Deserialize<TextDto>(
            Encoding.UTF8.GetBytes("Name: v # note")).Name).IsEqualTo("v");

    [Test]
    public async Task InlineComment_OnInt_IsStripped() =>
        await Assert.That(YamlSerializer.Deserialize<NumDto>(
            Encoding.UTF8.GetBytes("Count: 5 # note")).Count).IsEqualTo(5);

    [Test]
    public async Task EmptyNestedObjectAtEnd_DoesNotSpin()
    {
        // Nested 初值是 new()，IsNotNull 恒真；改为断言内容为空。
        var v = YamlSerializer.Deserialize<NestedDto>(Encoding.UTF8.GetBytes("Nested:"));
        await Assert.That(v.Nested.City).IsEqualTo("");
    }

    [Test]
    public async Task NestedBlockWithSibling_KeepsBoth()
    {
        var v = YamlSerializer.Deserialize<NestedDto>(
            Encoding.UTF8.GetBytes("Nested:\n  City: X\nCount: 3"));
        await Assert.That(v.Nested.City).IsEqualTo("X");
        await Assert.That(v.Count).IsEqualTo(3);
    }

    public class TextDto { public string Name { get; set; } = ""; }
    public class NumDto { public int Count { get; set; } }
    public class Inner { public string City { get; set; } = ""; }
    public class NestedDto
    {
        public Inner Nested { get; set; } = new();
        public int Count { get; set; }
    }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test PicoYaml/tests/PicoYaml.Tests -c Debug --no-progress -- --treenode-filter "/*/*/YamlInlineCommentTests/*"`
Expected: FAIL —— `"v # note"`、`FormatException`、`MaxTokens` 伪死循环。

- [ ] **Step 3: 实现**

按 Interfaces 修改 reader 与生成器。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（4/4）。

- [ ] **Step 5: 提交**

```bash
dotnet test PicoYaml/tests/PicoYaml.Tests -c Debug --no-progress
dotnet csharpier format PicoYaml/src/PicoYaml/YamlReader.cs PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs PicoYaml/tests/PicoYaml.Tests/YamlInlineCommentTests.cs
git add -u PicoYaml/src PicoYaml/tests/PicoYaml.Tests/YamlInlineCommentTests.cs
git commit -m "fix(picoyaml): strip inline comments and reset token type at EOF"
```

---

### Task 19: TOML/YAML/INI 的 null 字面量与缺省语义（C-30）

**Files:**
- Modify: `PicoToml/src/PicoToml/TomlReader.cs`、`PicoYaml/src/PicoYaml/YamlReader.cs`、`PicoIni/src/PicoIni/IniReader.cs`（各新增 `public bool IsNullLiteral`）
- Modify: 三个生成器的成员读取首行
- Test: `tests/PicoSerDe.Integration.Tests/NullLiteralTests.cs`（新建）

**Interfaces:**
- 三个 reader 各新增 `public bool IsNullLiteral { get; }`：
  - YAML：`ValueSpan` 为 `null`/`~`/`Null`/`NULL`，或为空（`Nested:` 形式）。
  - TOML：**未加引号**且 `ValueSpan` 为空，或 `ValueSpan` 等于 `null`（大小写不敏感）。
  - INI：**没有 `ValueSpan` 属性**（`IniReader.cs:236` 只有 `GetStringRaw()`）；用 `GetStringRaw()` 判空/判 `null`，同样只在未加引号时成立。
- 生成器在成员读取的解析分支之前插入：
  - `IsNullable || IsNullableReference` → `target = null;`（跳过解析）；
  - 非可空值类型 → `throw new FormatException($"null value for non-nullable member {prop.JsonName}")`；
  - 非可空 `string` → `target = null!;`（与既有 NRT 宽松语义一致）。
- 读取顺序：先判 `IsNullLiteral`，再走 `TokenType`/`TryParse` 分支；`string` 分支不得用 `GetStringRaw()`/`ValueSpan` 直接吃 `null` 文本。
- **引号内的空串不是 null**：reader 需记录 `_valueWasQuoted`（TOML `ReadBasicStringSpan`、YAML 双引号扫描、INI 引号值）。`V = ""` / `V: ""` 必须得到 `""` 而不是 `null`。

- [ ] **Step 1: 写失败测试**

```csharp
namespace PicoSerDe.Integration.Tests;

public class NullLiteralTests
{
    [Test]
    public async Task Yaml_NullTildeAndEmpty_MapToNull()
    {
        await Assert.That(YamlSerializer.Deserialize<StrDto>(Encoding.UTF8.GetBytes("V: null")).V).IsNull();
        await Assert.That(YamlSerializer.Deserialize<StrDto>(Encoding.UTF8.GetBytes("V: ~")).V).IsNull();
        await Assert.That(YamlSerializer.Deserialize<StrDto>(Encoding.UTF8.GetBytes("V:")).V).IsNull();
        await Assert.That(YamlSerializer.Deserialize<IntDto>(Encoding.UTF8.GetBytes("V: null")).V).IsNull();
        await Assert.That(YamlSerializer.Deserialize<IntDto>(Encoding.UTF8.GetBytes("V:")).V).IsNull();
    }

    [Test]
    public async Task Toml_And_Ini_NullLiteral_MapToNull()
    {
        await Assert.That(TomlSerializer.Deserialize<StrDto>(Encoding.UTF8.GetBytes("V = null")).V).IsNull();
        await Assert.That(TomlSerializer.Deserialize<IntDto>(Encoding.UTF8.GetBytes("V = ")).V).IsNull();
        await Assert.That(IniSerializer.Deserialize<StrDto>(Encoding.UTF8.GetBytes("V = null")).V).IsNull();
        await Assert.That(IniSerializer.Deserialize<IntDto>(Encoding.UTF8.GetBytes("V = ")).V).IsNull();
    }

    [Test]
    public void NonNullableInt_NullLiteral_Throws() =>
        Assert.Throws<FormatException>(() =>
            YamlSerializer.Deserialize<NonNullInt>(Encoding.UTF8.GetBytes("V: null")));

    [Test]
    public async Task QuotedEmptyString_IsNotNull()
    {
        await Assert.That(TomlSerializer.Deserialize<StrDto>(
            Encoding.UTF8.GetBytes("V = \"\"")).V).IsEqualTo("");
        await Assert.That(YamlSerializer.Deserialize<StrDto>(
            Encoding.UTF8.GetBytes("V: \"\"")).V).IsEqualTo("");
    }

    [Test]
    public async Task QuotedNullText_StaysString()
    {
        await Assert.That(TomlSerializer.Deserialize<StrDto>(
            Encoding.UTF8.GetBytes("V = \"null\"")).V).IsEqualTo("null");
        await Assert.That(YamlSerializer.Deserialize<StrDto>(
            Encoding.UTF8.GetBytes("V: \"null\"")).V).IsEqualTo("null");
    }

    [Test]
    public async Task OrdinaryValues_StillParse()
    {
        await Assert.That(YamlSerializer.Deserialize<IntDto>(
            Encoding.UTF8.GetBytes("V: 5")).V).IsEqualTo(5);
        await Assert.That(TomlSerializer.Deserialize<IntDto>(
            Encoding.UTF8.GetBytes("V = 5")).V).IsEqualTo(5);
    }

    public class StrDto { public string? V { get; set; } }
    public class IntDto { public int? V { get; set; } }
    public class NonNullInt { public int V { get; set; } }
}
```

- [ ] **Step 2: 确认红灯**

Run: `dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress -- --treenode-filter "/*/*/NullLiteralTests/*"`
Expected: FAIL —— `'null'`/`'~'` 字符串、TOML/INI `FormatException`。

- [ ] **Step 3: 实现**

按 Interfaces 修改 reader 与生成器。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 命令。Expected: PASS（6/6）。

- [ ] **Step 5: 提交**

```bash
dotnet test tests/PicoSerDe.Integration.Tests -c Debug --no-progress
dotnet csharpier format PicoToml/src/PicoToml/TomlReader.cs PicoYaml/src/PicoYaml/YamlReader.cs PicoIni/src/PicoIni/IniReader.cs PicoToml/src/PicoToml.Gen/TomlSerializerGenerator.cs PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs PicoIni/src/PicoIni.Gen/IniSerializerGenerator.cs tests/PicoSerDe.Integration.Tests/NullLiteralTests.cs
git add -u PicoToml/src PicoYaml/src PicoIni/src tests/PicoSerDe.Integration.Tests/NullLiteralTests.cs
git commit -m "feat: map null literals to null for nullable members in TOML/YAML/INI"
```

---

## Phase E — 测试基线与文档

### Task 20: 替换自证式测试断言并补 culture/精度覆盖

**Files:**
- Modify: `PicoJetson/tests/PicoJetson.Unit.Tests/FuzzingTests.cs`（37、81-83）
- Modify: `PicoJetson/tests/PicoJetson.Unit.Tests/JsonSerializerTests.cs`（162-183）
- Modify: `PicoJetson/tests/PicoJetson.Integration.Tests/GeneratorHintNameTests.cs`（147-151）
- Modify: `PicoSerDe.Core/tests/PicoSerDe.Core.Tests/SerializerFacadeTests.cs`（56-67）
- Add: `PicoJetson/tests/PicoJetson.Unit.Tests/CultureTests.cs`

**Interfaces:**
- `FuzzingTests`：合法输入断言首 token 与 token 数；随机测试只对合法 JSON 断言往返值。
- 并发测试：`ConcurrentBag` 收集结果，`WhenAll` 后逐条断言 `Name == "Concurrent" && Age == 42`。
- `GeneratorHintNameTests`：断言 `CSharpGeneratorDriver.GetRunResult().GeneratedTrees` 的 hintName 集合。
- `SerializerFacadeTests`：用同时实现 `ISerializer<int>`/`IOptionsSerializer<int>` 的探针断言 options 到达。

- [ ] **Step 1: 写补强测试**

```csharp
namespace PicoJetson.Tests;

public class CultureTests
{
    [Test]
    public async Task Decimal_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = JsonSerializer.Serialize(new DecimalDto { V = 1.5m });
            await Assert.That(json).Contains("1.5");
            await Assert.That(JsonSerializer.Deserialize<DecimalDto>(Encoding.UTF8.GetBytes(json)).V)
                .IsEqualTo(1.5m);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    public class DecimalDto { public decimal V { get; set; } }
}
```

同时把 `FuzzingTests`、`JsonSerializerTests`、`GeneratorHintNameTests`、`SerializerFacadeTests` 的恒真断言改为值断言（按 **Interfaces**）。

- [ ] **Step 2: 跑测试并记录初始状态**

Run: `dotnet test PicoJetson/tests/PicoJetson.Unit.Tests -c Debug --no-progress`；`dotnet test PicoSerDe.Core/tests/PicoSerDe.Core.Tests -c Debug --no-progress`。
Expected: 逐条记录每个补强断言的初始状态（绿 = 补强，红 = 修复），并把**失败断言的消息原文**贴进任务记录；`Float_ShortestRoundTrip` 在初始实现下必须红，否则说明该断言没有守住精度契约，需重写。

- [ ] **Step 3: 使红的断言转绿**

只改使红断言通过所需的最小生产代码。逐条记录每个补强断言的初始状态（绿 = 补强，红 = 修复），写进提交信息；若全绿，在提交信息中写明“本任务无生产代码改动”。

- [ ] **Step 4: 确认绿灯**

Run: Step 2 的两个命令。Expected: 全绿。

- [ ] **Step 5: 提交**

```bash
dotnet csharpier format PicoJetson/tests PicoSerDe.Core/tests
git add -u PicoJetson/tests PicoSerDe.Core/tests
git commit -m "test: replace self-fulfilling assertions and cover culture/precision gaps"
```

---

### Task 21: 文档、null 语义矩阵与 API 基线

**Files:**
- Modify: `README.md`、`PicoJetson/README.md`、`PicoMsgPack/README.md`、`PicoToml/README.md`、`PicoYaml/README.md`、`PicoIni/README.md`
- Modify: `docs/bug-audit-report-2026-08-16.md`
- Regenerate: `api/*.public.txt`

**Interfaces:**
- README 增加“每格式 × null/缺省 × 写/读”矩阵，明确：JSON/MsgPack 默认写 `null`；TOML/YAML/INI 省略 null；三者的 `null`/`~`/空值 = 缺省 = `null`。
- 修正事实错误：`Serialize` 返回 `string`；MsgPack 有 `MsgPackOptions`；MsgPack 流式是缓冲实现。
- 测试数字以 Task 20 后一次全量运行的输出为准。

- [ ] **Step 1: 更新文档与基线**

1. 用一次全量测试的真实计数替换 README 的 1330/452 行数字。
2. 修正三处事实错误（见 Interfaces）。
3. 写 null 语义矩阵。
4. 重新 seed api 基线：`dotnet run --project scripts/api-surface -- dump <dll> --out api/<PackageId>.public.txt`（按 `picohex-release` 流程）。
5. 更新 `docs/bug-audit-report-2026-08-16.md` 的 BUG-05/BUG-11 状态。

- [ ] **Step 2: 校验**

Run: `dotnet test PicoSerDe.slnx -c Debug --no-progress` 记录总数；`git diff --stat` 检查只改了文档与 api 基线。
Expected: 计数与 README 一致；api 基线 diff 只反映本计划的公开 API 变化。

- [ ] **Step 3: 提交**

```bash
git add README.md */README.md api docs/bug-audit-report-2026-08-16.md
git commit -m "docs: document null semantics, fix counts and streaming claims"
```

---

## 收尾门禁（全部任务后）

- [ ] `dotnet build PicoSerDe.slnx -c Debug` → 0 warning / 0 error
- [ ] `dotnet build PicoSerDe.slnx -c Release` → 0 warning / 0 error
- [ ] `dotnet test PicoSerDe.slnx -c Debug --no-build --no-progress` → 全绿，记录新总数
- [ ] `dotnet publish PicoJetson/samples/PicoJetson.Sample -c Release -r win-x64 -p:PublishAot=true` → 成功并可运行
- [ ] `git status` → clean，`main` 未改动
- [ ] 用 `requesting-code-review` 对整分支做一次独立 review
