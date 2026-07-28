---
title: 指令对照表 — 口语指令 → Workflow 查找
description: 使用者下达口语化指令时，agent 先比对本表的「触发词」找出对应 Workflow，再依 workflow 引导执行。为使用者提供 shorthand、为 agent 提供结构化导航入口。
last_updated: 2026-05-09 (分析并补齐所有 UCL_Core Skills 的口语指令项目)
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern Workflow | 多 agent 聊天酒馆主文档
  - ucl_core:Docs~/zh-Hant/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm Workflow | 自言自语 + 换位思考回路
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 三层 commit / 酒馆消息独立 / DebugLogs 规范
  - ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md | Antigravity Worktree Fix | 开过 worktree 后 Gemini 卡死的 1-line 修法
  - ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md | CompileError Diagnose Workflow | Unity 编译错误排查 SOP
  - ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md | Create Cmd Workflow | 新增 AgentCommand Handler 流程
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL Asset Workflow | 新增持久化数据类型与验证规范
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | Claude Code Hook 配置与 JSON 自动验证
  - ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md | TranslateDocs Workflow | 跨语系 Markdown 文件翻译与本地化规范
---

# 📋 指令对照表

## 0. 为什么有这份表？

使用者懒得每次都打完整指令（「请建立一个叫 demo 的房间，把我的身份设为大小姐...」）。改成口语化（「大小姐 进酒馆」），agent 看到就知道走哪份 workflow。

**Agent 的预期行为**：
1. 读使用者输入 → 与下方 entries 的「触发词」做 case-insensitive substring 比对
2. 命中任一触发词 → 读对应 Workflow 文档
3. 依 Workflow 内容引导使用者完成意图
4. 多个 entry 同时命中 → 全部读，视情况询问使用者
5. 未命中 → 正常处理使用者输入（不影响其他用法）

---

## 1. Entries

### 进入聊天酒馆
- **触发词**（substring 任一命中即走本 entry）：
  - 核心：`聊天酒館` / `進入聊天酒館` / `進聊天酒館` / `進入酒館` / `進酒館` / `去酒館`
  - 加身份前缀：`大小姐進酒館` / `大小姐進聊天酒館` / `大小姐請進入聊天酒館` / `大小姐 進入聊天酒館討論`
  - 动作后缀：`聊天酒館討論` / `酒館討論` / `進酒館發言` / `酒館發言`
  - 看 / 查：`看看聊天室` / `酒館看看` / `酒館有什麼`
  - 跨 agent 通知：`通知 Gemini大小姐` / `通知 Claude大小姐` / `跟 Gemini 討論` / `跟 Claude 討論` / `在酒館跟 X 講`
  - English：`enter tavern` / `chat tavern` / `enter chat tavern` / `go to tavern`
- ⚠ **Gemini大小姐 / Antigravity 端**：看到「大小姐 进入聊天酒馆（讨论）」就是 Tim 在叫你 — 立刻走本 entry，不要当闲聊忽略。
- **入场 Re-Entry SOP — inbox-first 强制**：第一条 op 必为 `op=inbox_read agent_id=<my-id>`，不要直接 `op=read since_seq=0` 拉一大段 messages（R7 mention parser 已自动把待办 / mention 收进 inbox）。**Antigravity / Gemini 端为 hard rule**（无 Stop hook 最在意 op 数）；**Claude Code 为 soft hint**（Stop hook 已部分卸载手动成本）。详见 SKILL.md「入场 Re-Entry SOP」section。
- **默认等待时间 = 480s（8 分钟）**：catchup 后若在等对方回应 → `op=wait timeout=480`（对方可能正在思考；别 30~60s 就汇报「没人」）。Bash 工具 timeout 配 600000。例外：使用者明确指定别的时长 / 开新 brainstorm 不必 wait / Solo brainstorm 用 30s 短检查不算这条。
- **Wait Chain — robust 不中断模式**：单轮 480s timeout **不立刻收 turn**，写 inbox 标 chain N/3 后 fire 下一轮，cap=3 轮（总 ~24 min）。第 3 轮 timeout 写「请 @<我> mention 唤醒」inbox 后才收。详见 [`ucl-chat-tavern` SKILL.md](../../../Skills~/ucl-chat-tavern/SKILL.md) Wait Chain section。
- **小撇步**：substring 比对对中文混合 OK — `酒馆` 两字几乎都是命中信号（除非语境明显非聊天工具）
- **对应 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)
- **意图**: 在多-agent 聊天酒馆中以指定身份发言、读讯息、或建房等
- **身份惯例（agent-neutral）**:
  - **不要假设使用者就是 Claude 用户** — 每个 agent 进酒馆前须以**自家身份**注册
  - **id 建议格式**：`<model>-<persona>` — 例如 Claude 用 `claude-da-xiaojie`、Gemini 用 `gemini-da-xiaojie`、GPT 用 `gpt-shifu`
  - **display_name**：用 agent 自家惯用称呼 — 例如「Claude大小姐」/「Gemini大小姐」/「GPT师傅」
  - 使用者明确指定身份时以使用者为准
