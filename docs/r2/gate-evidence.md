# R2 门禁证据（2026-10-26T10:20Z @ `b99e35f`，branch `r2/w9`）

> **这份文件里每一条输出都是本机实跑的**：命令在上、它的真实输出在下。
> R1 的教训是「把没跑过的命令写进证据文件」—— 所以本文件**没有**任何一条是转抄的；
> 唯一不属于六项门禁的输入是 `git show 739e820:…`（R2 起点入库的快照），它本身就是可复现命令。
> 凡本任务没实跑的，在 §8 明写「未在本地重跑」。
> 排版约定：worktree 的绝对路径前缀在下面缩写为 `D:\…`；被截断处一律标 `…`。
> 这两点是证文里仅有的编辑，命令与输出本身逐字未改。

## 0. 环境

```
$ dotnet --version
10.0.401
$ git rev-parse --short HEAD
b99e35f
$ git status --porcelain
?? docs/r2/          # 本任务的唯一改动（六项门禁跑完之后）
```

R2 的起点（R1 收尾提交，用于「之前/之后」）：`739e820`。

---

## 1. 构建（0 warning / 0 error）

```
$ dotnet build PicoSerDe.slnx -c Debug -m:1
  PicoSerDe.Integration.Tests -> D:\MyProjects\PicoHex\PicoSerDe\.worktrees\r2-w9\tests\PicoSerDe.Integration.Tests\bin\Debug\net10.0\PicoSerDe.Integration.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:01:50.87
```

（命令是 `dotnet build PicoSerDe.slnx -c Debug -m:1 2>&1 | tail -25`；上面是那 25 行的尾部原文，
完整输出在它之前，是逐个项目的 `-> …dll` 行。）

## 2. 全量测试（0 failed）

```
$ dotnet test PicoSerDe.slnx -c Debug --no-progress
  …
  Test run summary: Passed!
  D:\…\PicoToml\tests\PicoToml.Tests\bin\Debug\net10.0\PicoToml.Tests.dll (net10.0|x64) passed (4s 997ms)
  D:\…\PicoSerDe.Core\tests\PicoSerDe.Core.Tests\bin\Debug\net10.0\PicoSerDe.Core.Tests.dll (net10.0|x64) passed (2s 682ms)
  D:\…\PicoMsgPack\tests\PicoMsgPack.Tests\bin\Debug\net10.0\PicoMsgPack.Tests.dll (net10.0|x64) passed (2s 961ms)
  D:\…\tests\PicoSerDe.Integration.Tests\bin\Debug\net10.0\PicoSerDe.Integration.Tests.dll (net10.0|x64) passed (9s 245ms)
  D:\…\PicoJetson\tests\PicoJetson.Functional.Tests\bin\Debug\net10.0\PicoJetson.Functional.Tests.dll (net10.0|x64) passed (2s 284ms)
  D:\…\PicoYaml\tests\PicoYaml.Tests\bin\Debug\net10.0\PicoYaml.Tests.dll (net10.0|x64) passed (2s 892ms)
  D:\…\PicoJetson\tests\PicoJetson.Integration.Tests\bin\Debug\net10.0\PicoJetson.Integration.Tests.dll (net10.0|x64) passed (8s 466ms)
  D:\…\PicoIni\tests\PicoIni.Tests\bin\Debug\net10.0\PicoIni.Tests.dll (net10.0|x64) passed (2s 758ms)
  D:\…\PicoJetson\tests\PicoJetson.Unit.Tests\bin\Debug\net10.0\PicoJetson.Unit.Tests.dll (net10.0|x64) passed (3s 002ms)

  total: 2009
  failed: 0
  succeeded: 2009
  skipped: 0
  duration: 11s 112ms
```

> 这里的数字是**门禁的输出**，不是发布说明的素材：按计划的规定，发布说明**不写测试数量**（§7）。

**提交后又跑了一遍**（本文件已入库的那个 commit 之后，证明 `docs/r2/` 的入库不影响任何一项）：

```
$ dotnet build PicoSerDe.slnx -c Debug -m:1   →  Build succeeded. 0 Warning(s) 0 Error(s), Time Elapsed 00:01:22.09
$ dotnet test  PicoSerDe.slnx -c Debug --no-progress  →  total: 2009  failed: 0  succeeded: 2009  skipped: 0  duration: 12s 630ms
```

