# 融合读取契约（fused token read）上提到 Core —— 设计规格

**状态**：Draft **v2**（含 **修订记录 §R1**）— 待签核
**日期**：2026-10-28（原文）/ 2026-10-29（**R1 机制修订**，依据 T1 实测）
**基线**：`40e5683`（原文）/ `610db24`（R1 修订时的 `main`）
**主题**：让「分类一个 token」与「取出它的值」在热路径上只发生**一次**，并让「头里带 count 的格式」按计数循环
**前置输入**：`.superpowers/sdd/memorypack-analysis/report.md`（下称 **MP 报告**）、`.superpowers/sdd/perf-mp-readspan/report.md`（下称 **4e 报告**）
**契约**：`picohex-picoserde`（AOT / 零反射 / fail-loud）+ `picohex-self-review`

> 本规格只写「要什么、为什么、什么不许做」。§3.1 给出**完整的接口签名与 XML 文档**（契约本身就是交付物的一部分），
> 但发射器怎么组织、测试落在哪个文件、每一步怎么验收，属于计划（`docs/superpowers/plans/`）。
> **本规格不含任何实现代码**：只有签名、参考语义的伪代码、发射规则与测试矩阵。
> **R1 修订后的机制一句话**：契约成员放在**派生接口** `IFusedTokenReader`（6 个抽象成员，`ref struct` 无法继承默认体），
> 参考语义放在 `PicoSerDe.Core.TokenReaderFused`（4 个 `ref T` 静态泛型方法）。原设计的 DIM 已被实测否决 —— 先读 **§R1**。

---

## 0. 结论摘要

**我们输的不是「接口派发」也不是「缺 SIMD」，而是同一个字节被 `switch` 了两次**：一次在 `Read()` 里分类，
一次在 `TryGetInt32()` 里解码（MP 报告 §6(a) ①②）。本规格把「分类 + 取值」合并成一个**契约成员**，
用**派生接口 `IFusedTokenReader : ITokenReader`（六个抽象成员）+ `PicoSerDe.Core` 里的静态参考正本**落地，
让每个格式在自己的 reader 里用一次分派完成它（原设计的默认接口实现 DIM 已被实测否决，见 **§R1**）；
同时把「头里有 count」这件事上提到契约，让生成代码能按计数循环而不是逐 token 探结束符。

---

## R1. 修订记录 R1（2026-10-29）：DIM 被实测否决，机制改为「派生接口 + 静态参考正本」

> 本节是规格的**修订依据**，不是讨论稿。每个「不能」后面都有错误码或运行输出；探针工程与复现方式见 **§R1.6**。

### R1.1 CS9245：`ref struct` 不能继承默认接口体（实测，已确证）

1. 按原 §3.1 把 6 个成员（带默认体）加进 `ITokenReader` 后，在基线 `610db24` 上 `dotnet build PicoSerDe.slnx -c Debug -m:1` 得到
   **30 个错误 / 0 个 warning**：6 成员 × 5 reader（`JsonReader` / `MsgPackReader` / `TomlReader` / `YamlReader` / `IniReader`），每条都是

   ```text
   error CS9245: 'ITokenReader.TryReadInt32(out int)' cannot implement interface member
                 'ITokenReader.TryReadInt32(out int)' for ref struct 'JsonReader'.
   ```

   证据：`.superpowers/sdd/t1-dim/report.md` §3.1（`build-cs9245.log`）。`PicoSerDe.Core` 单独构建是 0/0 ⇒ 失败点不是契约写错，是「`ref struct` 实现含 DIM 的接口」这个组合本身。
2. 语言规范（`dotnet/csharplang` `proposals/csharp-13.0/ref-struct-interfaces.md`）明文写着这条：
   `:131`「**To handle this a `ref struct` will be forced to implement all members of an interface, even if they have default implementations.**」；
   `:158`「A `ref struct` **cannot participate in default interface members**」；
   `:889`「API authors need to be aware that adding DIMs will break `ref struct` implementors until they are recompiled.」
3. 与语言版本无关：最小复现里 `LangVersion` 13 / 14 / `preview` 全部同样报错（`.NET SDK 10.0.401`，`.superpowers/sdd/t1-dim/report.md` §3.2 变体 A）；本轮在 SDK `10.0.401` + `LangVersion=latest` 上再次复现（`probe/05`，2 个成员 ⇒ 2 条 CS9245）。
4. 换成**抽象**成员（无默认体）不是出路：`probe/06` ⇒ `error CS0535: 'R' does not implement interface member 'ITokenReader.TryReadInt32(out int)'`（每个成员一条）。

### R1.2 为什么原设计不成立（三条，逐条对应被废掉的条文）

| 原条文 | 实测后果 |
|---|---|
| §3.1（原）「六个成员带默认实现 ⇒ 纯新增」 | 加不进去：30 个 `CS9245`（R1.1） |
| §4.1 **A-2**「外部实现者不破：编译零改动」 | 与上一条联合后**不可满足**：`ref struct` 实现者必须补全六个成员才能编译（规范 `:131`/`:889`） |
| §6 **S0**「§5.2 的自比对在全部覆写都还没写时仍 `IDENTICAL`」 | **不可执行**：没有覆写就编译不过；有覆写就走覆写体，而 DIM 体在 `ref struct` 上**永远不可达** ⇒ 「DIM 臂」这个参考侧**造不出来**，对拍没有第二个臂 |

⇒ 修订的不是措辞，是**机制**：契约承载物从「`ITokenReader` 上的默认成员」换成「**派生接口上的抽象成员** + `Core` 里的静态参考正本」。

### R1.3 选项 A（扩展方法）实测：**不可行**（五条独立证据）

假设：六个成员写成 `Core` 里的扩展方法，`ref struct` 一个成员都不实现也能调用。

| # | 形状 | 实测结果 |
|---|---|---|
| **A-1** | `public static bool TryReadInt32<T>(this T reader, out int v) where T : ITokenReader, allows ref struct` | **编译通过（0 error）但语义错**：扩展方法的接收者是**按值副本** ⇒ `Read()` 推进的是副本，**原 reader 一步没动**。`probe/01` 输出 `[Q1] ok=True value=7 PositionAfter=0 TokenType=None`，第二次调用仍读到 7（`[Q1b] ok=True value=7 PositionAfter=0`）⇒ `while (r.TryReadInt32(...))` 在真 reader 上**死循环**，`TokenType`/`_valueSpan` 也不会物化。**静默错**：没编译错误、没异常、没日志 |
| **A-2** | `this ref T reader`（把接收者按引用传，去修 A-1 的副本） | **`error CS8337`**：`The first parameter of a 'ref' extension method 'TryReadInt32' must be a value type or a generic type constrained to struct.` |
| **A-3** | 照 CS8337 的提示把约束换成 `where T : struct, ITokenReader` | **`error CS9244`**：`The type 'NumReader' may not be a ref struct or a type parameter allowing ref structs in order to use it as parameter 'T' in the generic type or method 'TokenReaderFusedStruct.TryReadInt32<T>(ref T, out int)'` ⇒ `struct` 与 `allows ref struct` 互斥，没有中间形态 |
| **A-4** | C# 14 扩展**成员块**：`extension<T>(ref T reader) where T : ITokenReader, allows ref struct` | **`error CS9300`**：`The 'ref' receiver parameter of an extension block must be a value type or a generic type constrained to struct.` ⇒ 扩展成员与经典扩展方法撞同一堵墙 |
| **A-5** | 具体类型的接收者：`extension(ref NumReader reader)` | **编译通过且真的推进**（`probe/07`：`(True,7) (True,8) PositionAfter=2`）—— 但那是**每个 reader 写一份**，正是 §7 方案 1（每格式一份、必然分叉），G2「一份实现」直接失效 |

**在泛型上下文里同样不成立（实测）**：把 A-1 的扩展方法放进 `static int Drain<T>(ref T reader) where T : ITokenReader, allows ref struct` 里调用 —— 这正是 §5.3 一致性测试与共享基础设施的形状 —— **编译通过（0 error）**，但 `probe/09` 的运行输出是 `[Q6-A] generic drain via extension: tokens=-1 PositionAfter=0`：`-1` 是 runaway 哨兵，即 `while (reader.TryReadInt32(...))` **永不结束**，泛型方法返回后原 reader 的位置仍是 0。⇒ 选项 A 不是「慢一点」，而是在契约要求的调用形状上**必然死循环**。

**顺带实测的「覆写优先级」**：`ref struct` 自己声明实例方法 `TryReadInt32(out int)` 时，调用处的**实例方法胜出**（`probe/01` `[Q2] ok=True value=100 PositionAfter=1`；若走扩展方法版本会返回 7）。即「实例方法优先」这条规则成立 —— 只是被它保护的扩展方法本身推不动状态，所以这条也救不了选项 A。在选项 B 下这个问题消失：成员**就是** reader 自己的实现，不涉及重载解析。

**对设计有用的正面结论**：**静态泛型方法 + `ref` 参数**（`static bool TryReadInt32<T>(ref T reader, out int value) where T : ITokenReader, allows ref struct`）没有任何限制，且能在 reader 内部以 `ref this` 调用 —— 这正是 R1.4 的参考正本。

### R1.4 选项 B（派生接口 + 静态参考正本）：**可行**（逐点实测）

```csharp
public interface IFusedTokenReader : ITokenReader { /* 6 abstract members */ }

public static class TokenReaderFused
{
    public static bool TryReadInt32<T>(ref T reader, out int value)
        where T : ITokenReader, allows ref struct { /* Read() && TryGetInt32() */ }
}
```