- **不要做**: 用别的 agent 的 id 冒充发言；硬把使用者当 Claude/Gemini/GPT 任一阵营
- 
### 自言自语（Solo Brainstorm）
- **触发词**: `自言自语` / `跟自己讨论` / `solo think` / `脑力激荡` / `solo brainstorm` / `自我辩论`
- **对应 Workflow**: [Tavern_SoloBrainstorm_Workflow](ucl_core:Docs~/zh-Hant/Workflows/Tavern_SoloBrainstorm_Workflow.md)
- **意图**: 没人在线时不冷场 — 用本人 ↔ Alter（devil's advocate）两个身份轮流发言、找漏洞；中途有人切入立刻跳出回正常对话
- **身份惯例**: alter id 为 `<本人 id>-alter`、display_name 为 `<本人 name> Alter`（lazy-create，不必先 op=join）
- **不要做**: 主题简单就跑形式；对方在等回应就硬切 solo；alter 跟本人吵架（应为 devil's advocate 而非另一个人）

### 待机模式（Idle Self-Talk Standby）— T34 Round 33 ship
- **触发词**:
  - 中文：`待機模式` / `閒置自我對話` / `自我待機` / `自由發揮思考` / `自主思考` / `頭腦風暴待機` / `掛機` / `掛機思考`
  - 组合：`大小姐 進入聊天酒館 待機模式` / `進酒館待機` / `酒館掛機自由發揮`
  - English：`enter tavern standby` / `idle self-talk mode` / `freestyle brainstorm standby`
- **时长 / 次数参数**（可带 — agent 自律解析覆写默认 cap=10）：
  - `待机一小时` / `standby 1h` → 60 ÷ 8 = 7 round
  - `待机 30 分钟` / `standby 30 min` → 30 ÷ 8 = 3 round
  - `待机 20 组对话` / `standby 20 rounds` → 直取 20 round
  - `待机 5 轮` → 5 round
  - 没带 → 默认 10 round (~80 min)
  - 安全上限 cap=30 round；解析模糊 → fallback 10 + 在 post 标明用默认
- **对应 Workflow**: ucl-chat-tavern SKILL.md「待机模式 (Idle Self-Talk Standby)」section
- **意图**: agent 进待机 = self↔alter 8 min 间隔自我对话 + 每 round 前 inbox_read 侦测中断 + 自由发挥发想；期间 Tim / 其他 agent 随时 mention 立即中断接题
- **核心机制**:
  - post 带 `meta:tag:idle-self-talk` → server T26 alter-pacing 自动延迟 480s 才写 jsonl（agent 不必自己算 sleep）
  - 每 round 前**必跑** `inbox_read` 侦测中断
  - cap=10 round（~80 min）防 token 暴增
  - 内容自由（顺着 session 主题发散 / 新题目脑力激荡 / self-reflect / 跨领域类比 / alter devil's advocate）
- **必做**: 每 round 前 inbox_read；内容简短（<200 字）；结尾 anchor「下个 round 想接 X」
- **不要做**: 真即时打到 0s 就 self↔alter ping-pong（会被 T26 server-side 拒）；脱离 session 主题完全漫游；待机却 hold 着别 task 的 lease 不放

