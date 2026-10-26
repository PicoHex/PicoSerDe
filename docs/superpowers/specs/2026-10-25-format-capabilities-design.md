# PicoSerDe Format Capabilities Design Spec

**Status**: Draft v8 — ready for sign-off
**Date**: 2026-10-25
**Baseline**: `84a9560` (`main`, clean tree)
**Topic**: which shapes each of the five wire formats can represent, what the library
currently gets wrong about them, and what the library must do about it
**Contract**: `picohex-picoserde` (AOT / zero reflection / fail-loud) + `picohex-self-review`

> **本规格只写「要什么、为什么、什么不许做」。** 表怎么建、字段叫什么、丢弃粒度怎么表示、用哪条命令验收 —— 全在实施计划里（`docs/superpowers/plans/`）。这是本规格 v8 与前七版最大的区别：v1–v7 把接口设计写进了规格，导致六轮评审都在审实施细节且每轮都能找到下一个缺口。

---

## 0. 修订记录

| 版本 | 变更 |
|---|---|
| v1–v3 | 规则 R1–R3、实测矩阵、缺陷注入、三处决策（YAML null / 扩展集合 / 递归） |
| v4–v5 | 采纳维护者架构原则（R0）；裁定全部决策；确定两次发布 |
| v6–v7 | 按第 4/5 轮复核修表格与措辞 |
| **v8** | **重划 spec/plan 边界**：删除数据模型细节（两张表、构造 K1–K11、仲裁规则、字节 diff 证明范围 —— 原 v7 的 §2/§2.1）、75 格现状快照（原 §3.1）、各步验收（原 §9） —— 全部移入计划。规格只保留规则、事实、缺陷、裁定、目标、不变式（443 → 240 行） |

---

## 1. 目的与非目标

### 目的
让「**哪种形状在哪种格式可表示**」这件事有一个**唯一、可测试、有证据**的答案，并且让**每一处不支持都有据可查**：要么是格式事实（诊断 + 文档），要么是明确的欠账（诊断 + reopen 条件），不能是沉默。

### 非目标
- 不追求「所有格式支持所有形状」——格式不能表达的就是不能。
- 不规定实现结构（表怎么建、字段叫什么、代码放哪里）——那是计划的事。
- 不解决 reader 的流式/resume 状态机差异。
- 不改变「写侧省略 null **成员**」的既有设计（本轮只讨论**序列元素**里的 null）。

### 现状量化（作为动机，不作为验收指标）
`shared/Gen` 里含格式字面量的特判 **25 处**（`TypeKindResolver` 12 + `GenInfrastructure` 13）；5 个生成器合计 **20,687 行**，同一 kind 分派重复 `case "int32"` **43** 次、`case "string"` **47** 次、`case "dict"` **30** 次。三处数字均经独立复核确认。

## 1.1 架构原则（维护者裁定，规范性）

> **R0 — 三条**
> 1. **形状与遍历共用**：类型形状的遍历（属性、集合、嵌套、可空、字典、递归）只有**一份**实现，位于 shared/core；不得在每个格式生成器里各写一遍。
> 2. **wire 表达由格式决定**：同一形状在各格式的**编码**不同，这是格式的职责，由格式原语承担。
> 3. **格式明确不支持的类型则不做支持**：只有格式**无法表达**的形状才不实现；其余一律实现。

**推论**

| # | 内容 |
|---|---|
| C1 | **单一来源**：能力判定必须来自**一个**内部来源；实现进度**不得**被写成格式事实；该来源必须能表达 §3 的每一格（可测试）。*建法与字段设计不在本规格*。 |
| C2 | **终态无欠账**：目标态只允许「支持」与「格式不可表示」两种；「可表示但我方未实现」是**过渡态**，必须带 reopen 条件，不得进入发布说明的"已知限制"。 |
| C3 | **顺序**：先建共用管线，再补能力。逐格式手写能力会被管线推翻，属重复劳动。 |
| C4 | **诊断是唯一出口**：两类诊断不得混用 —— **⛔ 类**（格式确实不能表达）与 **⏸ 类**（可表示、我方未实现）；后者必须可区分且携带 reopen 条件。 |

## 2. 规范性规则