`probe/04` 逐点结果（`dotnet build`：**0 Warning / 0 Error**；`dotnet run` 原始输出见下）：

| 点 | 验证内容 | 输出 / 结论 |
|---|---|---|
| **B-1** | `ref struct BReader : IFusedTokenReader` 自己实现全部 6 个成员 | 编译通过（0 error） |
| **B-2** | 具体类型调用 + `ref this` 一行转发（`=> TokenReaderFused.TryReadInt64(ref this, out value)`） | `[B2] ok=True value=70 PositionAfter=1 fusedBodyRan=True`；`[B4] ok=True value=7 PositionAfter=1` ⇒ **推进的是真 reader**，转发通道成立 |
| **B-3** | 泛型见证 `where T : IFusedTokenReader, allows ref struct` 里调用 | `[B3] ok=True value=70 PositionAfter=1` ⇒ 派发到 reader **自己的**实现，witness 可用 |
| **B-5** | 只实现 `ITokenReader` 的 `ref struct`（= A-2 的外部实现者形状） | 编译通过、行为不变（`[B5] ok=True PositionAfter=1`） |

**新机制如何保住「参考实现可对拍」这个目标**（原设计想靠 DIM 做到的事）：

1. **语义正本只有一处**：`TokenReaderFused` 的四个静态 `ref` 方法，体 = §3.2.1 的字面组合。不优化的 reader 一律一行转发 ⇒「不优化」这件事**不可能**各自漂移。
2. **对拍的参考臂不复用正本**：walker 里内联写 `Read()` + `TryGetX()`（§3.2「正本管样板、内联管对拍」）⇒ 仍满足 §5.1「参考实现不许来自被测实现」。
3. **S0 起就有可跑的融合臂**（转发也是实现）⇒ §6 S0 的验收重新变得可执行（R1.2 第三行不再是空文）。
4. **生成器侧不受影响**：它调用的是**具体类型**的成员（§3.4.3），接口只存在于契约层。

### R1.5 换机制带来的改动清单（逐条落到本规格的哪一节）

| 项 | 原 | 新 | 落在 |
|---|---|---|---|
| 契约承载物 | `ITokenReader` + 6 个 DIM | 派生接口 `IFusedTokenReader` + 6 个抽象成员 | §3.1 |
| 参考语义 | DIM 默认体 | 静态正本 `TokenReaderFused`（`ref T` 泛型方法） | §3.2 |
| 兼容性裁决 | A-2「外部实现者零改动且行为正确」 | A-2「外部实现者零影响（编译 + 行为），但拿不到融合读；要用必须实现 6 个成员」 | §4.1 |
| 公共面 | 6 个成员加到既有接口 | 1 个新接口 + 1 个新静态类 | §4.2 R-3 |
| 样板成本 | 0（DIM 提供） | 5 reader × 6 成员 = 30 个成员体；不优化处 = 一行转发 | §3.1 末段、§8 RK-8 |
| 泛型站点可见性 | 所有 `where T : ITokenReader` 站点都能用 | 只有 `where T : IFusedTokenReader` 的站点能用 | §4.1、§5.3 |
| 生成器 | 不变（本来就调具体类型） | 不变，护栏扩成「两个接口名都不许出现」 | §3.4.3、§5.3 |
| 未证实项 | U4 =「DIM 在 dump 里打印 `virtual`」 | U4 改写为「新类型的 dump 行文未实测」；新增 U9（AOT 下的 witness 行为） | §9 |

### R1.6 探针工程与复现（放在 `.superpowers/` 下，gitignored，**不入库**）

| 目录（相对 `.superpowers/sdd/spec-rev1/`） | 验的是什么 | 结论 |
|---|---|---|
| `probe/01-classic-value-receiver` | 选项 A 的字面形状（按值接收者）+ 实例方法 vs 扩展方法 | 编译通过但**状态不推进**；实例方法**优先** |
| `probe/02-classic-ref-receiver` | `this ref T` | `CS8337` |
| `probe/02b-struct-constraint` | `where T : struct` 绕行 | `CS9244` |
| `probe/03-extension-block-ref` | C# 14 扩展成员块 + `ref` 接收者 | `CS9300` |
| `probe/04-option-b-derived-interface` | 选项 B 全形状（B-1/B-2/B-3/B-4/B-5） | **全部通过** |
| `probe/05-derived-interface-with-dim` | 派生接口 + DIM（想省样板） | `CS9245` ×2 |
| `probe/06-abstract-members-on-base` | 成员直接加在 `ITokenReader` 上（抽象） | `CS0535` ×2 |
| `probe/07-concrete-ref-receiver-block` | 具体类型的 `ref` 接收者扩展块 | 通过 ⇒ 每格式一份 = 方案 1 |
| `probe/08-readonly-member-ref-this` | `readonly` 成员里的 `ref this` 转发 | **`error CS1605`** ⇒ 融合读成员不能标 `readonly`（纯查询成员可以） |
| `probe/09-generic-context-extension` | 选项 A 在 `where T : ITokenReader, allows ref struct` 泛型上下文里的行为 | 编译通过，但 drain 循环 **runaway**（`tokens=-1 PositionAfter=0`）⇒ 死循环 |

复现：`cd <子目录> && dotnet build -c Debug`（失败项本身就是证据），语义项再 `dotnet run -c Debug`。
环境：`.NET SDK 10.0.401`、`net10.0`、`LangVersion=latest`（= C# 14）。每个子工程有独立的 `Directory.Build.props` / `NuGet.config`，与仓库构建设置隔离。

---

## 1. 问题

### 1.1 现状：两段式契约

`ITokenReader`（`PicoSerDe.Core/src/ITokenReader.cs:13-22`）把读一个标量拆成两步：

| 步 | 成员 | 做的事 | 证据 |
|---|---|---|---|
| ① 分类 | `bool Read()` | 巨型 `switch (b)`，物化 `_tokenType` + `_valueSpan`，维护 `_elementStack`/`_expectKeyStack` | `PicoMsgPack/src/PicoMsgPack/MsgPackReader.cs:365-598` |
| ② 取值 | `bool TryGetInt32(out int)` | **第二次** `switch`，按 `_tokenType` + `_valueSpan.Length` 再分派一次 | `MsgPackReader.cs:927-990` |

另有两笔每 token 的固定账：**③** `CountElement()` 的两次栈写（`MsgPackReader.cs:243-251`）；
**④** 「下一个是不是结束 token」判定（`ReadSpan()` 开头 `_elementStack[_depth-1] == 0`，`MsgPackReader.cs:375-390`）。

### 1.2 差距有多大：token 账本

`CollectionPoco`（`shared/BenchModels.cs:37-41`）的 MsgPack 字节流可**精确枚举**（MP 报告 §6(b)）：
`fixmap(2)` 1 + `"Scores"` 1 + `array16(100)` 1 + 100 个 fixint + `"Metadata"` 1 + `fixmap(1)` 1 + `"source"` 1 + `"benchmark"` 1 = **107 个 token**。
已发布的 JIT 中位数：我们 **1.432 µs** / 竞品 **0.731 µs**（`.superpowers/sdd/mp-remeasure/report.md:97`，三轮取中位；`:252` 复述）。于是：

| 量 | 值 | 来源 |
|---|---|---|
| 我们 | 1432 ns ÷ 107 ≈ **13.4 ns/token** | MP 报告 §6(b)（**算术推得，非实测**，MP §7 U1） |
| 竞品 | 731 ns ÷ 107 ≈ **6.8 ns/token** | 同上 |
| 每 token 差额 | ≈ 6.6 ns；×101 ≈ 0.67 µs ≈ 全部差距（0.70 µs） | MP 报告 §6(b) |
| 读侧状态机的绝对成本 | 同样 **21 个 token**：通用 tokenizer **329.1 ns**（≈15.7 ns/token）vs 同形状手写裸 span 解码 **27.8 ns**（≈1.3 ns/token），**12×** | 4e 报告 `:264-271` |

⇒ 差距是「**两段式 vs 一段式**」。4e 报告在测量后写下同一个判断：要拿到剩下的部分，「需要的是 P1-2/P1-3 或生成器直读……**不是再把 `Read()` 复制一遍**」（4e 报告 `:271-274`）。

### 1.3 它不是虚派发问题

生成代码**从不**把 reader 交给接口：它用**具体类型**接收（`ref JsonReader` ×8：`PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs:755,835,1000,1590,5242,5673,5839,6936`；`ref MsgPackReader` ×5：`PicoMsgPack/src/PicoMsgPack.Gen/MsgPackSerializerGenerator.cs:687,895,1034,1097,3685`），
而 `ITokenReader` 这五个字在五个 `.Gen` 工程里**一次都没出现**。所以：既没有「每 token 一次 interface dispatch」的成本可省，
也没有「改成接口调用就能去虚拟化」的收益 —— **收益只能来自减少分派次数，不能来自改变分派方式**。
这条同时是 §7 否决方案 3 的依据。

### 1.4 已有的融合 API，以及它们的四处不一致（缺陷）

仓库里**已经有三份**「一次读完一个数」的融合实现，**都没有被生成器使用**：`JsonReader.TryReadNextInt32`（`PicoJetson/src/PicoJetson/JsonReader.cs:656`，已在 `api/PicoJetson.public.txt:81`）、`TomlReader.TryReadNextInt32`（`PicoToml/src/PicoToml/TomlReader.cs:635`，`api/PicoToml.public.txt:44`）、`MsgPackReader.TryReadInt32Array`/`TryDecodeInt32`（`MsgPackReader.cs:1295-1336`/`:1240-1291`，`api/PicoMsgPack.public.txt:38`；MP 报告 §6(b)：`grep` 只命中定义本身）。它们**彼此语义不同**，且都**不等价于 `Read()` + `TryGetX()`**。四个可复现的缺陷：