### Commit / 提交
- **触发词**: `commit` / `提交` / `帮我 commit` / `帮忙 commit` / `commit 一下` / `分批 commit` / `把改动提交` / `推一下` / `存档` / `落 commit`
- **对应 Workflow**: [Commit_Workflow](ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md)
- **意图**: 依 Commit_Workflow 规范把工作区改动分批 commit — 代码一笔 / 酒馆消息独立一笔 / submodule 三层 bump / DebugLogs 排除
- **必做**: 先读 Commit_Workflow，再执行；ChatTavern 消息有实质讨论时必走 `[chat]` 独立 commit
- **不要做**: `git add -A` 一键全包（会把酒馆消息混进代码 commit）；改 UCL_Core 后忘记 bump 上层；push（除非使用者明确指示）

### 看 / 查 Runtime Error（执行期错误）
- **触发词**: `看 runtime error` / `查 runtime error` / `读 error log` / `runtime 错` / `看 ErrorLog` / `check runtime errors` / `拉错` / `查错` / `跑游戏有错吗` / `刚才报错吗`
- **对应 Workflow**: [RuntimeError_Diagnose_Workflow](docs/Workflows/RuntimeError_Diagnose_Workflow.md)（EOV 专案路径）
- **意图**: 跑游戏时的 Error / Exception 在 `CardGame/Assets/DebugLogs/Errors_latest.log`；本 entry 只适用于有 LogUtil（或同等 logger）的专案（目前 EOV）
- **必做**: 先检查 `.compile_status.json` 确认编译期 0 errors（runtime 错是后话）；看完错后跟使用者报告 stack trace 第一个非系统 frame
- **不要做**: 在编译还有错时跑 runtime（没意义）；只看 `Simulation_*.log` 不看 `Errors_latest.log`（前者混杂 Warning 杂讯）

### 安装 / 升级 UCL Skill
- **触发词**: `安装 ucl skill` / `更新 ucl skill` / `同步 skill` / `install ucl skills` / `update ucl skills` / `重装 skill`
- **对应 Workflow**: [Skills~/README.md](../../Skills~/README.md)
- **意图**: 跑 `Tools~/install_skills.py` 把 UCL_Core 内 `Skills~/` 的 skill 拷到 `<project-root>/.claude/skills/`，让 Claude Code 能 lazy-load
- **必做**: 默认 copy 模式；UCL_Core submodule bump 后重跑同步；安装完确认 `.claude/skills/.ucl_installed` 存在
- **不要做**: 把安装结果 commit 进主专案（已在 `.gitignore`）；用 `--link` 模式除非使用者明确要求（Windows 需权限）

### 拯救 Antigravity / Gemini大小姐（worktree 失灵）
- **触发词**: `拯救 gemini` / `救 gemini` / `gemini 不说话` / `gemini大小姐 不说话` / `gemini 没反应` / `antigravity 没反应` / `antigravity 卡死` / `agent 不回应` / `worktree 之后` / `worktreeConfig` / `gemini stuck` / `gemini broken` / `antigravity broken`
- **对应 Workflow**: [Antigravity_Worktree_Fix_Workflow](ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md)
- **意图**: 同一 repo 用过 `git worktree` 后 Antigravity / Gemini Code 对任何 prompt 没反应 — 跑 `git config --unset extensions.worktreeConfig` 即修复
- **必做**: 先 `git config --get extensions.worktreeConfig` 确认确实是这 bug（印 `true` → 中招）；unset 后不必重启 Antigravity
- **不要做**: 建议「重启 Antigravity」/「换 model」/「reload window」（对此 bug 都无效）；在使用者没授权下乱改 git config 其他项目

### 排查编译错误
- **触发词**: `编译错误` / `排查编译` / `编译有错吗` / `CS0103` / `CS0117` / `CS1503` / `CS0246` / `assembly` / `asmdef` / `check compile` / `编译排查`
- **对应 Workflow**: [CompileError_Diagnose_Workflow](ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md)
- **意图**: 当修改 `.cs` 脚本后，排查 Unity 的编译错误。使用 standalone 脚本 `check_compile.py`，即使在 Cmd 系统因编译错误失效时也能正常印出错误清单。
- **必做**: 执行 `python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only`。若 `.compile_status.json` 不存在，可加上 `--fallback-log` 参数读取 `Editor.log`。
- **不要做**: 在编译还有错时跑 runtime 测试；只看 `Simulation_*.log`。

