# PicoJetson JsonOptions 实现计划

> **For agentic workers:** Inline execution.

**Goal:** 实现 P1-P3 所有 JsonOptions 选项，与 STJ 对齐

**Architecture:** JsonOptions 通过 ThreadStatic Current 传递到 SG 生成的代码/Reader/Writer

---