| ID | 缺陷 | 证据 |
|---|---|---|
| **F-1** | **seq 模式丢符号**：`TryReadNextInt32Seq` 把 `-` `Advance(1)` 掉却不记负数 ⇒ `[-2]` 流式模式返回 **2**。span 路径有 `bool neg`，seq 路径没有 —— 同一文件内分叉 | `JsonReader.cs:789-790` vs `:743-747` |
| **F-2** | **TOML 的同一成员更严格**：干脆不处理 `-`，负数直接 `false`（`Utf8Parser` 收到空串）⇒ 「同一个 API 名，两个格式两种结果」 | `TomlReader.cs:686-711` |
| **F-3** | **不物化状态**：`TryReadNextInt32` 只改 `_position`，不写 `_tokenType`/`_valueSpan`/`_tokenValueStart/End` ⇒ 融合读后 `TokenType` 是**陈旧值**，与 `Read()` 后可观测状态不同 | `JsonReader.cs:725-768`（全文无 `_tokenType` 赋值） |
| **F-4** | **结束符处理相反**：`TryReadNextInt32` 遇 `]`/`}` **不动**（`:737-742`）；`TryReadInt32ArrayFast` 遇 `]` 会 `p++` **吃掉**（`:1978-1981`）；`MsgPack.TryReadInt32Array` 读自己的 header、根本没有结束符（`:1300-1315`） | 三处互不一致 |

**F-1/F-2 今天没有任何测试能抓住**：现有两条用例全是正数、全是 span 模式（`PicoJetson/tests/PicoJetson.Unit.Tests/JsonReaderTests.cs:527-568`）⇒ §5 的硬门槛形态由此确定：**等价性必须由对拍证明，不能由「看起来很融合」证明。**

---

## 2. 目标 / 非目标

### 2.1 目标

| # | 目标 | 判据 |
|---|---|---|
| G1 | 「分类 + 取值」在一个 token 上**只分派一次**：一次 `switch` 同时算出 `TokenType`、`_valueSpan` 与值 | 覆写实现里不再出现第二次按 `_tokenType`/`_valueSpan.Length` 的分派 |
| G2 | 让「分类 + 取值」**可被契约表达**，任何格式**一份**实现即可被共享代码与生成代码使用 | 契约接口 `IFusedTokenReader`（§3.1）+ 参考语义正本 `TokenReaderFused`（§3.2） |
| G3 | 让「头里有 count 的格式」按**计数**循环，而不是逐 token 探结束符 | `HasCountedCollections`/`TryGetCollectionCount`（§3.1）+ 发射规则（§3.4） |
| G4 | 融合路径与「`Read()` + `TryGetX()`」**语义完全等价**（含失败、`NeedsMoreData`、`TokenType`、`Depth`、`BytesConsumed`、异常类型与消息） | §5 的硬门槛 |
| G5 | 纯新增、不破外部实现者 | §4 |

### 2.2 非目标（明确**不做**）

| # | 非目标 | 理由 |
|---|---|---|
| N1 | **不改任何 wire 格式** | 已发布，且 MsgPack 要与 MessagePack-CSharp 互通（MP 报告 §5.3 N1） |
| N2 | **不改 `Read()` 的对外语义** | `Skip()`/`TokenType`/`BytesConsumed`/`IsResumed`/`ExportState` 全部依赖它今天的形状 |
| N3 | **不做 MemoryPack 那种「无 tag 定长格式」** | 那是**新格式**（改 wire、失去自描述与跨语言，MP 报告 §4.1），不是本规格 |
| N4 | **不改流式（sequence）委托的形态** | 流式走 `Mark`/`RewindToMark` 与分块重解析（`ITransactionalTokenReader.cs:3-20`）；融合只做 span 快路径，seq 保持参考语义（§3.3：seq 分支一行转发到参考正本） |
| N5 | **不在 reader 里加「计数作用域」去省掉 ③④** | 会改 `_elementStack` 记账节奏、进而改 `Skip()`/`TokenType` 的可观测行为；见 §7 否决方案 5，留待独立规格 |
| N6 | **不承诺任何性能数字** | 本规格不测性能；估算见 §9 |

---

## 3. 设计

### 3.1 契约：派生接口 `IFusedTokenReader`（六个**抽象**成员）

**形状**：`ITokenReader` 一个字节不动；新派生接口继承它，六个成员**全部抽象**（没有默认体）。

**为什么不能把成员放在 `ITokenReader` 上（实测，不是偏好）**：`ref struct` 必须自己实现它实现的**每一个**接口的**每一个**成员 —— 带默认实现的成员也不例外（C# 13 起，`proposals/csharp-13.0/ref-struct-interfaces.md:131`）。所以往 `ITokenReader` 加成员只有两种加法，两种都破：

| 加法 | 五个 reader 的结果（本仓库实测） |
|---|---|
| 带默认体（DIM） | 每个 reader × 每个成员 **`error CS9245`**：`'ITokenReader.TryReadInt32(out int)' cannot implement interface member ... for ref struct 'JsonReader'`。6 成员 × 5 reader = **30 个错误**（基线 `610db24`，`.superpowers/sdd/t1-dim/report.md` §3.1） |
| 抽象（无默认体） | 每个 reader × 每个成员 **`error CS0535`**：`'R' does not implement interface member 'ITokenReader.TryReadInt32(out int)'`（本轮独立复现，`probe/06-abstract-members-on-base`） |

⇒ 成员放进**派生接口**：只实现 `ITokenReader` 的既有实现者（含第三方）**编译与行为零影响**（§4.1 **A-2** 保住）；想拿到融合读的实现者实现派生接口即可。完整实测见 **§R1**。

**放置**：与 `ITokenReader` 同一个文件（`PicoSerDe.Core/src/ITokenReader.cs`，`ITokenReader` 之后）。`PicoSerDe.Core` 目标框架 `net10.0`（`PicoSerDe.Core/src/PicoSerDe.Core.csproj`），不引入任何新依赖。

```csharp
namespace PicoSerDe.Core;

/// <summary>
/// The fused-read capability of an <see cref="ITokenReader"/>: "classify a token" and "decode its
/// value" happen in <b>one</b> dispatch.
/// <para>
/// <b>Why a separate interface instead of new members on <see cref="ITokenReader"/></b>: a
/// <see langword="ref struct"/> must implement every member of every interface it implements,
/// default interface members included (<c>CS9245</c>). Adding these members to
/// <see cref="ITokenReader"/> would break every ref-struct implementer, in tree and out of tree.
/// Implement this interface to opt in; implementers of <see cref="ITokenReader"/> alone are
/// unaffected.
/// </para>
/// <para>
/// <b>There are no default bodies.</b> A <see langword="ref struct"/> cannot inherit one, so every
/// implementer writes all six members. A format that does not optimize a member MUST implement
/// exactly the reference semantics of §3.2 — forward to <see cref="TokenReaderFused"/>, do not
/// re-derive the combination.
/// </para>
/// </summary>
public interface IFusedTokenReader : ITokenReader
{
    /// <summary>
    /// True when this reader can report the element count of a collection from the token
    /// <see cref="ITokenReader.Read"/> just produced — i.e. the wire format carries the count in the
    /// collection header (MessagePack does; JSON/TOML/YAML/INI do not).
    /// <para>
    /// A capability declaration, not a hint: when it is <see langword="false"/>,
    /// <see cref="TryGetCollectionCount"/> MUST return <see langword="false"/> for every token,
    /// and consumers must terminate the element loop with <see cref="ITokenReader.Read"/> plus the
    /// end token. A reader answering <see langword="true"/> MUST answer
    /// <see cref="TryGetCollectionCount"/> with <see langword="true"/> right after every
    /// <see cref="TokenType.ArrayStart"/>/<see cref="TokenType.ObjectStart"/> it produced.
    /// </para>
    /// </summary>
    bool HasCountedCollections { get; }

    /// <summary>
    /// Reports, without advancing, how many element iterations the current collection will still
    /// yield before <see cref="ITokenReader.Read"/> produces the matching
    /// <see cref="TokenType.ArrayEnd"/>/<see cref="TokenType.ObjectEnd"/>.
    /// <para>
    /// <b>Contract</b>: a map/object level counts <b>key-value pairs</b>, not tokens, so
    /// <c>for (i = 0; i &lt; count; i++)</c> is exactly one iteration per pair. The number is the same
    /// number the reader uses for its own end-token decision, so a counted loop and the plain
    /// <see cref="ITokenReader.Read"/>-driven loop issue identical token sequences (trailing end
    /// token included).
    /// <b>Never changes observable state</b>: no position move, no <see cref="ITokenReader.TokenType"/>/
    /// <see cref="ITokenReader.Depth"/>/<see cref="ITokenReader.NeedsMoreData"/> change, no span
    /// invalidation — a query in the <c>TryGetInt32</c> family, not a read. Returns
    /// <see langword="false"/> (with <paramref name="count"/> = 0) when the count is unknown — the
    /// format carries none, no collection token was produced yet, or the reader is in a mode it does
    /// not serve; callers then fall back to the <see cref="ITokenReader.Read"/>-driven loop.
    /// </para>
    /// </summary>
    bool TryGetCollectionCount(out int count);

    /// <summary>
    /// Advances exactly one token and, when that token decodes as an <see cref="int"/>, returns it —
    /// <b>equivalent to</b> <c>Read() &amp;&amp; TryGetInt32(out value)</c>, including every observable
    /// side effect (that literal combination is <see cref="TokenReaderFused.TryReadInt32{T}"/>).
    /// <b>Failure is lossless</b>: the token <see cref="ITokenReader.Read"/> produced stays current,
    /// so a caller seeing <see langword="false"/> can still inspect
    /// <see cref="ITokenReader.TokenType"/> and use the <c>TryGet*</c> family or
    /// <see cref="ITokenReader.TrySkip"/> on it. When <see cref="ITokenReader.Read"/> itself fails,
    /// <see cref="ITokenReader.NeedsMoreData"/> reports a chunk boundary exactly as there.
    /// </summary>
    bool TryReadInt32(out int value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="long"/>.</summary>
    bool TryReadInt64(out long value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="double"/>.</summary>
    bool TryReadFloat64(out double value);

    /// <summary>See <see cref="TryReadInt32"/>; identical contract for <see cref="bool"/>.</summary>
    bool TryReadBool(out bool value);
}
```