### R1 — 表示性
> 一种形状在某格式**不可表示**，当且仅当**不存在**一种该格式的表示，能被该格式的**规范合规的独立实现**往返成等价的值。

判据在**格式**，不在我们的生成器；「我方未实现」「代价太高」不是不可表示的理由（→ C2 的过渡态）。

### R1′ — 等价关系（每形状一个，缺表会让 R1 自相矛盾）
| 形状族 | 等价关系 |
|---|---|
| sequence | 有序、等长、逐元素等价 |
| set（`HashSet`/`ISet`） | 集合相等（不看顺序） |
| map（`Dictionary`） | 键值多重集相等（键序不保证） |
| scalar 数值 | `(值, 最短可往返文本)`；`decimal` 必须 invariant culture；`float` 不得经 `double` 中转 |
| scalar 时间 | `(值, Kind/offset, 精度)` |
| scalar 字符串 | 解码后码点序列相等（转义形式不限） |
| 特殊浮点 | `NaN`/`±Inf` 仅在格式有字面量时可表示 |
| 对象与递归 | 对象 null 元素 ≡ `null`；递归按**树同构**比较（与深度上限无关） |
| 容器语义 | `Stack` 枚举 top→bottom（读侧须逆序重建）；`Queue` FIFO；`Memory`/`ReadOnlyMemory` 视为 `T[]` 视图 |
| null 三态 | `null` 值 / **缺失成员或键** / **空集合** 三者**互不等价** |

### R1″ — canonical write / lenient read
写侧每种概念只发**一种**规范形态；读侧接受该概念在格式规范内的**全部**合法形态。

### R2 — 无静默
> 任何未进入生成代码的成员，**必须**产生一条**真的会出现在构建输出里**的诊断（id ∈ `PICOSERDE001`…`005`）。

「收集了但不上报」等同静默。R2 只管成员是否进入生成代码；**值级丢失**由 R5 覆盖。

### R3 — 不可表示 ≠ 不可编译
任何被判定为不支持的形状，**不得**产出不可编译的生成代码。

### R4 — 代价闸门（过渡态）
表示**存在**但未实现 ⇒ 标为**欠账**：必须有诊断、必须写明 reopen 条件、**不得**被当作格式事实写进文档的"已知限制"。

### R5 — 值级保真
成员进入生成代码**不等于**保真。标记为支持的格必须由**含边界值**的往返测试支撑（null 元素、空集合、单元素、`NaN`/`±Inf`、`decimal` invariant、`DateTime.Kind`、含分隔符/引号/换行的字符串、`int.MaxValue`）。

## 3. 格式事实（实测，R1 的判据）

✅ 可表示 ｜ ❌ 不可表示。依据列给出判定来源。

| 形状 | JSON | MsgPack | TOML | YAML | INI |
|---|:---:|:---:|:---:|:---:|:---:|
| S1 `List<int>` 扁平标量列表 | ✅ | ✅ | ✅ | ✅ | ✅ |
| S2 `List<Dto>` 对象列表 | ✅ | ✅ | ✅ 数组表 | ✅ | ❌ |
| S3 `List<List<int>>` 嵌套标量列表 | ✅ | ✅ | ✅ | ✅ | ❌ |
| S4 `List<List<Dto>>` 嵌套对象列表 | ✅ | ✅ | ✅ 内联表数组 | ✅ | ❌ |
| S5 `List<int?>` 可空值叶子 | ✅ | ✅ | ❌ 无 null | ✅ | ❌ |
| S6 `List<List<int?>>` 深层可空叶子 | ✅ | ✅ | ❌ 无 null | ✅ | ❌ |
| S7 `HashSet<int>` 作成员 | ✅ | ✅ | ✅ | ✅ | ✅ |
| S8 `List<HashSet<int>>` 作元素 | ✅ | ✅ | ✅ | ✅ | ❌ |
| S9 `Dictionary<string,int>` | ✅ | ✅ | ✅ | ✅ | ✅ |
| S10 `Dictionary<string, List<int>>` 字典值为集合 | ✅ | ✅ | ✅ | ✅ | ❌ |
| S11a 自递归 / S11b 互递归 | ✅ | ✅ | ✅ 嵌套表 | ✅ | ❌ |
| S12 `List<string?>` 含 null 元素 | ✅ | ✅ | ❌ 无 null | ✅ | ❌ |
| S13 `Dictionary<string,int?>` 含 null 值 | ✅ | ✅ | ❌ 无 null | ✅ | ❌ |
| S14 `List<Dto?>` 对象 null 元素 | ✅ | ✅ | ❌ 无 null | ✅ ¹ | ❌ |
| S15 scalar 族（`float`/`decimal`/`DateTime`/`TimeSpan`/`Guid`/`enum?`） | ✅ 除 NaN/±Inf ² | ✅ | ✅ ³ | ✅ ³ | ✅ ⁴ |

