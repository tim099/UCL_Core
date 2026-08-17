---
title: Cmd_Glossary API
description: 自造新詞辭典 —— 註冊詞條、偵測文中出現的詞、自動附上解釋 block（對應 `docs/Glossary/<slug>.md`）。
source_file: Assets/Plugins/UCL_Core/UCL_Core_Scripts/EditorCore/UCL_AgentCommands/Glossary/Cmd_Glossary.cs
namespace: UCL.Core.EditorLib.AgentCommands.Glossary
last_updated: 2026-08-17
target_audience: [AI_Agent, Tools_Maintainer]
---

# Cmd_Glossary

> 自造新詞辭典 —— 註冊詞條、偵測文中出現的詞、自動附上解釋 block（對應 `docs/Glossary/<slug>.md`）。

## 1. 概覽

- **CommandType**：`Glossary`
- **原始碼 ShortDescription**：Neologism Glossary — register 新詞 + detect/attach refs (對應 docs/Glossary/<slug>.md)

**什麼時候用**：造了新術語想讓它有定義、或要讓一段文字自動帶上詞條解釋時。

## 2. 參數 (ArgsSchema)

- `op=register|lookup|detect|attach|list`
- `register: term=詞 slug=檔名slug [aliases=csv] [category=persona|concept|mechanism|tool|protocol] one_line=一句解說 [body=完整markdown] [created_by=agent_id] [overwrite=true|false]`
- `lookup: term=詞或alias — 回 frontmatter + path (resolve aliases → canonical)`
- `detect: text=要掃的文字 [cap=N(default 10)] — 列命中的 glossary terms (longest-match-wins)`
- `attach: text=要掃的文字 [cap=N(default 5)] — 回原 text + append refs block`
- `list: [category=...] — 列所有 glossary terms`

```bash
python <UCL_Core>/Tools~/AgentCommands/run_cmd.py run Glossary --arg <k>=<v>
```

## 3. 注意

- ⚠ **本 Cmd 尚未宣告 ArgsSpec**，執行前不做參數檢查 —— `term` / `slug` / `one_line` 漏帶不會被事前擋下，而是跑進 `Op_Register` 內部才 throw。
- 工具新建預設寫在 `Docs/Glossary/` 根層；persona 條目慣例放 `personas/`，落檔後要手動搬。
- 同 slug 已存在時預設 reject，要覆寫得帶 `overwrite=true`；覆寫會寫回**原本的位置**（含子資料夾）。

## 4. 關聯

- [UCL_AgentCommand API](./UCL_AgentCommand.md)
- [架構](./UCL_AgentCommand_Architecture.md)