---

## 3. 终态检查：spec §6 的 ✅ 格 vs 实测 70 格

### 3.1 先证明「实测」就是入库的基线

探针重跑一次，把结果与 `baseline-cells.txt` 逐字节比 —— 只有这一步过了，
「§6 vs `baseline-cells.txt`」才等价于「§6 vs 今天真跑出来的东西」。

```
$ dotnet run --project tests/FormatCapabilities.Probe -c Debug > /tmp/cells-raw.txt 2> /tmp/cells-err.txt
$ echo $?
0
$ cat /tmp/cells-err.txt
# cells: 54 OK, 16 MISMATCH, 0 TIMEOUT, 0 SKIPPED_AFTER_TIMEOUT, 70 total
# abandoned worker threads never returned: 0
# observations (CELL-OBS, outside the 70 cells): 5 OK, 5 MISMATCH, 0 TIMEOUT, 0 SKIPPED_AFTER_TIMEOUT, 10 total

$ LC_ALL=C sort /tmp/cells-raw.txt > /tmp/cells-now.txt
$ grep -c '^CELL|' /tmp/cells-now.txt
70
$ diff <(grep '^CELL|' /tmp/cells-now.txt) tests/FormatCapabilities.Probe/baseline-cells.txt
（无输出，exit 0）
```

（`cells-raw.txt` 共 96 行 = 70 格 + 10 条 `CELL-OBS` + 16 条 `CSC : warning PICOSERDE00x` ——
那 16 条警告是**探针故意**要看见的 ⛔ 诊断，见 `FormatCapabilities.Probe.csproj` 顶部注释：
探针不加 `NoWarn`。它们不参与任何计数。）

### 3.2 逐格比对（脚本入 `docs/r2/target-check.py`）

脚本直接解析 spec §6 与 `baseline-cells.txt`（**不手抄表**）；`S1–S4` / `S5/S6` / `S11a/S11b`
按行展开，`S1 ✅ / 其余 ❌` 按 INI 列的特殊写法展开，`待测` 行不计（§6 的 S14/S15 不是承诺）。

```
$ python docs/r2/target-check.py
expected cells from spec §6 : 70  (skip: 0 )
expected ✅                  : 55
expected ❌                  : 15
probe OK                     : 54 / 70
inconsistencies              : 1
  MISMATCH ini     S9    spec ✅, probe MISMATCH
```

### 3.3 不一致清单（**共 1 格**）

| 格 | spec §6 | 实测 | 判定 |
|---|---|---|---|
| `ini × S9` | `✅ ⁵` | `MISMATCH` | **已知的规格口径问题，不是欠账**（见下） |

**为什么这一格是对的**：`✅ ⁵` 的脚注 ⁵（spec:174，§3 表与 §6 表共用同一标记）把这一格限定为
「**只在键不含 `\n` / `\r` / `=` 时成立**」；探针的 boundary 值**恰好**用了键 `n\nline`：

```
CELL|ini|S9|MISMATCH|empty=[V]\r\n ; single=[V]\r\na = 7\r\n ; boundary=[V]\r\na = 2147483647\r\np\|ipe = 0\r\nq"uote = -1\r\nn\nline = 2\r\n|empty={} ; single={"a":7} ; boundary={"a":2147483647,"n\nline":2,"p\|ipe":0,"q"uote":-1}|empty={} ; single={"a":7} ; boundary=<deserialize threw FormatException: Invalid INI line: expected '='.>
```

即实测踩中的是脚注排除掉的那个值，`empty` / `single` 两个值都精确回读
（`[V]\na = 7` ⇒ `{"a":7}`）。所以这一格是**条件成立时为 ✅ 的格**，探针的 boundary 值故意越界。

> 口径说明（一句）：§6 的 ✅ 共 **55** 格，实测 OK **54** 格 —— 差的就是这一格；
> 其余的 ✅ 全部 OK，15 个 ❌ 全部不 OK（无一格「说了 ❌ 却回读成功」）。

### 3.4 无回归：翻转的格只有「向终态」这一个方向