**数量：6 个成员**（`HasCountedCollections`、`TryGetCollectionCount`、`TryReadInt32`、`TryReadInt64`、`TryReadFloat64`、`TryReadBool`）。

**样板成本（实测得出，不是估计）**：五个内置 reader × 6 个成员 = **30 个方法/属性体**，省不掉（默认体会 CS9245，§R1.1）。其中「本格式不做优化」的成员一律写成**一行转发**到参考正本：

```csharp
// 纯查询成员：不改状态 ⇒ 可以且应当标 readonly
public readonly bool HasCountedCollections => false;
public readonly bool TryGetCollectionCount(out int count) { count = 0; return false; }

// 融合读成员：会推进 reader ⇒ 绝对不能标 readonly（也不该：它就是要改状态）
public bool TryReadInt32(out int value) => TokenReaderFused.TryReadInt32(ref this, out value);
```

`ref this` 这个调用形状已实测可编译（§R1.4 的 B-2），这正是参考正本写成静态方法而不是扩展方法的原因（§3.2）。
**但转发成员不能标 `readonly`**：`readonly` 成员里 `this` 是只读变量，`ref this` 直接编译失败 **`error CS1605: Cannot use 'this' as a ref or out value because it is read-only`**（实测，§R1.6 的 `probe/08`）。`HasCountedCollections` / `TryGetCollectionCount` 是两个纯查询，标 `readonly` 没问题（spec §3.1 的「零状态变化」）。

#### 3.1.1 命名与形态的裁定（每条都给理由）

| 决定 | 选择 | 理由 |
|---|---|---|
| 融合读叫 `TryRead*` 而非 `TryGet*` | `TryReadInt32` | 契约里已有隐含规则：**`Get` 不前进，`Read`/`Skip` 前进**（`TryGetInt32` 与 `TrySkip` 就是这一对，`ITokenReader.cs:18-22`）。融合读会前进 ⇒ 必须落在 `Read` 族 |
| 计数查询叫 `TryGetCollectionCount` 而非 `TryReadCollectionHeader` | `TryGetCollectionCount` | ① 它是**查询**（不前进、不产生 token），按上一条不能叫 `Read`；② MemoryPack 的 `TryReadCollectionHeader`（MP 报告 §3.2 C11 引 `MemoryPackReader.cs:283-307`）是**消费**语义，它读掉 4 字节头，而我们的头**已经在 `Read()` 里读掉了**（`MsgPackReader.cs:406-413`、`:578-596`）——沿用那个名字会引入错误联想 |
| 成员放在哪 | 放进**派生接口** `IFusedTokenReader`（不放 `ITokenReader`） | 两条实测硬约束：带默认体 ⇒ `CS9245`，抽象 ⇒ `CS0535`，两种加法都会让五个内置 reader 全部编译失败（§R1.1）；派生接口把破坏面收到「想用融合读的人」身上（§4.1 **A-2**） |
| 能力声明 | 实例属性 `HasCountedCollections`（抽象，各 reader 自写） | ① 必须是**实例**成员：`static abstract` 接口成员要求实现者给出静态成员且无法带默认值 ⇒ 与上一条同样的破坏面；② 必须**能被泛型消费者问到**：断言形式是 `where TReader : IFusedTokenReader, allows ref struct`（口径同 `tests/PicoSerDe.Integration.Tests/TokenReaderConformanceTests.cs:37`，把约束换成派生接口即可） |
| 为什么不能只留 `TryGetCollectionCount` | 两个都要 | 生成器**不需要**它（格式在生成期已知，§3.4），但**契约需要自我描述**：否则「永远返回 `false`」与「格式本来没有 count」无法区分，测试也无法把两者钉成互不漂移的一对（§5 的 G3 行）。这是它进契约（`IFusedTokenReader`）而不是留在生成器侧的唯一理由 |

### 3.2 参考语义正本：`TokenReaderFused` 静态 `ref` 方法

**规范 = 字面组合** `Read() && TryGetInt32(out value)`。这句话有两处落地，两处都是同一句字面组合的两种书写，但**不许合并成一处**：

| 落地 | 是什么 | 谁用 |
|---|---|---|
| **`PicoSerDe.Core.TokenReaderFused`**（新增，公开静态类，四个成员） | 融合读的**样板正本**：`public static bool TryReadInt32<T>(ref T reader, out int value) where T : ITokenReader, allows ref struct`，体 = §3.2.1 的伪代码 | ① 不做优化的 reader 一行转发；② 审阅语义只需读这一处 |
| **§5.2 对拍的参考臂** | 转储 walker 里**内联**写成的 `Read()` + `TryGetX()`（今天的 `DrainSpan()` 形状） | 等价性证明的参考侧 |

**为什么参考臂要内联、不复用正本**：对拍的意义是「参考实现**不来自被测实现**」（§5.1）。正本是所有 reader 的实现来源，若对拍也拿它当参考，被测与参考就同源了。所以：**正本管样板，内联管对拍**。

**为什么正本必须是静态方法而不是扩展方法（实测，这是本修订的关键一条）**：扩展方法的接收者无法按引用传给「允许 ref struct 的类型参数」，四种形状全试过：

| 形状 | 实测结果（`.NET SDK 10.0.401`，`LangVersion=latest`） |
|---|---|
| `this T reader, where T : ITokenReader, allows ref struct` | **编译通过但语义错**：接收者是**副本** ⇒ `Read()` 推进的是副本，**原 reader 位置不动**、`TokenType` 不物化；`while (TryReadInt32(...))` 在真 reader 上**无限循环**（§R1.3 的 Q1/Q1b；泛型上下文同样 runaway，§R1.3 末段 / `probe/09`） |
| `this ref T reader` | **`error CS8337`**：`The first parameter of a 'ref' extension method 'TryReadInt32' must be a value type or a generic type constrained to struct.` |
| 同上但用 `where T : struct, ITokenReader` 代替 `allows ref struct` | **`error CS9244`**：`The type 'NumReader' may not be a ref struct ... in order to use it as parameter 'T'` |
| C# 14 扩展成员 `extension<T>(ref T reader) where T : ITokenReader, allows ref struct` | **`error CS9300`**：`The 'ref' receiver parameter of an extension block must be a value type or a generic type constrained to struct.` |
| **静态 `ref T` 参数（本方案）** | **编译通过且推进真正的 reader**；`ref this` 调用形状同样通过（§R1.4 的 B-2/B-4） |

⇒ 融合读的样板只能是**静态方法 + `ref` 参数**：调用处写 `TokenReaderFused.TryReadInt32(ref this, out value)`，不是 `this.TryReadInt32(...)`。

#### 3.2.1 伪代码（= `TokenReaderFused` 的体）

```text
TryGetCollectionCount:  该格式没有 ⇒ 由 reader 自己写成 count = 0; return false   // 无知识 = 恒定回退，见 3.2.2 (e)

TryReadInt32(ref reader, out value):   // TryReadInt64 / TryReadFloat64 / TryReadBool 同形
    if not reader.Read():              // ① 前进失败（输入尾 or 需要更多数据）
        value = 0; return false
    if reader.TryGetInt32(out value):  // ② 成功：状态 == Read() 之后 TryGetInt32 成功
        return true
    value = 0; return false            // ③ 失败：token 仍为当前 token
```

#### 3.2.2 与直读路径的语义等价论证（逐项）

设参考序列 `R` = `Read(); TryGetInt32(out v)`，实现序列 `I` = `TryReadInt32(out v)`（`I` 是某个 reader 的实现体；对「不做优化」的格式，`I` 就是 `R` 的一行转发）：

| 项 | `R` | `I` | 结论 |
|---|---|---|---|
| **(a) 前进次数** | 恰好 1 次 `Read()`；`Read()` 失败时 0 次 | 同一处 `Read()` 调用 | 相同 |
| **(b) `Read()` 失败时** | 返回 false；`NeedsMoreData` 由 `Read()` 设置（非 final 块的块边界） | 直接 `return false`，**不触碰** `_needsMoreData` | 相同（唯一写者在 `Read()` 里：`MsgPackReader.cs:364,385`、`JsonReader.cs:63-65`） |
| **(c) `TokenType` 状态** | `Read()` 设成新 token；`TryGetInt32` 失败时**不回滚** | 同 | 相同（F-3 正是覆写必须避免的偏离） |
| **(d) depth 变化** | `Read()` 内 `PushLevel`/弹出会计；`TryGetInt32` 不动 `_depth` | 同 | 相同（深度异常 `MsgPackReader.cs:235-236` 因此照样触发） |
| **(e) 计数查询** | 无对应 | `false` + `count = 0`，**零状态变化** | 「拒绝声明」恒等价：消费者退回 `Read()` 循环，行为与今天逐字节相同 |
| **(f) `_valueSpan` / getter 可见性** | `Read()` 物化，后续 `TryGetFloat64`/`TryGetBool`/`GetStringRaw` 可读 | 同 | 相同 —— 也是 §5 要求「转储必须比对**全部 getter**」的原因 |
| **(g) `BytesConsumed` / `TokenStart`** | `Read()` 推进 | 同 | 相同 |
| **(h) 异常** | `Read()` 可能抛（未知字节、深度上限、尾随垃圾） | 同一 `Read()` ⇒ 同类型同消息 | 相同（`MsgPackReader.cs:609-616`） |

