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
    # Step 1: 先 normalize（handle case-insensitive + alias），避免大小寫 split-brain
    canonical = normalize_agent(reg, agent)
    # Step 2: Schema v2 — agent_banks dict 為權威映射
    banks = reg.get("agent_banks", {})
    if canonical in banks:
        return banks[canonical]
    # Step 3: Schema v1 legacy — agent_model_combos，只查 agent（model 不參與）
    for combo in reg.get("agent_model_combos", []):
        if combo["agent"] == canonical:
            return combo["bank_account"]
    # Step 4: 命名慣例 fallback（canonical 仍認不出 → 開新 bank）
    return f"{canonical}-da-xiaojie"


def load_registry_meta(meta_path) -> dict:
    """
    區塊職責：輕量讀取 _registry_meta.json（只取 agent_banks / agent_aliases 等 metadata）。
    物理意義：給「不需要 scan personas/*.json」的 caller（如 canvas.py 扣款）一條最省的
              registry 載入路徑 — pure resolver 函式只吃 agent_banks + agent_aliases 兩欄。
    數值影響：純讀；檔不存在或解析失敗時回空 dict（resolver 自動退命名慣例 fallback，不 fatal）。

    注意：awakening.py 有自己的 load_registry()（會 trigger migration + scan personas），
          回傳的 dict 同樣含 agent_banks/agent_aliases，可直接餵給上面的 resolver — 無需改用本函式。
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