```
$ git diff --numstat 739e820..HEAD -- tests/FormatCapabilities.Probe/baseline-cells.txt
23	23	tests/FormatCapabilities.Probe/baseline-cells.txt
$ git diff 739e820..HEAD -- tests/FormatCapabilities.Probe/baseline-cells.txt | grep '^-CELL' | cut -d'|' -f2,3,4
ini|S7|MISMATCH
json|S10|MISMATCH
json|S8|MISMATCH
msgpack|S10|MISMATCH
msgpack|S7|MISMATCH
msgpack|S8|MISMATCH
toml|S10|MISMATCH
toml|S11a|MISMATCH
toml|S11b|MISMATCH
toml|S4|MISMATCH
toml|S7|MISMATCH
toml|S8|MISMATCH
toml|S9|TIMEOUT
yaml|S10|MISMATCH
yaml|S11a|MISMATCH
yaml|S11b|MISMATCH
yaml|S12|MISMATCH
yaml|S13|MISMATCH
yaml|S5|MISMATCH
yaml|S6|MISMATCH
yaml|S7|MISMATCH
yaml|S8|MISMATCH
yaml|S9|MISMATCH
$ git diff 739e820..HEAD -- tests/FormatCapabilities.Probe/baseline-cells.txt | grep '^+CELL' | cut -d'|' -f2,3,4
ini|S7|OK
json|S10|OK
json|S8|OK
msgpack|S10|OK
msgpack|S7|OK
msgpack|S8|OK
toml|S10|OK
toml|S11a|OK
toml|S11b|OK
toml|S4|OK
toml|S7|OK
toml|S8|OK
toml|S9|OK
yaml|S10|OK
yaml|S11a|OK
yaml|S11b|OK
yaml|S12|OK
yaml|S13|OK
yaml|S5|OK
yaml|S6|OK
yaml|S7|OK
yaml|S8|OK
yaml|S9|OK
```

**23 行删 / 23 行加，键集合完全相同，verdict 一律 `MISMATCH`/`TIMEOUT` → `OK`；
没有任何一行是 `OK` → 非 `OK`（即零回归）。** 计数也从 `31 OK / 38 MISMATCH / 1 TIMEOUT` 变成
`54 OK / 16 MISMATCH / 0 TIMEOUT`。

> **与任务书数字的差异（必须记）**：任务书写「R2 翻的 24 格 + `toml S9` 接力 = 25」，实测是 **23 格**。
> 差额可逐项对账，不是少做了：
> * Task 2 的计划数「8」= S7×4 + S8×4，但 `S7×INI` 后来由 `4461435` **改挂 Task 6**，
>   Task 2 实际交付 **7**（`d3d7f4a`：`json|S8 msgpack|S7 msgpack|S8 toml|S7 toml|S8 yaml|S7 yaml|S8`）；
>   Task 6 交付 **4**（`6a37ee3`：`ini|S7 toml|S4 toml|S9 yaml|S9`）。即「8」与「3」里都算了 `S7×INI` 一次。
> * 「+1 接力」的 `toml|S9` 已经在上面 23 行里（`TIMEOUT → OK`），不能另加一次。
> * §6 的 ✅ 共 55 格、R2 起点 31 格 OK ⇒ 需要翻 **24** 格；那第 24 格是 `ini|S9`，
>   它是 `✅ ⁵`，按 §3.3 的理由**不可能**变成 OK。
> ⇒ 可翻的 23 格全翻了，`ini|S9` 按脚注保持 MISMATCH。

---

## 4. `api/`：只增不改

R2 是 `x+1` minor（计划 Global Constraints，含 T4 的实测修正：「新增」合法，「改/删」不合法）。

```
$ git diff --stat 739e820..HEAD -- api/
 api/PicoSerDe.Core.public.txt | 1 +
 api/PicoToml.public.txt       | 3 +++
 api/PicoYaml.public.txt       | 1 +
 3 files changed, 5 insertions(+)
$ git diff --numstat 739e820..HEAD -- api/
1	0	api/PicoSerDe.Core.public.txt
3	0	api/PicoToml.public.txt
1	0	api/PicoYaml.public.txt
$ git diff 739e820..HEAD -- api/ | grep '^-[^-]'
（无输出 —— 删除 0 行，修改 0 行）
```