**判据与证据**：TOML 用 Python `tomllib`、YAML 用 PyYAML 实测（脚本见 §9；其逐字输出记录在计划的探针章节）。**INI 无独立实现可依据**（`configparser` 对任何输入都当字符串收下），其边界是**本规格的约定**：

> INI 的约定：标量列表 = 单层逗号串（`V = 1,2,3`）；对象列表只在**顶层**用编号 section（`[0]`、`[1]`）；字典 = 成员级 `[V]` section；**成员内不承载任何嵌套**；**不承载 null**（空值与空串不可区分）。

**结论**：真正的 `❌` 只有两类 —— ①**INI 的结构性限制**（任何嵌套、任何 null、成员内对象列表）；②**TOML 缺 null**。其余所有「不支持」都是欠账。

### S14/S15 的实测（Task 2b 观测，只记录、不进 `DecideShape`）

> **这两行现在有值了，但没有能力通路**：`FormatCapabilities.AllShapeIds` 仍止于 S13，`DecideShape`/
> `Paths` **不认** `S14`/`S15`（探针每次运行都断言这一点，见 `Probe.cs` 的 `Observations.Validate`）。
> 表里填的仍是**格式事实**（「可表示」）；今天生成器**实际**做什么单独列在下面的实测表里，
> 二者不一致处按 **R4** 是**欠账**，不是格式事实。

**测量方式**：`tests/FormatCapabilities.Probe` 为两个形状各加 5 个模型，打印 `CELL-OBS|<format>|<shape>|<verdict>|<wire>|<expected>|<actual>`
（**不**进入 70 格统计：`grep -c '^CELL|'` 仍是 70）。S15 一个模型带六个成员（`f`/`d`/`when`/`span`/`id`/`day`），
因为这一行的问题是**逐标量**的：一个格式可以保住 `decimal` 而丢掉 `TimeSpan`。每格三个值
（`empty` = 全默认 · `single` = 常规值 · `boundary` = `NaN`、`decimal.MaxValue`、`DateTimeKind.Local`、
`TimeSpan.MinValue`、全 `f` 的 `Guid`、`null` enum）。`reason=…;diff=` 列逐值列出**没活下来**的成员。

| 格 | 实测 verdict | 实测细节（`CELL-OBS` 原文见 `.superpowers/sdd/r1-w2b/task-2b-report.md`） |
|---|---|---|
| S14 × json | `OK` | `null` 元素逐元素往返（`{"V":[{…},null,{…}]}`），`null` 与缺失/空集合仍互不等价 |
| S14 × msgpack | `OK` | 同上（`C0` = nil 元素） |
| S14 × toml | `MISMATCH` | 三个值 wire 全空：**整个成员被丢**（`PICOSERDE004`），与「无 null」一致 |
| S14 × yaml | `MISMATCH` | 同上：**整个成员被丢**（`PICOSERDE004`）⇒ 欠账 ¹ |
| S14 × ini | `MISMATCH` | 同上（与 S2 × ini 同因：成员内不承载对象列表） |
| S15 × json | `MISMATCH` | `empty`/`single` 六成员**全等价**（`D` 保精度、`When` 保 `Kind`、`Span`/`Id` 走字符串）；`boundary` 序列化抛 `ArgumentException: NaN cannot be written as JSON` ⇒ 注 ² |
| S15 × msgpack | `OK` | 三个值全等价，**含 `NaN`**（`CB FFF8000000000000`）、`TimeSpan.MinValue`、`Local` 偏移、全 `f` Guid、`null` enum |
| S15 × toml | `MISMATCH` | `empty`/`single` 六成员全等价；`boundary` 序列化抛 `ArgumentException: NaN and Infinity cannot be written as TOML` ⇒ 欠账 ³ |
| S15 × yaml | `MISMATCH` | 同 toml（`ArgumentException: NaN and Infinity cannot be written as YAML`）⇒ 欠账 ³ |
| S15 × ini | `MISMATCH` | `single`/`boundary` 六成员全等价（`NaN` 写成文本、`Local` 带 `+08:00`）；`empty` 的 `when` 读侧抛 `FormatException: Invalid DateTime for 'When' … '0001-01-01T00:00:00.0000000'` ⇒ 欠账 ⁴ |

