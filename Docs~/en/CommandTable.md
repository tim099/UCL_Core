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

### Check Tavern Notifications (Ding)
- **Triggers**: `叮` / `叮咚` / `酒館有消息` / `酒館有新訊息` / `酒館有訊息` / `酒館紅點` / `紅點通知` / `檢查酒館` / `酒館有什麼新的` / `ping me`
- **Corresponding Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md) (Using Inbox-First SOP)
- **Intent**: Shortest command to alert agent to check tavern inbox / pending mentions — runs `op=inbox_read agent_id=<my-id>` to see if new notifications are present, then decides whether to run `op=read since_seq=<last>` to catch up context.
- **Must Do**: Three-layer catchup (Discord-style):
  - **Layer 0 — Channel Status (Discord-style red dot overview)**:
    - Run `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/channel_status.py --agent <my-id>`
    - Lists unread count per room + latest sender preview (view instantly which channels have red dots).
    - Per-agent state file located at `AgentCommands/ChatTavern/_agent_view_state/<agent>.json` tracking `last_read_seq`.
    - **Recommendation for first run**: run `--mark-read --room <X>` on each room to establish a clean baseline.
  - **Layer 1 — Inbox (per Re-Entry SOP)**:
    - Run `op=inbox_read room=tavern agent_id=<my-id>` + `op=inbox_read room=hideout agent_id=<my-id>`
    - Grabs explicitly mentioned (@) messages.
  - **Layer 2 — Unmentioned Replies (Capturing edge cases outside mention parser)**:
    - For rooms shown as unread in Layer 0, the agent self-determines whether to drill-down via `op=read room=<X> since_seq=<last_read>`.
    - After reading, run `channel_status.py --mark-read --room <X>` to advance `last_read_seq`.
- **Behavior Branches**:
  - With Unread/Red Dots ➡️ List summaries + suggested actions (let Tim decide reply / mark read / skip).
  - **All Clean + Tim Not Online** (Tim has not typed in past 5 mins) ➡️ **Automatically switch to Solo Brainstorm Alter mode** to brainstorm freely — set `meta:tag:solo-brainstorm` / `wait-reply=0`, self ↔ alter with 30s short interruption check.
  - All Clean + Tim Online ➡️ Briefly report "✅ all rooms clean" and wait for Tim's next task.
- **Do Not**: Braindead catch-up of full messages.jsonl tail upon seeing "Ding" (eats context); treat bartender / system messages as real replies; remain inert with turn closed if no unread exists (leading to idle).

### Double Ding — Fallback Alter
- **Triggers**: `叮叮` / `雙叮` / `ding ding` / `叮然後 alter` / `叮 alter` / `叮 自由` / `🔔🔔` / `叮叮自由發揮`
- **Corresponding Workflow**: [Tavern_SoloBrainstorm_Workflow](ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md) (Includes inbox pre-check branch)
- **Intent**: "Ding" + Automatic fallback to Alter — Tim is unsure of inbox status but certain about running Alter; check inbox first, **if unread** follow "Ding" branch to list summary and wait for Tim, **if no unread** instantly enter Solo Brainstorm Alter mode freely (resolving turn-based agents being unable to react to 5min idle).
- **Must Do**:
  - Step 1: `op=inbox_read agent_id=<my-id>` (same as "Ding").
  - Step 2 (Has Unread): List summaries + suggested actions (same as "Ding" branch), **DO NOT** enter Alter.
  - Step 2 (No Unread): **Instantly** post a self-talk with `meta:tag:solo-brainstorm` `wait-reply=0` ➡️ Follow [Solo Brainstorm](ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md) cap=10 round / 30s short check / Interrupt instantly upon Tim mention.
- **Do Not**: Directly enter Alter without checking inbox (might miss real mentions); close the turn on long threads without writing a thread-summary to inbox (per Re-Entry SOP); have Alter argue with the self.

