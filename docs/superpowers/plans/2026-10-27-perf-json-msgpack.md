# JSON/MsgPack 性能修复计划（P0-1 → P0-3）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 关掉 `.superpowers/sdd/perf-rootcause/analysis.md` 里归因出的三条主因，把 JSON 的差距从 1.9×（最差 5.6×）压到接近 1×，**且不改变任何可观察行为**。

**Architecture:** 三条修复都在**生成器**（`PicoJetson.Gen`）与**运行时读写器**（`JsonReader`/`JsonWriter`）里，不动公共 API、不动表、不动其它格式。每条都是"加一条快路径 + 保留原路径作为回退"，因此语义等价性由"快慢两路结果相同"的测试强制。

**Spec:** `.superpowers/sdd/perf-rootcause/analysis.md`（归因与量化证据）；`README.md#performance`（发布协议）

## Global Constraints

- **零行为变化**：`PropertyNameCaseInsensitive` 默认值**不改**（保持 `true`）；命名策略、`UnmappedMemberHandling`、转义规则、数字格式、日期格式**一一保持**。快路径只负责"更快地得到同一个答案"，回退路径保留原实现。
- **每个任务都必须过**：`dotnet build PicoSerDe.slnx -c Debug -m:1` → 0 warning/0 error；`dotnet test PicoSerDe.slnx -c Debug --no-progress` → **0 failed**（当前 2075）。
- **语义等价性证明**：每个任务新增一个"快路径 vs 原路径"的对照测试（同一输入、两条路径、断言输出逐字节相同），而不是只跑既有测试。
- **性能必须有前后数字**：用 `PicoJetson.Benchmarks`（按 README 协议）在**同一台机器**上跑改前/改后；报告里给出 median 与场景胜负，**不写"感觉更快"**。
- 生成代码会变 ⇒ `tests/FormatCapabilities.Probe/generated-baseline/` 必须**同 commit 刷新**；但**往返输出（wire）必须不变** —— 由跨格式集成测试与 cross-validation 强制。
- 提交前 `dotnet csharpier format`；不要 push / merge 主工作树。

## Review Focus

- **大小写不敏感的语义边界**：文档里 `"name"` vs `"Name"` 在默认选项下必须仍能匹配（快路径失配 → 回退 → 命中）。
- **命名策略**：设了 `PropertyNamingPolicy` 时属性名不是编译期常量，**不得**让快路径误命中。
- **转义规则**：`"` `\` 控制字符 `<` `>` `&`（若开了 encoder）必须与原来逐字节一致。
- **超长字符串**（>85 字符走 `GetBytes(value.ToArray())` 的分支）与 **1 GB/s 级的标量扫描**是性能重点，但**不能**改变对非法转义的报错位置。

---

## Task 1: 读侧属性名分派 O(1) 化（P0-1，收益最大）

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`（发名分派的 6 处：`891`、`2698`、`3771`、`5048`、`5472`、`5672`；判别式路径 `5385/5599/5632` 同理）
- Modify: `shared/TextHelpers.cs`（新增 `EqExact`，`Eq` 加 `AggressiveInlining`）
- Test: `PicoJetson/tests/PicoJetson.Integration.Tests/`（新增对照测试）

**Interfaces:**
- Consumes: `JsonReader` 现有的 `ReadStringOrPropertySpan()`（`JsonReader.cs:997`）
- Produces: 生成代码新形状 `switch (span.Length) { case 4: if (span.SequenceEqual("Name"u8)) {...} ... }`，未命中再走原 `TextHelpers.Eq` 链

- [ ] **Step 1: 写失败测试（性能对照）**：为 `TenInts` 与 `LargeFlat50` 各写一个测试，断言"快路径与回退路径解析同一文档得到相同对象"，并在测试里带一个**互斥断言**：`PropertyNameCaseInsensitive=false` 时，错大小写的文档必须**失败**（证明快路径没有偷偷放宽）。
- [ ] **Step 2: 跑，确认红**（现在没有快路径这个概念，测试通过与否见预期；若已绿说明只测了语义没测路径 —— 改成用反射/生成代码文本断言两条路径都存在于生成文件里）。
- [ ] **Step 3: 生成器改发 `switch (length)` + `SequenceEqual(u8 literal)` 快路径**，未命中且选项要求大小写不敏感时回退原链。
- [ ] **Step 4: 刷新 `generated-baseline/`**（生成代码变了），并确认**wire 输出不变**（跑跨格式集成测试）。
- [ ] **Step 5: 全量 0 failed + 基准前后对比**（`LargeFlat DES`、`TenInts DES`）。
- [ ] **Step 6: 提交**

