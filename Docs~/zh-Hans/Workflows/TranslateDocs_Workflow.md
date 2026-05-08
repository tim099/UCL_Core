---
title: UCL 文档翻译与本地化工作流程 (Document Translation Workflow)
description: 说明如何使用 ucl-translate-docs skill 进行跨语系文档翻译、套用三层语气架构、保证术语一致性、以及采用双轨 Fallback 链接防止死链接的 SOP
last_updated: 2026-05-08
target_audience: [AI_Agent, Designer, Technical_Writer]
aliases: [翻译, 本地化, translate, localization, i18n, translate doc, document translation]
tags: [workflow, localization, doc]
---

# 🗺️ UCL 文档翻译与本地化工作流程 (Document Translation Workflow)

> 代码与工具参考：[`Tools~/translate_docs.py`](../../Tools~/translate_docs.py) (规划中)
>
> 核心 Skill 定义：[`Skills~/ucl-translate-docs/SKILL.md`](../../Skills~/ucl-translate-docs/SKILL.md)

---

## 🚪 0. 为什么有这份工作流？

随着项目日益壮大，跨国协作与多语系 AI 辅助开发成为核心关键。为了防止文档翻译在多个 LLM 转手过程中出现**“格式崩溃（Markdown 语法遗漏）”**、**“术语混乱（同一概念译名漂移）”**、**“链接失效（FileNotFoundException 报错）”**，或**“大小姐优雅傲娇的灵魂被机械化翻译给抹杀”**，我们特此制定这套工程化、高精度的翻译工作流。

---

## 📌 1. 核心翻译原则

### 1.1 📖 术语第一 (Glossary-First Rule)
在开始翻译任何文档前，**必须先读取 `Docs/translate_glossary.json`（或 `_synonyms.txt` 增补区）**。
- **专有名词对齐**：诸如“大地图”、“状态效果”、“反应式 Effect”等词汇，必须严格对齐术语字典定义，不允许任何 AI 自行发挥的同义词。
- **代码与 C# 符号 100% 保持**：所有 C# 类名、方法名、Enum 字段（例如 `UCL_Asset`、`m_LoadOrder`、`TriggerOn`）在任何语系中都**绝对不能意译**，必须保持原样。

### 1.2 🔗 双轨 Fallback 链接 (Dual-Path Fallback Links)
在多语系目录（如 `Docs~/zh-Hant/` 与 `Docs~/en/`）中，经常面临“A 文档已翻译，但 A 文档引用的 B 文档尚未翻译”的尴尬情况。
> [!CAUTION]
> **绝对禁止在实体文件不存在时，将链接改成死链接！** 这会直接导致 Unity 的 Markdown 阅读器抛出 `FileNotFoundException` 错误。

** fallback 处理方案**：
- 如果被引用文件在目标语系中**尚不存在** ➡️ **保持链接指向原语系（中文 `zh-Hant`）文件，并在链接文字后方追加语系标记**。
  - *正确范例*：`[Design Principles](../../design.md) (zh-Hant)`
- 如果被引用文件在目标语系中**已存在** ➡️ **改写路径至目标语系下的正确路径**。
  - *正确范例*：`[Design Principles](../en/design.md)`

### 1.3 🎭 三层语气架构 (Tri-Tier Tone Framework)
依照文档的本质与职责，翻译时必须切换至正确的语气模式：

| 模式 (Mode) | 适用文档 | 语气规范 | 翻译示范 (以傲娇大小姐为例) |
|---|---|---|---|
| **Mode A: Dry Specs** | API 规格、数据结构、JSON 字段说明 | 100% 严肃、精准、去情绪化、剔除任何无关赘词。 | `“这段逻辑用于重置缓存，别乱动。”` ➡️ `"This logic resets the cache. Do not modify."` |
| **Mode B: Workflows** | SOP、建立资产指南、开发流程 | 保持清晰有条理，语气积极自信，带有极简高雅的修饰。 | `“请按照步骤建立 JSON。”` ➡️ `"Please follow these elegant steps to establish the JSON."` |
| **Mode C: Readability** | 核心读我、AI 阅读规范、导览说明 | 100% 完美本地化，将本小姐高贵优雅的傲娇吐槽完美对齐！ | `“哼！本小姐才不是为了你才写的...”` ➡️ `en: "Hmph! It's not like I wrote this for you..."` / `ja: "ふん！別にあんたのために書いたんじゃないんだからね！"` |

---

## 🛠️ 2. SOP ── 文档翻译五步走

### Step 1：环境与路径推算
1. 确定要翻译的源文档（如 `Docs/Workflows/Lucia_CardArt_Generation_Workflow.md`）与目标语系（如 `en`）。
2. 在目标目录建立对应语系的文件夹。
3. 复制源文档至目标路径，并进行 Frontmatter 初始化：
   - 更新 `last_updated: <当前日期 YYYY-MM-DD>`。
   - 保留原 `title` 并翻译其余字段，或在 frontmatter 追加 `translation_status: Draft` 标记。

### Step 2：术语库载入
- 读取 `Docs/translate_glossary.json` 与 `_synonyms.txt`，分析该文档中涉及的核心概念，列出术语替换清单。

### Step 3：分段高精度翻译 (与语气匹配)
- 根据文档类型（API 规格属 Mode A、Workflow 属 Mode B、Readability 属 Mode C）进行分段翻译。
- 100% 保持所有 Markdown 语法，包括 GitHub alerts、表格、Fenced Code Blocks 的语言标签。

### Step 4：链接安全检测 (Link Fallback Audit)
- 列出文件中所有相对路径引用，逐一检查目标语系下对应路径的文件是否存在。
- 若不存在，套用 **§1.2 双轨 Fallback 链接规范**。

### Step 5：索引与 Catalog 回填
- 翻译完成并存档后，在 [INDEX.md](../../../INDEX.md)（若是项目层文档）或 UCL_Core `index.md` 加上对应语系的导航条目。
- 重新跑 `ExportDocsCatalog` 指令更新 `_catalog.md`。

---

## ⚠️ 3. 常见地雷 (Common Pitfalls)

- ❌ **直接翻译 C# 代码注释时破坏双重注释铁律**：
  在翻译带有 C# 代码片段的文件时，代码内的 XML `/// <summary>` 与单行 `//` 注释也必须同步翻译成对应语系，但**严禁遗漏任何一行的注释或改变其格式**。
- ❌ **用机器翻译一键复制导致 Frontmatter 格式坏掉**：
  Frontmatter 内的 `aliases` 数组或 `tags` 如果被意译，会直接导致目录检索功能（Catalog）失效。
- ❌ **产生实体文件前先改了链接**：
  再次强调，改链接前一定要确认该目标文件“真的存在”，否则编辑器内会报错！