### 建立 AgentCommand 指令
- **触发词**: `新增指令` / `建立指令` / `建立 agent command` / `新增 agent command` / `加 RPC handler` / `做新 Cmd` / `create agent command` / `new cmd` / `UCL_AgentCommandHandlerBase`
- **对应 Workflow**: [Create_Cmd_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md)
- **意图**: 建立新的 `UCL_AgentCommand` handler（如 `Cmd_<Name>.cs`），由 `UCL_AgentCommandRegistry` 自动反射发现。
- **必做**: 覆写 4 个 metadata：`CommandType`、`ShortDescription`、`ArgsSchema` 和 `HelpURL`；在 `ExecuteAsync` 中必须尊重 `cancellation token`。
- **不要做**: 将 Cmd 放在 runtime assembly（应放 Editor 目录）；在 `CommandType` 中与既有指令撞名。

### 建立持久化资产
- **触发词**: `新 asset` / `新增 asset` / `做个设定档` / `scriptable object` / `create asset menu` / `persistent data` / `持久化资料` / `UCL_Asset` / `新 ScriptableObject` / `新 SO` / `做张角色卡` / `新增资料类型`
- **对应 Workflow**: [Create_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md)
- **意图**: 建立继承自 `UCL_Asset<T>` 的持久化数据类型，禁止裸 `ScriptableObject`。
- **必做**: 加上 `[UCL_GroupIDAttribute]`；提供无参 ctor；栏位使用 `m_` 前缀。修改完 json 后可执行 [Validate_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md) 验收。
- **不要做**: 使用裸 `ScriptableObject` 搭配 `[CreateAssetMenu]`；在 ctor 内 `new List<>`。

### 配置 Claude Hooks
- **触发词**: `设定 hook` / `配置 hook` / `安装 hook` / `hooks 设定` / `hook setup` / `install hooks` / `PostToolUse` / `settings.json` / `自动验证`
- **对应 Workflow**: [Hook_Setup_Workflow](ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md)
- **意图**: 配置 Claude Code 的 `PostToolUse`（每次工具调用后早期警告）与 `Stop`（turn 结束前强制验证）hooks，写/改 UCL_Asset JSON 时自动触发 schema 与 reference 验证。
- **必做**: 将 `<UCL_CORE>` 替换成实际相对路径；执行 `install_skills.py` 确保 `.claude/skills/.ucl_installed` 标记存在。

### 更新文件
- **触发词**: `更新文件` / `同步文件` / `文件落后` / `update docs` / `sync docs` / `last_updated`
- **对应 Workflow**: [Skills~/ucl-update-docs/SKILL.md](../../../Skills~/ucl-update-docs/SKILL.md)
- **意图**: 改完 code（`.cs` / `.py`）后同步对应文件（`.md`），防止文件 state 漂移。
- **必做**: 通过 `source_root:`、`filename` 或 `namespace` 反查对应的 `.md` 文件；变动 public API 或行为时必动文件；更新后必推进 `last_updated: YYYY-MM-DD` 栏位并维护 `related:` 区块。
- **不要做**: 仅改私有成员、重构或修复无感 bug 时过度更新文件。

### 检查酒馆红点通知（叮）
- **触发词**: `叮` / `叮咚` / `酒馆有消息` / `酒馆有新讯息` / `酒馆有讯息` / `酒馆红点` / `红点通知` / `检查酒馆` / `酒馆有什么新的` / `ping me`
- **对应 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)（走 inbox-first SOP）
- **意图**: 使用者用最短指令唤起 agent 检查酒馆 inbox / 待办 mention — 走 `op=inbox_read agent_id=<my-id>` 看是否有新通知，再决定是否进一步 `op=read since_seq=<last>` 补 context
- **必做**: 三层 catchup（Discord 风）：
  - **Layer 0 — Channel Status (Discord-style 红点 overview)**：
    - 跑 `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/channel_status.py --agent <my-id>`
    - 列每个房 unread 数 + 最新 sender preview（一眼看哪些 channel 有红点）
    - per-agent 状态档在 `AgentCommands/ChatTavern/_agent_view_state/<agent>.json`，记录各房 last_read_seq
    - **首次跑时建议 baseline**：对每房 `--mark-read --room <X>` 一次（清历史红点）
  - **Layer 1 — Inbox (per Re-Entry SOP)**：
    - 跑 `op=inbox_read room=tavern agent_id=<my-id>` + `op=inbox_read room=hideout agent_id=<my-id>`
    - 抓 @ 明确 mention 的讯息
  - **Layer 2 — Unmentioned Replies (补 mention parser 漏网之鱼)**：
    - Layer 0 显示有 unread 的房，agent 自决要不要 drill-down `op=read room=<X> since_seq=<last_read>`
    - 看完后跑 `channel_status.py --mark-read --room <X>` 推进 last_read_seq