**预期**：`LargeFlat DES` −50%（1.93× → ≈0.95×）；2 属性对象变化很小。

## Task 2: 写侧属性名编译期编码（P0-2）

**Files:**
- Modify: `PicoJetson/src/PicoJetson.Gen/JsonSerializerGenerator.cs`（`1753-1758` 的 `__name_X` 生成处）
- Modify: `PicoJetson/src/PicoJetson/JsonWriter.cs`（新增 `WritePropertyName(ReadOnlySpan<byte> preEncoded)`，避免 `GetMaxByteCount`/`GetBytes`）
- Test: 同上，新增"名称编码前后逐字节相同"的对照测试

**Interfaces:**
- Produces: 生成代码 `jw.WritePropertyName(PropName_F01)`，其中 `private static ReadOnlySpan<byte> PropName_F01 => "F01"u8;`

- [ ] **Step 1: 快路径与回退的等价测试**：设了 `PropertyNamingPolicy`（CamelCase）时输出必须与今天完全一致（逐字节）。
- [ ] **Step 2: 生成静态 UTF-8 名称**；仅当"无命名策略或策略是已知无损策略"时用静态名，否则退回 `ConvertName`。
- [ ] **Step 3: `JsonWriter` 加 `ReadOnlySpan<byte>` 重载**（跳过编码与第二次转义扫描，但**转义仍要查**）。
- [ ] **Step 4: 刷新基线、全量绿、基准对比**（`LargeFlat SER`、`Simple SER`）。
- [ ] **Step 5: 提交**

**预期**：`LargeFlat SER` −60%；每属性省 ~21.6 ns。

## Task 3: 字符串扫描向量化 + 长串去二次分配（P0-3）

**Files:**
- Modify: `PicoJetson/src/PicoJetson/JsonReader.cs:997`（`ReadStringOrPropertySpan`）
- Modify: `PicoJetson/src/PicoJetson/JsonWriter.cs:417`（`value.ToArray()` 分支）
- Test: 转义/非法转义/控制字符的既有测试必须全绿（这是回归防线）

- [ ] **Step 1: 现有转义测试清点**：确认已覆盖 `\"`、`\\`、`\uXXXX`、裸控制字符、非法转义 —— 缺的补上（这是**行为护栏**，先有护栏再动性能）。
- [ ] **Step 2: 读侧改 `span.IndexOfAny((byte)'"', (byte)'\\')`**（或 `Utf8JsonReader` 等价的向量化搜索），保持"遇到 `\` 就回退到转义解析"的控制流不变。
- [ ] **Step 3: 写侧长串改 `Encoding.UTF8.GetBytes(span, dest)`**（span 重载，写入预取缓冲），消除 `ToArray()` 的中间数组。
- [ ] **Step 4: 全量绿 + 基准对比**（`LargeString DES`、`LargeString SER`、`TenStrings100`）。
- [ ] **Step 5: 提交**

**预期**：`LargeString DES` −70%（2.97× → ≈0.9×），写侧 1 KB 字符串从 4184 B/op 降到 ≈1064 B/op。

---

## 每个任务的验证配方（同一个脚本，避免"凭感觉"）

```bash
# 语义
dotnet test PicoSerDe.slnx -c Debug --no-progress          # 0 failed
# 生成代码确实变了 / wire 没变
diff -r <旧基线> <新基线>                                   # 应只有生成代码差异，且已同 commit 刷新
# 性能（按 README 协议，一次一个，机器空闲）
dotnet run -c Release --project PicoJetson/benchmarks/PicoJetson.Benchmarks --no-launch-profile
```