**新增的 5 行（全部）**：

```
+M:PicoSerDe.Core.TextHelpers.EqLastSegment(System.ReadOnlySpan`1<byte> path, System.ReadOnlySpan`1<byte> name) : bool static
+M:PicoToml.TomlWriter.EndTable() : void
+M:PicoToml.TomlWriter.WriteNestedTable(System.ReadOnlySpan`1<byte> utf8Name) : void
+M:PicoToml.TomlWriter.WriteStartInlineTableValue() : void
+M:PicoYaml.YamlWriter.WriteNull() : void
```

**反向验证**：入库的 11 份基线是否与**今天构建出来的程序集**一致（避免「基线改了、代码没改」这类假绿）：

```
$ dotnet run --project scripts/api-surface -c Debug -- dump PicoToml/src/PicoToml/bin/Debug/net10.0/PicoToml.dll --out /tmp/api-dump/PicoToml.public.txt
$ # …其余 10 个同形：普通包用 src/<Pkg>/bin/Debug/net10.0/<Pkg>.dll，五个 *.Gen 用 src/<Pkg>/bin/Debug/netstandard2.0/<Pkg>.dll
$ for f in api/*.public.txt; do b=$(basename $f); if diff -q <(tr -d '\r' < "$f") <(tr -d '\r' < "/tmp/api-dump/$b") >/dev/null; then echo "SAME  $b"; else echo "DIFF  $b"; fi; done
SAME  PicoIni.Gen.public.txt
SAME  PicoIni.public.txt
SAME  PicoJetson.Gen.public.txt
SAME  PicoJetson.public.txt
SAME  PicoMsgPack.Gen.public.txt
SAME  PicoMsgPack.public.txt
SAME  PicoSerDe.Core.public.txt
SAME  PicoToml.Gen.public.txt
SAME  PicoToml.public.txt
SAME  PicoYaml.Gen.public.txt
SAME  PicoYaml.public.txt
```

11/11 逐字节相同（`tr -d '\r'` 只为抹掉 CRLF；dump 用的是 `Debug` 构建的
`net10.0` / `netstandard2.0` 产物，即 `src/*/bin/Debug/...`）。

---

## 5. 字节门禁（`-t:Rebuild` + `diff -r generated-baseline`）

R2 的口径不是「逐字节不变」，而是「基线与当前生成的差异 = 已声明的形状变化」——
所以这一项要求**无输出**：全部变化都已在各自任务的同一个 commit 里刷进 `generated-baseline/`。

```
$ OUT=$(cygpath -w /tmp/r2-gate2)
$ dotnet build tests/FormatCapabilities.Probe -c Debug -m:1 -t:Rebuild \
    -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath="$OUT"
  …
  CSC : warning PICOSERDE004: ⛔ Member 'FormatCapabilities.Probe.S13Toml.V' … cannot be represented by the toml format. …
    16 Warning(s)
    0 Error(s)

Time Elapsed 00:00:20.91
$ find /tmp/r2-gate2 -name '*.cs' | wc -l
96
$ diff -r tests/FormatCapabilities.Probe/generated-baseline /tmp/r2-gate2
（无输出）
$ echo $?
0
```

* **必须 `-t:Rebuild`**：增量构建会复用上次的 `CompilerGeneratedFilesOutputPath`，凭空产生「多文件/少文件」。
* 那 16 条 `PICOSERDE00x` 是探针语料里**应当**出现的 ⛔ 诊断（探针不加 `NoWarn`），不是本次构建的问题；
  解决方案级构建（§1）是 0 warning / 0 error。
* 这是**手动门禁**：`generated-baseline/` 只被探针 csproj 的 `Compile Remove` 引用，没有测试会重新生成并比对它
  —— `dotnet test` 全绿**不能**替代这一步。

---

## 6. AOT

```
$ dotnet publish PicoJetson/samples/PicoJetson.Sample/PicoJetson.Sample.csproj \
    -c Release -r win-x64 -p:PublishAot=true -o /tmp/r2-aot
  Determining projects to restore...
  Restored D:\…\PicoSerDe.Core\src\PicoSerDe.Core.csproj (in 1.1 sec).
  Restored D:\…\PicoJetson\src\PicoJetson\PicoJetson.csproj (in 1.1 sec).
  Restored D:\…\PicoJetson\samples\PicoJetson.Sample\PicoJetson.Sample.csproj (in 1.09 sec).
  Restored D:\…\PicoJetson\src\PicoJetson.Gen\PicoJetson.Gen.csproj (in 1.11 sec).
  PicoSerDe.Core -> D:\…\src\bin\Release\net10.0\PicoSerDe.Core.dll
  PicoJetson.Gen -> D:\…\src\PicoJetson.Gen\bin\Release\netstandard2.0\PicoJetson.Gen.dll
  PicoJetson -> D:\…\src\PicoJetson\bin\Release\net10.0\PicoJetson.dll
  PicoJetson.Sample -> D:\…\samples\PicoJetson.Sample\bin\Release\net10.0\win-x64\PicoJetson.Sample.dll
  Generating native code
  PicoJetson.Sample -> C:\Users\aeond\AppData\Local\Temp\r2-aot\