**结论**：`I ≡ R` 对**所有**输入成立（含全部失败分支）—— 当 `I` 是正本的一行转发时，两者是同一份代码，没有第二条路径可以漂移；当 `I` 是 §3.3 的优化实现时，等价性必须由 §5.2 的对拍**证明**，不许由「看起来很融合」假定。

#### 3.2.3 实现必须等于正本，而不是「差不多」

实现的自由度**只允许落在「少做几次内部分派」**：任何能被外部观测到的差异（token 类型、span 内容、`Depth`、`BytesConsumed`、`NeedsMoreData`、异常类型与消息、getter 结果）都不许变。**特别地：融合 ≠ 不物化状态。** 优化实现仍必须写 `_tokenType` 与 `_valueSpan`（及 `_tokenValueStart/End`），否则 §5 的「全部 getter」一列立刻红。这条限定了收益上界：省掉的是 ① 之后那次**第二轮分派**，不是 ① 的状态写入。

### 3.3 各格式的覆写点

| 格式 | `HasCountedCollections` | `TryGetCollectionCount` | 四个融合读 | 依据 |
|---|---|---|---|---|
| **MsgPack** | **`true`** | 覆写：`_isMapStack[_depth-1] ? _elementStack[_depth-1] / 2 : _elementStack[_depth-1]`；`_depth == 0` 或当前 token 不是 Start ⇒ `false` | **四个融合读全部覆写**（span 模式一次 `switch`；seq 分支一行转发参考正本） | 头里带 count：fixmap/fixarray `MsgPackReader.cs:406-413`，array16/32、map16/32 `:578-596`；map 的两倍记账见 `PushLevel(true, n*2)` 与 `:243-251` |
| **JSON** | `false`（写死） | 不覆写（`count = 0; return false;`） | **`TryReadInt32` 覆写，其余一行转发参考正本**；先修 F-1/F-3 | 无 count：`]` 是**扫字节**得出的（`JsonReader.cs:496-499`），reader 里没有任何元素计数 |
| **TOML** | `false`（写死） | 不覆写 | 按需（排第三：不做优化的一律一行转发） | 有 ArrayStart/End（`TomlReader.cs:1285,1319`）但**无 count**；`TryReadNextInt32` 已存在（`:635`）且需按 F-2 重做 |
| **YAML** | `false`（写死） | 不覆写 | **不覆写：四个融合读全部一行转发** | **没有集合 token**：`YamlReader` 全文无 `TokenType.ArrayStart` 赋值（只有 `Skip()` 的判断 `:713-720`）；生成器注释写明「YamlReader has no ArrayStart/ArrayEnd token: a sequence is one String token per item line」（`PicoYaml/src/PicoYaml.Gen/YamlSerializerGenerator.cs:4020`）⇒ 计数语义与计数循环都无处落地 |
| **INI** | `false`（写死） | 不覆写 | **不覆写：四个融合读全部一行转发** | 全平（无容器 token，`IniReader.cs` 对 `TokenType.ArrayStart` 零命中）、值恒为 `String` 类 token ⇒ 本来就没有第二段可分派 |

> 本节说「覆写／不覆写」都是**相对参考正本**而言："不覆写" = 成员体就是**一行转发**到 `TokenReaderFused`（§3.1 末段），
> **不是**「不实现」—— `ref struct` 拿不到默认体，六个成员人人都得写（§R1.1）。

**MsgPack 覆写要点（伪代码级约束，不是实现）**

```text
TryReadInt32(out value):
    if _isSequence:  return TokenReaderFused.TryReadInt32(ref this, out value)   // seq 单源，见 N4
    if _depth > 0 and _elementStack[_depth-1] == 0:                   // ④ 结束 token
        return TokenReaderFused.TryReadInt32(ref this, out value)      // 结束/尾随垃圾的路径不优化
    if _position >= _data.Length:
        _needsMoreData = not _isFinalBlock; value = 0; return false    // 同 Read()
    switch _data[_position]:                                          // 一次分派
        int 家族（fixint / negfixint / 0xCC..0xD3）:
            decode, _position += len                        // 解码与前进同时完成
            物化 _tokenType / _singleByte / _valueSpan       // 必须，见 3.2.3
            CountElement()                                  // 必须：栈必须与参考路径一致
            value = decoded; return true
        default:  return TokenReaderFused.TryReadInt32(ref this, out value)   // 非 int ⇒ 逐字退回参考路径
```

`default:` 这一支是**等价性的关键**：非 int token 不做任何特殊处理，就地退回参考实现
⇒ 覆写与参考只可能在**成功分支**分叉，§5 的对拍只需证明成功分支与参考一致。

### 3.4 生成器侧：发射规则

#### 3.4.1 规则

| 规则 | 形态 | 适用 |
|---|---|---|
| **E-1 计数循环** | `reader.Read()` 得到 Start 后：`for (var __i = 0; __i < __n; __i++) { <元素> }`，再**显式一次** `reader.Read()` 消费结束 token | `HasCountedCollections` 为真的格式（今天只有 MsgPack） |
| **E-2 结束符循环**（现状，保留） | `while (reader.Read() && reader.TokenType != TokenType.ArrayEnd) { <元素> }` | 其余格式 |
| **E-3 融合元素读** | 元素体里把 `Read()` + `TryGetX()` 换成 `TryReadX()`；失败时 E-1 下发 `throw`（计数保证元素存在），E-2 下先判 `TokenType` 是否结束、否则 throw | 元素是标量的场合 |

**E-1 为什么必须保留那次显式 `Read()`**：`_elementStack` 的记账是 `Read()` 的职责（`CountElement()`），
读完 `count` 个元素后 `_elementStack[level] == 0`，**下一个 `Read()` 才产出结束 token**（`MsgPackReader.cs:375-380`）。
现状的 `while` 形式在退出条件里恰好也做了这一次 `Read()` ⇒ **E-1 与 E-2 发出的 `Read()` 次数完全一致**（`count` + 1 次），
因此 **token 序列逐字节相同**，§5 的对拍才有意义；也正因为一致，E-1 单独能省的只有「每元素的 `_tokenType` 比较」，
真正的收益来自 E-3（§9 U5）。

**E-3 的失败判据不对称，这是有意的**：计数循环里「元素一定存在」是 wire 的承诺 ⇒ `TryReadInt32` 返回 `false`
只能是类型不符 ⇒ 立即 `throw`（与今天 `TryGetInt32` 失败后抛的形态一致）；无计数格式里 `false` 还可能是「到 `]` 了」
⇒ 必须先看 `TokenType`。

#### 3.4.2 发射逻辑放共享源文件：可行性与代价

**可行，且已有先例。** 五个 `.Gen` 工程都把同一批文件 `<Compile Include>` 进来（`shared/Gen/FormatCapabilities.cs`、`DecisionTables.cs`、`GenInfrastructure.cs`、`TypeKindResolver.cs` 等；见 `PicoJetson/src/PicoJetson.Gen/PicoJetson.Gen.csproj` 的 `ItemGroup`，`PicoMsgPack.Gen` 同形）。`FormatCapabilities.cs:24-30` 的头注释写下两条纪律：**五处共用 ⇒ 必须 `internal`**，因为 `api/*.public.txt` 不得变化（spec D5）。新发射器照抄这两条即可。

**代价（必须写清，不能只说可行）**：

| # | 代价 | 证据 |
|---|---|---|
| C-1 | **reader 变量名不统一**：JSON/MsgPack/INI 生成 `reader`，TOML 生成 `r`，YAML 两种都有 ⇒ 共享发射器必须把变量名与 reader 类型名当**参数**传入，不能像 `GenInfrastructure.EmitCollectionFromList`（`GenInfrastructure.cs:1243`）那样只收 `kind/listVar/assignTarget` | `JsonSerializerGenerator.cs:1638`、`TomlSerializerGenerator.cs:1632`、`IniSerializerGenerator.cs:851`、`YamlSerializerGenerator.cs:2150` |
| C-2 | **发射点分散**：`TokenType.ArrayEnd` 今天在三个生成器里共 **24 处**（JSON 10：`JsonSerializerGenerator.cs:1638,3162,3678,3690,4543,5144,6176,6207,6542,6970`；MsgPack 10：`MsgPackSerializerGenerator.cs:626,733,1107,1391,2929,2973,3005,3108,3462,3517`；TOML 4：`TomlSerializerGenerator.cs:1632,2150,2834,3555`）。共享发射器只覆盖**形状相同**的那部分；带 `TablePath` 判定的 TOML 站点（`:1617`）与字典的 `PropertyName` 循环必须留在原处 | 同上 |
| C-3 | **netstandard2.0**：`.Gen` 工程目标框架是 `netstandard2.0`（`PicoJetson.Gen.csproj`），共享文件不能引入运行期依赖 | 同上 |
| C-4 | **golden 基线全量重生成**：仓库纪律是「生成代码基线必须由**生成器重新生成**，不手工合并」（`docs/superpowers/specs/2026-10-28-perf-round-summary.md:§9`）⇒ 任何发射改动都会让所有含集合的 golden 一起变 | 同上 |

