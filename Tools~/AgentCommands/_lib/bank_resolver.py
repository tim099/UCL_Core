"""
bank_resolver — agent → Treasury bank account 的「單一 source-of-truth」resolver。

跨專案共用 (UCL_Core 代碼側 _lib)。所有需要把 placer 的 `agent` 字串映射到 Treasury
bank account 的工具 (awakening.py / canvas.py / 未來其他扣款 CLI) **MUST** 走本模組，
不得各自維護平行對照表 — 平行表是 identity-layer drift 的根源 (2026-06-04 canvas 把
Zeta 麾下 persona 的 token 誤扣到 claude-da-xiaojie，就是 canvas 自維護一張 case-sensitive
硬寫表、與 awakening 的 registry-based resolver 漂移所致)。

Canonical 規則 (Tim 2026-06-04 拍板):
  - **Bank account 由 agent 決定，無 persona-level override** — 同 agent 麾下所有 persona
    共用同一個 bank (self-constitution「Token bank 共用 per Agent」rule)。
  - **Source of truth = AwakenInit/_registry_meta.json 的 `agent_banks` dict**。
  - Lookup **case-insensitive** (Windows 大小寫不敏感，'Zeta' / 'zeta' 同歸一 canonical)
    + alias resolution (registry `agent_aliases` > 內建 DEFAULT_AGENT_ALIASES)。
  - registry / alias 都認不出 → 命名慣例 fallback `{canonical}-da-xiaojie`
    (caller 自決開新 bank；新 agent 應同步補進 _registry_meta.json 讓 source 完整)。
"""

import json
import sys as _sys
from pathlib import Path


# 區塊職責：vendor brand → canonical agent 的內建 alias fallback。
# 物理意義：registry meta 沒寫 `agent_aliases` 時的兜底，只把「明顯同一 IDE 的別名」
#           寫死 (Claude / Anthropic 都歸 claude-code)，不替「同公司不同 brand」猜對應
#           (Tim 2026-05-13 二輪拍板：gemini 是獨立 canonical agent，不混進 antigravity)。
# 數值影響：key 一律小寫 (lookup 端會把輸入轉小寫比對)；value 必須是 agent_banks 的 canonical key。
DEFAULT_AGENT_ALIASES = {
    "claude": "claude-code",       # Claude → Anthropic IDE canonical
    "anthropic": "claude-code",
}


def normalize_agent(reg: dict, agent: str) -> str:
    """
    區塊職責：把使用者輸入的 agent 字串歸到 canonical agent key。
    物理意義：Windows 大小寫不敏感，所以 'Gemini' / 'GEMINI' / 'gemini' 都該歸到既有
              canonical key，避免 lock / persona file / tavern post / bank 出現 split-brain。
    數值影響：純查表，不寫檔。

    Resolution order (Tim 2026-05-13 拍板 agent-login-case-insensitive T01)：
      1. Direct hit on agent_banks → return as-is
      2. Case-insensitive match against agent_banks keys → return canonical key
      3. Alias lookup (registry meta `agent_aliases` 或 DEFAULT_AGENT_ALIASES) 撈小寫
         alias → canonical name → recurse step 1/2
      4. 不認得 → 原樣 return (caller 自決開新 bank or warn)
    """
    # 空字串原樣回 — caller 端自行處理 missing agent
    if not agent:
        return agent
    # agent_banks 是 canonical agent → bank 的權威映射表；缺欄位時當空 dict
    banks = reg.get("agent_banks", {}) or {}
    # Step 1: direct hit（輸入已是 canonical key）
    if agent in banks:
        return agent
    # Step 2: case-insensitive 比對 banks keys（Windows 大小寫不敏感）
    lower = agent.lower()
    for k in banks.keys():
        if k.lower() == lower:
            return k
    # Step 3: alias（registry override 優先於內建 default，皆以小寫 key 合併）
    aliases = reg.get("agent_aliases", {}) or {}
    merged = {k.lower(): v for k, v in DEFAULT_AGENT_ALIASES.items()}
    merged.update({k.lower(): v for k, v in aliases.items()})
    if lower in merged:
        canonical = merged[lower]
        # alias 指向的 canonical 也走一輪 banks lookup（萬一 alias 寫了 typo / 大小寫）
        if canonical in banks:
            return canonical
        for k in banks.keys():
            if k.lower() == canonical.lower():
                return k
        return canonical
    # Step 4: unknown — 原樣 return（交給 resolve_bank_account 的命名慣例 fallback）
    return agent