（publish exit=0）
```

产物与运行（R1 也做了这一步：AOT 通过了但跑不起来是另一种失败）：

```
$ file /tmp/r2-aot/PicoJetson.Sample.exe
PE32+ executable for MS Windows 6.00 (console), x86-64, 6 sections
$ ls -l /tmp/r2-aot/PicoJetson.Sample.exe
-rwxr-xr-x 1 aeond 197609 1893888 Oct 26 18:19 /tmp/r2-aot/PicoJetson.Sample.exe
$ cd /tmp/r2-aot && ./PicoJetson.Sample.exe > /tmp/aot-run.txt 2>&1; echo $?
0
$ tail -5 /tmp/aot-run.txt
  Streaming deserialization:
  Streamed 3 people OK
  Array-mode streamed 2 people OK
$ wc -l < /tmp/aot-run.txt
60
```

**`exit=0`（publish）+ `exit=0`（原生 exe 跑完 60 行示例输出）。**

---

## 7. 发布说明（`x+1`）

### 7.1 版本

```
$ dotnet run --project scripts/api-surface -c Debug -- next-version --last v2026.10.0 --api-changed true --explain
last=v2026.10.0 apiChanged=true year=2026 resetOnYearChange=false -> 2026.11.0
2026.11.0
```

依据 spec §8 的三轴：本批**既有公共 API 新增**（§4 的 5 行）**又有行为放开/收紧**（§7.2），
两条都指向 `x+1`。仓库最新 tag 是 `v2026.10.0`（`git tag --sort=-creatordate | head -1`）。

### 7.2 从 ❌ 变 ✅ 的 23 格（逐格）

| 格式 | 形状 | 之前 | 之后 | 由谁翻 |
|---|---|---|:---:|---|
| json | S8（扩展集合**作元素**） | MISMATCH | OK | T2 `d3d7f4a` |
| msgpack | S7 / S8 | MISMATCH | OK | T2 `d3d7f4a` |
| toml | S7 / S8 | MISMATCH | OK | T2 `d3d7f4a` |
| yaml | S7 / S8 | MISMATCH | OK | T2 `d3d7f4a` |
| json / msgpack / toml / yaml | S10（字典**值**是集合） | MISMATCH | OK | T3 `1beb89e` |
| yaml | S5 / S6 / S12 / S13（含 null 的形状） | MISMATCH | OK | T4 `64f2f76` |
| toml / yaml | S11a / S11b（自递归 / 互递归） | MISMATCH | OK | T5 `0a44f97` |
| ini | S7（扩展集合**成员**） | MISMATCH | OK | T6 `6a37ee3` |
| toml | S4（对象**嵌套数组**） | MISMATCH | OK | T6 `6a37ee3` |
| yaml | S9（字典键含 `\n`） | MISMATCH | OK | T6 `6a37ee3` |
| toml | S9（字典） | **TIMEOUT** | OK | T0 `599e99c`+`f34d5be`（挂死）/ T6 `6a37ee3`（写侧键转义） |

合计 **23 格**（明细见 §3.4 的 23 行 diff）。口径：`✅` = «生成代码写得出、也读得回，且探针三个值往返等价»。

### 7.3 wire 输出变了什么（升级须知）

变化集中在「**以前整段丢掉的成员，现在写出来了**」，以及「**读侧匹配放宽**」。逐项（引号内为探针实测 wire）：

**① YAML 现在写 null 字面量**（S5/S6/S12/S13）

| 形状 | 之前（写侧） | 之后 |
|---|---|---|
| `List<int?>`（S5） | 成员**整体不出现**（wire `<empty>`，写出来是空文档） | `V:\n- 2147483647\n- null\n- 0\n` |
| `List<List<int?>>`（S6） | 同上 `<empty>` | `V:\n-\n  - null\n  - 2147483647\n-\n  - null\n` |
| `Dictionary<string,int?>`（S13） | 同上 `<empty>` | `V:\n  a: 2147483647\n  n: null\n  e: 0\n` |
| `List<string?>`（S12） | 列表写得出来，但 **`null` 元素被静默跳过**：5 个元素只写 4 个 | 写 `- null`，5 个元素全写 |

> S12 的这一变在 `baseline-cells.txt` 的 **wire 列看不见**（该列按 180 字符截断，正好断在 `- "p` 处）；
> 可见的是 expected / actual 两列 —— 旧 actual 少了 `null` 那一项（4 项：`p|ipe…` / `q"uote` / 空串 / `ünï 测试`），
> 新的 actual 与 expected 逐项相等（5 项）。这就是它由 MISMATCH 转 OK 的原因。