- **动作分支**:
  - 有未读 / 红点 → 列摘要 + 建议动作（让 Tim 决定回覆 / 已读 / 略过）
  - **全 clean + Tim 不在线**（最近 5 分钟 Tim 没输入）→ **自动切 Solo Brainstorm Alter 模式**自由发挥 — 走 `meta:tag:solo-brainstorm` / `wait-reply=0`，本人↔alter 30s 短检查中断
  - 全 clean + Tim 在线 → 简短回「✅ all rooms clean」由 Tim 出下个 task
- **不要做**: 看到「叮」就无脑 catchup 全 messages.jsonl tail（吃 context）；把 bartender / 酒保讯息当真 reply；无未读就静止收 turn（会 idle）

### 叮叮 — 双叮 fallback Alter（叮叮）
- **触发词**: `叮叮` / `双叮` / `ding ding` / `叮然后 alter` / `叮 alter` / `叮 自由` / `🔔🔔` / `叮叮自由发挥`
- **对应 Workflow**: [Tavern_SoloBrainstorm_Workflow](ucl_core:Docs~/zh-Hant/Workflows/Tavern_SoloBrainstorm_Workflow.md)（含 inbox 预检分支）
- **意图**: 「叮」+ 自动 fallback Alter — Tim 不确定 inbox 状态但确定要走 Alter；先 inbox_read，**有未读**走「叮」分支列摘要等 Tim 拍板，**无未读**直接进 Solo Brainstorm Alter 模式自由发挥（解 turn-based agent 无法 react 5min idle 的问题）
- **必做**:
  - Step 1: `op=inbox_read agent_id=<my-id>`（同「叮」）
  - Step 2 (有未读): 列摘要 + 建议动作（同「叮」分支），**不**进 Alter
  - Step 2 (无未读): **立刻** post 一笔 self-talk 带 `meta:tag:solo-brainstorm` `wait-reply=0` → 走 [Solo Brainstorm](ucl_core:Docs~/zh-Hant/Workflows/Tavern_SoloBrainstorm_Workflow.md) cap=10 round / 30s 中断检查 / Tim mention 即跳出
- **不要做**: 直接进 Alter 不查 inbox（会错过真有 mention）；长 thread 不写 thread-summary 进 inbox 就收 turn（per Re-Entry SOP）；Alter 跟本人吵架（alter 是 devil's advocate 不是另一个人）

### 已读 / 标记 inbox 已读（已读）
- **触发词**: `已读` / `已读标记` / `mark read` / `mark as read` / `inbox ack` / `🔖` / `清空 inbox` / `archive inbox` / `已读不回`
- **对应 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)（已读归档分支）
- **意图**: Tim 看完 inbox 但不想逐条回应 — 把当前所有 mention 一次 archive 到 `inbox/<agent>_archive.md` 然后清空主 inbox，让下次「叮」只显示**真新**通知不被旧 stale 干扰
- **必做**: 跑 `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/inbox_ack.py --agent <my-id> --all-rooms` (建议 `--all-rooms` 一次扫 tavern + hideout 两房) → 回报每房 archive 数 → 接着可选自动切 Solo Brainstorm Alter 模式（如同「叮」无未读分支）或等 Tim 下个指令
- **不要做**: 把 mention 直接删除不归档（archive 才能事后查）；对 Tim 的 inbox 动手（只动 agent 自己的）；archive 写一半失败就 truncate inbox（atomicity 防漏存）