# ⚠ 帳號解析模式開關（`account_resolve_unified`）已於 2026-08-20 移除（Tim 拍板：徹底改用新流程）。
#   兩個專案都跑完合一遷移 ⇒ **合一是唯一模式**：agent id 就是帳號 id，一跳到底，
#   `agent_banks` 不參與解析。C# 端（`UCL_CentralBankSettings`）同批移除 ——
#   只拔一端的症狀是「同一個 persona 在兩條路徑上拿到不同帳號」，而兩邊都不報錯。
#   舊的兩跳鏈與它的血證見 git。


def resolve_bank_account(reg: dict, agent: str, model: str = None) -> str:
    """
    區塊職責：把 agent 解析成 Treasury bank account id（本系統唯一的 agent→bank 入口）。
    物理意義：Bank account 綁 agent（per Tim 拍板 self-constitution Token bank 共用 rule），
              不該按 (agent, model) 雙鍵查 — model 是 free-form display field，跨 model 共用 bank。
    數值影響：純查表，不寫檔。

    `model` 參數保留 backward-compat（v1 caller 仍可傳，但不參與 lookup）。

    解析順序：
      1. normalize_agent() 歸 canonical name（case-insensitive + alias）
      2. Schema v2 (preferred)：agent_banks dict 直接命中
      3. Schema v1 fallback (legacy)：agent_model_combos list — 只比 agent 不比 model
      4. 最終 fallback：命名慣例 `{canonical}-da-xiaojie`（registry 認不出 → 隱含開新 bank）
    """
    # Step 1: normalize 僅供**舊鏈**（agent 名 → 帳號名）使用；合一鏈刻意不走它，見下。
    canonical = normalize_agent(reg, agent)
    # Step 1.5（Tim 2026-08-20 合一遷移）：開關為 true ⇒ **agent id 就是帳號 id**，一跳到底。
    #   ⚠ 這裡回的是**原始輸入**而不是 `canonical` —— 這一格是血證，不是風格：
    #     `normalize_agent` 會把輸入 case-insensitive 歸一成 `agent_banks` 的 key，
    #     於是合一模式下 `zeta` 會被歸成 `Zeta`（實測），而 **`Zeta` 是已銷戶帳號** ⇒
    #     錢會流向一個合法但禁止金流的帳號，全程零報錯。
    #     合一後 `zeta` 與 `Zeta` 是**兩個不同帳戶**，大小寫歸一在這條鏈上是有害的，
    #     它是舊鏈（agent 名 → 帳號名）才需要的東西。
    #   ⚠ 連帶：alias 在合一模式下**不生效**。這是刻意的 —— alias 表映射的是舊 agent 名
    #     （如 `claude`→`claude-code`），而那些名字在合一後已經不是任何人的帳號。
    #     遷移後要保留的別名要重新登記成「新帳號名的別名」，不能靠舊表順延。
    #   ⚠ 開關拔除後這一段是**唯一**路徑；底下 Step 2-4 保留為 legacy，
    #     只有在輸入是空字串時才可能走到（見下方 guard）。
    _unified = (agent or "").strip()
    if _unified:
        return _unified
    # ⚠ 以下是 legacy 兩跳鏈（agent → agent_banks → 帳號）。合一之後不該被走到 ——
    #   走到代表輸入是空的 agent。出聲是為了讓「沒人在讀舊表」有讀數可看，而不是靠猜。
    print("⚠ [bank_resolver] 走到 legacy agent_banks 兩跳鏈（agent 為空）"
          f" —— input={agent!r}", file=_sys.stderr)
    # Step 2: Schema v2 — agent_banks dict 為權威映射
    banks = reg.get("agent_banks", {})
    if canonical in banks:
        return banks[canonical]
    # Step 3: Schema v1 legacy — agent_model_combos，只查 agent（model 不參與）
    for combo in reg.get("agent_model_combos", []):
        if combo["agent"] == canonical:
            return combo["bank_account"]
    # Step 4: 認不出 → **原樣回傳 canonical 名並大聲警告，絕不 derive 新 bank 名**
    #
    # ⚠ 2026-08-14 修（summit）：本步原本是 `return f"{canonical}-da-xiaojie"` ——
    #   「registry 認不出 → 隱含開新 bank」。那句話讀起來像便利功能，實際是**孤兒帳戶製造機**：
    #   它會憑空造出一個沒有主人的帳號名，而錢就落在那裡，沒有任何一層會出聲。
    #
    #   實查 12,742 筆 ledger 留著它的指紋：`gemini-da-xiaojie`(94 token)、
    #   `zeta-da-xiaojie`(3)、`ClaudeCode-da-xiaojie`(1)、`tim099-da-xiaojie`(1)，
    #   以及最有說服力的 `antigravity-da-xiaojie-da-xiaojie`(**雙後綴**) ——
    #   derive 跑在一個已經 derive 過的名字上。
    #
    #   同期 C# 端（UCL_TreasuryAccountResolver）對同一個問題的答案是相反的：
    #   「查不到就標記 Unresolved 並原樣通過，絕不 derive、絕不 mint」。
    #   兩套實作對同一問題給出相反答案 = 已經在產出的 split-brain。本步向 C# 對齊。
    #
    #   為什麼是「原樣回傳」而不是丟例外：拒絕會讓一筆真實勞動的薪水直接消失，
    #   而標記只是讓它留在看得見的地方等人歸戶。**標記比丟棄安全。**
    print(f"⚠ [bank_resolver] agent '{agent}' 查無登記的 bank（canonical='{canonical}'）—— "
          f"原樣回傳，不 derive。錢會落在孤兒帳戶，請到銀行後台補登記或加別名。",
          file=_sys.stderr)
    return canonical


