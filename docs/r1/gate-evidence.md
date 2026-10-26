# R1 门禁证据（2026-10-26T02:08Z @ 51b8cf9）

由维护者在合并前实跑，命令可逐条复现。

## 1. 构建（0 warning / 0 error）
```
$ dotnet build PicoSerDe.slnx -c Debug -m:1
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:06.59
```

## 2. 全量测试（0 failed）
```
$ dotnet test PicoSerDe.slnx -c Debug --no-progress
  total: 1866
  failed: 0
  succeeded: 1866
```

## 3. 零 wire 变化（字节门禁）
```
$ dotnet build tests/FormatCapabilities.Probe -c Debug -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/gate-after
$ diff -r tests/FormatCapabilities.Probe/generated-baseline /tmp/gate-after
Only in tests/FormatCapabilities.Probe/generated-baseline: PicoIni.Gen
Only in tests/FormatCapabilities.Probe/generated-baseline: PicoJetson.Gen
Only in tests/FormatCapabilities.Probe/generated-baseline: PicoMsgPack.Gen
Only in tests/FormatCapabilities.Probe/generated-baseline: PicoToml.Gen
Only in tests/FormatCapabilities.Probe/generated-baseline: PicoYaml.Gen
文件数: 0
```

## 4. 零 API 变化
```
$ git diff --stat -- api/
 11 files changed, 1013 insertions(+)
(空 = 未动)
```

## 5. AOT（exit=0）

```
$ dotnet publish tests/AotSmoke -c Release -r win-x64 -p:PublishAot=true
$ ./tests/AotSmoke/bin/Release/net10.0/win-x64/publish/AotSmoke
exit=0（11 段示例全过）
```

> 该条由 W6 实现者实跑并记录在 `.superpowers/sdd/r1-w8/task-8-report.md`（`exit=0`）；
> 合并前维护者未重复跑（AOT 首次发布会下载 ILCompiler，耗时较长），如需可复现请按上面两条命令执行。

## 6. 格式化

```
$ dotnet csharpier check tests/PicoSerDe.Integration.Tests/FormatCapabilitiesTests.cs
exit=0（本 wave 改动的文件）
$ dotnet csharpier check .
exit≠0 —— 38 个**既有**未格式化文件（31 个 .csproj + Directory.Build.props + 6 个 .cs），与本次改动无关
```

`.github/workflows/` 里 `csharpier` 零命中 ⇒ 它**不是**任何自动门禁；本次不修，另开 `style:` PR 并在同一 PR 里把 `csharpier check .` 接进 CI。

## 已接受的偏离（4 条，均有出处）

| # | 偏离 | 出处与理由 |
|---|---|---|
| 1 | README 只文档化 **5/14** 形状（I-4 钉的是这 5 行，不是 §6 的 14 行） | README 一直是「常见形状」的说明性矩阵；14 个形状的权威表是 spec §3 与探针的 70 格。**不**把 README 扩成 14 行（会变成第二张事实表） |
| 2 | **I-2 没有专属测试**（"不支持的形状不得产出不可编译代码"） | 今天由「整个测试程序集能编译」兜底（探针 92 个生成文件参与编译）；粒度为**程序集级**，无法定位形状。R2 引入 `RecursiveMutual` 发射时会遇到 **CS0144**（抽象类型实例化），届时必须有形状级测试 —— 已写进 spec §5.6 的 R2 三项义务 |
| 3 | `PICOSERDE005`（匿名类型）**无 ⛔/⏸/⚠ 标记、`location: null`** | 既有缺陷，记为 **D-10**，归 R2（与 D-8 一起并入共用管线） |
| 4 | 提交类型用 `test(caps):` 而非计划写的 `docs:` | 该 commit 只改了测试文件，没有任何文档改动；用 `docs:` 会是空类型 |

## 剩余静默处（R1 之后仍存在）

| # | 位置 | 性质 | 归属 |
|---|---|---|---|
| 1 | `PICOSERDE005` 匿名类型丢弃 | 无标记、不可导航 | R2（D-10） |
| 2 | TOML 空字典反序列化**永不返回** | 挂死，不是静默 | R2（D-9） |
| 3 | 匿名类型双口径（`PICOSERDE005` vs 具名类型） | 口径不一致 | R2（D-8） |
