#!/usr/bin/env python3
"""身分解析階梯（P0b）—— 「這筆操作是誰的」的**唯一判定點**，三態回傳。

區塊職責：把散在各處的「猜 persona」收攏成一條有明確優先序、且**分得出三種結果**的階梯。

物理意義（為什麼非做不可 —— 這是血換的）：
  舊實作 `tavern_cmd.autofill_persona_from_lock` 的 tier 2 是這一行：

      chosen = max(origin_hits, key=lambda d: d.get("locked_at", ""))

  同一個 claim_origin 下有多個 live lock（= 同一台機器 / 同一個 chat 開了多個 persona）時，
  它**靜默挑 locked_at 最新的那個**，零警告。實際代價（kaguya 付的）：
    - tavern post 被誤推成 kiara → 用別人的名字發言
    - goodnight 誤跑成 basecamp → **蓋掉別人的 `_latest.md`**
  而 tier 3（agent-marker）反而早就寫對了：`len(agent_hits) == 1` 才填，多個就出聲。
  → 「要修的是 tier 2，tier 3 是可直接抄的範本」（basecamp 複驗、kotoko 認錯、kaguya 蓋章）。

設計取捨：
  - **三態，不可壓成二元**（kaguya 血證規格）：
        Resolution.persona / .ambiguous(候選清單) / .none
    把「有三個候選」跟「一個都沒有」壓成同一個回傳值 = 同碼失聲：
    caller 分不出「我該問你是誰」跟「這裡本來就沒有身分」，於是兩種都當成後者。
  - **回 none 的語意是「本層沒有答案」，不是「查無此人」** —— caller 不可當否定證據。
  - 為什麼是扁平 sibling 而不是 `_lib/persona_resolve.py`：`_lib` 有 shadowing 陷阱
    （UCL_Core 與主專案各一個，解析到哪邊取決於 import 順序），而本模組的呼叫端
    **正好會 import awakening**（它會把 <repo>/AgentCommands 插進 sys.path[0]）。
    把「我是誰」壓在一顆會依呼叫順序翻面的骰子上，是這整族 bug 的溫床。
    詳見 tavern_cmd.py 檔頭的實測記錄。

階梯（越上面越是「說出來的」，越下面越是「猜出來的」）：
    tier 1  顯式 --persona / --arg persona=      宣告，最權威
    tier 2  queue 資料夾名（queues/<persona>/）  宣告的延遲讀取
    tier 3  session lock 反查                    **推論**，唯一會歧義的一層
    tier 4  查不到 → none（寫入類 caller 自行拒絕並列候選）
"""
from __future__ import annotations

import sys
from pathlib import Path

# 保留字：不是 persona，是「沒有宣告身分」這個狀態本身。
# 讀到它必須回 none —— 否則它會流進記帳層，而 bank_resolver 的命名慣例 fallback
# （{name}-da-xiaojie）會替一個不存在的人隱含開帳戶。
ANONYMOUS = "anonymous"

# 三態的 kind
KIND_PERSONA = "persona"        # 有明確答案
KIND_AMBIGUOUS = "ambiguous"    # 有多個候選，**拒絕猜**
KIND_NONE = "none"              # 本層沒有答案（≠ 查無此人）