class PersonaResolutionError(LookupError):
    """
    persona → agent 解析失敗 — 查無此 persona，或 persona 檔缺 agent 欄。

    fail-loud 專用例外 (basecamp 2026-07-21 Bank 整合釘子一)：persona→agent 這一跳
    查不到時一律炸給人看，**絕不 silent-default 到某個預設 agent / bank**。
    理由：mis-resolve 到錯帳號比 stale 顯示更毒 — 錢進錯 bank、外觀卻完全正常，
    正是「舊快照假綠」的錢版 (Zeta→claude 錯位就是 silent-default 的前科)。
    """
    pass


def resolve_persona_to_agent(reg: dict, persona: str) -> str:
    """
    區塊職責：persona → 所屬 agent 的唯一解析入口（persona→agent→bank 兩跳鏈的第一跳）。
    物理意義：persona→agent 的 source-of-truth 早已存在 = personas/<name>.json 的 `agent` 欄
              (awakening.load_registry() 掃 personas/*.json 併進 reg["personas"])。本函式只「讀」
              這個既有 SOT，**不另存一份 persona→agent 表** (Bank 整合 #3 拍板：API 層集中、
              data 層不複製；再存一份就是親手造第二張表 = 2026-06-04 canvas drift bug 的形狀)。
    數值影響：純查表，不寫檔。
    fail-loud (basecamp 釘子一)：persona 不在 registry 或缺 agent 欄 → raise PersonaResolutionError。
    """
    # personas 由 awakening.load_registry() 掃 personas/*.json 併入；缺欄位視為空 dict
    personas = reg.get("personas", {}) or {}
    # QA 硬化 (summit + kaguya 2026-07-21 converging footgun): reg 完全沒 personas 通常不是「查無此人」,
    # 是 loader 誤用 — load_registry_meta() 只載 meta (agent_banks/aliases)、不掃 personas。persona-based
    # resolve 必須用 awakening.load_registry() (掃 personas/*.json 併入)。明示這條, 免每個 persona 都炸得莫名。
    if not personas:
        raise PersonaResolutionError(
            f"reg 完全沒有 personas (0 known) — persona-based resolve 需 awakening.load_registry() "
            f"(掃 personas/*.json 併入 reg['personas']); 你可能誤用了 load_registry_meta() (只載 meta、無 personas)。"
            f"resolve persona '{persona}' 失敗。"
        )
    # 第一道 fail-loud：查無此 persona → 炸，不 default 到任何 agent
    if persona not in personas:
        raise PersonaResolutionError(
            f"persona '{persona}' 不在 registry personas ({len(personas)} known) — "
            f"無法解析所屬 agent；拒絕 silent-default。"
            f"請確認 persona 檔存在 (AwakenInit/personas/{persona}.json) 且 reg 已載入 personas。"
        )
    # 第二道 fail-loud：persona 檔缺 agent 欄 → SOT 破損，炸
    agent = (personas[persona] or {}).get("agent")
    if not agent:
        raise PersonaResolutionError(
            f"persona '{persona}' 檔缺 'agent' 欄 — persona→agent SOT 破損；拒絕 silent-default。"
        )
    return agent