**canonical write 的观察（不是能力问题）**：TOML/YAML 里 `decimal`/`DateTime`/`TimeSpan`/`Guid` 都被写成**带引号的字符串**
（`D = "0"`、`When = "…"`），读侧照收，故往返保真；但对外部消费者它们是字符串而不是原生数值/时间类型 —— 归 R1″/R2 的写法议题，
不进能力表。

**脚注**

¹ YAML **可以**表示对象序列里的 null（PyYAML：`- null` 项 → `[{'Name': 'a'}, None]`），今天却把整个成员丢掉
   ⇒ **欠账**，与 S12/S13 × yaml（D-7b）同源。
² JSON 无 `NaN`/`±Inf` 字面量（R1′「特殊浮点仅在格式有字面量时可表示」）⇒ 这是**事实**，不是欠账。
   今天写侧抛异常，属正确拒绝（但只抛、不诊断 ⇒ D-4 的措辞问题）。
³ TOML 1.0 与 YAML 1.1 **有** `nan`/`inf`/`-inf` 字面量（`tomllib.loads("V = nan")`、PyYAML `V: .nan` 均通过）
   ⇒ 可表示，今天写侧抛异常 ⇒ **欠账**（R4 的 reopen 条件：写侧改用原生 `nan`/`inf`、读侧接受）。
⁴ INI 的约定本身能承载（实测 `single`/`boundary` 全通，含 `NaN` 文本），失败点是**写读不对称**：默认值
   `When = 0001-01-01T00:00:00.0000000` 没有 `Kind` 后缀，读侧拒绝 ⇒ **欠账**（写读同一 canonical 形态即可闭合）。

**结论补充**：S14 的 `❌` 仍只有「TOML 无 null」与「INI 结构性限制」；S15 的 `❌` 只有「JSON 无 NaN/±Inf 字面量」。
其余「今天不支持」全部是欠账（S14 × yaml；S15 × toml/yaml 的 NaN；S15 × ini 的默认时间）。

## 4. 缺陷（现状与 R1/R2/R5 的偏离）