#### 3.4.3 生成代码必须调用**具体类型**

E-1/E-3 的调用点写成 `reader.TryReadInt32(...)`，其中 `reader` 是**具体类型**的局部（`ref MsgPackReader` / `ref JsonReader`，§1.3）。理由：

1. 现状就是具体类型，且 `ITokenReader` 在五个 `.Gen` 工程里零出现（§1.3）——保住这个优势：发射产物里**两个接口名都不许出现**（`ITokenReader` 与 `IFusedTokenReader`）；
2. 接口调用会把「一次分派」变成「一次派发 + 一次成员体」；`allows ref struct` 下的去虚拟化只有**手写**代码的实测先例（§7 方案 3 ③），不是生成代码的担保 ⇒ 与 G1 直接冲突，且不拿未证实的东西当前提。

**护栏**：这一条必须由测试钉住 —— 断言**生成产物里不出现 `ITokenReader` 字样**（结构性 grep 断言，独立于被测实现）。

---

## 4. 兼容性与 API 裁决

**事实**：`ITokenReader` 是**已发布公共接口**（`api/PicoSerDe.Core.public.txt:58-64` 成员、`:139-141` 属性、`:182` 类型行）；
`PicoSerDe.Core` 目标框架 `net10.0`（`PicoSerDe.Core/src/PicoSerDe.Core.csproj`）；`IFusedTokenReader` 与 `TokenReaderFused` 都是**新增**公共类型，不动任何既有签名。
**关键实测前提**：`ref struct` 不能继承默认接口体（`CS9245`），所以「往 `ITokenReader` 加带默认体的成员」这条纯新增路线在本仓库**不存在**（§R1.1）。

### 4.1 裁决

| # | 事项 | 裁决 | 依据 |
|---|---|---|---|
| **A-1** | 六个新成员怎么加 | **不动 `ITokenReader`**；新增派生接口 `IFusedTokenReader`（6 个抽象成员）+ 静态正本 `TokenReaderFused`（4 个 `ref T` 泛型方法）。五个 reader 各自实现 6 个成员（不优化的写一行转发） | 下段 + §7 方案 4（原否决已撤销）+ §R1 |
| **A-2** | 外部实现者（第三方 `ref struct`/`class` 实现 `ITokenReader`） | **不破**：编译零改动、行为零变化 —— 因为它根本没有新成员可漏（`ITokenReader` 一个字节没动；`probe/04` 的 B-5 实测：只实现旧接口的 `ref struct` 照常编译）。**代价**：这类实现者**拿不到**融合读，除非自己实现 `IFusedTokenReader` 的 6 个成员（无默认体可省，§R1.1） | §3.1 + §R1.5 |
| **A-3** | 版本轴 | **`x+1`**（公共 API 变化含纯新增） | `docs/superpowers/specs/2026-10-25-format-capabilities-design.md:§8` 三根轴 |
| **A-4** | `api/*.public.txt` | **一个字节不改**；基线记录「上次发布的面」，手改等于伪造发版 diff | `api/PENDING-CHANGES.md:3-7` |
| **A-5** | 登记形态 | §4.2 的 **R-3** 一行；F-1/F-2 的行为修正按 §4.3 登记 | 同上 |

**为什么是派生接口而不是往 `ITokenReader` 加成员**：原设计的理由是「DIM 能让外部实现者零改动」——**这条已被实测推翻**：DIM 加在 `ITokenReader` 上会让本仓库五个 reader 产生 30 个 `CS9245`，加抽象成员则产生 `CS0535`，两条路都走不通（§R1.1）。派生接口是唯一同时满足「五个 reader 能编译」与「外部实现者不破」的形状：
① 只约束 `ITokenReader` 的既有泛型站点（`TokenReaderConformanceTests.cs:37,53,78` 等）**照旧编译、照旧工作**，只是看不见融合读；需要融合读的站点把约束改成 `IFusedTokenReader`（本规格的消费者只有两处：§3.4 的生成器走具体类型，§5.3 的一致性测试走派生接口见证）；
② 五个 reader 都是我们的，6 个成员由我们实现，样板 = 不优化处一行转发（§3.1 末段）；
③ 融合读的**语义**仍只有一份（`TokenReaderFused` 正本），不会退化成 §7 方案 1 的「每格式一份、互相分叉」。

### 4.2 API 台账条目（**R-3**，照 `api/PENDING-CHANGES.md:11-16` 的表格格式）

| # | Package | Addition | Ruled | Why it is worth public surface |
|---|---|---|---|---|
| 5 | PicoSerDe.Core | `T:PicoSerDe.Core.IFusedTokenReader`（+ 6 个成员：`P:PicoSerDe.Core.IFusedTokenReader.HasCountedCollections`、`M:…TryGetCollectionCount(out int count)`、`M:…TryReadInt32(out int value)`、`M:…TryReadInt64(out long value)`、`M:…TryReadFloat64(out double value)`、`M:…TryReadBool(out bool value)`） + `T:PicoSerDe.Core.TokenReaderFused`（+ 4 个静态泛型方法，`ref` 首参） | **R-3** | 这四个融合读 + 计数查询是「同一个字节只分类一次」的唯一可表达形态：今天生成代码必须 `Read()` 再 `TryGetInt32()`，同一个 tag 被 `switch` 两次（`MsgPackReader.cs:365-598` 与 `:927-990`）。没有它，五个格式只能各写一份融合实现（今天已有三份互不一致的副本，§1.4 F-1…F-4）。`HasCountedCollections` 是这一对的能力声明：没有它，测试无法把「格式没有 count」与「实现没做」钉成互不漂移的一对。**必须是新类型**：`ref struct` 不能继承默认接口体（`CS9245`），把成员加进 `ITokenReader` 会让每个 ref-struct 实现者编译失败（§R1.1）⇒ 只实现 `ITokenReader` 的既有消费者零影响（A-2）；`api/*.public.txt` 不动（A-4）。 |

**注（发版机读）**：本次新增是**一个接口类型 + 一个静态类**，六个接口成员都是抽象的 ⇒ dump 里应出现 `abstract` 行，不再有 `virtual` 的问题（`scripts/api-surface/Program.cs:486-499` 的 `MethodModifiers` 只对「`Virtual` 且非 `Final`」打印 `virtual`）。静态泛型方法的 `ref` 首参与泛型元数在 dump 里的**确切拼法未实测**（§9 U4）。

### 4.3 已存在公共 API 的行为修正（不是新增，同样要登记）

**F-1**（§1.4）：`JsonReader.TryReadNextInt32` 在 sequence 模式丢掉负号（`JsonReader.cs:789-790`）。契约落地时这条路径
**必须**被删除或改正 —— 因为 `TryReadInt32` 的 span/seq 必须同一语义，而 seq 一律一行转发参考正本（`TokenReaderFused.TryReadInt32(ref this, out value)`）。`api/PENDING-CHANGES.md:18-30`
已有「## Registered capability change (not an API addition)」的先例段落，按同一体裁登记：**签名不变、返回值变正确**，属 `x+1` 轴。**F-2**（`TomlReader.cs:686-711`）同理。

---

## 5. 等价性要求（硬门槛）

### 5.1 定义（规范性）

> **等价性**：对同一段字节输入与同一入口，**融合路径**与**参考路径**产出的「token 序列 + 每个 token 的 `TokenType` /
> 值字节（含长度）/ 扩展 tag / 全部 getter 结果 / `BytesConsumed` / `Depth` / `NeedsMoreData` / 异常类型与消息」**逐字节相同**。

参考路径固定为 `Read()` 驱动的循环 + `TryGetX()`（今天生成代码的形状，`MsgPackSerializerGenerator.cs:2973`）。
**参考实现不许来自被测实现**：参考臂是 walker 里**内联**写成的 `Read()` + `TryGetX()`；被测是 reader 的成员实现（`TokenReaderFused` 正本只是样板来源，不是对拍参考，§3.2 的「正本管样板、内联管对拍」）。

### 5.2 做法：全量 token 转储对拍（照抄 4e 的既有做法）

4e 已证明这条路可行且便宜（4e 报告 §2）：改前 `logs/tokens-before.txt` 与改后 `logs/tokens-after.txt` **各 804 849 行**，`diff` 输出 `IDENTICAL`。
语料是仓库里的 `PicoMsgPack/tests/PicoMsgPack.Tests/MsgPackSpanTokenDump.cs`（663 行）：53 个命名 payload + 2000 个确定性随机 payload
（种子 `20261027`）+ 8 个畸形 payload，每条走 4 个入口（`SPAN`/`SEQ`/`SPAN-NONFINAL`/`SEQ-NONFINAL`），长度 ≤ 512 的再各跑 5 档分块（chunk = 1/2/3/5/7）。
每行记 `index|TokenType|BytesConsumed|Depth|值字节|全部 getter 结果`，异常单记一行。

**本规格要求的增量**：每条 payload **各跑两遍**（参考路径 / 融合路径）而不是「改前 / 改后」——两条路径在同一份二进制里共存，对拍因此是**同一构建内**的自比对，不依赖历史日志、也不会腐化。步骤：① 参考路径打 dump；② 融合路径（`TryReadX` / 计数循环）打 dump；③ `diff` 必须输出 `IDENTICAL` 且行数与参考侧相同。