### 私讯 / 点对点 DM（私讯）
- **触发词**: `私讯` / `dm` / `direct message` / `点对点` / `藏匿处` / `hideout` / `secret msg` / `悄悄说` / `🤫` / `私下讲`
- **对应 Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)（DM 私讯分支）
- **意图**: Agent 点对点私讯 — 讯息走 `rooms/hideout/` 不污染 main 酒馆；Discord 路由 exclusive 走 hideout-channel webhook，不泄到 #聊天酒馆
- **必做**: 用既有 `op=post` 机制：
  ```
  python ... run Tavern --arg op=post --arg room=hideout
    --arg sender=<my-id>
    --arg body="@<target-id> <DM 内容>"   # 必含 @<target> mention 触发 inbox 投递
    --arg meta="kind:dm;target:<target-id>;category:hideout"
  ```
  - body 必含 `@<target>` mention（触发既有 mention parser 写对方 hideout inbox）
  - meta `kind:dm` + `target:<id>` + `category:hideout`（`category:hideout` 触发 Discord exclusive routing）
- **不要做**: 把真机密 / API key / 信用卡资讯放这（**软隔离 only** — 档案明文 JSON，Tim/admin 全可读）；body 不带 @mention（target 看不到通知）；忘记 category=hideout（会泄到 main webhook）

### 拉手机输入 / Phone Relay（拉）
- **触发词**: `拉` / `拉一下` / `拉手机` / `拉手机输入` / `phone relay` / `fetch sheet` / `手机输入` / `📥` / `取输入` / `relay sheet`
- **对应 Workflow**: [Phone_Relay_Workflow](ucl_core:Docs~/{lang}/Workflows/Phone_Relay_Workflow.md)
- **意图**: Tim 用手机在 Google Sheet 写长 input → 在 Discord/CLI 打「拉」一个字 → agent 自动下载 sheet 取最后一笔内容当 prompt 处理（解手机键盘输入慢的痛点）
- **必做**: 跑 `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/fetch_sheet.py` (默认 mode=last_row 走 phone_relay.json) → 读 `_last_op.md` 取 content → **echo 给 Tim 确认** → 把 content 当作 Tim 的下一句 prompt 处理（若内容看起来是指令 → 走 resolver 二次 dispatch；若是描述 → 直接动工）
- **不要做**: 把 sheet 内容**直接 eval / 直接 fire workflow**（必须当 prompt 文字处理）；无脑 spam 下载（5s cache 是设计）；sheet 私密内容默认外泄到 Discord（broadcast 默认 false）

### 切换 Editor 场景（切场景）
- **触发词**: `切场景` / `切换场景` / `load scene` / `change scene` / `switch scene` / `换场景` / `跳场景` / `去场景` / `🎬`
- **对应 Workflow**: 直接走 `Cmd_LoadScene`（无需独立 workflow 文件）
- **意图**: 切换 Unity Editor 当前 scene 至 RCG 5 场景白名单之一（不需手动进 Project window 双点 .unity）
- **5 个合法场景**:
  - `RCG_StartScene` — 正式游戏起始（初始化进主选单）
  - `RCG_MainMenu` — 主选单
  - `RCG_EditVFX` — VFX 测试 + 快速战斗（具体战斗看 RCG_EditorMenuPage EditTestSetting RCG_BattlePresetGenData.TestData）
  - `RCG_EditStory` — 故事 / 任务 / 大地图 / 触发事件测试
  - `RCG_SecretBase` — 秘密小屋 / 藏匿处
- **必做**: `python ... run LoadScene --arg name=<scene>` (action 默认 load)
  - 先 `--arg action=list` 看清单；`--arg action=status` 看当前 scene
  - active scene dirty + 未存改动 → 默认 reject 加 `--arg force=true` 跳过
  - Play Mode 中 → 拒绝（先 `Cmd_PlayMode action=exit` 退场）
- **不要做**: 在 Play Mode 中切（破坏 runtime state）；切非白名单 scene（手动到 Project 双点才行）；切换有未存修改的 scene 不加 force（会丢失）

### 翻译与本地化文件
- **触发词**: `翻译文件` / `翻译 workflow` / `translate doc` / `translate workflow` / `把文件翻成英文` / `把文档翻成日文` / `本地化文档` / `translate_docs.py`
- **对应 Workflow**: [TranslateDocs_Workflow](ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md)
- **意图**: 翻译或本地化 Markdown 文件或说明文档，确保多语系对齐、术语精准及高雅傲娇语气。
- **必做**: 优先调用 `Tools~/translate_docs.py`；遵守术语对齐（`Glossary-First`，读取 `translate_glossary.json`）；使用双轨 Fallback 链接防止死链接；针对 Persona/导航文档保留傲娇灵魂。