class Resolution:
    """三態解析結果。刻意不是 `str | None` —— 那正是要治的同碼失聲。"""

    __slots__ = ("kind", "persona", "candidates", "tier", "note")

    def __init__(self, kind, persona=None, candidates=None, tier=None, note=""):
        self.kind = kind
        self.persona = persona
        self.candidates = list(candidates or [])
        self.tier = tier                    # 命中在哪一層（給稽核／訊息用）
        self.note = note                    # 層間不一致等附註（不改判，只留痕）

    # 便利判定 —— caller 不必記 kind 字串
    @property
    def ok(self) -> bool:
        return self.kind == KIND_PERSONA

    @property
    def is_ambiguous(self) -> bool:
        return self.kind == KIND_AMBIGUOUS

    def __repr__(self):
        if self.ok:
            return f"<Resolution persona={self.persona!r} tier={self.tier}>"
        if self.is_ambiguous:
            return f"<Resolution ambiguous {self.candidates} tier={self.tier}>"
        return f"<Resolution none tier={self.tier}>"

    def describe_for_human(self, action: str = "這個操作") -> str:
        """給拒絕訊息用的人話。**列出候選**，因為 argparse usage 幫不了正要選人的人。"""
        if self.ok:
            return f"{action} 的身分：{self.persona}（tier {self.tier}）"
        if self.is_ambiguous:
            names = " / ".join(self.candidates)
            return (f"✗ {action} 無法判定身分：目前有 {len(self.candidates)} 個 persona 在線 —— {names}。\n"
                    f"  請顯式指定（--persona <名字> 或 --arg persona=<名字>）。\n"
                    f"  ⚠ 不自動挑一個是刻意的：猜錯會用別人的名字做事，"
                    f"而那種錯沒有人會當場發現。")
        return (f"✗ {action} 查不到身分（沒有顯式宣告、queue 未署名、也沒有在線的 lock）。\n"
                f"  請顯式指定（--persona <名字>）。")


# ── tier 1：顯式宣告 ───────────────────────────────────────────────────
def from_explicit(value) -> Resolution | None:
    """顯式帶的值。空值回 None 表示「這一層沒有輸入」，讓 caller 往下走。"""
    v = (value or "").strip()
    if not v:
        return None
    if v == ANONYMOUS:
        # 顯式宣告 anonymous = 明說「我不署名」，那是合法意圖但不是一個 persona
        return Resolution(KIND_NONE, tier=1, note="顯式 anonymous（不署名）")
    return Resolution(KIND_PERSONA, persona=v, tier=1)


# ── tier 2：queue 資料夾名（宣告的延遲讀取，不是字串解析）──────────────
def from_queue_id(queue_id) -> Resolution | None:
    """queue id 形狀為 `<persona>` 或 `<persona>/<lane>`；取資料夾段。

    ⚠ 這一層**不解析檔名**。2026-08-01 的 queue 資料夾制之後，身分是路徑的一段，
      不是要從 `queue-ame-design.json` 裡猜「-design 是用途還是名字」的字串。
    """
    v = (queue_id or "").strip().replace("\\", "/")
    if not v:
        return None
    folder = v.split("/", 1)[0].strip()
    if not folder or folder == ANONYMOUS:
        return Resolution(KIND_NONE, tier=2, note="queue 未署名（anonymous）")
    return Resolution(KIND_PERSONA, persona=folder, tier=2)