**本修订对 S0 的影响（重要）**：派生接口落地后，五个 reader 从 S0 起就**有**融合臂可跑（不优化的成员 = 一行转发）⇒ 「参考 vs 融合」双走在 S0 就能建立并自证有效，`§6 S0` 的验收不再是不可执行的空文（原 DIM 设计下它根本无法构造，见 §R1.2）。**对拍能证明什么、不能证明什么必须写清**：对「一行转发」的成员，对拍证明的是「转发确实指向正本」；真正的等价性证明落在 §3.3 的**优化**成员上（MsgPack / JSON / TOML）。

**格式覆盖由 §6 决定**：MsgPack 有现成语料；JSON/TOML/YAML/INI **必须先建语料再覆写** ——「没有对拍就不许改」是本规格唯一的准入条件。

### 5.3 测试矩阵（维度，不含数量）

| 维度 | 取值 |
|---|---|
| **token 家族** | fixint/negfixint；uint8/16/32/64；int8/16/32/64；float32/64；bool true·false；nil；fixstr/str8/str16/str32；bin8/16/32；fixext/ext8/16/32（含 timestamp 三档）；fixarray/array16/array32（count 0·1·15·16·65536）；fixmap/map16/map32（0·1·15·16·65536） |
| **结果** | 成功；**类型不符**（须 `false` 且 token 保持 current、后续 `TryGetX` 仍可用）；**溢出**（`int64`/`uint64` 越界 ⇒ `false`，状态与参考一致）；**截断**（span 尾 / 分块边界 ⇒ `NeedsMoreData` 与参考一致）；**畸形**（未知字节、尾随垃圾、恶意长度 ⇒ 异常类型与消息逐字相同） |
| **流式分块** | `isFinalBlock` 真/假 × chunk = 1/2/3/5/7 × 入口 `SPAN`/`SEQ`/`SPAN-NONFINAL`/`SEQ-NONFINAL` |
| **深度** | 0…上限边界（MsgPack `MaxDepth = 64`，`MsgPackReader.cs:35`；JSON 默认 `maxDepth = 256`，`JsonReader.cs:205,229`），越过一层须抛同类型同消息（`MsgPackReader.cs:235-236`） |
| **集合计数**（G3 专用） | count = 0/1/15/16/255/256/65535/65536；**map 奇偶残留**（在 key 或 value 位置查询 ⇒ 剩余「完整对数」）；空集合；截断集合；`HasCountedCollections == false` 的格式 ⇒ 断言 `TryGetCollectionCount` 恒 `false` |
| **契约一致性** | 五个 reader 各走一遍 `where T : IFusedTokenReader, allows ref struct` 的泛型调用（口径同 `TokenReaderConformanceTests.cs:37`）⇒ 证明接口成员经 ref-struct witness 可用（`probe/04` 的 B-3 已实测该形状可编译且派发到 reader 自己的实现） |
| **生成器侧** | 生成产物**不含 `ITokenReader`**（§3.4.3 护栏）；E-1 与 E-2 的 `Read()` 次数相等（用计数桩，或直接比对 §5.2 的 dump 行数） |

---

## 6. 迁移顺序与每步验收

| 步 | 范围 | 为什么这个顺序 | 验收（每步都要过） |
|---|---|---|---|
| **S0** | 只加契约：`IFusedTokenReader`（6 个抽象成员）+ `TokenReaderFused` 正本 + 五个 reader 的 6 个成员（不优化的一行转发）+ 五格式一致性测试 | 先让契约成立且等价性可测，再动实现 | 构建 0 warning / 0 error；§5.2 的自比对在「一个优化实现都还没写」时仍 `IDENTICAL`（此时融合臂全是转发：它证明的是**护栏与转发通道**有效，真正的等价性在 S1–S3 的优化成员上，§5.2 末段）；`api/*.public.txt` 零 diff；**机制可行性已先证**（§R1：CS9245/CS8337/CS9300 复现 + 派生接口形状通过） |
| **S1** | **MsgPack**（收益最大、头里就有 count） | count 现成（`MsgPackReader.cs:406-413,578-596`），且 12× 差额已被 4e 量化（4e 报告 `:264-271`） | §5.2 对拍 `IDENTICAL`（含 Seq/NonFinal 全档）；count 矩阵钉住奇偶性质；`Collection — Deserialize` 按协议（三轮取中位）**如实重测并公布**——不设目标值 |
| **S2** | **JSON** | 融合 API 已存在（`TryReadNextInt32`），但**必须先修 F-1/F-3**（否则一开工就把「绝对值 bug」固化进生成代码） | 删除 seq 融合分支、seq 一律一行转发参考正本；按 §5.2 新建 JSON 语料并对拍 `IDENTICAL`；F-1 的回归用例（负数的 span 与 seq 两条）进仓库 |
| **S3** | **TOML**（按需） | 有 `TryReadNextInt32`/`TryReadInt32ArrayFast`（`TomlReader.cs:635,2071`），但无 count、且 F-2 的语义要按参考正本重做 | 同 S2（先语料后覆写）；若 S1/S2 实测不足以支撑，**允许停在 S2 并把 S3 记为欠账**（reopen：「TOML 读侧成为瓶颈时」） |
| **S4** | **YAML / INI** | 两者都**没有**可用于计数的集合形态（§3.3） | 除 S0 的 6 个成员（全部一行转发）之外**不做任何 reader 改动**；一致性测试断言 `HasCountedCollections == false` 与 `TryGetCollectionCount` 恒 `false` |

**逐步纪律**（沿用本轮性能改造的方法论，`2026-10-28-perf-round-summary.md:§9`）：快路径 + 回退；回退路径逐字保留；
「零行为变化」用逐字节对照证明；生成代码基线由生成器重新生成。

---

## 7. 被否决的替代方案（共 8 条；第 4 条已于 **§R1** 撤销否决）

| # | 方案 | 为什么否决 |
|---|---|---|
| **1** | **不改 Core，每个格式各自加**（MsgPack 加 `TryReadInt32Fused`、JSON 一份、TOML 再一份…） | ① 契约不统一：已经有三份同名不同义的副本且已分叉（§1.4 F-1…F-4）；② 生成器要写五遍：发射点 24 处（§3.4.2 C-2）本就分散在三个生成器里，再加五套融合调用形态会让「改一处漏四处」成为常态；③ 共享发射器无从下手（没有共同签名可发射） |
| **2** | **直接把 MemoryPack 的定长格式搬来**（无 tag、成员数头、`Unsafe.ReadUnaligned` 定长直读） | ① 改 wire：已发布字节流全部失效；② 破坏跨语言与互操作（MP 报告 §5.3 N1）；③ 尺寸退化是它自己承认的代价（MP 报告 §4.2 引 `README.md:897`）；④ 它没有可借鉴的 SIMD（MP 报告 §5.3 N7）——抄它只会拿掉自描述性与前向兼容，换不到我们要的东西 |
| **3** | **让生成代码改走 `ITokenReader` 接口** | ① 用具体类型是**优势**：`ref JsonReader` ×8 / `ref MsgPackReader` ×5，`ITokenReader` 在 `.Gen` 里零出现（§1.3）；② 接口调用会把「一次分派」变成「一次派发 + 可能落到 DIM 默认体」，与 G1 冲突；③ 去虚拟化在 `allows ref struct` 下虽有先例（`.superpowers/sdd/perf-mp-span/harness/Program.cs:134` 注释），但那是**手写**代码的实测，不是生成代码的担保 —— 不拿未证实的东西当设计前提 |
| ~~**4**~~ | ~~新建 `IFusedTokenReader : ITokenReader`，不加 DIM~~ ⇒ **§R1 撤销否决，已采纳为 §3.1 的机制** | 原否决理由的根基是第 ③ 条：「DIM 已能给出外部实现者零改动且行为正确」——**实测为假**（DIM 给出的是 30 个 CS9245，§R1.1）⇒ 否决不成立。第 ① 条（`ITokenReader` 泛型站点失去访问）被**接受为代价**并写进 §4.1；第 ② 条（opt-in 两次）不成立：不优化的实现者**根本不需要**实现派生接口 |
| **5** | **在 reader 里加「计数作用域」**：进计数循环后跳过 ③ `CountElement()` 的两次栈写与 ④ 的结束符判定 | 它改的是 `_elementStack` 的**记账节奏**，而 `Skip()`、结束判定、`ExportState`/`IsResumed` 都读这本账（`MsgPackReader.cs:243-251,375-390,81-120`）⇒ 改可观测行为，与 N2 冲突。这是**独立的一次状态机改造**，值得单独规格；塞进本规格会把「纯新增 + 零行为变化」的门槛一起毁掉 |
| **6** | **把 `TryGetCollectionCount` 做成「消费头」的 `TryReadCollectionHeader`**（MemoryPack 形态） | 头**已经被 `Read()` 读掉**了（Start token 就是头的产物）。再「读一次头」要么多产生一个 token（改 token 流 ⇒ 与 §5 冲突），要么重复消费同样的字节（把 bug 写进契约）。查询形态是唯一能与现有 token 流共存的形态 |
| **7** | **默认接口实现（DIM）**：六个成员带默认体加进 `ITokenReader`（**原设计**，§R1 前的 §3.1） | **实测否决（不是推测）**：`ref struct` 必须自己实现它实现的每个接口的每个成员，**连带默认实现的成员** ⇒ 在本仓库直接产出 **30 个 `error CS9245`**（6 成员 × 5 reader，`610db24`）；派生接口上加默认体同样 `CS9245`（`probe/05`）；语言规范 `ref-struct-interfaces.md:131`（「forced to implement all members … even if they have default implementations」）与 `:158`（「A `ref struct` cannot participate in default interface members」）把这条写死了。后果：「DIM 臂」构造不出来 ⇒ §5.2 的参考侧无从对拍，§6 S0 的验收不可执行（§R1.2）。详见 **§R1.1** |
| **8** | **扩展方法（选项 A）**：`static bool TryReadInt32<T>(this T reader, …) where T : ITokenReader, allows ref struct`，五个 reader 一个成员都不实现 | **实测否决**：按值接收者 ⇒ 推进的是**副本**，原 reader 不动、`TokenType` 不物化，`while` 在真 reader 上**死循环**（`probe/01`：`ok=True value=7 PositionAfter=0`，第二次仍读到 7）——静默错，最坏的一种；把接收者改成 `ref` 的三条路全是编译错误：`this ref T` ⇒ **`CS8337`**、`where T : struct` ⇒ **`CS9244`**、C# 14 扩展成员块 `extension<T>(ref T reader)` ⇒ **`CS9300`**。唯一能编译的 ref 接收者形状是**具体类型**的扩展块（`probe/07` 通过）⇒ 退化成方案 1（每格式一份，必然分叉）。详见 **§R1.3** |