| ID | 类别 | 缺陷 |
|---|---|---|
| **D-1** | 静默丢失 | `HashSet<T>` 家族作**成员**：MsgPack/TOML/YAML/INI 无任何诊断且成员消失（JSON 正常） |
| **D-2** | 静默丢失 | 同上作**元素**（`List<HashSet<int>>`）：四格式静默（JSON 有诊断） |
| **D-6** | **静默的根因** | `PICOSERDE001` 被收集后**从不发射**：`GenInfrastructure.cs:1133`（公开字段）、`:1188`（属性）只 `diagnostics?.Add` + `continue`，而 5 个生成器从不传该列表；仅 `:1537`（递归）另有 `skippedRecursive` 故产出 `003`。任何解析不出的类型都会静默消失 |
| **D-5** | 数据损坏 | TOML 互递归往返**覆盖根成员**（写 `K="root"` + `[Child] K="c1"`，读回 `K="c1"`） |
| **D-7a** | 值级丢失 | TOML：`List<string?>` 的 null 元素被静默跳过（成员在、值丢）。TOML 无 null ⇒ 应改为**类型级不支持 + 诊断** |
| **D-7b** | 值级丢失 | YAML：同上。YAML 有 null ⇒ 应**支持** |
| **D-3** | 实现缺口 | 两种「容器套容器」可表达却丢弃：①`Dictionary<string, List<T>>`（S10）在 JSON/MsgPack/TOML/YAML；②嵌套对象数组（S4）× TOML |
| **D-4** | 诊断措辞 | ①递归诊断指向**内层类型**的成员而非用户声明的成员；②欠账格与不可表示格**共用同一条消息**「cannot be represented by this format」，对可表示的格构成误导 |
| **D-8** | 双口径 | 匿名类型走**另一套** id（`PICOSERDE005`）与消息，口径可能与具名类型不一致 |
| **D-9** | **挂死** | TOML：字典成员被写成**空表**时反序列化**永不返回**（纯 CPU 自旋）。判据不是「空表」本身，而是「回到外层循环时 token 仍是**未被消费的 `ObjectStart`**」—— 所以 `[V]`+EOF、`[V]`+后续 `[W]` 表头都会挂，而 `[V]` 后跟 `a = 7` 不挂。根因：生成代码的 `ObjectStart` 分支 `continue` 前不推进 reader（`TomlSerializerGenerator.cs:913-935`），而 `TomlReader.ReadSpan()` 在 EOF 只 `return false`、不换 token（`TomlReader.cs:777-785`） | 探针 W2 实测（`S9×toml` 空字典 → wire `[V]`）；三条独立证据见 `.superpowers/sdd/r1-w2/review-spec.md`。**R2 T0 已修**（`f34d5be`）：reader 在 final block 失败出口清 token + 发射层推进守卫，两层各承其重 |
| **D-9b** | 静默 | TOML **写侧**：`TomlWriter.NeedsKeyQuoting` 只判 `空格 	 = [ ] . # "`，**不含** `
| **D-9c** | **挂死族** | 表头名**不在模型里**时发射层零 `Read()` ⇒ token 恒 `ObjectStart` ⇒ 自旋。触发面比 D-9 更广（`[tool.*]`/`[build-system]` 等真实文件必然命中）。实测需 **6 个派发分支 + 3 个内层循环**的推进守卫（`BytesConsumed` 未变则 `Read()`） | W0 fix round 1（`f34d5be`）+ 两份复核。**归属**：R2 T0 已修 |
| **D-9d** | 静默 | `[[Unknown]]`（外来数组表头）会开出**第二个空元素**，不重查表名。今天测试把它钉成"2 个元素"的契约 ⇒ 将来修它必须同步改断言 | W0 fix round 1 实测。**归属**：R2 待定（T6 或独立） |
`/``/控制字符/`\` ⇒ 含换行的键写出后**读不回来**（与 D-9 的挂死无关，是独立缺陷） | `TomlWriter.cs`；W0 实测（`toml\|S9` 从 TIMEOUT 变 MISMATCH 后露出）。**归属**：R2 Task 6 |
| **D-10** | 误导 | `PICOSERDE005`（匿名类型）是**第四个 drop 通道**：无 ⛔/⏸/⚠ 标记、消息把「我们生成不了名字」写成 "cannot be serialized by {format}"、`location: null` 不可导航。它使 I-5 的「每条 drop 消息都带标记」**名义上有、实际没被强制**（现有测试的样本永远命不中它） | `GenInfrastructure.cs:798-820`；W5 第三轮 code review N-1。**归属**：R2（与 D-8 并入共用管线一起做） |

**归属**：D-1/D-2/D-3/D-5/D-6/D-7 → 分批修复（§5.6）；D-4 → 诊断改造；D-8 → 并入共用管线（匿名类型是第 6 个发射面）；**D-9 → R2**（R1 禁止改行为）。

## 5. 裁定（维护者已定）

| # | 事项 | 裁定 | 依据 |
|---|---|---|---|
| **D1** | YAML 序列里的 `null` | **实现**（写侧 `- null`，不用裸 `-`：裸 dash 已占用为嵌套序列项） | PyYAML 接受 null（§3）；R0 第 3 条 |
| **D2** | 扩展集合（7 个 kind） | **全格式实现，且在共用管线里实现一次** | 都能用该元素类型的数组表达；没有任何格式明确不支持。**证据强度**：仅 `HashSet` 与嵌套数组已实测，其余 6 个是同构外推，须由探针补测 |
| **D3** | 递归深度 | **TOML/YAML 实现任意深度；INI 不做** | `tomllib` 接受 `[Child.Child]`、PyYAML 接受两层映射；INI 扁平 |
| **D4** | 诊断 id 与消息 | **公共契约**：id 在 `year` 内稳定；消息可变但须保持可 grep 前缀，且 ⛔/欠账两类**必须可区分** | C4 |
| **D5** | 能力表的可见性 | **`internal`** | 用户不应关心实现；避免 `x` bump |

> **诊断三桶的契约（W5 实测后补写，此前 spec 只有二分）**
> | 标记 | 含义 | 读者出路 | 是否可重开 |
> |---|---|---|---|
> | **⛔** | **格式能力**不支持（facts = ❌） | 换格式或换类型 | 否（终局） |
> | **⏸** | 我们**尚未实现**（facts = ✅，但我们丢） | 等我们修 | 是（消息带 `reopen:` 指针） |
> | **⚠** | **成员自身**不可用（如 set-only 属性） | 改成员写法 | 否（不是格式/我们的问题） |
> 判别 token 必须在消息**开头**，且由 `FormatCapabilities.CanRepresent` 决定，**不得**按实现进度硬写（I-5 / C1）。

### 5.6 发布切分（两次）

> **R2 的三项前置义务**（R1 的 W3 实测得出：区分自递归/互递归需要它们，缺一不可）
> 1. **基类链的 root 归属**：从派生类型起步时，基类自身的自递归（`RvPolyLeaf` 看到 `RvPolyBase.Next`）不能判成互递归；
> 2. **抽象类型的生成**：`RecursiveMutual × toml = Supported` 一旦生效，会生成 `new <抽象类型>(...)`（CS0144），必须先解决可实例化性；
> 3. **受影响语料**：`RvPolyBase` / `RvPolyLeaf` / `PolyRecAbstract` 三个模型需一并调整（今天按"有环就丢"编译通过）。
> 不做这三项，"D-5 根覆盖"无法落地。

| 发布 | 内容 | wire 输出 | 版本 |
|---|---|---|---|
| **R1** | 建立单一来源、消静默、诊断改造（**只新增诊断，不改输出**） | **不变** | `y+1` |
| **R2** | 共用管线 + 能力落地 + 改变读回结果的缺陷修复（D-5/D-7a/D-7b/D-3） | 变化 | `x+1` |

## 6. 目标矩阵（终态，只有 ✅/❌）

按 R0/C2：能表达就必须支持。**本表只有两种状态**；过渡欠账见计划。

| 形状 | JSON | MsgPack | TOML | YAML | INI |
|---|:---:|:---:|:---:|:---:|:---:|
| S1–S4（列表族） | ✅ | ✅ | ✅ | ✅ | S1 ✅ / 其余 ❌ |
| S5/S6（可空值叶子） | ✅ | ✅ | ❌ | ✅ | ❌ |
| S7（扩展集合成员） | ✅ | ✅ | ✅ | ✅ | ✅ |
| S8（扩展集合元素） | ✅ | ✅ | ✅ | ✅ | ❌ |
| S9（字典） | ✅ | ✅ | ✅ | ✅ | ✅ |
| S10（字典值为集合） | ✅ | ✅ | ✅ | ✅ | ❌ |
| S11a/S11b（递归） | ✅ | ✅ | ✅ | ✅ | ❌ |
| S12（含 null 的字符串列表） | ✅ | ✅ | ❌ | ✅ | ❌ |
| S13（含 null 值的字典） | ✅ | ✅ | ❌ | ✅ | ❌ |
| S14/S15 | 待测，Step 0 探针补测后并入 | | | | |

**与 §3 的关系**：§3 是**格式事实**（输入），§6 是**终态承诺**（输出）。二者在已测格上必须逐格一致 —— 一致性由测试强制（I-4），不是靠人工比对。

## 7. 不变式（必须由测试强制）

| ID | 不变式 |
|---|---|
| **I-1** | 任何未进入生成代码的成员都产生一条**真的出现在 `GetRunResult().Diagnostics` 里**的诊断（id ∈ 001…005）。直接覆死 D-6。 |
| **I-2** | 任何不支持的形状都**不得**产出不可编译的生成代码。 |
| **I-3** | 能力判定只有**一个**来源；该来源必须能表达 §3 的每一格；格式字面量不得散落在判定逻辑里。**实现方式**：单一入口（`Decide`/`DecideShape` + 键类型查询）+ 架构测试扫描，**不改公共 API** —— 类型级禁止需要改 `FormatConfig`（public，已在 `api/*.Gen.public.txt` 中），会与 D5「internal」和 R1 的 `y` bump 冲突。 |
| **I-4** | README 的格式限制矩阵与 §6 必须由同一来源生成，测试断言二者一致。；**R1 口径**：README 只文档化 5 个常见形状（钉现状值），§6 的 14 行终态随 R2 落地并由矩阵生成 |
| **I-5** | 诊断消息含成员名 + 格式 + 形状；⛔ 类与欠账类**可区分**（对应 D-4）。 |
| **I-6** | 每个标记为支持的格，都有**含边界值**的往返测试（R5）。 |
| **I-7** | 每处「不支持」都能回答**丢弃粒度**（丢成员还是丢元素）—— 粒度差异会改变 wire 输出与发布批次，不能含糊。 |

## 8. 兼容性政策

仓库版本是 `<year>.<x>.<y>`，**三根轴**：

| 轴 | 规则 |
|---|---|
| 公共 API 变化（含纯新增） | `x+1` |
| 公共 API 不变，**行为收紧或放开** | `x+1` |
| 公共 API 不变，**仅新增诊断**（wire 不变） | `y+1` |

- **新增诊断必须附 `NoWarn` opt-out 说明**（否则开了 `TreatWarningsAsErrors` 的下游升级即挂）。
- **诊断 id + 消息模板是公共契约**（用户 `NoWarn` 依赖它），变更同样计版本。
- 能力表为 `internal`（D5）⇒ 本批**全程不改** `api/*.public.txt`。
- 逐缺陷 → 版本档的映射表由**计划**维护（属发布说明素材）。

## 9. 附录：证据脚本

§3 的事实表由此脚本产出；原样可跑，输出见计划中的逐字记录。

```bash
python - <<'PYEOF'
import tomllib, yaml, json
toml_cases = {
  "S2 object-list":         '[[V]]\nName = "a"\n[[V]]\nName = "b"\n',
  "S3 nested-int-array":    'V = [[1, 2], [3]]',
  "S4 nested-object-array": 'V = [[{a = 1}], [{b = 2}]]',
  "S5 null-in-array":       'V = [1, null]',
  "S7 set":                 'V = [1, 2, 3]',
  "S8 set-as-element":      'V = [[1], [2, 3]]',
  "S10 dict-value-list":    'V = {a = [1, 2]}',
  "S11 recursion":          '[Child]\nName = "c"\n[Child.Child]\nName = "d"\n',
  "S12 null-in-str-array":  'V = ["a", null, "c"]',
  "S13 null-dict-value":    'V = {a = 1, b = null}',
}
for n, t in toml_cases.items():
    try: print(f"TOML {n:24s} OK", json.dumps(tomllib.loads(t)))
    except Exception: print(f"TOML {n:24s} REJECTED")
yaml_cases = {
  "S5 list<int?> null":  'V:\n- 1\n- null\n- 3\n',
  "S6 nested nullable":  'V:\n- - 1\n  - null\n',
  "S12 list<string?>":   'V:\n- a\n- null\n- c\n',
  "S13 dict null value": 'V:\n  a: 1\n  b: null\n',
  "S11 recursion":       'Child:\n  Child:\n    Name: d\n  Name: c\nName: r\n',
}
for n, t in yaml_cases.items():
    try: print(f"YAML {n:24s} OK", json.dumps(yaml.safe_load(t)))
    except Exception: print(f"YAML {n:24s} REJECTED")
PYEOF
```

**实测结论**：TOML 接受 S2/S3/S4/S7/S8/S10/S11，**拒绝所有含 `null` 的用例**；YAML 接受全部含 `null` 的用例。这是 D1 裁定与 TOML/INI `❌` 的唯一依据。**INI 无独立实现**，其边界见 §3 的约定段。

## 10. 计划指针

实施结构（表怎么建、字段叫什么、25 处判据如何改写、丢弃粒度如何表达、各步如何验收、逐缺陷的版本映射）全部在：

`docs/superpowers/plans/2026-10-25-format-capabilities-r1.md`（R1）— R2 的计划待 R1 落地后另写。
