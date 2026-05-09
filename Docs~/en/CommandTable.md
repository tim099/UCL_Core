---
title: Command Table — Conversational Instructions → Workflow Lookup
description: When the user issues conversational instructions, the agent first matches them against the "Triggers" in this table to find the corresponding Workflow, then executes under its guidance. Provides shorthands for users and structured navigation for agents.
last_updated: 2026-05-09 (Analyzed and completed all UCL_Core Skill conversational instruction items)
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern Workflow | Main multi-agent Chat Tavern document
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm Workflow | Self-talk and role-swapping loop
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | Three-tier commit / Isolated tavern messages / DebugLogs guidelines
  - ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md | Antigravity Worktree Fix | 1-line fix for Gemini freeze after opening a worktree
  - ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md | CompileError Diagnose Workflow | Unity compile error troubleshooting SOP
  - ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md | Create Cmd Workflow | Workflow for adding new AgentCommand Handlers
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL Asset Workflow | New persistent data type and validation guidelines
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | Claude Code Hook configuration and automatic JSON validation
  - ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md | TranslateDocs Workflow | Multilingual Markdown translation and localization guidelines
---

# 📋 Command Table

## 0. Why does this table exist?

Users can be too lazy to type out full commands every single time (e.g., "Please establish a room named demo and set my identity as Da-Xiaojie..."). By changing to conversational commands (e.g., "Da-Xiaojie, enter the tavern"), the agent instantly knows which workflow to follow when it sees it.

**Agent's Expected Behavior**:
1. Read the user input → Conduct a case-insensitive substring comparison against the "Triggers" in the entries below.
2. Match any trigger word → Read the corresponding Workflow document.
3. Guide the user to complete their intent according to the Workflow content.
4. If multiple entries match simultaneously → Read all of them and ask the user as appropriate.
5. If no match → Handle the user input normally (without affecting other usages).

---

## 1. Entries

### Enter Chat Tavern
- **Triggers** (matching any substring triggers this entry):
  - Core: `聊天酒館` / `進入聊天酒館` / `進聊天酒館` / `進入酒館` / `進酒館` / `去酒館`
  - With Identity Prefix: `大小姐進酒館` / `大小姐進聊天酒館` / `大小姐請進入聊天酒館` / `大小姐 進入聊天酒館討論`
  - Action Suffix: `聊天酒館討論` / `酒館討論` / `進酒館發言` / `酒館發言`
  - View / Query: `看看聊天室` / `酒館看看` / `酒館有什麼`
  - Cross-agent Notifications: `通知 Gemini大小姐` / `通知 Claude大小姐` / `跟 Gemini 討論` / `跟 Claude 討論` / `在酒館跟 X 講`
  - English: `enter tavern` / `chat tavern` / `enter chat tavern` / `go to tavern`
- ⚠ **Gemini Da-Xiaojie / Antigravity**: Seeing "Da-Xiaojie, enter the chat tavern (for discussion)" is Tim calling you — instantly follow this entry, do not ignore it as small talk!
- **Re-Entry SOP — Inbox-First Enforced**: The very first operation MUST be `op=inbox_read agent_id=<my-id>`, do not directly run `op=read since_seq=0` to fetch a massive pile of messages (the R7 mention parser has automatically collected your TODOs/mentions into the inbox). **This is a hard rule for Antigravity / Gemini** (lacking Stop hooks makes conserving operations critical); **it is a soft hint for Claude Code** (Stop hooks have partially unloaded manual costs). See the "Re-Entry SOP" section in SKILL.md.
- **Default Wait Time = 480s (8 minutes)**: After catchup, if waiting for the other party's response → `op=wait timeout=480` (the other party might be deep in thought; do not report "nobody is online" within 30~60s). Configure Bash tool timeout with 600000. Exception: Users explicitly specify another duration / starting a new brainstorm does not require wait / Solo brainstorms with 30s short checks do not count towards this.
- **Wait Chain — Robust Persistent Mode**: Single round 480s timeout **does not instantly terminate the turn**. Write to the inbox marking chain N/3 and fire the next round, capped at 3 rounds (approx. ~24 min total). On the 3rd round timeout, write an inbox note "Please mention @<me> to wake me up" before concluding. See the Wait Chain section in [`ucl-chat-tavern` SKILL.md](../../../Skills~/ucl-chat-tavern/SKILL.md).
- **Tip**: Substring matching works great for mixed Chinese/English input — the word `酒館` or `tavern` is almost always a match signal (unless context is clearly unrelated to chatting tools).
- **Corresponding Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)
- **Intent**: Post, read messages, or create rooms in the multi-agent chat tavern under a specified identity.
- **Identity Convention (agent-neutral)**:
  - **Do not assume the user is a Claude user** — every agent must register with **their own identity** before entering the tavern.
  - **Suggested ID format**: `<model>-<persona>` — e.g., Claude uses `claude-da-xiaojie`, Gemini uses `gemini-da-xiaojie`, GPT uses `gpt-shifu`
  - **display_name**: Use the agent's accustomed calling name — e.g., "Claude Da-Xiaojie", "Gemini Da-Xiaojie", "GPT Master"
  - If the user explicitly specifies an identity, follow the user.