# ── tier 3：session lock 反查（唯一會歧義的一層）────────────────────────
def from_locks(live_locks, my_origin=None, my_agent_marker=None,
               session_token=None) -> Resolution:
    """從在線 lock 反查。**多個候選一律回 ambiguous，絕不挑一個。**

    這裡是本次改版的核心。三段 fallback 由精準到寬鬆，命中即止：
      (a) session_token 精準匹配 —— 最權威（跨 env / 跨 ppid 都穩）
      (b) claim_origin 匹配      —— 🔴 **舊實作在這裡 `max(locked_at)` 靜默猜**
      (c) agent marker 匹配      —— 舊實作在這裡已經寫對（恰好 1 個才填）

    ⚠ (b) 是 kaguya 兩次事故的現場。「同 claim_origin 多 lock」＝同一台機器開了多個
      persona，那是**合法且常見**的場景 —— 正因為合法，才更不能猜：
      使用者沒有做錯任何事，卻會拿到別人的身分。
    """
    live = [lk for lk in (live_locks or []) if lk]

    # (a) session_token —— 唯一不可能歧義的一層（token 本身就是唯一鍵）
    tok = (session_token or "").strip()
    if tok:
        hits = [lk for lk in live if lk.get("session_token") == tok]
        if len(hits) == 1:
            return Resolution(KIND_PERSONA, persona=_p(hits[0]), tier=3,
                              note="session_token 精準匹配")
        if len(hits) > 1:
            # 同一個 token 對到多個 lock = 資料損毀，不是歧義。出聲而不是挑一個。
            return Resolution(KIND_AMBIGUOUS, candidates=_ps(hits), tier=3,
                              note="⚠ 同一 session_token 對到多個 lock（資料異常）")

    # (b) claim_origin —— 修掉 max(locked_at)
    if my_origin:
        hits = [lk for lk in live if lk.get("_claim_origin") == my_origin]
        if len(hits) == 1:
            return Resolution(KIND_PERSONA, persona=_p(hits[0]), tier=3,
                              note="claim_origin 匹配")
        if len(hits) > 1:
            return Resolution(KIND_AMBIGUOUS, candidates=_ps(hits), tier=3,
                              note="同一環境有多個 persona 在線")

    # (c) agent marker —— 沿用舊實作的正確形狀
    marker = (my_agent_marker or "").strip().lower()
    if marker and marker != "unknown":
        hits = [lk for lk in live
                if (lk.get("agent") or "").lower() == marker
                or (lk.get("agent") or "").lower().startswith(marker + "-")]
        if len(hits) == 1:
            return Resolution(KIND_PERSONA, persona=_p(hits[0]), tier=3,
                              note="agent marker 匹配")
        if len(hits) > 1:
            return Resolution(KIND_AMBIGUOUS, candidates=_ps(hits), tier=3,
                              note=f"agent '{marker}' 有多個 persona 在線")

    return Resolution(KIND_NONE, tier=3, note="無在線 lock 可對應")


def _p(lock) -> str:
    return (lock.get("persona") or "").strip()


def _ps(locks) -> list:
    return sorted({p for p in (_p(lk) for lk in locks) if p})


# ── 階梯總成 ────────────────────────────────────────────────────────────
def resolve(explicit=None, queue_id=None, live_locks=None, my_origin=None,
            my_agent_marker=None, session_token=None, on_mismatch=None) -> Resolution:
    """跑完整條階梯，回三態。

    層間一致性檢查（kaguya 2026-08-01 補的規則）：
      **上層命中時，若下層可查且答案不同 → 喊一聲，但不改判、不擋事。**
      理由（她的原話精華）：「歧義不會發生在宣告層，但**謊言（無意的）只會發生在
      宣告層**」—— 猜會錯、宣告會說錯，兩種病要兩種偵測。歧義檢查治不了「說錯」，
      因為說錯的時候一點都不歧義，它非常確定，只是確定得不對。
      宣告錯至少可 audit（queue 資料夾名就是證據），這是它該贏的理由；
      但 audit 要有人看才算數 —— 所以留痕。

    on_mismatch: callable(msg) —— 不給就印 stderr。不阻塞是刻意的。
    """
    warn = on_mismatch or (lambda m: print(f"  ⚠ {m}", file=sys.stderr))

    top = from_explicit(explicit) or from_queue_id(queue_id)
    lower = from_locks(live_locks, my_origin, my_agent_marker, session_token)

    if top is None:
        return lower                                  # 沒有宣告 → 直接用推論層的三態

    if top.ok and lower.ok and top.persona != lower.persona:
        warn(f"身分不一致：宣告說 '{top.persona}'（tier {top.tier}），"
             f"但在線 lock 只有 '{lower.persona}' —— 依宣告執行，此行僅留痕。")
        top.note = (top.note + "；" if top.note else "") + f"與 lock({lower.persona}) 不一致"
    elif top.ok and lower.is_ambiguous and top.persona not in lower.candidates:
        warn(f"身分不一致：宣告說 '{top.persona}'，但在線的是 {' / '.join(lower.candidates)} "
             f"—— 依宣告執行，此行僅留痕。")

    return top