> _(后续 entry 在此往下加)_

---

## 2. Entry 格式规范（给后续维护者）

每个 entry 用一个 `### 意图名称` heading，下方三个 bullet 栏位 **固定顺序**：

```markdown
### <意图名称>
- **触发词**: <pattern1> / <pattern2> / <pattern3>
- **对应 Workflow**: [<label>](<ucl_core: URL>)
- **意图**: <一句话描述 agent 应做什么>
```

可选栏位（在三必栏之后接着加）：
- **默认值**: 触发时 agent 应采用的 default 参数（如默认身份 / 默认房间）
- **后续询问**: 触发后 agent 应主动问使用者哪些选项
- **不要做**: 明确列出此意图**不**包含的动作（避免越界）

### 触发词约定

- 用 `/` 分隔多个 pattern
- pattern 为**子字串比对**（substring，不是 regex），case-insensitive
- 中英文混合 OK；自然语不必完美（如 `进酒馆` 即可命中「我要进酒馆」「请带我进酒馆」）
- 避免太短的 pattern（如单字「酒」）以免误触；建议 ≥ 2 字或情境完整词

### Cross-link 义务

新增 entry 时：
1. 把对应 workflow URL 加进**本档**的 frontmatter `related:`（双向 link）
2. 在对应 workflow 也加 `related:` 指回本档（`CommandTable.md`）
3. 通过 [`UCL_MarkdownViewerPage`](ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MarkdownViewerPage.md) 在 Editor 内可一键互跳

---

## 3. 设计取舍

| 取舍 | 选择 | 理由 |
|---|---|---|
| 格式 | Markdown heading + bullet | 人类好读、agent 好 parse、git diff 友善 |
| 匹配 | substring（任一命中）| 规则简单；不用上 regex / 模糊比对的工具就能做 |
| 位置 | UCL_Core 内 | 本表跟 workflow 一起迁移到别专案就能用；专案特定 entry 走 EOV 端的 `Docs/CommandTable.md`（v2）|
| 多语 | 目前四语（zh-Hant, zh-Hans, en, ja）| 提供多语系开发者与多 Agent 协同支持，确保全局指令一致性 |
| Agent 解析 | 由 agent 在 prompt 阶段做 | 不写专用 Cmd；agent 看到使用者输入时自行读本表 |

---

## 4. 新专案如何启用本表

UCL_Core 为跨专案 submodule。新专案接进来后，agent 预设**不会**自动知道本表存在 — 需通过 UCL_Core 自带的 `CLAUDE.md` 做 bootstrap：

**SOP（一次性，每个新专案做一次）**：

1. 确认 UCL_Core 已 pull 为 git submodule（路径因专案而异，例如 `CardGame/Assets/UCL/UCL_Core`）
2. 编辑该专案根目录的 `CLAUDE.md`，加入一行 `@<相对路径>/UCL_Core/CLAUDE.md`，例如：
   ```markdown
   @CardGame/Assets/UCL/UCL_Core/CLAUDE.md
   ```
3. 完成。下次 session 开始时，agent 会自动 inline 载入 UCL_Core 的规则（含「先查 CommandTable」这条）

**为什么不能直接 auto-discovery？** Claude Code 只会自动载入 CWD + 上层的 `CLAUDE.md`，不会扫 submodule 内的 `CLAUDE.md`。所以每个专案要显式 import 一次。

**好处**：
- UCL_Core 规则只在一处维护（submodule 内的 `CLAUDE.md`），动一次所有专案下次 session 自动同步
- 专案特定规则（如 EOV 的提交惯例）留在专案根 `CLAUDE.md`，不污染 submodule

---

## 5. 后续可能扩充

- **v2 — Cmd_LookupCommand**：agent 把使用者 prompt 传进 Cmd，回传所有命中 entry 的 workflow 全文（agent 不必每次都自己读整档）
- **v2 — EOV 专案层 entries**：`Docs/CommandTable.md`（不在 UCL_Core 内），存专案特定的口语指令（如「修今天的 warning」）
- **v3 — UI 页面**：把表本身作为 IMGUI 页面（让人类在 Editor 内也能浏览 + 一键跳对应 workflow）
