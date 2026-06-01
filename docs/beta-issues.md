# Beta 模块问题清单

> 420 tests ✅ | 6 modules | 2026-06-01

---

## ✅ 已修复 (2026-05-31 ~ 2026-06-01)

### MsgPack (3/4 已修复)
- [x] **Reader: 构造器堆分配** — `new int[64]`/`new bool[64]` → `[InlineArray(64)]` struct
- [x] **SG: `static _varCounter` 改局部** — static→`ref int` 参数传递
- [x] **SG: bytes 支持** — TypeKindResolver + SG WriteBytes/ReadBytes
- [ ] **SG: 支持 ext 扩展类型** — → P2 v2 规划

### TOML (3/3 已修复)
- [x] **Reader: `goto StartSpan` 重构** — goto→结构化 `while(true)+flag`
- [x] **全局: Trim/TrimEnd 去重** — 统一到 `TextHelpers`
- [x] **全局: IsDigit 去重** — 统一到 `TextHelpers`

### YAML (4/4 已修复)
- [x] **Reader: 多文档 `---`** — 已支持
- [x] **Reader: 复杂键 `?`** — 已支持
- [x] **Reader: `!type` / `&anchor` 跳过** — Sequence 模式已添加
- [x] **Reader: 锚点堆分配** — Dictionary/List → 4 组内联字段 (max 4 anchors × 8 pairs)

### 新增修复 (审计驱动)
- [x] **INI: list 逗号损坏** — backslash-escape 替代裸 join/split
- [x] **TOML: SG dict 编译错误** — EmitDictRead 全类型分支
- [x] **YAML: SG inner list/dict 缺失** — EmitSerializeInline/DeserializeInline 补全
- [x] **5 SG Transform 统一** — 删除 4 份 ~200 行重复 Transform
- [x] **SIMD Vector128→256→512** — 三层分层
- [x] **JSON `stackalloc`** — `new T[256]` → `stackalloc T[256]`
- [x] **Reader 构造器零分配** — 统一 8-field 内联 buffer 追踪
- [x] **JSON 严格前导零** — RFC 8259 §6
- [x] **TOML 特殊浮点** — +nan/inf 字面量
- [x] **INI Dict + Comment + Enum.TryParse**
- [x] **YAML block scalar** — 最小实现

---

## 🟡 v2 规划 — 需要架构变更

> 全部需要 Reader/SG 层的多 token 发射或递归解析能力

| 模块 | 项目 | 阻塞原因 | 建议方案 |
|------|------|---------|---------|
| TOML | Dotted keys | Reader 单 token/Read() | Token 缓冲队列 (inline 4-slot) |
| YAML | Block scalar 完整 | indent 剥离 + chomping + folded | Reader 块读取重构 |
| YAML | Tag 序列化 `!type` | Writer 无 `WriteTag()` | Writer API + `[YamlTag]` attribute |
| JSON | `[JsonConstructor]` | SG 反序列化器 | 构造函数探测 → temp 变量 → 构造调用 |
| MsgPack | ext SG 生成 | TypeKindResolver 缺 `extension` | Converter 优先，或新增 type kind |
| 全局 | `List<List<T>>` | 5 SG 各自深层循环 | ExtractNestedProperties 递归 + SG 代码生成 |

---

## 按优先级排序

```
v1.0 完成线 (已达成):
  Bug 修复 → SG 统一 → 性能优化 → 特性补全

v2.0 规划线 (建议顺序):
  P1: JSON JsonConstructor → TOML dotted keys → YAML block scalar 完整
  P2: YAML Tag → 嵌套集合
  P3: MsgPack ext SG
```