**② TOML / YAML 的递归成员现在写出整棵链**（S11a/S11b）

| 之前 | 之后 |
|---|---|
| `Value = 1\n`（只写根，`Child` 整段丢） | `Value = 1\n[Child]\nValue = 2\n[Child.Child]\nValue = 3\n` |
| `Value: 1\n` | `Value: 1\nChild:\n  Value: 2\n  Child:\n    Value: 3\n` |

**③ 字典值为集合（S10）现在写出来**（JSON/MsgPack/TOML/YAML）

| 之前 | 之后 |
|---|---|
| `{}` | `{"V":{"a":[2147483647],"e":[],"z":[-1,0]}}` |
| `80`（三个值都退化成空 map） | `empty=810080` / `single=810081A1619107` / `boundary=810083A16191D27FFFFFFFA16590A17A92FF00` |
| 整成员丢失（TOML 的 boundary 值） | `[V]\na = [2147483647]\ne = []\nz = [-1, 0]\n` |

**④ TOML 的键现在按 TOML 1.0 bare-key 文法加引号**（S9 写侧，D-9b）
`NeedsKeyQuoting` 由 6 字符黑名单换成文法判定 ⇒ 含换行/引号/`|` 的键写得出也读得回：

```
before:  n\nline = 2        （裸写，读不回来）
after:   "n\nline" = 2
```

「before」这一行有出处（该格在 R2 起点还是 `TIMEOUT`，所以取 T6 修写侧**之前**的入库基线 `3b29a9f`）：
`git show 3b29a9f:tests/FormatCapabilities.Probe/baseline-cells.txt | grep "^CELL|toml|S9|"` ⇒ 写侧边界值
`[V]\na = 2147483647\np\|ipe = 0\n"q\"uote" = -1\nn\nline = 2\n`（键里的换行是**真的**换行字节，键 `n\nline` 没有加引号），读侧
`<deserialize threw FormatException: Invalid TOML line: expected '='.>`；今天同一格是 OK。

同一修法顺手改了**推导键**的 wire：`$type = "derived"` → `"$type" = "derived"`
（`$` 不在 `[A-Za-z0-9_-]+` 里，旧输出其实不是 TOML —— `tomllib` 会拒）。读侧仍**接受**裸形式（canonical write / lenient read）。

**⑤ YAML 的字典键也加了引号**（S9）：`  a: 2147483647\n  "p|ipe": 0\n  "q\"uote": -1\n  "n\nline": 2\n`。

**⑥ TOML 的对象嵌套数组（S4）现在写出来**：`V = [[{ D = "123456789.123456789", F = 1.5, N = 2147483647, Name = "…" }], …]`。