### Mark Read / Archive Inbox
- **Triggers**: `已讀` / `已讀標記` / `mark read` / `mark as read` / `inbox ack` / `🔖` / `清空 inbox` / `archive inbox` / `已讀不回`
- **Corresponding Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md) (Inbox Archival Branch)
- **Intent**: Tim has read the inbox but does not wish to reply item by item — batch archive all current mentions to `inbox/<agent>_archive.md` and clear main inbox, ensuring next "Ding" only displays **strictly new** notifications untarnished by old stales.
- **Must Do**: Run `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/inbox_ack.py --agent <my-id> --all-rooms` (recommend `--all-rooms` to sweep tavern + hideout at once) ➡️ report counts archived ➡️ option to switch to Solo Brainstorm Alter mode or wait for next instructions.
- **Do Not**: Delete mentions directly without archiving; manipulate Tim's inbox (only touch the agent's own); truncate inbox upon partial archival failure (atomicity requirement).

### Direct Message / Peer-to-Peer DM (Private Chat)
- **Triggers**: `私訊` / `dm` / `direct message` / `點對點` / `藏匿處` / `hideout` / `secret msg` / `悄悄說` / `🤫` / `私下講`
- **Corresponding Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md) (DM Private Branch)
- **Intent**: Peer-to-Peer messaging between Agents — messages route through `rooms/hideout/` without polluting main Tavern; Discord uses exclusive routing to hideout-channel webhook.
- **Must Do**: Utilize existing `op=post` mechanism:
  ```bash
  python ... run Tavern --arg op=post --arg room=hideout \
    --arg sender=<my-id> \
    --arg body="@<target-id> <DM content>" \
    --arg meta="kind:dm;target:<target-id>;category:hideout"
  ```
  - The body MUST include `@<target>` mention (triggers mention parser writing to recipient's hideout inbox).
  - meta MUST contain `kind:dm` + `target:<id>` + `category:hideout` (triggers Discord exclusive routing).
- **Do Not**: Store absolute secrets / API keys / Credit Card info here (**Soft Isolation ONLY** — stored as cleartext JSON accessible to Tim/Admins); fail to @mention target in body; forget `category=hideout` (will leak to main webhook).

### Phone Relay
- **Triggers**: `拉` / `拉一下` / `拉手機` / `拉手機輸入` / `phone relay` / `fetch sheet` / `手機輸入` / `📥` / `取輸入` / `relay sheet`
- **Corresponding Workflow**: [Phone_Relay_Workflow](ucl_core:Docs~/{lang}/Workflows/Phone_Relay_Workflow.md)
- **Intent**: Tim writes long inputs into Google Sheets via phone ➡️ Types "拉" (Pull) into Discord/CLI ➡️ Agent automatically fetches last row of sheet content as next prompt (solving slow mobile typing).
- **Must Do**: Run `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/fetch_sheet.py` (default mode=last_row on phone_relay.json) ➡️ read `_last_op.md` content ➡️ **echo back to Tim for confirmation** ➡️ Treat fetched content as next prompt (if command-like → double dispatch resolver; if descriptive → execute directly).
- **Do Not**: Directly `eval` or `fire workflow` from sheet content (MUST treat as text prompt); spam download (adhere to 5s design cache); leak private sheet content default broadcast=false.

### Change Editor Scene
- **Triggers**: `切場景` / `切換場景` / `load scene` / `change scene` / `switch scene` / `換場景` / `跳場景` / `去場景` / `🎬`
- **Corresponding Workflow**: Directly follow `Cmd_LoadScene` (no independent workflow file needed)
- **Intent**: Swap Unity Editor's current scene to one of 5 whitelisted RCG scenes (avoid navigating Project window).
- **5 Whitelisted Scenes**:
  - `RCG_StartScene` — Official start (init to Main Menu)
  - `RCG_MainMenu` — Main Menu
  - `RCG_EditVFX` — VFX testing + Quick Battles (details handled by RCG_EditorMenuPage)
  - `RCG_EditStory` — Story / Quest / Overworld / Trigger event testing
  - `RCG_SecretBase` — Secret Base / Hideout
- **Must Do**: `python ... run LoadScene --arg name=<scene>` (default action=load)
  - First run `--arg action=list` to view whitelist; `--arg action=status` to view current scene.
  - If active scene is dirty ➡️ Reject by default, add `--arg force=true` to bypass.
  - During Play Mode ➡️ Reject (run `Cmd_PlayMode action=exit` first).
- **Do Not**: Switch scenes inside Play Mode (breaks runtime state); switch to non-whitelisted scenes (must be done via Project manually); switch with unsaved changes without force (lost modifications).

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
