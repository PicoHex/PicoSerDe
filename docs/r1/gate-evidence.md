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

**必须用 `-t:Rebuild`** —— 增量构建会复用上次的 `CompilerGeneratedFilesOutputPath`，从而伪造出"新增/缺失文件"的假失败。

```
$ dotnet build tests/FormatCapabilities.Probe -c Debug -m:1 -t:Rebuild     -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=/tmp/gate2
0 error
生成文件数: 92
$ diff -r tests/FormatCapabilities.Probe/generated-baseline /tmp/gate2
diff_exit=0
diff 输出行数: 0
```

> ⚠️ 这是**手动门禁**：`generated-baseline/` 只被探针 csproj 的 `Compile Remove` 引用，
> **没有测试**会重新生成并比对它。`dotnet test` 全绿**不能**替代这一步。

## 4. 零 API 变化

```
$ git diff --stat f7df2fa..HEAD -- api/      # 本 wave
(空)
$ git diff --stat 63f3134~1..HEAD -- api/    # 整个 R1
(空)
$ git log --oneline 63f3134~1..HEAD -- api/ | wc -l
0
```

## 5. AOT

```
$ dotnet publish PicoJetson/samples/PicoJetson.Sample/PicoJetson.Sample.csproj     -c Release -r win-x64 -p:PublishAot=true -o /tmp/r1-aot
$ /tmp/r1-aot/PicoJetson.Sample.exe
exit=0
```

> 该条为 W6 实现者实跑（`.superpowers/sdd/r1-w8/task-8-report.md`，publish 成功 + 原生 exe 运行 11 段示例全过，`exit=0`）；
> **合并前维护者未重跑**（AOT 首次发布会下载 ILCompiler）。R1 零 wire / 零 API ⇒ 该面未被本次改动触及。

## 已接受的偏离（4 条，均有出处）

| # | 偏离 | 出处与理由 |
|---|---|---|
| 1 | README 只文档化 **5/14** 形状（I-4 钉的是这 5 行，不是 §6 的 14 行） | README 一直是「常见形状」的说明性矩阵；14 个形状的权威表是 spec §3 与探针的 70 格。**不**把 README 扩成 14 行（会变成第二张事实表）。**所有者**：spec §7 的 I-4 措辞由 R1 修订为「README = 现状 5 行；§6 的 14 行随 R2 落地并由矩阵生成」 |
| 2 | **I-2 没有专属测试**（"不支持的形状不得产出不可编译代码"） | 今天由「整个测试程序集能编译」兜底（探针 92 个生成文件参与编译）；粒度为**程序集级**，无法定位形状。R2 引入 `RecursiveMutual` 发射时会遇到 **CS0144**（抽象类型实例化），届时必须有形状级测试 —— 已写进 spec §5.6 的 R2 三项义务 |
| 3 | `PICOSERDE005`（匿名类型）**无 ⛔/⏸/⚠ 标记、`location: null`** | 既有缺陷，记为 **D-10**，归 R2（与 D-8 一起并入共用管线） |
| 4 | 提交类型用 `test(caps):` 而非计划写的 `docs:` | 该 commit 只改了测试文件，没有任何文档改动；用 `docs:` 会是空类型 |

## 剩余静默处（R1 之后仍存在）

| # | 位置 | 性质 | 归属 |
|---|---|---|---|
| 1 | `PICOSERDE005` 匿名类型丢弃 | 无标记、不可导航 | R2（D-10） |
| 2 | TOML 空字典反序列化**永不返回** | 挂死，不是静默 | R2（D-9） |
| 3 | 匿名类型双口径（`PICOSERDE005` vs 具名类型） | 口径不一致 | R2（D-8） |