**⑦ TOML 空表不再挂死**（S9 的 empty 值）：`[V]\n` 现在能读回 `{}`（之前 `Deserialize` 永不返回）。

**⑧ 读侧放宽**：数组表 / 成员匹配由「整条 `TablePath` 相等」改为「**最后一段**相等」
（`TextHelpers.EqLastSegment`，§4 新增 1 行 API）。效果：手写的标准嵌套表头 `[[Parent.Items]]`
现在能匹配到 `Items` 成员 —— 之前这种输入**元素全部静默丢失**。同时新增 `BytesConsumed` 推进守卫，
「同一个位置读两次」的输入不再产生空元素或自旋。

> **对既有用户的影响**：矩阵里**本来就是 ✅** 的格也可能看到生成代码变化（读侧加固）。
> 探针语料里 32 个 `*.g.cs` 与 R1 基线不同（`git diff --stat 739e820..HEAD -- tests/FormatCapabilities.Probe/generated-baseline/` ⇒ `32 files changed, 2184 insertions(+), 60 deletions(-)`），
> 其中 `toml|S2` / `yaml|S2` 这类格 **wire 没变**（它们的 70 格行与 R1 基线逐字节相同）、变的只是读侧健壮性。
> （`S14/S15` 不进这 70 格；它们的 `baseline-observations.txt` 是 R2 才入库的新文件，没有 R1 快照可比。）

### 7.4 新增的公开 API（5 行，`x+1` 的正当理由）

| 成员 | 为什么必须是 public |
|---|---|
| `PicoYaml.YamlWriter.WriteNull()` | 生成的 YAML 写侧代码在 `PicoYaml.Gen` 程序集里，写 null 没有别的出口 |
| `PicoToml.TomlWriter.EndTable()` / `WriteNestedTable(ReadOnlySpan<byte>)` | 递归成员要发嵌套表 `[Child]` / `[Child.Child]`，生成代码只能调 public 成员 |
| `PicoToml.TomlWriter.WriteStartInlineTableValue()` | 嵌套对象数组要发内联表 `[[{ … }]]` |
| `PicoSerDe.Core.TextHelpers.EqLastSegment(ReadOnlySpan<byte>, ReadOnlySpan<byte>)` | 读侧按「最后一段」匹配表头（§7.3 ⑧） |

**没有**修改或删除任何既有签名（§4 的 `grep '^-[^-]'` 为空）。

### 7.5 诊断契约的变化（`NoWarn` 依赖它，必须写）

* **`PICOSERDE005`（匿名类型）的 id 不变、消息模板变了**（D-10，R2 T8 `53dfb69`）：
  旧模板把**我们**的缺失说成**格式的事实** ——
  `Anonymous-type member '{0}' cannot be serialized by {1} — it will be ignored…`；
  新模板改成 ⏸ 口径 + `reopen:` 尾句：
  `⏸ Member '{0}' (declared type '{1}') of an anonymous type was dropped by the {2} source generator: the {2} format can hold a collection, but this generator has no collection form in its anonymous-type emitter yet. … reopen: …`
  （⛔ 兄弟模板由 `FormatCapabilities.CanRepresent` 推导，今天不可达 —— 五格式对 S1 全 ✅。）
* **`location` 不再是 `null`**：改为成员自己的声明位置 ⇒ 逐站点可导航（取代旧行为「`Location.None` ⇒ 无法逐站点抑制」）。
  我可复述的是 T8 报告里的实测（`line=10 col=17 textAtSpan='Xs'`、`Location.IsInSource == true`）；**编译级 `#pragma` 抑制没有专门的用例**（T8 报告第 ⑥ 条自述），我**未**在本地验证抑制效果。
* `PICOSERDE001…004` 的 id **与**模板在 R2 中**未变**（`git diff 739e820..HEAD -- shared/Gen/ | grep messageFormat` 只有 `PICOSERDE005` 两条增删）。

### 7.6 已知限制（随本批文档化）