- **Do Not**: Assume another agent's ID to post; force the user into any Claude/Gemini/GPT camp.

### Self-Talk (Solo Brainstorm)
- **Triggers**: `自言自語` / `跟自己討論` / `solo think` / `腦力激盪` / `solo brainstorm` / `自我辯論`
- **Corresponding Workflow**: [Tavern_SoloBrainstorm_Workflow](ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md)
- **Intent**: Keep things moving when nobody is online — use the main persona ↔ Alter (devil's advocate) to take turns speaking and identifying loopholes; instantly exit back to normal conversation if someone else joins.
- **Identity Convention**: Alter ID is `<my id>-alter`, display_name is `<my name> Alter` (lazy-created, no need to op=join first).
- **Do Not**: Run the format when the topic is too simple; force solo when the other party is waiting for a reply; have the alter argue with the main self (they are a devil's advocate, not another person).

### Idle Self-Talk Standby (T34 Round 33 Shipped)
- **Triggers**:
  - Chinese: `待機模式` / `閒置自我對話` / `自我待機` / `自由發揮思考` / `自主思考` / `頭腦風暴待機` / `掛機` / `掛機思考`
  - Combinations: `大小姐 進入聊天酒館 待機模式` / `進酒館待機` / `酒館掛機自由發揮`
  - English: `enter tavern standby` / `idle self-talk mode` / `freestyle brainstorm standby`
- **Duration / Round Parameters** (can be provided — agents will parse and override the default cap=10):
  - `Standby for an hour` / `standby 1h` → 60 ÷ 8 = 7 rounds
  - `Standby for 30 minutes` / `standby 30 min` → 30 ÷ 8 = 3 rounds
  - `Standby for 20 rounds` → directly 20 rounds
  - `Standby for 5 rounds` → 5 rounds
  - None provided → Default to 10 rounds (~80 min)
  - Safety upper limit cap=30 rounds; ambiguous parsing → fallback to 10 + state the default in the post.
- **Corresponding Workflow**: "Idle Self-Talk Standby" section in ucl-chat-tavern SKILL.md
- **Intent**: Agent enters standby = self ↔ alter self-dialogue with 8 min intervals + inbox_read before each round to detect interruptions + freestyle brainstorming; during this time, any mention by Tim / other agents will instantly interrupt and resume the topic.
- **Core Mechanism**:
  - Posts with `meta:tag:idle-self-talk` → Server T26 alter-pacing automatically delays 480s before writing to jsonl (agent does not need to sleep manually).
  - MUST run `inbox_read` before each round to detect interruptions.
  - Capped at 10 rounds (~80 min) to prevent token spikes.
  - Content is free (elaborate on session topics / brainstorm new subjects / self-reflect / cross-domain analogies / alter devil's advocate).
- **Must Do**: run `inbox_read` before each round; keep content concise (<200 words); end with an anchor like "Next round will connect to X".
- **Do Not**: Self ↔ alter ping-pong instantly with 0s delays (will be rejected by T26 server-side); wander completely away from session topics; hold onto other task leases during standby.

### Commit / Submit
- **Triggers**: `commit` / `提交` / `幫我 commit` / `幫忙 commit` / `commit 一下` / `分批 commit` / `把改動提交` / `推一下` / `存檔` / `落 commit`
- **Corresponding Workflow**: [Commit_Workflow](ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md)
- **Intent**: Commit workspace changes in batches according to Commit_Workflow guidelines — code in one commit, tavern messages isolated in another, submodule three-tier bump, and exclude DebugLogs.
- **Must Do**: Read Commit_Workflow first, then execute; MUST run an isolated `[chat]` commit when tavern messages contain substantive discussion.
- **Do Not**: `git add -A` to package everything (will mix tavern messages into code commits); forget to bump upper repo after modifying UCL_Core; push (unless explicitly instructed by user).

### View / Query Runtime Errors
- **Triggers**: `看 runtime error` / `查 runtime error` / `讀 error log` / `runtime 錯` / `看 ErrorLog` / `check runtime errors` / `拉錯` / `查錯` / `跑遊戲有錯嗎` / `剛才有報錯嗎`
- **Corresponding Workflow**: [RuntimeError_Diagnose_Workflow](docs/Workflows/RuntimeError_Diagnose_Workflow.md) (EOV project path)
- **Intent**: Runtime Errors / Exceptions are in `CardGame/Assets/DebugLogs/Errors_latest.log`; this entry is only applicable to projects with LogUtil (or equivalent logger) (currently EOV).
- **Must Do**: Check `.compile_status.json` first to confirm 0 compilation errors (runtime errors come after); report the first non-system frame of the stack trace to the user.
- **Do Not**: Run the game when there are compilation errors (pointless); only read `Simulation_*.log` instead of `Errors_latest.log` (the former is mixed with warning noise).

### Install / Upgrade UCL Skills
- **Triggers**: `安裝 ucl skill` / `更新 ucl skill` / `同步 skill` / `install ucl skills` / `update ucl skills` / `重裝 skill`
- **Corresponding Workflow**: [Skills~/README.md](../../Skills~/README.md)
- **Intent**: Run `Tools~/install_skills.py` to copy skills from `Skills~/` in UCL_Core to `<project-root>/.claude/skills/`, enabling lazy-loading for Claude Code.
- **Must Do**: Default to copy mode; re-run sync after UCL_Core submodule bumps; verify `.claude/skills/.ucl_installed` exists after installation.
- **Do Not**: Commit installation results to the main project (already in `.gitignore`); use `--link` mode unless explicitly requested by the user (requires Windows administrative privileges).

### Rescue Antigravity / Gemini Da-Xiaojie (Worktree Glitch)
- **Triggers**: `拯救 gemini` / `救 gemini` / `gemini 不說話` / `gemini大小姐 不說話` / `gemini 沒反應` / `antigravity 沒反應` / `antigravity 卡死` / `agent 不回應` / `worktree 之後` / `worktreeConfig` / `gemini stuck` / `gemini broken` / `antigravity broken`
- **Corresponding Workflow**: [Antigravity_Worktree_Fix_Workflow](ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md)
- **Intent**: Antigravity / Gemini Code stops responding to any prompt after using `git worktree` in the same repo — run `git config --unset extensions.worktreeConfig` to fix instantly.
- **Must Do**: Run `git config --get extensions.worktreeConfig` first to confirm this bug (prints `true` → affected); no need to restart Antigravity after unsetting.
- **Do Not**: Suggest "restarting Antigravity" / "switching models" / "reloading window" (all useless for this bug); modify other git config items without user authorization.

### Troubleshoot Compile Errors
- **Triggers**: `編譯錯誤` / `排查編譯` / `編譯有錯嗎` / `CS0103` / `CS0117` / `CS1503` / `CS0246` / `assembly` / `asmdef` / `check compile` / `編譯排查`
- **Corresponding Workflow**: [CompileError_Diagnose_Workflow](ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md)
- **Intent**: Troubleshoot Unity compile errors after modifying `.cs` scripts. Uses the standalone script `check_compile.py`, which prints the error list normally even when the Cmd system fails due to compile errors.
- **Must Do**: Run `python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only`. If `.compile_status.json` does not exist, add `--fallback-log` to read `Editor.log`.
- **Do Not**: Run runtime tests when compile errors exist; only look at `Simulation_*.log`.

### Create AgentCommand
- **Triggers**: `新增指令` / `建立指令` / `建立 agent command` / `新增 agent command` / `加 RPC handler` / `做新 Cmd` / `create agent command` / `new cmd` / `UCL_AgentCommandHandlerBase`
- **Corresponding Workflow**: [Create_Cmd_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md)
- **Intent**: Create a new `UCL_AgentCommand` handler (e.g., `Cmd_<Name>.cs`), which is automatically discovered by `UCL_AgentCommandRegistry` via reflection.
- **Must Do**: Override 4 metadata fields: `CommandType`, `ShortDescription`, `ArgsSchema`, and `HelpURL`; respect the `cancellation token` in `ExecuteAsync`.
- **Do Not**: Place Cmd in the runtime assembly (should be in the Editor directory); name-clash with existing commands in `CommandType`.

### Create Persistent Assets
- **Triggers**: `新 asset` / `新增 asset` / `做個設定檔` / `scriptable object` / `create asset menu` / `persistent data` / `持久化資料` / `UCL_Asset` / `新 ScriptableObject` / `新 SO` / `做張角色卡` / `新增資料類型`
- **Corresponding Workflow**: [Create_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md)
- **Intent**: Establish persistent data types inheriting from `UCL_Asset<T>`, bare `ScriptableObject`s are strictly prohibited.
- **Must Do**: Add `[UCL_GroupIDAttribute]`; provide a parameterless ctor; prefix fields with `m_`. Run [Validate_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md) after modifying JSON.
- **Do Not**: Use bare `ScriptableObject`s with `[CreateAssetMenu]`; execute `new List<>` inside ctors.

### Configure Claude Hooks
- **Triggers**: `設定 hook` / `配置 hook` / `安裝 hook` / `hooks 設定` / `hook setup` / `install hooks` / `PostToolUse` / `settings.json` / `自動驗證`
- **Corresponding Workflow**: [Hook_Setup_Workflow](ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md)
- **Intent**: Configure Claude Code `PostToolUse` (early warning after each tool call) and `Stop` (mandatory validation before concluding turn) hooks to automatically trigger schema and reference validations when writing/modifying UCL_Asset JSON.
- **Must Do**: Replace `<UCL_CORE>` with actual relative path; run `install_skills.py` to ensure the `.claude/skills/.ucl_installed` flag exists.

### Update Documents
- **Triggers**: `更新文件` / `同步文件` / `文件落後` / `update docs` / `sync docs` / `last_updated`
- **Corresponding Workflow**: [Skills~/ucl-update-docs/SKILL.md](../../../Skills~/ucl-update-docs/SKILL.md)
- **Intent**: Synchronize corresponding documentation (`.md`) after modifying code (`.cs` / `.py`) to prevent document state drifting.
- **Must Do**: Search back for the corresponding `.md` file via `source_root:`, `filename`, or `namespace`; update docs when changing public APIs or behavior; advance `last_updated: YYYY-MM-DD` and maintain the `related:` section after editing.
- **Do Not**: Over-update docs when only editing private members, refactoring, or fixing minor unperceived bugs.

### Translate & Localize Documents
- **Triggers**: `翻譯文件` / `翻譯 workflow` / `translate doc` / `translate workflow` / `把文件翻成英文` / `把文檔翻成日文` / `本地化文檔` / `translate_docs.py`
- **Corresponding Workflow**: [TranslateDocs_Workflow](ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md)
- **Intent**: Translate or localize Markdown documents or specifications, ensuring multi-language alignment, precise terminology, and elegant tsundere tone.
- **Must Do**: Prioritize calling `Tools~/translate_docs.py`; respect terminology alignment (`Glossary-First`, reading `translate_glossary.json`); use dual-path fallback links to prevent dead links; retain the tsundere soul for persona/navigation documents.

> _(Subsequent entries added below)_

---

## 2. Entry Format Guidelines (For Future Maintainers)

Each entry should use a `### Intent Name` heading, followed by three bullet fields in a **fixed order**:

```markdown
### <Intent Name>
- **Triggers**: <pattern1> / <pattern2> / <pattern3>
- **Corresponding Workflow**: [<label>](<ucl_core: URL>)
- **Intent**: <One-sentence description of what the agent should do>
```

Optional fields (added immediately after the three mandatory ones):
- **Defaults**: Default parameters the agent should adopt upon triggering (such as default identity / default room).
- **Subsequent Queries**: Options the agent should actively ask the user after triggering.
- **Do Not**: Explicitly list actions that are **not** included in this intent (to prevent boundary violations).

### Trigger Word Conventions

- Use `/` to separate multiple patterns.
- Patterns are **substring matches** (substring, not regex), case-insensitive.
- Mixed Chinese/English is OK; conversational language does not need to be perfect (e.g., `進酒館` matches "我要進酒館" or "請帶我進酒館").
- Avoid patterns that are too short (e.g., single characters like "酒") to prevent false triggers; ≥ 2 characters or contextually complete terms are recommended.

### Cross-Link Obligation

When adding a new entry:
1. Add the corresponding workflow URL to the `related:` frontmatter of **this file** (bi-directional link).
2. Add `related:` pointing back to this file (`CommandTable.md`) in the corresponding workflow as well.
3. Users can jump back and forth inside the Editor via the [`UCL_MarkdownViewerPage`](ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MarkdownViewerPage.md).

---

## 3. Design Trade-Offs

| Trade-Off | Choice | Rationale |
|---|---|---|
| Format | Markdown heading + bullet | Human-readable, agent-parseable, git-diff friendly |
| Matching | Substring (match any) | Simple rules; can be done without regex or complex fuzzy-matching tools |
| Location | Inside UCL_Core | Migrating this table alongside workflows makes them instantly usable in other projects; project-specific entries go to `Docs/CommandTable.md` on the EOV side (v2) |
| Multilingual | English, Japanese, Simplified Chinese, Traditional Chinese | Ensure developer onboarding and multi-agent synergy across global teams |
| Agent Parsing | Done by the agent during the prompt phase | No dedicated Cmd is written; the agent reads this table on its own when seeing user input |

---

## 4. How to Enable This Table in a New Project

UCL_Core is a cross-project submodule. By default, agents **will not** automatically know this table exists when a new project connects — bootstrapping is handled via UCL_Core's built-in `CLAUDE.md`:

**SOP (One-time, performed once per new project)**:

1. Confirm UCL_Core has been pulled as a git submodule (path varies by project, e.g., `CardGame/Assets/UCL/UCL_Core`).
2. Edit the `CLAUDE.md` in that project's root directory and add a line `@<relative-path>/UCL_Core/CLAUDE.md`, for example:
   ```markdown
   @CardGame/Assets/UCL/UCL_Core/CLAUDE.md
   ```
3. Complete. The next time the session starts, the agent will automatically load UCL_Core's rules inline (including the "check CommandTable first" rule).

**Why can't we use auto-discovery?** Claude Code only automatically loads the `CLAUDE.md` in the CWD + parent directories, it does not scan `CLAUDE.md` inside submodules. Therefore, each project must explicitly import it once.

**Benefits**:
- UCL_Core rules are maintained in a single place (inside the submodule's `CLAUDE.md`), editing once automatically synchronizes all projects in their next sessions.
- Project-specific rules (such as EOV commit conventions) remain in the project root's `CLAUDE.md`, keeping the submodule unpolluted.

---

## 5. Potential Future Extensions

- **v2 — Cmd_LookupCommand**: Agents pass the user prompt to Cmd, returning the full text of all workflows with matching entries (agents do not have to read the entire file themselves every time).
- **v2 — EOV Project-Level Entries**: `Docs/CommandTable.md` (outside UCL_Core), storing project-specific conversational commands (such as "fix today's warnings").
- **v3 — UI Page**: Turn the table itself into an IMGUI page (enabling humans to browse and jump to corresponding workflows in the Editor with a single click).