def resolve_persona_bank(reg: dict, persona: str, model: str = None) -> str:
    """
    區塊職責：persona → bank account 的兩跳鏈解析（persona→agent→bank），對外唯一入口。
    物理意義：券綁 persona、幣(token)綁 bank，兩軸分開 (Tim 2026-07-21 拍板)。本函式解的是
              「幣」軸 — 某 persona 的 token / 餘額該記到哪個 bank = 它所屬 agent 的 bank
              (self-constitution「Token bank 共用 per Agent」rule，同 agent 麾下 persona 共用一 bank)。
    數值影響：純查表，不寫檔。
    解析鏈：
      1. resolve_persona_to_agent(reg, persona)  # persona →(personas agent 欄=SOT)→ agent  [fail-loud]
      2. resolve_bank_account(reg, agent, model) # agent →(agent_banks=SOT)→ bank
    fail-loud：第一跳 persona 查不到直接 raise PersonaResolutionError；第二跳沿用
              resolve_bank_account 既有語意（認得→bank；未知 agent→命名慣例 {agent}-da-xiaojie，
              此為「開新 bank」的刻意 derive、非 mis-route，不違反 fail-loud）。
    """
    # ① 反向登記優先（§8.1，Tim 2026-08-19 拍板）—— 銀行端宣告誰是自己的人
    aBank, aWhy = resolve_persona_bank_reverse(reg, persona)
    if aBank is not None:
        return aBank

    # ② 反向表沒有這個人 ⇒ 舊的正向鏈（persona→agent→bank）。
    #    過渡期刻意保留：反向表是新資料，缺一位不該讓那位的錢無處可去。
    #    ⚠ 但**不准安靜地退** —— 退了要留痕，否則「反向表漏一位」與「反向表已完整」同形，
    #      收斂就永遠量不出來（同 §8.4 那條 log 歸零判準的道理）。
    print(f"⚠ [bank_resolver] persona '{persona}' 不在 bank_personas 反向表裡（{aWhy}）"
          f" —— 退回正向鏈 persona→agent→bank。請把它登記到某個 bank 的 personas[]。",
          file=_sys.stderr)
    agent = resolve_persona_to_agent(reg, persona)
    return resolve_bank_account(reg, agent, model)