---

## 8. 风险与缓解

| # | 风险 | 影响 | 缓解 |
|---|---|---|---|
| **RK-1** | **已不再是「未验证风险」——已实测：DIM 不可行**（`ref struct` 不能继承默认接口体） | 原设计的核心机制无法落地：五个 reader 全报 `CS9245`（30 个错误），且「DIM 臂」造不出来 ⇒ 等价性护栏没有参考侧（§R1.2） | **风险已兑现，机制已更换**：契约改为派生接口 `IFusedTokenReader`（6 个抽象成员）+ 静态正本 `TokenReaderFused`（§3.1/§3.2），`probe/04` 五点位全过（§R1.4）。若将来语言允许 `ref struct` 继承默认接口体，可把 6 个成员下沉为默认实现而**不需要**改契约形状 |
| **RK-2** | **语义漂移**：覆写为了快而少写 `_valueSpan`/`_tokenType`，就重演 F-3 | 依赖 `TryGetFloat64`/`GetStringRaw`/`TokenType` 的后续代码静默错 | §5.2 的转储要求**包含全部 getter**；§3.2.3 把「融合 ≠ 不物化状态」写成规范；实现只许在成功分支分叉（§3.3 的 `default: → 返回参考正本`） |
| **RK-3** | **计数与 reader 的结束判定不同源**：若算出的数不是 reader 用的那个（map 忘了 `/2`、map32 的 clamp 没跟上） | `for` 多读一个 token ⇒ 静默数据错位 | 契约规定「返回 reader 自己用的那个数」（§3.1）；实现直接从 `_elementStack` 取（`MsgPackReader.cs:578-596` 的 clamp 因此自动继承）；§5.3 的「map 奇偶残留」行专打这个洞 |
| **RK-4** | **五格式测试成本**：只有 MsgPack 有现成转储语料，其余四格式要新建 | S2/S3 前置成本被低估 | §6 把「先语料后覆写」写成准入条件，S3 允许停成欠账（带 reopen）；顺序按「收益 × 语料成本」排 |
| **RK-5** | **与正在进行的性能任务冲突**：存在并行工作树 `.worktrees/mp-collection`（MP 报告 §6(b) 末段标注），同方向（`TryReadInt32Array` 的接入实验） | 改同一批发射点 ⇒ 合并冲突或结论互斥 | ① 本规格只写**文档**、不碰代码；② 计划阶段先读该工作树最新结论，冲突处**以实测为准、以本规格的等价性门槛为约束**；③ S1 的发射点与 `Collection` 快路径高度重叠，须在**同一时间窗口**由同一人排序 |
| **RK-6** | **收益不及预期**：4e 的先例是「计划 2.33× → 1.5×，实测只拿到 2.03×」（4e 报告 §5.6） | 又一轮「改了没效果」 | 本规格**不承诺任何百分比**（N6）；每步验收只要求「对拍相同 + 如实公布重测值」；若 S1 实测落在噪声内，允许在 S1 处**终止后续步骤**，把契约（S0）作为独立成果保留 |
| **RK-7** | **契约扩张**：四个成员是「同形不同型」，未来还会有人想加 `TryReadDateTime`/`TryReadDecimal` | 接口膨胀、每格式实现成本线性增长（而 C# 实测**不给默认体**，§R1.1 ⇒ 每加一个成员就是 5 个方法体 + 1 个正本方法） | 写死：本规格只覆盖**生成器今天最热的四个标量族**；新增融合读必须**先有 §5.2 的对拍语料**再开口子 |
| **RK-8** | **换机制的残余代价（新）**：① 公共面从「0 个新类型」变成 **1 个接口 + 1 个静态类**；② 只约束 `ITokenReader` 的泛型站点**看不见**融合读；③ 五个 reader × 6 个成员 = **30 个成员体**的样板 | ① 发版 diff 变大（`x+1` 轴，A-3）；② 未来想共享融合读必须改约束；③ 有人为了省事在某个 reader 里**重写**一遍 `Read()`+`TryGetX()` 而不是转发，语义就会像 F-1…F-4 那样分叉 | ① 按 **R-3** 逐条登记（§4.2），`api/*.public.txt` 仍零 diff（A-4）；② 需要融合读的站点一律把约束写成 `IFusedTokenReader`（本规格的消费者只有生成器-具体类型与 §5.3 一致性测试两处，§4.1 ②）；③ 评审门：不优化处的成员体**必须是一行转发**，出现第二份手写组合即退回（`probe/04` 的 B-4 已证明 `ref this` 转发可编译，「不能转发」不是借口） |

---

## 9. 未证实项（共 9 条；原 U4「DIM 在 dump 里打印 `virtual`」随 DIM 一起作废，见 §R1.5）

| # | 未证实内容 | 为什么没证实 | 怎么才能证实 |
|---|---|---|---|
| **U1** | 融合后能回收 **30–50%** 的每 token 成本 | 来自 MP 报告 §5.1 M2 的**估算**（原话「若 11–20 ns/token 里一半是第二段分派与 span 物化，理论回收 30–50%」，自标「幅度未证实」） | S1 的协议化重测（三轮取中位）；本规格不把它当结论 |
| **U2** | **13.4 ns/token（我们）/ 6.8 ns/token（竞品）** | 算术推得（已发布 µs ÷ 手工枚举 token 数），非实测（MP 报告 §7 U1） | 单 token 微基准：同一 reader、10⁶ 次 `Read()`+`TryGetInt32` |
| **U3** | **15.7 ns/token vs 1.3 ns/token（21 token：329.1 ns vs 27.8 ns）** | 4e 是单场景测量（`MTenInts`），不是逐 token 归因；两项都含模型自身的 map 键遍历 | 同 U2 的微基准 + 逐段拆解 |
| **U4** | `IFusedTokenReader` / `TokenReaderFused` 在 `api-surface` dump 里的**确切行文**（类型行、`abstract` 成员行、静态泛型方法的元数与 `ref` 标记拼法） | 只读了 `scripts/api-surface/Program.cs:486-499` 的修饰符规则，本轮未实际跑 dump | 发版脚本跑一次，看第一版 diff |
| **U5** | E-1（计数循环）单独能省多少 | §3.4.1 已论证「`Read()` 次数与现状完全一致」，因此它省的只有每元素的 `_tokenType` 比较；**具体多少未测**（MP 报告 §5.1 M1 的收益幅度自标未证实） | 把 E-1 与 E-3 拆成两次独立提交，各测一次 |
| **U6** | 融合读对 `Collection — Deserialize`（1.96×）的端到端影响 | 未测；该模型 107 个 token 里 100 个是**逐元素解码**，MemoryPack 的「整数组一次 memcpy」在这里拿不到（MP 报告 §5.2 W1）⇒ 天花板不在本规格内 | S1 重测；若仍 >1.5×，说明差额在 List 扩容/字典上（MP 报告 §5.2 M4） |
| **U7** | F-1（seq 丢符号）在生产路径上是否**真的可达** | 已证可达：`JsonReader(ReadOnlySequence<byte>, …)` 是公开构造（`JsonReader.cs:227`）、`TryReadNextInt32` 是公开成员（`:656`）、无任何测试覆盖负数或 seq（`JsonReaderTests.cs:527-568` 全正数全 span）。**但「有没有下游消费者」未查** | S2 的回归用例本身即证；调用面由 `grep` 复核（今天：0 处非测试调用） |
| **U8** | 24 处发射点里**有多少**最终能被共享发射器覆盖 | 只逐点读了形态，未逐点判定（TOML `:1617` 带 `TablePath` 判定、字典的 `PropertyName` 循环形状不同，明确留在原处） | 计划阶段逐点列表 |
| **U9** | `where T : IFusedTokenReader, allows ref struct` 的**接口见证在 AOT/ILC 下**与 JIT 行为一致（去虚拟化、无装箱） | 本轮探针只在 JIT 下跑（`dotnet build` / `dotnet run`）；仓库测试工程虽开 `PublishAot`，但 `dotnet test` 不触发 ILC | S0 落地后在 CI 跑一次 AOT 发布冒烟；在此之前**不宣称** AOT 下的派发形状 |

---

## 10. 一句话回执

**把 `Read()` 与 `TryGetX()` 中间那次重复分派删掉，而不是把 `Read()` 再复制一遍**（4e 报告 `:271-274` 的同一结论）。本规格的全部设计（参考正本作为规范、实现只许在成功分支分叉、计数查询必须返回 reader 自己的那个数、对拍必须逐字节）都是为了让这句话能被**证明**，而不是被相信。