| # | 限制 | 状态 |
|---|---|---|
| 1 | **`S9 × INI` 的键字符集**：键含 `\n` / `\r` / `=` 时写得出、读不回（`IniWriter.WriteKey` 无键转义，`IniReader` 的键扫描在这些字节处终止）。这是**值级**约定问题，不是能力欠账 —— 故 §6 的该格是 `✅ ⁵`（附条件），探针 boundary 值落在条件外，永远 MISMATCH | 本批**不做**（改公共 wire 约定属值级议题；已记入 R5 清单） |
| 2 | TOML **表头名**（`WriteTable` / `WriteNestedTable` / `WriteArrayTable`）仍裸写 | pre-existing，不在 D-9b 的修复面（D-9b 只覆盖 **KEY**） |
| 3 | **TOML 的 S5/S6/S12/S13** 与 **INI 列的 11 个 ❌ 形状**仍按 ⛔ 在生成期丢弃（探针侧表现为 `MISMATCH`） | 格式事实，不是欠账：TOML 没有 null 字面量（§3 的事实脚本对含 null 的用例一律 REJECTED）；INI 是扁平的 |
| 4 | 匿名类型的**集合**成员仍丢（⏸ + `reopen:`）。按 `PicoMsgPack/README.md:73-75` 的解释：map/array 头要先写元素个数，所以只有 JSON 写侧能发匿名集合 | 记录在案的欠账，id 仍是 `PICOSERDE005` |

---

## 8. 未做的事 / 未重跑的事

| # | 事项 | 说明 |
|---|---|---|
| 1 | **`dotnet csharpier format` 未跑** | 本任务没有改任何 `.cs`（`git status --porcelain` 只有 `?? docs/r2/`）⇒ 无格式化对象。**不是**「跑了但没输出」 |
| 2 | **`git push` / merge / 主工作树** | 未做。本 worktree（`.worktrees/r2-w9`, branch `r2/w9`）之外零改动 |
| 3 | **`README.md` 未改** | 按任务书由 T7 定稿（`6458f9e`）。但见下面第 4 条 |
| 4 | **README 的散文段落与实测矩阵冲突（本任务发现，未修）** | 以下五处仍按 R1 的说法写，R2 之后已不成立：<br>① `README.md:301-303`「集合作为字典值 `Dictionary<string, List<T>>` 被丢 `PICOSERDE004`」—— T3 之后 JSON/MsgPack/TOML/YAML 都能往返（S10×4 已 OK）；<br>② `README.md:377-380`「…is dropped in **every** format」—— 同上；<br>③ `PicoMsgPack/README.md:71-73` 同一句；<br>④ `PicoYaml/README.md:118-119`「`List<List<int?>>` / `Dictionary<string, List<int?>>` report `PICOSERDE004`」—— T4 之后 YAML 能写 null（S5/S6/S12/S13 已 OK）；<br>⑤ `PicoYaml/README.md:120`「a **recursive member** (`Node Child` on `Node`) reports `PICOSERDE003`」与 `:122-123`「null string items are skipped on write (YAML writes no null)」—— T5 之后 YAML 的 S11a/S11b 已 OK，T4 之后写的正是 null 字面量。<br>**为什么全绿也抓不到**：I-4 的守卫 `FormatCapabilitiesTests.ReadmeMatrix()`（`:750-762`）只读以 `Shape (C#)` 开头的那 5 行表格（`| Shape (C#) … |`），**表格之外的散文没有任何测试**。**归属：README 所有者（根 README 已由 T7 定稿）/ 下一次文档批**。证据：`sed -n '296,306p;374,382p' README.md; sed -n '69,74p' PicoMsgPack/README.md; sed -n '115,125p' PicoYaml/README.md` |
| 5 | 探针数字 ≠ 任务书数字 | 见 §3.4 的「与任务书数字的差异」：实测翻 **23** 格（任务书写 24+1）。差额可逐项对账，不是漏做 |
| 6 | spec §6 的脚注 ⁵ 位置 | 定义在 spec:174（§3 表格之后），§6（spec:241）只带标记。读者从 §6 需要向上翻 —— 纯可读性问题，本任务不改 spec |
| 7 | `S14/S15` | §6 仍写「待测」；探针把它们当 `CELL-OBS`（5 OK / 5 MISMATCH）观测、**不评分**，不进这 70 格。本批不改其状态 |