# 區塊職責：§8.1 反向登記的解析 —— 「這個 persona 屬於哪家 bank」由**銀行端**回答。
# 物理意義：舊模型是 persona 記自己的 agent、agent 記自己的 bank（兩跳正向鏈）。
#          Tim 2026-08-19 拍板反轉：`bank_personas[<bank>] = [personas…]`，
#          錢的歸屬由銀行宣告 ⇒「說話認 persona、錢認 bank」兩條線各自獨立，
#          不再經 agent 中轉推導。
# ⚠ 同一 persona 出現在兩家 bank ⇒ **fail-loud**（raise），不挑一個。
#   理由是這條錯誤的代價：錢進錯帳戶不會有人喊痛，而挑一個就是替它做決定。
# ⚠ 對側契約：C# 端等價實作在 UCL_TreasuryAccountResolver（同一張 bank_personas 表）。
#   兩端要一起改 —— 只改一端的後果是同一個 persona 在兩邊解到不同 bank，而兩邊都不報錯。
# 數值影響：純查表不寫檔。回 (bank, why)；查無此人回 (None, 理由)。
#          比對用 casefold（Windows 大小寫不敏感，'Kiara' 與 'kiara' 必須同歸一位）。
def resolve_persona_bank_reverse(reg: dict, persona: str):
    aTable = reg.get("bank_personas")
    if not isinstance(aTable, dict) or not aTable:
        return None, "反向表不存在或為空"

    aKey = (persona or "").strip().casefold()
    if not aKey:
        return None, "persona 名為空"

    aHits = []
    for aBank, aList in aTable.items():
        if not isinstance(aList, list):
            continue
        for aName in aList:
            if str(aName).strip().casefold() == aKey:
                aHits.append(aBank)
                break

    if len(aHits) > 1:
        raise PersonaResolutionError(
            f"persona '{persona}' 同時登記在 {len(aHits)} 家 bank：{sorted(aHits)} —— "
            f"拒絕解析（§8.1：錢進錯帳戶是最貴的靜默錯，這裡不替你挑一個）。"
            f"請到 _registry_meta.json 的 bank_personas 把多餘那筆刪掉。")
    if len(aHits) == 1:
        return aHits[0], "反向表命中"
    return None, "反向表沒有這個人"


def all_account_ids(reg: dict) -> set:
    """
    區塊職責：回傳「帳號宇宙」= 所有合法 account_id 的集合（Bank 整合 2026-07-21 收攏）。
    物理意義：帳號 taxonomy 兩類由本 registry 管——agent-wallet (agent_banks 的 value) +
              system-npc (system_accounts 的 key, e.g. tavern-keeper)。external-human (如 Tim)
              只存在於 ledger、不由本 registry 宣告，故不在此集合（呼叫端若要含 Tim 自行 union）。
    數值影響：純讀 reg，不寫檔。給 derive 型 consumer 用（e.g. 通知 watched 清單「全部已知帳號」時
              可 derive 自本函式而非另存一份會漂的清單）。
    """
    banks = set((reg.get("agent_banks") or {}).values())
    system = set((reg.get("system_accounts") or {}).keys())
    return banks | system


def load_registry_meta(meta_path) -> dict:
    """
    區塊職責：輕量讀取 _registry_meta.json（只取 agent_banks / agent_aliases 等 metadata）。
    物理意義：給「不需要 scan personas/*.json」的 caller（如 canvas.py 扣款）一條最省的
              registry 載入路徑 — pure resolver 函式只吃 agent_banks + agent_aliases 兩欄。
    數值影響：純讀；檔不存在或解析失敗時回空 dict（resolver 自動退命名慣例 fallback，不 fatal）。

    注意：awakening.py 有自己的 load_registry()（會 trigger migration + scan personas），
          回傳的 dict 同樣含 agent_banks/agent_aliases，可直接餵給上面的 resolver — 無需改用本函式。

    ⚠️ 本 loader 回傳的 reg **不含 personas**（只有 meta）。故它只適合 **agent-based** resolve
       (resolve_bank_account / all_account_ids)。**persona-based** resolve
       (resolve_persona_to_agent / resolve_persona_bank) 需要 reg["personas"] —
       請改用 awakening.load_registry()，否則會對每個 persona 觸發 PersonaResolutionError
       (summit + kaguya 2026-07-21 QA 撞出的 footgun)。
    """
    p = Path(meta_path)
    if not p.exists():
        return {}
    try:
        with open(p, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        # 壞檔不該擋扣款流程崩潰：回空 dict，resolver 走命名慣例 fallback
        return {}
