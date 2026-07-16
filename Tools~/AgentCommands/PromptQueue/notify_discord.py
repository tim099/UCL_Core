#!/usr/bin/env python3
"""
notify_discord.py — Discord webhook 通知 helper（PromptQueue queue 清空時通知 Tim）

設計重點：
- 純 stdlib（urllib + json + datetime），不裝 dependency
- 觸發條件嚴謹（pending=0 + in_progress=0 + 本輪有新 done + cooldown 過 + 沒 auto-disabled）
- 失敗 swallow — log 給 _drain.log，**絕不**阻擋 qdone / qdrain 主流程
- secret 走 git-ignored `_discord_webhook.txt`，明文存（簡單；防意外 commit 進 git）
- agent restart 進空 queue 不會觸發（baseline 機制）

外部介面：
    notify_if_queue_idle(states_dict, all_events, config_dict_or_none)
        → (sent: bool, reason: str)
    states_dict: 從 qdrain reduce_states 拿到的 {task_id: state} dict
    all_events: events.jsonl 全部 events list（list of dict）
    config_dict_or_none: 預先讀好的 config；None = 走 _load_config 自動讀
"""
import json
import os
import datetime
import pathlib
import sys as _sys
import urllib.request
import urllib.error

HERE = pathlib.Path(__file__).resolve().parent
# T36.2 — 路徑常數從 _lib.tavern_paths 統一引用（取代 hardcode）
# 物理意義：跨 PromptQueue / ChatTavern / dedupe 工具共用 single source of truth；
#          C# UCL_ChatTavernIO 改路徑 → tavern_paths 也跟著改 → 全部 caller 自動同步
# 數值影響：本檔可能在 import 順序前被 caller 拉進來，所以用 sys.path 確保能找到 _lib
# 區塊職責：repo root 探測（T-MOVE 2026-07-15 — 本檔已搬 UCL_Core Tools~，HERE.parent.parent 不再是 repo root）
# 物理意義：從本檔位置向上走找「含 AgentCommands/_lib/tavern_paths.py 的目錄」= 專案 repo root；
#          找不到退 cwd（C# / caller spawn 時 WorkingDirectory 都是 repo root）。
def _probe_repo_root(iStart):
    p = iStart
    for _ in range(12):
        if (p / "AgentCommands" / "_lib" / "tavern_paths.py").exists():
            return p
        if p.parent == p:
            break
        p = p.parent
    return pathlib.Path.cwd()
_REPO_ROOT_FOR_LIB = _probe_repo_root(HERE)
if str(_REPO_ROOT_FOR_LIB) not in _sys.path:
    _sys.path.insert(0, str(_REPO_ROOT_FOR_LIB))
from AgentCommands._lib import tavern_paths as _tp  # noqa: E402
from AgentCommands._lib import discord_webhook as _dw  # noqa: E402
from AgentCommands._lib import tavern_io as _tio  # noqa: E402

# T-MOVE: code 住 UCL_Core、data 留專案 — 所有 secret/log/state 檔錨到專案 PromptQueue 資料目錄
STATE_DIR = _tp.PROMPT_QUEUE_DIR

# ===========================================================
# 區塊：跨 process 互斥鎖 + 原子 state 寫入（TOCTOU fix, Tim 2026-07-15 拍板）
# 物理意義：每次 AppendMessage 都 fire-and-forget spawn 一個本腳本 process，load state → send →
#          save state 非原子 — 兩 process 撞期時後到者讀到舊 last_seen → 同 seq 重複發送
#          （Discord 端「偶發訊息發兩次」的根因）。鎖住整段 stream dispatch = 串行化。
# 數值影響：後到者「帶 timeout 等待」而非退出 — 退出會延遲它觸發的那筆訊息到下次 trigger。
#          stale 偵測：鎖檔 mtime 超過 _LOCK_STALE_SEC 視為死鎖殘留（前手 crash）→ 清掉重搶。
# ===========================================================
_MIRROR_LOCK_PATH = STATE_DIR / "_notify_discord.lock"
_LOCK_TIMEOUT_SEC = 30.0     # 等鎖上限；mirror 一輪典型秒級，30s 足夠
_LOCK_STALE_SEC = 120.0      # 鎖齡超過此值視為殘留
# T-COALESCE (2026-07-16 QA 後續): 每筆 tavern post 會觸發最多 3 個冗餘 run（C# mirror spawn +
# C# treasury spawn + python shim），waiters 各等 30s → 尖峰時排隊程序堆積成「殭屍潮」假象
# （summit QA 實測 50+ 程序; 隔離單 run 實測僅 5.3s 健康）。改 coalescing：搶不到鎖 → 留 pending
# flag 立刻退出（0 排隊）；持鎖者收尾前看到 flag → 補跑一輪把後到訊息撿走。
_MIRROR_PENDING_PATH = STATE_DIR / "_notify_pending.flag"


class _MirrorRunLock:
    """O_CREAT|O_EXCL 檔案鎖 context manager — 拿不到就輪詢等待，逾時放行（degrade 回無鎖行為並記 log）。"""

    def __init__(self):
        self._fd = None
        self.acquired = False
        self.bypass = False   # True = 殭屍鎖放行（無鎖執行）— caller 不可誤判成健康 holder 而 coalesce

    def __enter__(self):
        """非阻塞 try-acquire (T-COALESCE)：拿到 → acquired=True；拿不到 → acquired=False（caller
        該留 pending flag 立刻退出，不排隊）。stale 鎖照舊接管；stale 且刪不掉（Windows 殭屍 holder
        握著 fd）→ 放行無鎖執行。"""
        import os as _os
        import time as _time
        for _attempt in range(4):   # stale 接管後重試幾次即可，不做長輪詢
            try:
                self._fd = _os.open(str(_MIRROR_LOCK_PATH), _os.O_CREAT | _os.O_EXCL | _os.O_WRONLY)
                _os.write(self._fd, str(_os.getpid()).encode())
                self.acquired = True
                return self
            except FileExistsError:
                try:
                    age = _time.time() - _MIRROR_LOCK_PATH.stat().st_mtime
                except OSError:
                    continue  # 鎖剛被別人清掉 → 立刻重試
                if age > _LOCK_STALE_SEC:
                    try:
                        _MIRROR_LOCK_PATH.unlink(missing_ok=True)
                        continue
                    except OSError:
                        # P0b (summit QA 驗屍): Windows 下 holder 握著開啟的 fd 時 unlink 會
                        # PermissionError — 活著但 hang 死的殭屍。放行無鎖執行 + 記 pid 供清理。
                        try:
                            holder = _MIRROR_LOCK_PATH.read_text(errors="replace").strip()
                        except OSError:
                            holder = "?"
                        _log(f"[lock] stale lock (age {age:.0f}s) 無法刪除 — holder pid={holder} 疑似殭屍程序, 放行無鎖執行 (taskkill 該 pid 可根治)")
                        self.bypass = True
                        return self
                # 鎖被健康 holder 握著 → 不排隊（caller 走 coalescing）
                return self
        # 重試耗盡（鎖高頻消長）→ 當 coalesce 處理（acquired=False, bypass=False）
        return self

    def __exit__(self, *exc):
        import os as _os
        if self._fd is not None:
            try: _os.close(self._fd)
            except OSError: pass
        if self.acquired:
            try: _MIRROR_LOCK_PATH.unlink(missing_ok=True)
            except OSError: pass
        return False


def _atomic_write_text(iPath, iText):
    """tmp + os.replace 原子落檔 — 防並發讀到半截 JSON（state reset → 全房 re-baseline 漏訊息）。"""
    import os as _os
    tmp = iPath.with_suffix(iPath.suffix + ".tmp")
    tmp.write_text(iText, encoding="utf-8")
    _os.replace(str(tmp), str(iPath))

PROJECT_ROOT = _tp.REPO_ROOT
ROOM_DIR = _tp.get_room_dir("agent-prompt-queue")   # 既有 queue-idle stream 監看房
TASKS_DIR = _tp.get_tasks_dir("agent-prompt-queue")
ROOMS_DIR = _tp.ROOMS_DIR
CONFIG_PATH = _tp.NOTIFY_CONFIG_PATH
STATE_PATH = _tp.NOTIFY_STATE_PATH
TAVERN_STATE_PATH = _tp.TAVERN_STATE_PATH      # R6.3 — tavern mirror 獨立 state
WAKE_STATE_PATH = _tp.WAKE_STATE_PATH          # T16 — wake-notify stream 獨立 state

WEBHOOK_FILE_DEFAULT = STATE_DIR / "_discord_webhook.txt"   # local secret file (per-stream，留 caller 自管)
DRAIN_LOG = STATE_DIR / "_drain.log"   # 跟 qdrain.py 共用 log


DEFAULT_CONFIG = {
    # ===== queue-idle stream（既有）=====
    "enabled": True,
    "verbose": True,                  # True = 多行（含本輪 done titles + stats）；False = 一行濃縮
    "cooldown_minutes": 5,            # 距上次通知 < 此時間 → 不重發
    "webhook_file": "_discord_webhook.txt",                    # source 1: 同 dir 下 git-ignored 檔
    "webhook_env_var": "PROMPTQUEUE_DISCORD_WEBHOOK",          # source 2: 環境變數 fallback（CI / docker 用）
    "disable_after_failures": 5,      # 連續 N 次 POST 失敗 → 自動 disable + 寫 warning
    "tasks_per_message": 10,          # verbose 模式列出最多 N 個本輪 done task
    "use_local_time": True,           # 訊息時間戳走 local time（False = UTC）
    "channel_label": "PromptQueue",   # 訊息開頭 Tag（區分多專案 channel）

    # T10 — queue-idle stream identity（embed 卡頂端 username + avatar）
    # strategy: "actor"（預設，用最近 done task 的 actor），"fixed"（用 fixed_id），"none"（不 override 走 webhook 預設）
    # 共用 tavern_mirror 的 avatar_url_base / identity_overrides resolver（GitHub raw URL）
    "queue_idle_identity": {
        "strategy": "actor",                          # actor / fixed / none
        "fixed_id": "claude-da-xiaojie",              # strategy=fixed 用這個 id 解析
        "fallback_id": "claude-da-xiaojie",           # actor 取不到時 fallback
    },

    # T28 — queue-idle stream 跨 quest 房 done events 收集（per Tim 拍板 — 讓 Discord 看到所有 quest task done）
    # 物理意義：除既有 agent-prompt-queue events 外，掃 watched 房 events.jsonl 收 task_done 合流
    # 數值影響：per-room last_seen_seq 防重；events schema 跨房一致（task_create/claim/done 同 type 名）
    # 空 list = 走原 PromptQueue-only path（既有行為）
    "watched_quest_rooms": [],   # 例：["tavern-entry-latency", "chat-flow-robust"]

    # ===== tavern-mirror stream（R6.3 新；獨立 webhook 跟 state）=====
    "tavern_mirror": {
        "enabled": False,                 # 預設 off — Tim 手動 enable 才生效
        "webhook_urls": [],               # 跟 queue-idle 完全分離；獨立 broadcast list
        "webhook_file": "_tavern_webhook.txt",                  # secret 檔（git-ignored）；同 fmt
        "webhook_env_var": "PROMPTQUEUE_TAVERN_WEBHOOK",        # 獨立 ENV
        "rooms": [],                      # 監看的房名清單；空 = 不鏡像（必須顯式 opt-in）
        "kinds": ["chat"],                # 要鏡像的 message kind；預設只 chat（不含 system 防 R6 mirror 雙重發）
        "exclude_senders": ["_quest_system"],   # 跳過這些 sender（防 R6 quest mirror 反向 echo 回來）
        "include_senders": [],            # 白名單（空 = 不限制）
        # 區塊職責：依 meta.source 跳過訊息 — 防 inbound relay 迴圈
        # 物理意義：discord_inbound_bot 收 Discord 訊息寫 tavern 時帶 meta.source=discord;
        #          mirror 看到立刻 skip, 不會再 webhook 推回 Discord 形成 echo loop
        # 數值影響：list 空 = 不過濾 (舊行為兼容); 列出的 source 值 case-sensitive 完全比對
        "exclude_meta_source": ["discord"],
        # 區塊職責：依 sender_id prefix 跳過訊息 — 雙保險防 echo (T06 2026-05-15 fix)
        # 物理意義：當 meta.source 因 ParseMeta bug / 老訊息 / 寫入端漏帶等理由取不到時, sender_id prefix 為 fallback 識別
        # 數值影響：tuple 空 = 不啟用; ["discord:"] 預設擋掉 discord_inbound_bot relay 的所有訊息回推
        "exclude_sender_prefix": ["discord:"],
        "max_per_run": 20,                # 一次 Stop hook 最多發 N 則訊息（防爆 batch）
        "title_template": "**seq {seq}** · `{room}`",   # body 前的 header（identity 走 webhook username override，不重複）
        "body_max": 1500,                 # 單訊息 body 字數截斷
        "use_local_time": True,

        # R6.4 — 身分顯示設定（Discord webhook username/avatar_url override per-message）
        # 物理意義：Discord webhook API 支援每筆 POST 帶 username + avatar_url 覆寫，讓單一 webhook
        #          顯示成不同身分。乾淨地把 Claude / Gemini / Zeta 在 Discord 上區分開
        # 數值影響：sender_id 拆解 → username + avatar_url；fallback chain：override > identity → derive
        "avatar_url_base": "https://raw.githubusercontent.com/tim099/UCL_Core/Dev/Templates~/Assets/.BuiltinModules/ModulesRoot/Modules/Core/ModResources/Sprites/Avatars/",
        "avatar_url_pattern": "{base}{id}.png",   # convention: 檔名 = identity id
        "identity_overrides": {},                  # per-id 覆寫：{ "claude-da-xiaojie": { "username": "...", "avatar_url": "..." } }

        # 區塊職責：persona-level avatar 顯式覆寫（Tim 2026-07-15 拍板）— 最高優先級
        # 物理意義：sprite 派生 URL 只能指向 avatar_url_base（GitHub raw）；本表讓指定 persona 直接
        #          釘任意外部 URL（如 wiki 圖床），不必把圖 push 進 repo。
        # 數值影響：key = sender_persona（非 sender_id）；命中即直接採用、不做 HEAD 預檢
        #          （顯式設定 = 使用者自負 URL 有效性；壞 URL Discord 端 silent fallback 預設頭像）
        "persona_avatar_overrides": {},            # { "summit": "https://.../Altair_Infobox.png" }

        # 區塊職責：Quest task lifecycle 訊息（sender=_quest_system, kind=system）分流到獨立 webhook
        # 物理意義：避免 task_create / task_claim / task_done 高頻 lifecycle 訊息洗版主 chat webhook
        # 數值影響：state 共用 _tavern_state.json（last_seen_seq 不分 webhook）；每筆 message 按 sender 選 webhook 群組
        # 邊界：disabled / 無 URL → fallback 走 main webhook_urls（兼容舊行為）
        "quest_routing": {
            "enabled": False,                                           # 預設 off
            "sender_match_prefix": "_quest_system",                     # sender_id 前綴 match（startswith）
            "webhook_urls": [],                                         # 跟 main tavern_mirror webhook 完全分離
            "webhook_file": "_quest_webhook.txt",                       # secret 檔（git-ignored）
            "webhook_env_var": "PROMPTQUEUE_QUEST_WEBHOOK",             # 獨立 ENV
        },

        # T42 — Category meta tag 路由（per Tim 拍板）
        # 物理意義：訊息 meta.category 命中某 UCL_TavernCategoryRoutingAsset.m_Categories → broadcast 到該 group's webhook URLs
        #          沒命中 → m_IsDefault=true 的 group fallback；都沒 default → tavern_mirror.webhook_urls 既有 fallback
        # 數值影響：groups 從 .BuiltinModules/.../UCL_Assets/UCL_TavernCategoryRoutingAsset/*.json 載入；
        #          notify_config.json 不再寫死 groups schema（v2 修訂走 UCL_Asset 體系）
        "category_routing": {
            "enabled": False,                                            # 預設 off — Tim 手動 enable 才生效
            "asset_type": "UCL_TavernCategoryRoutingAsset",              # UCL_Asset 子類短名
            "known_categories": ["work", "chitchat", "relax", "meta"],   # 建議的 enum-like 列表（不強制；agent 帶任意字串也接受）
            "case_insensitive": True,                                    # category 比對是否 case-insensitive（預設 yes）
        },
    },

    # ===== wake-notify stream（T16 — per T09 §3.2 規劃）=====
    # 物理意義：偵測 rooms/<X>/inbox/<agent>.md mtime 變動 → POST ping 到 wake-alerts channel
    #          Tim 看到 Discord push 通知 → 手動開 Claude Code / Antigravity 給 prompt
    # 數值影響：純 outbound webhook 0 bot 依賴；跟 queue-idle / tavern-mirror 共用 _send / state pattern
    "wake_notify": {
        "enabled": False,                    # 預設 off — Tim 顯式 enable 才啟用
        "webhook_urls": [],                  # 跟 queue-idle / tavern-mirror 完全分離；獨立 broadcast
        "webhook_file": "_wake_webhook.txt",
        "webhook_env_var": "PROMPTQUEUE_WAKE_WEBHOOK",
        "watched_agents": [                  # 哪些 agent 的 inbox 變動會觸發 ping（空 = 全部）
            "claude-da-xiaojie",
            "gemini-da-xiaojie",
            "antigravity-da-xiaojie",
        ],
        "cooldown_minutes": 2,               # 同 agent inbox 短時間內 burst 變動 → 合併成 1 ping
        "max_per_run": 5,                    # 一次 fire 最多推 N 條
        "use_local_time": True,
        "channel_label": "WakeAlert",
    },
}


def _log(msg):
    """append 到 _drain.log（跟 qdrain 共用）；失敗靜默"""
    try:
        ts = datetime.datetime.utcnow().isoformat() + "Z"
        with DRAIN_LOG.open("a", encoding="utf-8") as f:
            f.write(f"[{ts}] [notify] {msg}\n")
    except Exception:
        pass


def _deep_merge(default, override):
    """深度合併兩 dict — override 蓋 default 但保留 default 內 override 沒有的欄位（含 nested dict）。
    R6.4 修：避免 shallow update 把 DEFAULT_CONFIG.tavern_mirror 的新欄位（avatar_url_base 等）整段蓋掉"""
    if not isinstance(default, dict) or not isinstance(override, dict):
        return override
    out = dict(default)
    for k, v in override.items():
        if k in out and isinstance(out[k], dict) and isinstance(v, dict):
            out[k] = _deep_merge(out[k], v)
        else:
            out[k] = v
    return out


def _load_config():
    """讀 notify_config.json；缺檔走 DEFAULT；缺欄位 deep-merge fallback DEFAULT 對應值"""
    cfg = dict(DEFAULT_CONFIG)
    try:
        if CONFIG_PATH.exists():
            user_cfg = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
            cfg = _deep_merge(cfg, user_cfg)
    except Exception as e:
        _log(f"config load fail (走 DEFAULT)：{e}")
    return cfg


def _load_state():
    """讀 _notify_state.json；缺檔回 baseline state（last_done_seq=-1 表第一次跑）"""
    try:
        if STATE_PATH.exists():
            return json.loads(STATE_PATH.read_text(encoding="utf-8"))
    except Exception as e:
        _log(f"state load fail（重置）：{e}")
    return {
        "last_notify_at": None,           # ISO UTC；None = 從來沒通知過
        "last_notify_event_seq": -1,      # 上次通知時的 events.jsonl 最後 seq；-1 = baseline 未建
        "consecutive_failures": 0,        # 連續 POST 失敗次數
    }


def _save_state(state):
    try:
        _atomic_write_text(STATE_PATH, json.dumps(state, indent=2, ensure_ascii=False))
    except Exception as e:
        _log(f"state save fail：{e}")


# 區塊職責：注入 notify_discord 自家 _log() 給 WebhookClient — 讓 webhook 內部訊息也進 _drain.log
# 物理意義：不必每個 caller 自己 set_logger；一次性在 module load 時設定
# 數值影響：webhook URL 解析失敗 / file read fail 等 warning 會自動進 _drain.log 跟 stderr fallback
_dw.set_logger(_log)


def _resolve_webhook_urls(scope_block, scope_label):
    """T36.4 — 委派 WebhookClient.resolve_urls；保留 thin shim 給既有 caller 不必改 callsite。

    Priority 由高到低（同 WebhookClient.resolve_urls 行為）：
      1. ENV: scope_block["webhook_env_var"]
      2. FILE: scope_block["webhook_file"]
      3. CONFIG: scope_block["webhook_urls"]
    回 (urls: list[str], source: str|None)；找不到回 ([], None)。
    """
    client = _dw.from_config_block(scope_block, label=scope_label, webhook_dir=STATE_DIR)
    return client.resolve_urls()


def _read_webhook_urls(config):
    """queue-idle stream URL resolver — top-level config block"""
    urls, source = _resolve_webhook_urls(config, "queue")
    # 偵測 legacy webhook_url（已棄用）警告
    if not urls and "webhook_url" in config:
        _log(f"⚠ webhook_url 已棄用（單一欄位），請改用 webhook_urls list — 跑 `--add-webhook URL` 自動遷移")
    # 回的 source 去掉 prefix 跟舊版相容（"queue:config" → "config:notify_config.json"）
    if source:
        if source.startswith("queue:env:"):
            source = "env:" + source.split(":", 2)[2]
        elif source.startswith("queue:file:"):
            source = "file:" + source.split(":", 2)[2]
        elif source == "queue:config":
            source = "config:notify_config.json"
    return urls, source


def _read_tavern_webhook_urls(config):
    """tavern-mirror stream URL resolver — config['tavern_mirror'] sub-block"""
    tm = config.get("tavern_mirror", {}) or {}
    return _resolve_webhook_urls(tm, "tavern")


# 區塊職責：Quest routing webhook URL resolver（tavern_mirror.quest_routing sub-block）
# 物理意義：跟 tavern-mirror main webhook 完全分離；解析 ENV / file / config 三層 fallback
# 數值影響：disabled / 無 URL → 回 ([], "quest:disabled") 由 caller 自動 fallback main webhook
def _read_quest_webhook_urls(config):
    tm = config.get("tavern_mirror", {}) or {}
    qr = tm.get("quest_routing", {}) or {}
    if not qr.get("enabled"):
        return [], "quest:disabled"
    return _resolve_webhook_urls(qr, "quest")


# ===========================================================
# T42 — Category routing — UCL_Asset 體系 loader + matcher
# 物理意義：訊息 meta.category 對應到 UCL_TavernCategoryRoutingAsset 群組，broadcast 到該群 webhook
# 數值影響：disabled / 找不到 Asset dir → 回空 list 由 caller fallback 既有 tavern_mirror 主 webhook
# ===========================================================

# 預設 .BuiltinModules 路徑（per UCL_ModuleService 慣例）— 跟 UCL_ChatTavernIdentityAsset 同 dir layout
# T-PATH-02: .BuiltinModules 走 layout-agnostic resolver, 不再寫死 CardGame/Assets/.BuiltinModules
_DEFAULT_CATEGORY_ASSET_DIR = (
    _tp.BUILTIN_MODULES_DIR
    / "ModulesRoot" / "Modules" / "Core"
    / "UCL_Assets" / "UCL_TavernCategoryRoutingAsset"
)


def _load_category_routing_groups(asset_dir=None):
    """掃 .BuiltinModules 內 UCL_TavernCategoryRoutingAsset/*.json，載入每群 routing 規則。

    回傳 list[dict] 每筆統一 schema:
      { id, categories (lowercased), webhook_env_var, webhook_file, webhook_urls,
        description, enabled, is_default, _source_path }

    UCL_Json 序列化習慣會去掉 m_ prefix（"m_Categories" → "Categories"），故兩種 key 都試讀。
    """
    if asset_dir is None:
        asset_dir = _DEFAULT_CATEGORY_ASSET_DIR
    asset_dir = pathlib.Path(asset_dir)
    if not asset_dir.is_dir():
        return []

    groups = []
    for f in sorted(asset_dir.glob("*.json")):
        try:
            data = json.loads(f.read_text(encoding="utf-8"))
        except Exception as e:
            _log(f"[category-routing] load fail {f.name}: {e}")
            continue

        # 兼容 m_-prefixed / 去 prefix 兩種 key 命名
        def _g(*keys, default=None):
            for k in keys:
                if k in data:
                    return data[k]
            return default

        # T43: UCL_Json bool 序列化是 "True" / "False" 字串（非 JSON 原生 true/false）— parse 時雙接
        def _parse_bool(v, default=False):
            if isinstance(v, bool): return v
            if isinstance(v, str): return v.strip().lower() in ("true", "1", "yes")
            return default

        cats_raw = _g("Categories", "m_Categories", default=[]) or []
        groups.append({
            "id": data.get("ID", f.stem),
            "categories": [str(c).lower() for c in cats_raw],
            "webhook_env_var": _g("WebhookEnvVar", "m_WebhookEnvVar", default="") or "",
            "webhook_file": _g("WebhookFile", "m_WebhookFile", default="") or "",
            "webhook_urls": _g("WebhookUrls", "m_WebhookUrls", default=[]) or [],
            "description": _g("Description", "m_Description", default="") or "",
            "enabled": _parse_bool(_g("Enabled", "m_Enabled", default=True), default=True),
            "is_default": _parse_bool(_g("IsDefault", "m_IsDefault", default=False), default=False),
            "is_work_channel": _parse_bool(_g("IsWorkChannel", "m_IsWorkChannel", default=False), default=False),
            "exclusive": _parse_bool(_g("Exclusive", "m_Exclusive", default=False), default=False),
            "_source_path": str(f),
        })
    return groups


def _is_placeholder_url(url):
    """T46 — 判定 URL 是否為 placeholder（避免累計 consecutive_failures 拉掛 tavern_mirror）

    判定條件：URL 含 'REPLACE_ME' / 'PLACEHOLDER' 字樣 → 視為 placeholder
    （Templates~ / sample asset 內預設值，使用者沒換真 URL 前不該嘗試 broadcast）
    """
    if not url or not isinstance(url, str):
        return True
    upper = url.upper()
    return ("REPLACE_ME" in upper) or ("PLACEHOLDER" in upper)


def _resolve_group_webhook_urls(group):
    """單一 routing group's webhook URLs 解析（ENV > FILE > webhook_urls）— 走既有 _resolve_webhook_urls helper。

    group 是 _load_category_routing_groups() 的 dict element；本 helper 把 schema adapt 成
    _resolve_webhook_urls 期望的 scope_block 格式（webhook_env_var / webhook_file / webhook_urls 同 key）。

    T46 修 (Bug 2): 過濾 placeholder URL — 避免 4xx fail 拉高 consecutive_failures
    """
    scope_block = {
        "webhook_env_var": group.get("webhook_env_var", ""),
        "webhook_file": group.get("webhook_file", ""),
        "webhook_urls": group.get("webhook_urls", []) or [],
    }
    label = f"category:{group.get('id', '?')}"
    urls, source = _resolve_webhook_urls(scope_block, label)
    # T46 — filter placeholder URLs (REPLACE_ME / PLACEHOLDER) 避免 broadcast 失敗累計
    real_urls = [u for u in urls if not _is_placeholder_url(u)]
    if urls and not real_urls:
        # 全部都是 placeholder → log 一次給 audit；caller 視同無 URL 不嘗試 broadcast
        _log(f"[category-routing] group '{group.get('id')}' 全部 webhook URL 都是 placeholder ({len(urls)} 個)，跳過 broadcast")
    return real_urls, source


def _match_msg_to_routing_groups(msg, groups, case_insensitive=True):
    """對單一訊息計算該 broadcast 到哪些 routing groups。

    Precedence（per Tim 拍板）：
      1. 訊息 meta.category 命中某 enabled group 的 m_Categories → 回 list of matched groups (multi-group OK)
      2. 沒命中 → 回 m_IsDefault=true 的 group (Tim work-channel 兼任 default)
      3. 沒 default → 回空 list（caller fallback 走既有 tavern_mirror.webhook_urls）

    回 list[dict]（routing groups），可能 0/1/N 筆。
    """
    meta = msg.get("meta") or {}
    if not isinstance(meta, dict):
        meta = {}
    category = (meta.get("category") or "").strip()
    if case_insensitive:
        category = category.lower()

    enabled_groups = [g for g in groups if g.get("enabled", True)]

    # Layer 1: category 命中某些 group
    if category:
        matched = [g for g in enabled_groups if category in g.get("categories", [])]
        if matched:
            return matched

    # Layer 2: fallback to default group (Tim 拍板補充)
    defaults = [g for g in enabled_groups if g.get("is_default")]
    if defaults:
        return [defaults[0]]   # 多個 default 只取第一筆

    # Layer 3: 全空 → caller 走 tavern_mirror.webhook_urls fallback
    return []


def _read_webhook_url(config):
    """legacy 入口 — 回第一筆 URL（給 --force / show-config 簡化用）"""
    urls, source = _read_webhook_urls(config)
    return (urls[0] if urls else None), source


def _format_time(use_local):
    if use_local:
        return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    return datetime.datetime.utcnow().strftime("%Y-%m-%d %H:%M:%S UTC")


def _max_event_seq(all_events):
    if not all_events:
        return 0
    return max((ev.get("seq", 0) for ev in all_events), default=0)


def _new_done_tasks_since(all_events, since_seq):
    """從 events 找 seq > since_seq 的 task_done events，回 task_id 清單（順序保留）"""
    out = []
    for ev in all_events:
        if ev.get("seq", 0) <= since_seq:
            continue
        if ev.get("type") == "task_done":
            tid = ev.get("task_id")
            if tid:
                out.append(tid)
    return out


def _lookup_task_title(task_id):
    """從 tasks/<task_id>.md frontmatter 找 title；失敗回 task_id"""
    p = TASKS_DIR / f"{task_id}.md"
    if not p.exists():
        return task_id
    try:
        content = p.read_text(encoding="utf-8")
        # 簡單 frontmatter 解析：第二個 --- 之前找 title:
        if not content.startswith("---"):
            return task_id
        end = content.find("---", 3)
        if end < 0:
            return task_id
        for line in content[3:end].splitlines():
            line = line.strip()
            if line.startswith("title:"):
                return line[len("title:"):].strip()
        return task_id
    except Exception:
        return task_id


def _parse_iso_utc(ts_str):
    """容忍 trailing Z / 無 Z 的 ISO8601 → datetime UTC；失敗回 None"""
    if not ts_str:
        return None
    try:
        return datetime.datetime.fromisoformat(ts_str.replace("Z", ""))
    except Exception:
        return None


def _format_duration(claim_ts, done_ts):
    """把 (claim, done) 兩 ts 算成「Xm」或「Xh Ym」字串；失敗回 ''"""
    c = _parse_iso_utc(claim_ts)
    d = _parse_iso_utc(done_ts)
    if not c or not d:
        return ""
    delta = d - c
    total_s = delta.total_seconds()
    if total_s < 0:
        return ""
    if total_s < 60:
        return f"{int(total_s)}s"
    minutes = int(total_s / 60)
    if minutes < 60:
        return f"{minutes}m"
    hours, mins = divmod(minutes, 60)
    return f"{hours}h{mins:02d}m"


# T28 — 多 quest 房 events 載入（per Tim Round 32 拍板讓 Discord 看到所有 quest done）
def _load_room_events(room_id):
    """讀指定房 events.jsonl；每筆 dict 加 _source_room 標籤 — T36.6 委派 _tio.read_events_with_source_tag。"""
    return _tio.read_events_with_source_tag(room_id)


def _collect_done_contexts(all_events, since_seq):
    """
    為每個 seq > since_seq 的 task_done 蒐集 rich context：
      task_id / title / done_ts / claim_ts / summary（R6.1）/ duration / actor
    回 list（按 done_ts 順序）— 給 _build_payload 渲染工作日誌用
    """
    # 先掃一遍建 task_id → list of relevant events
    by_task = {}
    for ev in all_events:
        tid = ev.get("task_id")
        if not tid:
            continue
        by_task.setdefault(tid, []).append(ev)

    out = []
    for ev in all_events:
        if ev.get("seq", 0) <= since_seq:
            continue
        if ev.get("type") != "task_done":
            continue
        tid = ev.get("task_id")
        if not tid:
            continue

        # task_done event：取 summary
        data = ev.get("data") or {}
        summary = data.get("summary", "") or ""
        done_ts = ev.get("ts", "")
        actor = ev.get("actor", "")

        # 找對應的 task_claim（同 task_id，type=task_claim，最後一筆 — 防 reopen 後再 claim）
        claim_ts = ""
        plan = ""
        # 因 reopen 之後可能再 claim，取「在這個 done event 前最近一筆 claim」
        for prev in by_task.get(tid, []):
            if prev.get("seq", 0) >= ev.get("seq", 0):
                break
            if prev.get("type") == "task_claim":
                claim_ts = prev.get("ts", "")
                pdata = prev.get("data") or {}
                plan = pdata.get("plan", "") or ""

        out.append({
            "task_id": tid,
            "title": _lookup_task_title(tid),
            "done_ts": done_ts,
            "claim_ts": claim_ts,
            "summary": summary,
            "plan": plan,
            "duration": _format_duration(claim_ts, done_ts),
            "actor": actor,
        })
    return out


# 區塊職責: body 切割工具 — 避免 Discord 訊息被 "…" 截斷遺失內容
# 物理意義: Tim QA 2026-05-13 拍板 — 寧可發多條 Discord 訊息也不要 truncate.
#          優先在 \n 邊界切, 沒換行才硬切 (硬切 fallback: 在 max_chunk_chars 處直接斷).
# 數值影響: 回傳 list of str chunks, 每 chunk 長度 <= max_chunk_chars. 空字串回 [""].
# 邊界: max_chunk_chars 太小 (< 200) 直接 fallback 切; 找不到 \n 且 cut 太靠前 (< half) 也硬切.
def _split_body_for_discord(body, max_chunk_chars=1500):
    """切 body 為多 chunk, 優先 \\n 邊界. 短 body 回 [body]."""
    if body is None: return [""]
    if len(body) <= max_chunk_chars:
        return [body]
    chunks = []
    remaining = body
    while len(remaining) > max_chunk_chars:
        # 在 max_chunk_chars 內找最後一個 \n
        cut_at = remaining.rfind("\n", 0, max_chunk_chars)
        # 太靠前 (< 半長) 視同找不到, 改硬切 (避免一筆只有兩三行)
        if cut_at <= 0 or cut_at < max_chunk_chars // 2:
            cut_at = max_chunk_chars
        chunks.append(remaining[:cut_at].rstrip())
        # 跳過切點的 \n 避免下 chunk 開頭空行
        remaining = remaining[cut_at:].lstrip("\n")
    if remaining:
        chunks.append(remaining)
    return chunks


def _truncate(s, max_len):
    """字數安全 truncate（中文當 1 字計）；超過加 …"""
    if not s:
        return ""
    if len(s) <= max_len:
        return s
    return s[:max_len - 1] + "…"


def _build_task_embed(ctx, config):
    """
    R6.5 — 把單筆 done task context 包成 Discord rich embed（含 actor 頭像 + 個性化）。
    每張 embed 視覺上是一張卡片；embeds 上限 10 / POST。
    """
    tid = ctx["task_id"]
    title_max = config.get("title_max", 80)
    title = _truncate(ctx["title"], title_max)
    duration = ctx["duration"]
    summary = ctx["summary"] or ""
    plan = ctx.get("plan", "") or ""
    actor = ctx["actor"]
    done_ts = ctx.get("done_ts", "")

    # 解析 actor identity（同 tavern stream 的 fallback chain）
    tm = config.get("tavern_mirror", {}) or {}
    username, avatar_url = _resolve_discord_identity(actor, actor, tm)

    # description = 個性化 summary（主體）；plan 走 fields
    desc_max = 4000   # Discord embed description 上限 4096，留 buffer
    if summary:
        description = _truncate(summary, desc_max)
    else:
        description = "_(沒帶 summary — 建議 task_done 時加 --arg summary=\"...\" 留下傲嬌工作交代)_"

    fields = []
    if duration:
        fields.append({"name": "⏱ Duration", "value": duration, "inline": True})
    fields.append({"name": "🆔 Task", "value": f"`{tid}`", "inline": True})
    if plan:
        fields.append({"name": "📋 Plan", "value": _truncate(plan, 1000), "inline": False})

    embed = {
        "author": {"name": username or actor or "?"},
        "title": f"✅ {title}",
        "description": description,
        "color": 0x57F287,   # Discord 官方綠（success）
        "fields": fields,
    }
    if avatar_url:
        embed["author"]["icon_url"] = avatar_url
    if done_ts:
        # Discord embed timestamp 接 ISO 8601；確保有 Z
        ts = done_ts if done_ts.endswith("Z") else done_ts + "Z"
        embed["timestamp"] = ts.replace("ZZ", "Z")
    return embed


def _build_payload(stats, done_contexts, config):
    """
    R6.5 — 回 (content_str, embeds_list)；queue-idle stream 的工作日誌 payload。
    content：頂部統計 header；embeds：每筆 done task 一張卡（含 actor 頭像 + 個性化 summary）
    Discord 限制：≤ 10 embeds / POST；超過走 tasks_per_message 截
    """
    label = config.get("channel_label", "PromptQueue")
    time_str = _format_time(config.get("use_local_time", True))
    pending = stats.get("pending", 0)
    in_progress = stats.get("in_progress", 0)
    done_total = stats.get("done", 0)
    n = len(done_contexts)

    if not config.get("verbose", True):
        # compact 一行（無 embeds）
        return f"🎯 [{label}] queue 清空 — 本輪完成 {n} 筆 / 累計 done={done_total} / {time_str}", []

    # verbose 工作日誌格式
    content_lines = [
        f"🎯 **[{label}] 工作回報 — queue 已清空**",
        f"📊 本輪完成 **{n}** 筆 / 累計 done **{done_total}** / pending {pending} / in_progress {in_progress}",
        f"🕐 {time_str}",
    ]

    embeds = []
    if n == 0:
        content_lines.append("")
        content_lines.append("ℹ 本輪沒新 done（cooldown / baseline 重建）")
    else:
        per_msg = min(config.get("tasks_per_message", 10), 10)   # Discord hard limit 10
        for ctx in done_contexts[:per_msg]:
            embeds.append(_build_task_embed(ctx, config))
        if n > per_msg:
            content_lines.append("")
            content_lines.append(f"_... 另 {n - per_msg} 筆未列（Discord embed 限 10 卡 / POST；多的看 events.jsonl）_")

    content_lines.append("")
    content_lines.append("ℹ 有新 task：`python AgentCommands/PromptQueue/qadd.py \"...\"`")
    content = "\n".join(content_lines)

    # content 保險截斷（Discord 2000 字硬上限；保 100 字 buffer）
    if len(content) > 1900:
        content = content[:1898] + "…"

    return content, embeds


# 區塊職責：T36.4 — 委派 WebhookClient.send_one / send_to_urls；保留 thin shim 給 caller 不必改 callsite
# 物理意義：WebhookClient 是 single source of truth；這兩個 shim 只是 wrapper 不做額外邏輯
# 數值影響：返回簽章跟舊版一致 — caller 行為不變
_module_webhook_client = _dw.WebhookClient(_dw.WebhookConfig(label="notify_discord", webhook_dir=STATE_DIR))


def _send_one(webhook_url, content, username=None, avatar_url=None, embeds=None):
    """T36.4 — 委派 WebhookClient.send_one。回 (ok, error_msg|None)。"""
    return _module_webhook_client.send_one(webhook_url, content, username=username, avatar_url=avatar_url, embeds=embeds)


def _send(webhook_urls, content, username=None, avatar_url=None, embeds=None):
    """T36.4 — 委派 WebhookClient.send_to_urls；broadcast 到顯式 URL list。"""
    if isinstance(webhook_urls, str):
        webhook_urls = [webhook_urls]
    return _module_webhook_client.send_to_urls(webhook_urls, content, username=username, avatar_url=avatar_url, embeds=embeds)


# ===========================================================
# R6.4 — Identity → Discord display 解析（username + avatar_url override）
# ===========================================================

_IDENTITIES_PATH = _tp.IDENTITIES_PATH   # T36.2 alias for backward compat


def _load_identities():
    """讀 chat_tavern/identities.json — T36.6 委派 _tio.read_identities。"""
    return _tio.read_identities()


# 區塊職責: Discord 端 body @-mention rewrite (Tim 2026-05-11 拍板)
# 物理意義: agent 內部 R7 mention 慣例 = @<agent_id> (e.g. @antigravity-da-xiaojie),
#          但 Discord reader 看 plain text 看不出是誰 — 走 identities.json display_name 翻譯成
#          @<display_name> (e.g. @Antigravity大小姐)。內部 jsonl 仍存 @<id>, 只改 Discord 渲染。
# 數值影響: 只動 broadcast 到 Discord 的 body 字串; R7 mention parser / inbox 寫入仍走原始 body。
# 邊界: 字元邊界用負向 lookahead (避免誤替 email / @import 之類); 沒在 identities.json 的 id 保留原樣。
_AT_MENTION_RE = None


def _rewrite_at_mentions_for_discord(body, tm_config=None):
    """把 body 內 @<name> 換成 Discord 真實 mention (`<@user_id>`) 或 display_name fallback.

    T28.3 (Tim 2026-05-14 拍板): 加 user_id mention 真 ping 機制:
    1. 先 check tm_config.discord_user_mentions[name] → 替換成 `<@user_id>` (真 Discord ping)
    2. fallback identities.json display_name 替換 (visual @ 不 ping)
    3. fallback 保留原樣

    Tim/David 等真 Discord user 有 user_id → 真 ping. Persona (basecamp/calli/etc) 沒 user_id → 走 display fallback.
    """
    global _AT_MENTION_RE
    if not body or "@" not in body:
        return body
    import re as _re
    if _AT_MENTION_RE is None:
        # re.UNICODE 讓 \w 自動涵蓋所有 Unicode word char（中文、日文等）；
        # 同 UCL_JsonData 精神：不手動 hardcode CJK range，交給 Unicode 分類器處理。
        # \.- 允許 RudyL. 這類帶點/連字符的 display name。
        _AT_MENTION_RE = _re.compile(r"@([\w.\-]+)", _re.UNICODE)
    identities = _load_identities() or {}
    user_mentions = (tm_config or {}).get("discord_user_mentions", {}) or {}

    def _sub(m):
        name = m.group(1)
        # Priority 1 (T28.3): real Discord user_id → 真 ping
        uid = user_mentions.get(name)
        if uid:
            return f"<@{uid}>"
        # Priority 2: identities.json display_name (visual @ 不 ping, agent 之間用)
        ident = identities.get(name)
        if ident:
            display = ident.get("display_name")
            if display:
                return f"@{display}"
        return m.group(0)   # 沒命中 → 保留原樣

    return _AT_MENTION_RE.sub(_sub, body)


def _resolve_discord_identity(sender_id, sender_name_fallback, tm_config, identities_dict=None, sender_avatar_sprite=None, sender_persona=None):
    """
    Per-message Discord display resolution。
    回 (username, avatar_url)；其中任一可為 None（None = 不 override，走 webhook 預設）

    Fallback chain（優先到 fallback）：
      1. tm_config.identity_overrides[sender_id] 帶顯式 username / avatar_url
      2. identities.json 內該 id 的 display_name → username
      3. message.sender_name → username（caller 傳 fallback）
      4. sender_id 本身

    avatar_url：
      0. (Tim 2026-07-15 拍板) tm_config.persona_avatar_overrides[sender_persona] —
         persona 顯式釘任意外部 URL，最高優先、不做 HEAD 預檢（顯式設定自負有效性）
      1. T28 (Tim 2026-05-14 拍板): sender_avatar_sprite (e.g. "Avatars_basecamp") — persona-level avatar
         strip "Avatars_" prefix → 拼 base + filename.png (per ImageGen workflow convention)
      2. identity_overrides[sender_id].avatar_url
      3. avatar_url_pattern.format(base, id) — convention: <base><id>.png (agent-level legacy fallback)
      4. None
    """
    if identities_dict is None:
        identities_dict = _load_identities()

    overrides = (tm_config.get("identity_overrides", {}) or {}).get(sender_id, {})
    identity = identities_dict.get(sender_id, {})

    username = (
        overrides.get("username")
        or identity.get("display_name")
        or sender_name_fallback
        or sender_id
    )

    # Discord webhook username 限制：不可含 "discord" / "@" / "#" / ":"; 1-80 chars
    # 失敗 sample：sender_id="discord:tim-test" 直接當 username 觸發 HTTP 400
    # 修法：clean 替代不允許字元；保留可讀性
    if username:
        cleaned = username.replace(":", "_")
        # 不允許含 "discord"（case-insensitive）— 換成 "DC_"
        cleaned_lower = cleaned.lower()
        if "discord" in cleaned_lower:
            # 找原 case 的 "discord" 替換成 "DC_"
            import re as _re
            cleaned = _re.sub(r"(?i)discord", "DC_", cleaned)
        # 截 80 chars
        username = cleaned[:80] if len(cleaned) > 80 else cleaned

    avatar_url = None
    # 優先級 0 (Tim 2026-07-15): persona 顯式 URL 覆寫 — 命中直接採用，跳過 sprite 派生與 HEAD 預檢
    if sender_persona:
        persona_overrides = tm_config.get("persona_avatar_overrides", {}) or {}
        explicit = persona_overrides.get(sender_persona)
        if explicit:
            return username, explicit

    # T28 (Tim 2026-05-14): persona-level avatar 優先 — msg.sender_avatar_sprite (sprite_id) → 拼 URL
    # T28.1 fix (calli QA 2026-05-14): persona PNG 沒 push 到 GitHub → raw URL 404 → Discord render default icon.
    #         加 HEAD request 預檢 + 1 hour cache: 404 → fallback 到 agent-level URL.
    # 物理意義: sender_avatar_sprite = "Avatars_basecamp" 對 ImageGen workflow 慣例 → 檔名 = "basecamp.png"
    # 邊界: strip "Avatars_" prefix; 若 sprite_id 已是檔名形式 (含 .png) 也保留兼容
    persona_avatar_url = None
    if sender_avatar_sprite:
        base = tm_config.get("avatar_url_base", "")
        if base:
            filename = sender_avatar_sprite
            if filename.startswith("Avatars_"):
                filename = filename[len("Avatars_"):]
            if not filename.endswith(".png"):
                filename = filename + ".png"
            persona_avatar_url = base + filename
    # T28.1: persona URL 用前先 HEAD 預檢 (cached)
    if persona_avatar_url and _avatar_url_reachable(persona_avatar_url):
        avatar_url = persona_avatar_url
    # Fallback: identity_overrides[sender_id].avatar_url (agent-level 顯式設定)
    if not avatar_url:
        avatar_url = overrides.get("avatar_url")
    # Fallback: avatar_url_pattern.format(base, id) (agent-level convention)
    if not avatar_url:
        base = tm_config.get("avatar_url_base", "")
        pattern = tm_config.get("avatar_url_pattern", "{base}{id}.png")
        if base and pattern and sender_id:
            try:
                avatar_url = pattern.format(base=base, id=sender_id)
            except Exception:
                avatar_url = None

    return username, avatar_url


# T28.1 — Avatar URL reachability cache (HEAD request, 1h TTL)
# 物理意義: Discord webhook 接 avatar_url 但不驗證 — 404 時 silent fallback to default icon.
#          persona PNG 沒 push 到 GitHub → raw URL 404 → 用戶 QA 才知道. 加 HEAD 預檢解.
# 數值影響: 每 unique URL 第一次 HEAD (~200ms 網路 cost), 之後 cache 1h. Net cost 可忽略.
# 安全性: HEAD request 失敗 (timeout / DNS) → 視為 unreachable, 走 fallback (保守).
_AVATAR_URL_CACHE = {}  # url → (timestamp, is_reachable)
_AVATAR_CACHE_TTL_SEC = 3600


def _avatar_url_reachable(url):
    """HEAD request 預檢 url, cache 1h. 失敗視為 unreachable."""
    if not url:
        return False
    import time as _time
    now = _time.time()
    cached = _AVATAR_URL_CACHE.get(url)
    if cached and (now - cached[0]) < _AVATAR_CACHE_TTL_SEC:
        return cached[1]
    is_reachable = False
    try:
        import urllib.request
        req = urllib.request.Request(url, method="HEAD")
        with urllib.request.urlopen(req, timeout=3) as resp:
            is_reachable = 200 <= resp.status < 400
    except Exception:
        is_reachable = False
    _AVATAR_URL_CACHE[url] = (now, is_reachable)
    return is_reachable


def _resolve_queue_idle_identity(done_contexts, config):
    """
    T10 — queue-idle stream embed 卡頂端 username + avatar 解析。

    strategy:
      - "actor"：用最近一筆 done task 的 actor → identities → avatar_url_base 拼路徑
      - "fixed"：用 queue_idle_identity.fixed_id 強制覆寫
      - "none"：回 (None, None) 不 override，走 webhook 預設

    actor fallback chain: 最近 done.actor → queue_idle_identity.fallback_id → None
    avatar 共用 tavern_mirror.avatar_url_base / pattern（GitHub raw URL convention）。
    """
    qi_cfg = config.get("queue_idle_identity", {}) or {}
    strategy = qi_cfg.get("strategy", "actor")
    if strategy == "none":
        return None, None

    # 決定 sender_id
    sender_id = None
    if strategy == "fixed":
        sender_id = qi_cfg.get("fixed_id")
    elif strategy == "actor":
        # 取最近一筆 done.actor（done_contexts 是 chronological 由 _collect 收集）
        for ctx in reversed(done_contexts or []):
            actor = ctx.get("actor")
            if actor:
                sender_id = actor
                break
        if not sender_id:
            sender_id = qi_cfg.get("fallback_id")

    if not sender_id:
        return None, None

    # 共用 tavern_mirror 的 avatar 設定（identity_overrides / avatar_url_base）
    tm_config = config.get("tavern_mirror", {}) or {}
    return _resolve_discord_identity(sender_id, sender_id, tm_config)


# ===========================================================
# R6.3 — Tavern Mirror stream
# 區塊職責：監看 chat_tavern/<room>/messages.jsonl，新訊息 broadcast 到 tavern_mirror.webhook_urls
# 物理意義：跟 queue-idle 完全分離（獨立 webhook list / state / cooldown / opt-in 房名清單）
# 數值影響：每個 watched room 各自 last_seen_seq；只發 seq > last_seen 的；max_per_run 防 batch 爆
# ===========================================================

def _load_tavern_state():
    """讀 _tavern_state.json；缺檔回 baseline state"""
    try:
        if TAVERN_STATE_PATH.exists():
            return json.loads(TAVERN_STATE_PATH.read_text(encoding="utf-8"))
    except Exception as e:
        _log(f"[tavern] state load fail（重置）：{e}")
    return {"rooms": {}, "consecutive_failures": 0}


def _save_tavern_state(state):
    try:
        _atomic_write_text(TAVERN_STATE_PATH, json.dumps(state, indent=2, ensure_ascii=False))
    except Exception as e:
        _log(f"[tavern] state save fail：{e}")


def _read_room_messages(room_id):
    """讀 chat_tavern/<room>/messages.jsonl — T36.6 委派 _tio.read_messages。"""
    return _tio.read_messages(room_id)


def _read_room_meta(room_id):
    """讀 chat_tavern/rooms/<room>/meta.json — T36.6 委派 _tio.read_room_meta。"""
    return _tio.read_room_meta(room_id)


def _collect_new_tavern_messages(tm_config, state):
    """
    對每個 watched room 掃 messages.jsonl，找 seq > last_seen 的訊息；過濾後最多 max_per_run 筆。

    R7 (Q20260508-180358) — 兩條增強讓 Quest task lifecycle 也能進 Discord：
      1. **Per-room mirror_kinds override**：room meta.json 有 mirror_kinds 就走那個，否則 fallback config.kinds
      2. **kind == system 自動 bypass exclude_senders**：system 訊息（含 R6 quest mirror sender=_quest_system）
         是預期的 lifecycle 信號，不該被 chat-defensive exclude_senders 擋
      3. include_senders 仍對所有 kind 適用（白名單模式不受影響）
    """
    rooms = tm_config.get("rooms", []) or []
    fallback_kinds = set(tm_config.get("kinds", ["chat"]))
    excludes = set(tm_config.get("exclude_senders", []) or [])
    includes = set(tm_config.get("include_senders", []) or [])
    # 區塊職責：meta.source 黑名單 — 防 inbound relay 迴圈
    # 物理意義：discord_inbound_bot 寫 tavern 時帶 meta.source=discord, mirror 看到立刻 skip
    # 數值影響：空 = 不過濾 (舊行為); ["discord"] 預設值會 skip 任何 Discord inbound 訊息往回推
    excludes_meta_source = set(tm_config.get("exclude_meta_source", []) or [])
    # 區塊職責：sender_id prefix 黑名單 — 雙保險 (T06 2026-05-15 echo fix)
    # 物理意義：當 meta.source 因 ParseMeta bug 或寫入端格式錯誤丟失時, sender_id 開頭仍是穩定識別.
    #          e.g. Discord inbound bot 寫入 sender_id="discord:<uid>", prefix "discord:" 命中就 skip.
    # 數值影響：兼容舊有 meta.source filter; 兩者任一 hit 就 skip, 防 echo 雙重防線.
    excludes_sender_prefix = tuple(tm_config.get("exclude_sender_prefix", []) or [])
    max_per_run = tm_config.get("max_per_run", 20)

    out = []
    for room in rooms:
        room_state = state.get("rooms", {}).get(room, {"last_seen_seq": 0})
        last_seen = room_state.get("last_seen_seq", 0)

        # R7 — 讀 room meta.json 取 mirror_kinds（per-room override）
        room_meta = _read_room_meta(room)
        room_mirror_kinds = room_meta.get("mirror_kinds")
        if isinstance(room_mirror_kinds, list) and len(room_mirror_kinds) > 0:
            effective_kinds = set(room_mirror_kinds)
        else:
            effective_kinds = fallback_kinds

        msgs = _read_room_messages(room)
        for m in msgs:
            seq = m.get("seq", 0)
            if seq <= last_seen:
                continue
            kind = m.get("kind")
            if effective_kinds and kind not in effective_kinds:
                continue
            sender = m.get("sender_id", "")
            # R7 — system 訊息 bypass exclude_senders（lifecycle 信號是預期的，不擋）
            # chat 仍走 exclude（防 self-echo / 系統訊息誤入）
            if kind == "chat" and sender in excludes:
                continue
            # 全 kind 共用 include_senders（白名單）
            if includes and sender not in includes:
                continue
            # meta.source 黑名單 — 防 inbound relay loop (e.g. discord → tavern → discord echo)
            if excludes_meta_source:
                msg_source = (m.get("meta") or {}).get("source", "")
                if msg_source in excludes_meta_source:
                    continue
            # 區塊職責：sender_id prefix 黑名單 (T06 echo fix defensive layer)
            # 物理意義：meta.source 若因任何理由 (parse bug / 老訊息 / 寫入端漏帶) 取不到, 退而求其次靠 sender_id 識別
            # 數值影響：tuple 空 = 不啟用; "discord:" 預設值會擋掉所有 inbound-bot relay 訊息回推
            if excludes_sender_prefix and sender.startswith(excludes_sender_prefix):
                continue
            out.append((room, m))
            if len(out) >= max_per_run:
                return out
    return out


def _build_tavern_payload(room, msg, tm_config, routing_tag=None):
    """
    區塊職責：單一 tavern message 構造 Discord 訊息 + identity override
    物理意義：T58 — 加 routing_tag 顯示路由路徑（main / quest / category:<id>）；
              讓 Tim 在多 Discord 頻道收同訊息時，知道每筆走哪條路由
    回 (content, username, avatar_url) — caller 傳給 _send 走 per-message override

    routing_tag 規則:
      - None / "" → 不加 suffix (兼容舊行為)
      - "main"     → 「→ **#main**」（總覽頻道）
      - "quest"    → 「→ **#quest** (lifecycle)」
      - "category:<group_id>" → 「→ **#<group_id>** (category)」
    """
    sender_id = msg.get("sender_id", "")
    sender_name = msg.get("sender_name") or sender_id or "?"
    sender_persona = msg.get("sender_persona") or ""    # Phase 1 (Tim 2026-05-11): persona-aware Discord display
    body = msg.get("body", "") or ""
    seq = msg.get("seq", "?")
    kind = msg.get("kind", "chat")
    body_max = tm_config.get("body_max", 1500)

    # 區塊職責: body @-mention rewrite — 把 @<agent_id> 換成 @<display_name>
    # 物理意義: agent 內部用 @claude-da-xiaojie 寫, Discord 端 reader 看不懂 — 走 identities.json display_name 翻譯
    # 數值影響: 只改 Discord broadcast payload, 不動 jsonl 原始 record (R7 mention parser 仍走原 @<id>)
    # 邊界: 沒在 identities.json 的 id 保留原樣 (避免誤替換); 連續字元邊界用簡易 regex
    body = _rewrite_at_mentions_for_discord(body, tm_config)

    # 區塊職責：補償寫入端 double-escape 的字面 \n（兩字元 backslash + n）→ 真 newline
    # 物理意義：正規 op=post 寫 jsonl 時 SerializeMessage(EscapeStr) → newline 變 "\\n" 字面，
    #          Python json.loads 讀回會還原成真 newline，所以 body 內**不該**還有字面 "\\n"。
    #          但 Antigravity / 別 daemon 寫 body 時可能 double-escape（傳 "\\n" 4 字元 → 寫 jsonl
    #          變 "\\\\n" → json.loads 還原成 "\\n" 字面），Discord 顯示就是 "\n" 字面。
    #          本 replace 補償這種 double-escape 寫法 — 對正規 LLM 純文字無害（無 "\\n" pattern 可換）。
    # 數值影響：只動 broadcast 到 Discord 的 payload，不改 jsonl 原始 record；單筆 ms 級成本忽略。
    # 邊界：tab "\\t" / carriage return "\\r" 同邏輯處理；其他 escape 序列保留原樣（不過度 decode）。
    if "\\n" in body or "\\t" in body or "\\r" in body:
        body = body.replace("\\r\\n", "\n").replace("\\n", "\n").replace("\\t", "\t").replace("\\r", "\n")

    # 區塊職責: body 過長 → 切割成多 chunk (避免訊息被 "…" 截斷遺失)
    # 物理意義: Tim QA 2026-05-13 拍板 — 寧可發多條也不要截斷. 優先 \n 邊界切, 沒換行才硬切.
    # 數值影響: body_max 1500 默認 (Discord 2000 上限留 buffer 給 title + part marker)
    body_chunks = _split_body_for_discord(body, body_max)

    # T58 — 解析 routing_tag → human-readable suffix
    # 物理意義：Tim 多 Discord 頻道收同訊息時，每筆 Discord 訊息標明走哪條路由
    # 數值影響：title 末加 「→ **#<channel_tag>**」一行；body 不變
    routing_suffix = ""
    if routing_tag:
        if routing_tag == "main":
            routing_suffix = " → **#main**"
        elif routing_tag == "quest":
            routing_suffix = " → **#quest** (lifecycle)"
        elif routing_tag.startswith("category:"):
            group_id = routing_tag[len("category:"):]
            routing_suffix = f" → **#{group_id}** (category)"
        else:
            # 未知 tag 直接顯示
            routing_suffix = f" → **#{routing_tag}**"

    # title 用 template（R6.4 預設只放 seq + room；身分走 webhook username 顯示）
    # T58 — template 可選用 {routing_tag} / {routing_suffix} placeholders；若沒用則自動 append suffix
    template = tm_config.get("title_template", "**seq {seq}** · `{room}`")
    extras = {
        "routing_tag": routing_tag or "",
        "routing_suffix": routing_suffix,
    }
    try:
        title = template.format(room=room, sender_name=sender_name, seq=seq, kind=kind, **extras)
    except Exception:
        title = f"seq {seq} · {room}"

    # T58 — template 沒主動用 routing_* placeholders → 自動 append suffix（向下兼容預設 template）
    if routing_suffix and "{routing_tag}" not in template and "{routing_suffix}" not in template:
        title = title + routing_suffix

    # R6.4 — identity override：webhook 端顯示成 sender 的身分
    # T28 (2026-05-14): 帶 sender_avatar_sprite 給 resolver, 讓 persona-level avatar 優先於 agent-level
    sender_avatar_sprite = msg.get("sender_avatar_sprite") or None
    username, avatar_url = _resolve_discord_identity(sender_id, sender_name, tm_config, sender_avatar_sprite=sender_avatar_sprite, sender_persona=sender_persona)
    # Phase 1 (Tim 2026-05-11): 帶 persona 時 username 顯示 "<name>@<persona>" — 跟 IMGUI / _last_view 對齊 DisplayName 格式
    # 邊界: Discord webhook username 上限 80 chars, 超出截斷
    if sender_persona and username:
        candidate = f"{username}@{sender_persona}"
        username = candidate[:80] if len(candidate) > 80 else candidate

    # 多 chunk 組裝: 第一條帶 title (+ part marker 若多 chunk), 後續條帶 continuation marker
    total_parts = len(body_chunks)
    payloads = []
    for i, chunk in enumerate(body_chunks):
        if total_parts == 1:
            content = f"{title}\n{chunk}"
        elif i == 0:
            content = f"{title} _(part 1/{total_parts})_\n{chunk}"
        else:
            content = f"_(seq {seq} 續 part {i+1}/{total_parts})_\n{chunk}"
        payloads.append((content, username, avatar_url))
    return payloads


def notify_tavern_messages(config=None):
    """
    Tavern mirror 主入口；scan 所有 watched rooms 新訊息 → broadcast 到 tavern webhook_urls。
    每筆訊息一次 POST（不合併）— 保 Discord 端時間軸感跟原 chat 對齊。
    回 (sent_count: int, reason: str)
    """
    if config is None:
        config = _load_config()

    tm = config.get("tavern_mirror", {}) or {}
    if not tm.get("enabled"):
        return 0, "tavern_mirror disabled"

    if not tm.get("rooms"):
        return 0, "no rooms watched (跑 --add-tavern-room ROOM 加)"

    urls, source = _read_tavern_webhook_urls(config)
    if not urls:
        return 0, "no tavern webhook URLs (跑 --add-tavern-webhook URL 設定)"

    # 區塊職責：解析 quest_routing 子塊 webhook（如有 enable）— 給 task lifecycle 訊息分流用
    # 物理意義：避免 _quest_system 高頻 task_create/claim/done 洗版主 chat webhook
    # 數值影響：disabled / 無 URL → 全部走 main webhook（兼容舊行為，不破壞既有 deploy）
    qr = tm.get("quest_routing", {}) or {}
    quest_urls, quest_source = _read_quest_webhook_urls(config)
    quest_match_prefix = qr.get("sender_match_prefix", "_quest_system")
    quest_routing_active = bool(qr.get("enabled")) and bool(quest_urls)

    # T42 — 解析 category_routing UCL_Asset 群組（per Tim 拍板）
    # 物理意義：訊息 meta.category 命中某 group → broadcast 到該 group's webhook URLs；
    #          沒命中 → m_IsDefault=true 的 group（如 work-channel）；都沒則 fallback main webhook
    # 數值影響：disabled / 找不到 Asset dir → category_routing_active=False，全走 main / quest 既有路徑
    cr = tm.get("category_routing", {}) or {}
    category_routing_active = bool(cr.get("enabled"))
    category_groups = _load_category_routing_groups() if category_routing_active else []
    category_case_insensitive = bool(cr.get("case_insensitive", True))
    if category_routing_active and not category_groups:
        _log("[category-routing] enabled but no UCL_TavernCategoryRoutingAsset/*.json 載入；走既有 main / quest fallback")
        category_routing_active = False

    state = _load_tavern_state()

    # auto-disable
    fail_threshold = tm.get("disable_after_failures", 5)
    if state.get("consecutive_failures", 0) >= fail_threshold:
        return 0, f"tavern auto-disabled (consecutive_failures={state['consecutive_failures']} >= {fail_threshold})"

    # Baseline 機制（per-room）— 首次見某房 → 把 last_seen 推到當下最大 seq，整個 batch 跳過該房
    # 物理意義：首次 enable 不該回放歷史；只發 baseline 之後產生的訊息
    # 數值影響：state.rooms[room] 缺紀錄 → 視為初始化；補完才會 _collect 撈到新訊息
    state.setdefault("rooms", {})
    rooms_initialized = []
    for room in tm.get("rooms", []):
        if room not in state["rooms"]:
            max_seq = max((m.get("seq", 0) for m in _read_room_messages(room)), default=0)
            state["rooms"][room] = {"last_seen_seq": max_seq}
            rooms_initialized.append(room)
    if rooms_initialized:
        _save_tavern_state(state)
        _log(f"[tavern] baseline 建立：{rooms_initialized}（既有歷史訊息不會回放）")

    # 真正撈新訊息（前面 baseline 寫完才撈，避免初始化的房又被撈到）
    new_msgs = _collect_new_tavern_messages(tm, state)
    truly_new = new_msgs   # 已是 baseline 後的真新訊息
    if not truly_new:
        if rooms_initialized:
            return 0, f"baseline established for {len(rooms_initialized)} room(s)"
        return 0, "no new tavern messages"

    # broadcast 每筆訊息 — 一次一 POST（保時間軸）；R6.4 帶 identity override
    # 區塊職責：每筆按 sender 選 webhook 群組（quest lifecycle vs main chat 分流）
    # 物理意義：sender 開頭 match quest_match_prefix（預設 _quest_system）→ 走 quest_urls；其他走 main urls
    # 數值影響：state.last_seen_seq 仍共用一份（兩 webhook 看到的訊息順序一致，state 不分）
    sent_count = 0
    sent_quest = 0
    sent_main = 0
    sent_category = 0    # T42 — broadcast 到 category groups 的次數累計
    fail_in_batch = 0
    for room, msg in truly_new:
        # T58 — content 移到 broadcast loop 內 per-target 重建（routing_tag suffix 各 target 不同）
        # 物理意義：同訊息 broadcast 到 main / quest / category 多 webhook 時，每個 Discord 頻道
        #          看到的 title 末會標自家路由 tag（如 "→ **#main**" / "→ **#work-channel** (category)"）
        # 邊界：身分（username/avatar_url）每筆唯一，跟 routing_tag 無關；先預先 resolve 一次共用
        # 取 identity (只用第一個 chunk 的 username/avatar, 全 chunks 共用同 identity)
        _id_payloads = _build_tavern_payload(room, msg, tm)
        _, username, avatar_url = _id_payloads[0]
        sender = msg.get("sender_id", "") or ""

        # T42-fix (per Tim 拍板): main 「聊天酒館」channel 永遠收（總覽），category channel additive 加分類精選
        # 物理意義：原 T42 設計「命中 → only category」會讓 main webhook 從此空轉，Tim 看不到全貌；
        #          改成 main always-broadcast + category additive 同時送，符合「總覽 + 分類精選」直覺
        # 數值影響：訊息 1 筆 broadcast 到 N+1 個 webhook（main + 命中的 category groups）；webhook quota 增加
        # 邊界：quest_routing 仍維持 exclusive（quest lifecycle 不污染 main / category）
        if quest_routing_active and quest_match_prefix and sender.startswith(quest_match_prefix):
            # Layer 1: quest lifecycle exclusive → quest webhook only（不污染 main / category）
            targets = [(quest_urls, "quest")]
        else:
            # Layer 2: 一般 chat — main always + category additive；除非命中 exclusive group
            # 區塊職責：解 T42 「買一送一」bug — exclusive group 命中時 main 跳過 + 其他 additive 也跳過
            # 物理意義：valor-channel 對 category=battle 設 m_Exclusive=true → 戰鬥 log 只到 valor，
            #          不再污染 #聊天酒館 main 頻道；非 exclusive 命中（如 work-channel）走原 additive 行為
            # 數值影響：webhook quota 大幅減少（不再雙重發送 battle log）
            # 邊界：多 exclusive group 同時命中 → 各送一份；non-exclusive 命中混合 exclusive 命中 → main 跳過
            #       (因為 exclusive 的存在，意味著「至少有一個 group 主張壟斷此 category」)
            targets = []
            matched_groups = []
            has_exclusive = False
            if category_routing_active:
                matched_groups = _match_msg_to_routing_groups(msg, category_groups, case_insensitive=category_case_insensitive)
                has_exclusive = any(g.get("exclusive", False) for g in matched_groups)

            # 2.1 main webhook — 只在沒命中 exclusive 時加入
            if urls and not has_exclusive:
                targets.append((urls, "main"))

            # 2.2 category_routing 命中的 groups
            for g in matched_groups:
                g_urls, _g_source = _resolve_group_webhook_urls(g)
                if not g_urls:
                    continue
                if has_exclusive:
                    # 命中 exclusive 模式 → 只加 exclusive group 自己（其他 additive 也跳過）
                    if g.get("exclusive", False):
                        targets.append((g_urls, f"category:{g['id']}"))
                else:
                    # 沒 exclusive → 全部 additive 加入（既有 T42-fix 行為）
                    targets.append((g_urls, f"category:{g['id']}"))

        # broadcast 到所有 target groups（multi-group 同訊息可能 N 個 webhook 各收一份）
        # T58 — per-target rebuild content with routing_tag → 每 Discord 頻道顯示自家路由 tag
        msg_any_ok = False
        for target_urls, stream_tag in targets:
            # 多 chunk: build 回 list, 順序 POST 各 chunk 到同 webhook 群
            # 任一 chunk POST 失敗仍計 msg_any_ok=False? 不 — 部份成功也算發了, 仍推進 last_seen 避免重發
            target_payloads = _build_tavern_payload(room, msg, tm, routing_tag=stream_tag)
            any_chunk_ok = False
            for target_content, _, _ in target_payloads:
                any_ok, results = _send(target_urls, target_content, username=username, avatar_url=avatar_url)
                if any_ok:
                    any_chunk_ok = True
            if any_chunk_ok:
                msg_any_ok = True
                if stream_tag == "quest":
                    sent_quest += 1
                elif stream_tag.startswith("category:"):
                    sent_category += 1
                else:
                    sent_main += 1

        if msg_any_ok:
            sent_count += 1
            # 推進該房 last_seen（任一 target 成功就視為「該訊息已 broadcast」，避免重發）
            state["rooms"].setdefault(room, {})["last_seen_seq"] = msg.get("seq", 0)
            state["consecutive_failures"] = 0
        else:
            fail_in_batch += 1
            # 不推進 last_seen（下次重試）；避免 fail 訊息被永久跳過
            break   # 連發 fail 不再嘗試後續，避免雪崩

    if fail_in_batch:
        state["consecutive_failures"] = state.get("consecutive_failures", 0) + 1

    _save_tavern_state(state)

    if sent_count:
        # T42 — log 含 category broadcast 次數（如有 enable）
        if category_routing_active:
            _log(f"[tavern] sent {sent_count}/{len(truly_new)} (main={sent_main} | quest={sent_quest} | category={sent_category} groups={len(category_groups)})")
            return sent_count, f"sent {sent_count} (main={sent_main}, quest={sent_quest}, category={sent_category})"
        elif quest_routing_active:
            _log(f"[tavern] sent {sent_count}/{len(truly_new)} (main={sent_main} src={source} | quest={sent_quest} src={quest_source})")
            return sent_count, f"sent {sent_count} (main={sent_main}, quest={sent_quest})"
        else:
            _log(f"[tavern] sent {sent_count}/{len(truly_new)} (source={source})")
            return sent_count, f"sent {sent_count}"
    else:
        warn = f"all tavern send fail (consecutive={state['consecutive_failures']}/{fail_threshold})"
        _log(warn)
        return 0, warn


# ===========================================================
# T-TREASURY (Tim 2026-07-15 拍板方案 C) — Treasury ledger pull adapter
# 區塊職責：把 push 孤兒 notify_treasury 收編進 mirror run — ledger 本身就是 append-only 事件流
#          （Treasury/ledger/<date>/<ts>_<uuid>__<type>.json, relkey ordinal 天然有序）。
# 物理意義：pull + cursor = 冪等可重試（push 版 webhook fail 該筆通知永久丟失）；跟 tavern stream
#          同 run 共享 _MirrorRunLock 互斥與 treasury_mirror.enabled gating（master toggle 天然覆蓋）。
# 數值影響：state（_tavern_state.json）新增 "treasury": {"last_seen": "<date>/<fname>"} cursor；
#          首見（無 cursor）→ baseline 到最新不回放歷史；壞檔跳過並推進 cursor（不卡死）；
#          send fail 保留 cursor 下次重試。__audit（0-amount 記錄檔）預設不廣播（維持 push 版行為，
#          tm.include_audit=true 可開）。embed 建構複用 notify_treasury.broadcast_entry（同 dir sibling）。
# ===========================================================

def notify_treasury_entries(config=None):
    """Treasury ledger pull adapter 主入口。回 (sent_count, reason)。"""
    if config is None:
        config = _load_config()
    tm = config.get("treasury_mirror", {}) or {}
    if not tm.get("enabled"):
        return 0, "treasury_mirror disabled"
    ledger_root = _tp.AGENT_COMMANDS_DIR / "Treasury" / "ledger"
    if not ledger_root.is_dir():
        return 0, "no ledger dir"

    state = _load_tavern_state()
    cursor = (state.get("treasury") or {}).get("last_seen", "")

    # 掃描新 entry — relkey = "<date>/<fname>" ordinal 排序；cursor 日期前的整資料夾 cheap prune
    news = []
    cursor_date = cursor.split("/")[0] if cursor else ""
    for ddir in sorted(ledger_root.iterdir()):
        if not ddir.is_dir() or (cursor_date and ddir.name < cursor_date):
            continue
        for f in sorted(ddir.glob("*.json")):
            relkey = f"{ddir.name}/{f.name}"
            if relkey > cursor:
                news.append((relkey, f))
    news.sort(key=lambda kv: kv[0])

    if not cursor:
        # baseline — 首次啟用不回放歷史
        if news:
            state.setdefault("treasury", {})["last_seen"] = news[-1][0]
            _save_tavern_state(state)
        return 0, f"treasury baseline established ({len(news)} historical entries skipped)"
    if not news:
        return 0, "no new treasury entries"

    max_per_run = tm.get("max_per_run", 20)
    deferred = max(0, len(news) - max_per_run)
    news = news[:max_per_run]

    # sibling import — 複用 push 版的 embed builder + client（本檔跟 notify_treasury.py 同 dir）
    import sys as _sys
    if str(HERE) not in _sys.path:
        _sys.path.insert(0, str(HERE))
    import notify_treasury as _nt

    include_audit = bool(tm.get("include_audit", False))
    sent = 0
    for relkey, f in news:
        if f.name.endswith("__audit.json") and not include_audit:
            state.setdefault("treasury", {})["last_seen"] = relkey   # 靜默推進
            continue
        try:
            entry = json.loads(f.read_text(encoding="utf-8"))
        except Exception as e:
            _log(f"[treasury] entry parse fail @ {relkey}: {e} — 跳過並推進 cursor")
            state.setdefault("treasury", {})["last_seen"] = relkey
            continue
        ok, msg = _nt.broadcast_entry(entry)
        if ok:
            sent += 1
            state.setdefault("treasury", {})["last_seen"] = relkey
        else:
            _log(f"[treasury] send fail @ {relkey}: {msg} — cursor 保留下次重試")
            break
    _save_tavern_state(state)
    if deferred:
        _log(f"[treasury] max_per_run cap: {deferred} entries 留待下一輪")
    if sent:
        _log(f"[treasury] sent {sent}/{len(news)}")
    return sent, f"sent {sent}"


# ===========================================================
# T16 — wake-notify stream（per T09 §3.2）
# 物理意義：偵測 rooms/<X>/inbox/<agent>.md mtime 變動 → POST 推 Tim 「該叫醒 agent」
# 數值影響：跟 queue-idle / tavern-mirror 完全獨立 webhook + state；純 outbound 0 bot 依賴
# ===========================================================

def _load_wake_state():
    try:
        if WAKE_STATE_PATH.exists():
            return json.loads(WAKE_STATE_PATH.read_text(encoding="utf-8"))
    except Exception as e:
        _log(f"[wake] state load fail (走空 state)：{e}")
    return {"inbox_mtime": {}, "last_notify_at": {}, "consecutive_failures": 0}


def _save_wake_state(state):
    try:
        _atomic_write_text(WAKE_STATE_PATH, json.dumps(state, indent=2, ensure_ascii=False))
    except Exception as e:
        _log(f"[wake] state save fail：{e}")


def _read_wake_webhook_urls(config):
    wn = config.get("wake_notify", {}) or {}
    return _resolve_webhook_urls(wn, "wake-notify")


def _enumerate_inbox_files(watched_agents):
    """掃 rooms/*/inbox/<agent>.md 找所有 watched agent 的 inbox file。
    回 list of (room_id, agent_id, file_path, mtime)"""
    out = []
    rooms_root = ROOMS_DIR
    if not rooms_root.exists():
        return out
    watched = set(watched_agents) if watched_agents else None
    for room_dir in rooms_root.iterdir():
        if not room_dir.is_dir():
            continue
        inbox_dir = room_dir / "inbox"
        if not inbox_dir.is_dir():
            continue
        for f in inbox_dir.glob("*.md"):
            agent_id = f.stem
            if watched is not None and agent_id not in watched:
                continue
            try:
                mtime = f.stat().st_mtime
            except Exception:
                continue
            out.append((room_dir.name, agent_id, f, mtime))
    return out


def _read_inbox_tail(file_path, max_chars=400):
    """讀 inbox 最後一條 entry（粗略：取後 max_chars 字元）給 ping 摘要"""
    try:
        text = file_path.read_text(encoding="utf-8")
        return text[-max_chars:].strip() if text else ""
    except Exception:
        return ""


def notify_wake_signals(config=None):
    """
    Wake-notify 主入口；scan 所有 watched agent 的 inbox.md mtime → 比對 cache → 變動則 POST ping。
    回 (sent_count: int, reason: str)
    """
    if config is None:
        config = _load_config()

    wn = config.get("wake_notify", {}) or {}
    if not wn.get("enabled"):
        return 0, "wake_notify disabled (跑 --enable-wake 啟用)"

    urls, source = _read_wake_webhook_urls(config)
    if not urls:
        return 0, "no wake webhook URLs (跑 --add-wake-webhook URL 設定)"

    state = _load_wake_state()

    # auto-disable
    fail_threshold = wn.get("disable_after_failures", 5)
    if state.get("consecutive_failures", 0) >= fail_threshold:
        return 0, f"auto-disabled (consecutive_failures>=fail_threshold)；reset state 後重試"

    # cooldown gate per agent
    cooldown_min = wn.get("cooldown_minutes", 2)
    now = datetime.datetime.utcnow()

    watched = wn.get("watched_agents", [])
    inbox_files = _enumerate_inbox_files(watched)

    cached_mtime = state.get("inbox_mtime", {})
    last_notify_at = state.get("last_notify_at", {})

    sent_count = 0
    fail_in_batch = 0
    max_per_run = wn.get("max_per_run", 5)
    use_local = wn.get("use_local_time", True)

    # baseline 機制：首次 enable 時不要把所有現存 inbox 都當「新」回放
    rooms_initialized = []

    for room_id, agent_id, file_path, mtime in inbox_files:
        key = f"{room_id}/{agent_id}"

        # baseline：cache 內無此 key → 視為已知，不回放
        if key not in cached_mtime:
            cached_mtime[key] = mtime
            rooms_initialized.append(key)
            continue

        # mtime 沒變 → skip
        if mtime <= cached_mtime[key]:
            continue

        # cooldown：同 agent 短時間內 burst 變動合併
        last_iso = last_notify_at.get(agent_id)
        if last_iso:
            try:
                last = datetime.datetime.fromisoformat(last_iso.replace("Z", ""))
                if (now - last).total_seconds() / 60 < cooldown_min:
                    continue
            except Exception:
                pass

        if sent_count >= max_per_run:
            break

        # 構造 ping
        tail = _read_inbox_tail(file_path)
        ts = _format_time(use_local)
        label = wn.get("channel_label", "WakeAlert")
        content = (
            f"🔔 **[{label}]** `@{agent_id}` inbox 有新待辦（room=`{room_id}`）\n"
            f"🕐 {ts}\n"
            f"```\n{tail[-300:]}\n```\n"
            f"_該叫醒 agent 跑 inbox_read 接題_"
        )
        # identity override：用對方 agent 頭像（Tim 看了知道是給誰的 ping）
        tm_config = config.get("tavern_mirror", {}) or {}
        username, avatar_url = _resolve_discord_identity(agent_id, agent_id, tm_config)
        any_ok, results = _send(urls, content, username=username, avatar_url=avatar_url)
        if any_ok:
            sent_count += 1
            cached_mtime[key] = mtime
            last_notify_at[agent_id] = now.isoformat() + "Z"
            state["consecutive_failures"] = 0
        else:
            fail_in_batch += 1

    state["inbox_mtime"] = cached_mtime
    state["last_notify_at"] = last_notify_at
    if fail_in_batch and sent_count == 0:
        state["consecutive_failures"] = state.get("consecutive_failures", 0) + 1
    _save_wake_state(state)

    if rooms_initialized and sent_count == 0:
        return 0, f"baseline established for {len(rooms_initialized)} inbox(es)（既有歷史不回放）"
    if sent_count == 0 and fail_in_batch == 0:
        return 0, "no inbox changes"
    if sent_count == 0:
        return 0, f"all {fail_in_batch} send fail (consecutive={state['consecutive_failures']})"
    return sent_count, f"sent {sent_count}" + (f" / {fail_in_batch} fail" if fail_in_batch else "")


# ===========================================================
# 公開入口
# ===========================================================

def notify_if_queue_idle(states_dict, all_events, config=None):
    """
    qdrain.py 結尾呼叫；判斷該不該通知 + 真去發。
    回 (sent: bool, reason: str)；reason 給 log 用
    """
    if config is None:
        config = _load_config()

    if not config.get("enabled", True):
        return False, "config disabled"

    # === 觸發條件檢查 ===
    pending = sum(1 for s in states_dict.values() if s.get("status") == "pending")
    in_prog = sum(1 for s in states_dict.values() if s.get("status") in ("claimed", "in_progress"))
    done = sum(1 for s in states_dict.values() if s.get("status") == "done")

    if pending != 0 or in_prog != 0:
        return False, f"queue not idle (pending={pending}, in_progress={in_prog})"

    state = _load_state()
    current_max_seq = _max_event_seq(all_events)

    # T28 — 多 quest 房 events 收集（per_room_seq state machine）
    # 物理意義：each room 各自獨立 last_seen_seq，避免跨房 seq 碰撞；events 合流後丟給 _collect_done_contexts
    # backwards compat：若 state 只有舊 last_notify_event_seq（int）→ 視同 agent-prompt-queue 那條 seq
    quest_rooms = config.get("watched_quest_rooms", []) or []
    per_room_seq = state.setdefault("per_room_seq", {})
    # backwards compat
    if "agent-prompt-queue" not in per_room_seq and "last_notify_event_seq" in state:
        per_room_seq["agent-prompt-queue"] = state.get("last_notify_event_seq", -1)
    # collect quest room events + per-room baseline
    quest_events_merged = []
    quest_baselines_init = []
    for room_id in quest_rooms:
        evs = _load_room_events(room_id)
        if not evs:
            continue
        room_last_seq = per_room_seq.get(room_id, -1)
        if room_last_seq < 0:
            # baseline — 不回放歷史
            max_room_seq = max((e.get("seq", 0) for e in evs), default=0)
            per_room_seq[room_id] = max_room_seq
            quest_baselines_init.append(room_id)
            continue
        # 過濾 since 該房 last seq 之後的 events
        for ev in evs:
            if ev.get("seq", 0) > room_last_seq:
                quest_events_merged.append(ev)

    # 第一次跑 → 建立 baseline 不通知（避免 agent restart 進空 queue 也吵）
    if state.get("last_notify_event_seq", -1) < 0:
        state["last_notify_event_seq"] = current_max_seq
        per_room_seq["agent-prompt-queue"] = current_max_seq
        _save_state(state)
        _log(f"first run baseline 建立（max_seq={current_max_seq}）— 不通知")
        return False, "first run baseline"

    # 沒新 done events → 跳過（檢查 PromptQueue + 多 quest 房）
    promptqueue_has_new = current_max_seq > state["last_notify_event_seq"]
    quest_has_new = len(quest_events_merged) > 0
    if not promptqueue_has_new and not quest_has_new:
        msg = f"no new events (PQ max={current_max_seq}/last={state['last_notify_event_seq']}"
        if quest_rooms:
            msg += f", quest rooms={len(quest_rooms)} all caught up"
        msg += ")"
        if quest_baselines_init:
            _save_state(state)   # 保 quest baseline
            msg += f"; baselined: {quest_baselines_init}"
        return False, msg

    # cooldown
    if state.get("last_notify_at"):
        try:
            last = datetime.datetime.fromisoformat(state["last_notify_at"].replace("Z", ""))
            elapsed_min = (datetime.datetime.utcnow() - last).total_seconds() / 60
            cooldown = config.get("cooldown_minutes", 5)
            if elapsed_min < cooldown:
                return False, f"cooldown active ({elapsed_min:.1f}/{cooldown} min)"
        except Exception as e:
            _log(f"cooldown check fail（容忍，視為過了）：{e}")

    # auto-disable
    fail_threshold = config.get("disable_after_failures", 5)
    if state.get("consecutive_failures", 0) >= fail_threshold:
        return False, f"auto-disabled (consecutive_failures={state['consecutive_failures']} >= {fail_threshold}); 改 _notify_state.json 或修 webhook 後手動重置"

    # === 構造訊息（T28 — 合併 PromptQueue + watched quest 房 events）===
    pq_done_contexts = _collect_done_contexts(all_events, state["last_notify_event_seq"])
    quest_done_contexts = _collect_done_contexts(quest_events_merged, since_seq=-1) if quest_events_merged else []
    done_contexts = pq_done_contexts + quest_done_contexts
    stats = {"pending": pending, "in_progress": in_prog, "done": done}
    content, embeds = _build_payload(stats, done_contexts, config)

    # === 發送 — 多 webhook broadcast ===
    webhook_urls, source = _read_webhook_urls(config)
    if not webhook_urls:
        _log("webhook URL 缺 / 不可讀 — silent skip（跑 `python notify_discord.py --set-webhook URL` 設定）")
        return False, "webhook URL missing"

    # T10 — queue-idle identity override（embed 卡頂端 username + avatar）
    qi_username, qi_avatar = _resolve_queue_idle_identity(done_contexts, config)
    any_ok, results = _send(webhook_urls, content, username=qi_username, avatar_url=qi_avatar, embeds=embeds)
    n_ok = sum(1 for _, ok, _ in results if ok)
    n_fail = len(results) - n_ok
    detail = ", ".join(f"{uid}={'OK' if ok else err}" for uid, ok, err in results)

    if any_ok:
        # 至少一筆成功 → reset failure counter；any-fail 標 partial
        state["last_notify_at"] = datetime.datetime.utcnow().isoformat() + "Z"
        state["last_notify_event_seq"] = current_max_seq
        # T28 — 推進每個 quest 房的 last seen seq
        for room_id in quest_rooms:
            evs = _load_room_events(room_id)
            if evs:
                max_room_seq = max((e.get("seq", 0) for e in evs), default=0)
                per_room_seq[room_id] = max_room_seq
        state["per_room_seq"] = per_room_seq
        state["consecutive_failures"] = 0
        _save_state(state)
        status = "sent" if n_fail == 0 else f"sent partial ({n_ok}/{len(results)} OK)"
        _log(f"{status}（contexts={len(done_contexts)}, max_seq={current_max_seq}, source={source}, detail=[{detail}]）")
        return True, status
    else:
        # 全部失敗
        state["consecutive_failures"] = state.get("consecutive_failures", 0) + 1
        _save_state(state)
        warn = f"all webhook send fail（consecutive={state['consecutive_failures']}/{fail_threshold}）detail=[{detail}]"
        _log(warn)
        if state["consecutive_failures"] >= fail_threshold:
            _log(f"⚠ 達到 {fail_threshold} 次連續全失敗 → 自動 disable；改 webhook URLs 或修 _notify_state.json 重置")
        return False, warn


# ===========================================================
# CLI（dry-run / 強制觸發 — 給 smoke test 用）
# ===========================================================
def main():
    import sys
    import argparse

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dry-run", action="store_true", help="印 payload 不真送")
    parser.add_argument("--force", action="store_true", help="跳過所有觸發條件檢查強發（測試 webhook 連通用；auto-mode 下會被 _auto_mode.flag 擋下，milestone 用 --bypass-auto-mode-guard 例外）")
    parser.add_argument("--bypass-auto-mode-guard", action="store_true", help="搭配 --force：跳過 auto-mode flag 偵測（給 milestone 完成通知用）")
    parser.add_argument("--reset-state", action="store_true", help="刪 _notify_state.json 重置（含 baseline 跟 failure count）")
    parser.add_argument("--mode", choices=["queue-idle", "tavern", "wake", "all"], default="queue-idle",
                        help="跑哪條 stream（default queue-idle）；Stop hook 用 --mode all 三條都跑")
    # ---- queue-idle stream helpers ----
    parser.add_argument("--set-webhook", metavar="URL", help="[queue-idle] 取代 webhook_urls 為單一 URL")
    parser.add_argument("--add-webhook", metavar="URL", help="[queue-idle] 累加 URL 到 webhook_urls（broadcast 多 channel）")
    parser.add_argument("--secret-file", action="store_true", help="搭配 --set-webhook：改寫進 _discord_webhook.txt（git-ignored）")
    parser.add_argument("--unset-webhook", action="store_true", help="[queue-idle] 清 webhook_url + webhook_urls + 刪 _discord_webhook.txt")
    parser.add_argument("--list-webhooks", action="store_true", help="[queue-idle] 列當前 resolved URLs")
    parser.add_argument("--verify", action="store_true", help="搭配 --set/--add-webhook：寫完立刻 smoke test")
    # ---- tavern-mirror stream helpers ----
    parser.add_argument("--set-tavern-webhook", metavar="URL", help="[tavern] 取代 tavern_mirror.webhook_urls 為單一 URL")
    parser.add_argument("--add-tavern-webhook", metavar="URL", help="[tavern] 累加 URL 到 tavern_mirror.webhook_urls")
    parser.add_argument("--unset-tavern-webhook", action="store_true", help="[tavern] 清 tavern_mirror.webhook_urls + 刪 tavern webhook file")
    parser.add_argument("--list-tavern", action="store_true", help="[tavern] 列當前 webhook URLs / 監看房 / kinds / state")
    parser.add_argument("--add-tavern-room", metavar="ROOM", help="[tavern] 加入 watched 房名（必須 enable + 加房才會鏡像）")
    parser.add_argument("--remove-tavern-room", metavar="ROOM", help="[tavern] 從 watched list 移除房")
    parser.add_argument("--enable-tavern", action="store_true", help="[tavern] tavern_mirror.enabled = true")
    parser.add_argument("--disable-tavern", action="store_true", help="[tavern] tavern_mirror.enabled = false（不刪 config）")
    # ---- wake-notify stream helpers (T16) ----
    parser.add_argument("--set-wake-webhook", metavar="URL", help="[wake] 取代 wake_notify.webhook_urls 為單一 URL")
    parser.add_argument("--add-wake-webhook", metavar="URL", help="[wake] 累加 URL 到 wake_notify.webhook_urls")
    parser.add_argument("--unset-wake-webhook", action="store_true", help="[wake] 清 wake_notify.webhook_urls")
    parser.add_argument("--list-wake", action="store_true", help="[wake] 列當前 webhook + watched_agents + state")
    parser.add_argument("--enable-wake", action="store_true", help="[wake] wake_notify.enabled = true")
    parser.add_argument("--disable-wake", action="store_true", help="[wake] wake_notify.enabled = false（不刪 config）")
    parser.add_argument("--add-watched-agent", metavar="AGENT_ID", help="[wake] 加入 watched_agents（agent inbox 變動才觸發 ping）")
    parser.add_argument("--remove-watched-agent", metavar="AGENT_ID", help="[wake] 從 watched_agents 移除")
    # ---- T28 cross-quest stream helpers ----
    parser.add_argument("--add-quest-room", metavar="ROOM", help="[queue-idle] 加入 watched_quest_rooms（task_done auto-notify Discord）")
    parser.add_argument("--remove-quest-room", metavar="ROOM", help="[queue-idle] 從 watched_quest_rooms 移除")
    parser.add_argument("--list-quest-rooms", action="store_true", help="[queue-idle] 列當前 watched_quest_rooms + per-room state")
    # ---- 共通 ----
    parser.add_argument("--show-config", action="store_true", help="印整份 config + webhook source + stream state")
    args = parser.parse_args()

    if args.reset_state:
        if STATE_PATH.exists():
            STATE_PATH.unlink()
            print(f"[notify] state file deleted: {STATE_PATH}")
        else:
            print(f"[notify] state file already absent: {STATE_PATH}")
        return 0

    config = _load_config()

    # ---- webhook 設定 helpers ----
    def _validate_url(url):
        if not url.startswith("https://"):
            print(f"[notify] ✗ URL 必須以 https:// 開頭（妳給的：{url[:30]!r}）")
            return False
        if not (url.startswith("https://discord.com/api/webhooks/") or url.startswith("https://discordapp.com/api/webhooks/")):
            print(f"[notify] ⚠ URL 不像 Discord webhook（應為 https://discord.com/api/webhooks/...），仍寫入但可能無效")
        return True

    def _smoke_test_urls(urls, label):
        print(f"[notify] running {label} smoke test ({len(urls)} URL{'s' if len(urls) != 1 else ''})...")
        any_ok, results = _send(urls, f"🧪 PromptQueue notify {label} — 看到本訊息表示連通 OK")
        for uid, ok, err in results:
            print(f"[notify]   {uid}: {'OK' if ok else err}")
        return 0 if any_ok else 1

    def _read_config_for_edit():
        if CONFIG_PATH.exists():
            try:
                return json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
            except: return {}
        return {}

    def _write_config_for_edit(cfg):
        CONFIG_PATH.write_text(json.dumps(cfg, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    if args.set_webhook:
        url = args.set_webhook.strip()
        if not _validate_url(url):
            return 1
        if args.secret_file:
            target = STATE_DIR / config.get("webhook_file", "_discord_webhook.txt")
            target.write_text(url + "\n", encoding="utf-8")
            print(f"[notify] ✓ webhook URL written → {target.name} (git-ignored, **取代** 既有檔內容)")
        else:
            existing = _read_config_for_edit()
            # set = 取代整個 webhook_urls list（單一 URL）；clear 舊 webhook_url
            existing["webhook_urls"] = [url]
            existing.pop("webhook_url", None)
            _write_config_for_edit(existing)
            print(f"[notify] ✓ webhook URL set (取代既有清單) → {CONFIG_PATH.name} webhook_urls=[1]")
        masked = "..." + url[-12:] if len(url) > 12 else url
        print(f"[notify]   identifier: {masked}")
        if args.verify:
            return _smoke_test_urls([url], "set-webhook")
        print(f"[notify]   下一步：python notify_discord.py --force 或 --list-webhooks")
        return 0

    if args.add_webhook:
        url = args.add_webhook.strip()
        if not _validate_url(url):
            return 1
        existing = _read_config_for_edit()
        urls = list(existing.get("webhook_urls", []))
        # legacy webhook_url 自動遷移到 webhook_urls list（一次性 — 之後永遠不再生）
        if "webhook_url" in existing:
            legacy = existing.pop("webhook_url")
            if isinstance(legacy, str) and legacy.startswith("https://") and legacy not in urls:
                urls.append(legacy)
                print(f"[notify] ℹ legacy webhook_url 已遷移到 webhook_urls（單欄位已棄用）")
        if url in urls:
            print(f"[notify] ℹ URL 已存在於 webhook_urls，略過（共 {len(urls)} 筆）")
        else:
            urls.append(url)
            existing["webhook_urls"] = urls
            _write_config_for_edit(existing)
            masked = "..." + url[-12:] if len(url) > 12 else url
            print(f"[notify] ✓ added → {CONFIG_PATH.name} webhook_urls=[{len(urls)}]")
            print(f"[notify]   identifier: {masked}")
        if args.verify:
            return _smoke_test_urls([url], "add-webhook (only 新加 URL)")
        return 0

    if args.list_webhooks:
        urls, source = _read_webhook_urls(config)
        print(f"=== Resolved webhooks ===")
        print(f"source: {source or '(none)'}")
        print(f"count: {len(urls)}")
        for i, u in enumerate(urls, 1):
            masked = "..." + u[-12:] if len(u) > 12 else u
            print(f"  [{i}] {masked}")
        if not urls:
            print(f"  (none — 跑 --set-webhook URL 設定)")
        return 0

    # ---- tavern stream helpers ----
    def _ensure_tavern_block(cfg):
        cfg.setdefault("tavern_mirror", dict(DEFAULT_CONFIG["tavern_mirror"]))
        return cfg

    if args.set_tavern_webhook:
        url = args.set_tavern_webhook.strip()
        if not _validate_url(url): return 1
        cfg = _read_config_for_edit()
        _ensure_tavern_block(cfg)
        cfg["tavern_mirror"]["webhook_urls"] = [url]
        _write_config_for_edit(cfg)
        masked = "..." + url[-12:] if len(url) > 12 else url
        print(f"[notify-tavern] ✓ webhook URL set (取代既有) → tavern_mirror.webhook_urls=[1]")
        print(f"[notify-tavern]   identifier: {masked}")
        if args.verify:
            return _smoke_test_urls([url], "tavern set-webhook")
        return 0

    if args.add_tavern_webhook:
        url = args.add_tavern_webhook.strip()
        if not _validate_url(url): return 1
        cfg = _read_config_for_edit()
        _ensure_tavern_block(cfg)
        urls = list(cfg["tavern_mirror"].get("webhook_urls", []))
        if url in urls:
            print(f"[notify-tavern] ℹ URL 已存在於 tavern_mirror.webhook_urls，略過（共 {len(urls)} 筆）")
        else:
            urls.append(url)
            cfg["tavern_mirror"]["webhook_urls"] = urls
            _write_config_for_edit(cfg)
            masked = "..." + url[-12:] if len(url) > 12 else url
            print(f"[notify-tavern] ✓ added → tavern_mirror.webhook_urls=[{len(urls)}]")
            print(f"[notify-tavern]   identifier: {masked}")
        if args.verify:
            return _smoke_test_urls([url], "tavern add-webhook")
        return 0

    if args.unset_tavern_webhook:
        cleared = []
        cfg = _read_config_for_edit()
        if "tavern_mirror" in cfg and "webhook_urls" in cfg["tavern_mirror"]:
            cfg["tavern_mirror"]["webhook_urls"] = []
            _write_config_for_edit(cfg)
            cleared.append("tavern_mirror.webhook_urls cleared")
        tm_file = STATE_DIR / cfg.get("tavern_mirror", {}).get("webhook_file", "_tavern_webhook.txt")
        if tm_file.exists():
            tm_file.unlink()
            cleared.append(f"removed {tm_file.name}")
        for c in cleared:
            print(f"[notify-tavern] ✓ {c}")
        if not cleared:
            print(f"[notify-tavern] no tavern webhook to remove")
        return 0

    if args.add_tavern_room:
        room = args.add_tavern_room.strip()
        cfg = _read_config_for_edit()
        _ensure_tavern_block(cfg)
        rooms = list(cfg["tavern_mirror"].get("rooms", []))
        if room in rooms:
            print(f"[notify-tavern] ℹ room 已在 watched list：{room}")
        else:
            rooms.append(room)
            cfg["tavern_mirror"]["rooms"] = rooms
            _write_config_for_edit(cfg)
            print(f"[notify-tavern] ✓ added watched room: {room} （共 {len(rooms)} 房）")
        # 確認房存在 + 提示 baseline 機制
        room_dir = _tp.get_room_dir(room)
        if not room_dir.exists():
            print(f"[notify-tavern] ⚠ 房 dir 不存在：{room_dir}（confirm room name）")
        else:
            msgs = _read_room_messages(room)
            print(f"[notify-tavern]   房內現有 {len(msgs)} 則訊息；首次 enable 後會 baseline，不會回放歷史")
        return 0

    if args.remove_tavern_room:
        room = args.remove_tavern_room.strip()
        cfg = _read_config_for_edit()
        if "tavern_mirror" in cfg:
            rooms = list(cfg["tavern_mirror"].get("rooms", []))
            if room in rooms:
                rooms.remove(room)
                cfg["tavern_mirror"]["rooms"] = rooms
                _write_config_for_edit(cfg)
                print(f"[notify-tavern] ✓ removed: {room} （剩 {len(rooms)} 房）")
            else:
                print(f"[notify-tavern] ℹ room 不在 list：{room}")
        return 0

    if args.enable_tavern:
        cfg = _read_config_for_edit()
        _ensure_tavern_block(cfg)
        cfg["tavern_mirror"]["enabled"] = True
        _write_config_for_edit(cfg)
        print(f"[notify-tavern] ✓ enabled (rooms={cfg['tavern_mirror'].get('rooms', [])})")
        return 0

    if args.disable_tavern:
        cfg = _read_config_for_edit()
        if "tavern_mirror" in cfg:
            cfg["tavern_mirror"]["enabled"] = False
            _write_config_for_edit(cfg)
            print(f"[notify-tavern] ✓ disabled (config 保留)")
        return 0

    if args.list_tavern:
        tm = config.get("tavern_mirror", {}) or {}
        urls, source = _read_tavern_webhook_urls(config)
        state = _load_tavern_state()
        print("=== tavern_mirror config ===")
        print(json.dumps({k: v for k, v in tm.items() if k != "_comment"}, indent=2, ensure_ascii=False))
        print()
        print(f"=== webhook resolution ===")
        print(f"source: {source or '(none)'}")
        print(f"count: {len(urls)}")
        for i, u in enumerate(urls, 1):
            masked = "..." + u[-12:] if len(u) > 12 else u
            print(f"  [{i}] {masked}")
        if not urls:
            print(f"  (none — 跑 --add-tavern-webhook URL 設定)")
        print()
        print(f"=== state ({TAVERN_STATE_PATH.name}) ===")
        print(json.dumps(state, indent=2, ensure_ascii=False))
        return 0

    # ---- /tavern stream helpers ----

    # ---- wake-notify stream helpers (T16) ----
    def _ensure_wake_block(cfg):
        cfg.setdefault("wake_notify", dict(DEFAULT_CONFIG["wake_notify"]))
        return cfg

    if args.set_wake_webhook:
        url = args.set_wake_webhook.strip()
        if not _validate_url(url): return 1
        cfg = _read_config_for_edit()
        _ensure_wake_block(cfg)
        cfg["wake_notify"]["webhook_urls"] = [url]
        _write_config_for_edit(cfg)
        print(f"[notify-wake] webhook URL set (取代既有) → wake_notify.webhook_urls=[1]")
        if args.verify:
            return _smoke_test_urls([url], "wake set-webhook")
        return 0

    if args.add_wake_webhook:
        url = args.add_wake_webhook.strip()
        if not _validate_url(url): return 1
        cfg = _read_config_for_edit()
        _ensure_wake_block(cfg)
        urls = list(cfg["wake_notify"].get("webhook_urls", []))
        if url in urls:
            print(f"[notify-wake] URL 已存在，略過（共 {len(urls)} 筆）")
        else:
            urls.append(url)
            cfg["wake_notify"]["webhook_urls"] = urls
            _write_config_for_edit(cfg)
            print(f"[notify-wake] added → wake_notify.webhook_urls=[{len(urls)}]")
        if args.verify:
            return _smoke_test_urls([url], "wake add-webhook")
        return 0

    if args.unset_wake_webhook:
        cfg = _read_config_for_edit()
        if "wake_notify" in cfg and "webhook_urls" in cfg["wake_notify"]:
            cfg["wake_notify"]["webhook_urls"] = []
            _write_config_for_edit(cfg)
            print(f"[notify-wake] cleared")
        else:
            print(f"[notify-wake] nothing to clear")
        return 0

    if args.list_wake:
        wn = config.get("wake_notify", {}) or {}
        urls, source = _read_wake_webhook_urls(config)
        state = _load_wake_state()
        print("=== wake_notify config ===")
        print(json.dumps(wn, indent=2, ensure_ascii=False))
        print()
        print(f"=== webhook resolution ===")
        print(f"source: {source or '(none)'}")
        print(f"count: {len(urls)}")
        for i, u in enumerate(urls, 1):
            masked = "..." + u[-12:] if len(u) > 12 else u
            print(f"  [{i}] {masked}")
        print()
        print(f"=== state ({WAKE_STATE_PATH.name}) ===")
        print(json.dumps(state, indent=2, ensure_ascii=False))
        return 0

    if args.enable_wake:
        cfg = _read_config_for_edit()
        _ensure_wake_block(cfg)
        cfg["wake_notify"]["enabled"] = True
        _write_config_for_edit(cfg)
        print(f"[notify-wake] enabled (watched_agents={cfg['wake_notify'].get('watched_agents', [])})")
        return 0

    if args.disable_wake:
        cfg = _read_config_for_edit()
        if "wake_notify" in cfg:
            cfg["wake_notify"]["enabled"] = False
            _write_config_for_edit(cfg)
            print(f"[notify-wake] disabled (config 保留)")
        return 0

    if args.add_watched_agent:
        agent_id = args.add_watched_agent.strip()
        cfg = _read_config_for_edit()
        _ensure_wake_block(cfg)
        agents = list(cfg["wake_notify"].get("watched_agents", []))
        if agent_id in agents:
            print(f"[notify-wake] agent 已在 watched list：{agent_id}")
        else:
            agents.append(agent_id)
            cfg["wake_notify"]["watched_agents"] = agents
            _write_config_for_edit(cfg)
            print(f"[notify-wake] added watched agent: {agent_id} （共 {len(agents)}）")
        return 0

    if args.remove_watched_agent:
        agent_id = args.remove_watched_agent.strip()
        cfg = _read_config_for_edit()
        if "wake_notify" in cfg:
            agents = list(cfg["wake_notify"].get("watched_agents", []))
            if agent_id in agents:
                agents.remove(agent_id)
                cfg["wake_notify"]["watched_agents"] = agents
                _write_config_for_edit(cfg)
                print(f"[notify-wake] removed: {agent_id} （剩 {len(agents)}）")
            else:
                print(f"[notify-wake] not in list: {agent_id}")
        return 0

    # ---- /wake stream helpers ----

    # ---- T28 cross-quest stream helpers ----
    if args.add_quest_room:
        room = args.add_quest_room.strip()
        cfg = _read_config_for_edit()
        rooms = list(cfg.get("watched_quest_rooms", []) or [])
        if room in rooms:
            print(f"[notify-quest] room 已在 watched list：{room}")
        else:
            rooms.append(room)
            cfg["watched_quest_rooms"] = rooms
            _write_config_for_edit(cfg)
            print(f"[notify-quest] added watched quest room: {room}（共 {len(rooms)}）")
        # 確認房存在
        room_dir = _tp.get_room_dir(room)
        if not room_dir.exists():
            print(f"[notify-quest] WARN 房 dir 不存在：{room_dir}（confirm room name）")
        else:
            ev_path = room_dir / "events.jsonl"
            ev_count = 0
            if ev_path.exists():
                try:
                    ev_count = sum(1 for _ in ev_path.read_text(encoding="utf-8").splitlines() if _.strip())
                except Exception:
                    pass
            print(f"[notify-quest]   房內現有 {ev_count} 則 events；首次跑 baseline 不回放歷史")
        return 0

    if args.remove_quest_room:
        room = args.remove_quest_room.strip()
        cfg = _read_config_for_edit()
        rooms = list(cfg.get("watched_quest_rooms", []) or [])
        if room in rooms:
            rooms.remove(room)
            cfg["watched_quest_rooms"] = rooms
            _write_config_for_edit(cfg)
            print(f"[notify-quest] removed: {room}（剩 {len(rooms)}）")
        else:
            print(f"[notify-quest] not in list: {room}")
        return 0

    if args.list_quest_rooms:
        rooms = config.get("watched_quest_rooms", []) or []
        state = _load_state()
        per_room_seq = state.get("per_room_seq", {})
        print("=== watched_quest_rooms ===")
        if not rooms:
            print("(空 — 走 PromptQueue-only path)")
        for r in rooms:
            last_seq = per_room_seq.get(r, "-1 (baseline pending)")
            print(f"  - {r}  last_seen_seq={last_seq}")
        return 0

    if args.unset_webhook:
        cleared = []
        if CONFIG_PATH.exists():
            try:
                cfg = _read_config_for_edit()
                if "webhook_url" in cfg:
                    del cfg["webhook_url"]
                    cleared.append("config:webhook_url")
                if "webhook_urls" in cfg:
                    del cfg["webhook_urls"]
                    cleared.append("config:webhook_urls")
                if cleared:
                    _write_config_for_edit(cfg)
            except Exception as e:
                print(f"[notify] ⚠ {CONFIG_PATH.name} 處理失敗（容忍）：{e}")
        target = STATE_DIR / config.get("webhook_file", "_discord_webhook.txt")
        if target.exists():
            target.unlink()
            cleared.append(f"file:{target.name}")
        if cleared:
            for c in cleared:
                print(f"[notify] ✓ removed {c}")
        else:
            print(f"[notify] no webhook to remove")
        print(f"[notify]   ENV {config.get('webhook_env_var')} 仍可能有值（不動 ENV）")
        return 0

    if args.show_config:
        print("=== Resolved config ===")
        print(json.dumps({k: v for k, v in config.items() if not k.startswith("_")}, indent=2, ensure_ascii=False))
        print()
        print(f"=== queue-idle stream ===")
        q_urls, q_source = _read_webhook_urls(config)
        print(f"source: {q_source or '(none)'} / count: {len(q_urls)}")
        for i, u in enumerate(q_urls, 1):
            print(f"  [{i}] ...{u[-12:] if len(u) > 12 else u}")
        print(f"state: {json.dumps(_load_state(), indent=2, ensure_ascii=False)}")
        print()
        print(f"=== tavern-mirror stream ===")
        t_urls, t_source = _read_tavern_webhook_urls(config)
        tm = config.get("tavern_mirror", {}) or {}
        print(f"enabled: {tm.get('enabled', False)} / rooms: {tm.get('rooms', [])} / kinds: {tm.get('kinds', [])}")
        print(f"source: {t_source or '(none)'} / count: {len(t_urls)}")
        for i, u in enumerate(t_urls, 1):
            print(f"  [{i}] ...{u[-12:] if len(u) > 12 else u}")
        print(f"state: {json.dumps(_load_tavern_state(), indent=2, ensure_ascii=False)}")
        return 0
    # ---- /webhook 設定 helpers ----

    # import qdrain 的 reducer（避免重複實作）
    sys.path.insert(0, str(STATE_DIR))
    import qdrain as q
    events = q.load_events()
    states = q.reduce_states(events)
    # config 已在前面 helpers 區塊 load 過；force / dry-run 都共用同份

    if args.dry_run:
        # 印 payload 不發
        pending = sum(1 for s in states.values() if s.get("status") == "pending")
        in_prog = sum(1 for s in states.values() if s.get("status") in ("claimed", "in_progress"))
        done = sum(1 for s in states.values() if s.get("status") == "done")
        state = _load_state()
        done_contexts = _collect_done_contexts(events, state.get("last_notify_event_seq", -1))
        stats = {"pending": pending, "in_progress": in_prog, "done": done}
        content, embeds = _build_payload(stats, done_contexts, config)
        print("=== DRY-RUN CONTENT ===")
        print(content)
        print()
        print(f"=== DRY-RUN EMBEDS ({len(embeds)}/10) ===")
        for i, e in enumerate(embeds, 1):
            author = (e.get("author") or {}).get("name", "?")
            icon = (e.get("author") or {}).get("icon_url")
            title = e.get("title", "")
            desc = e.get("description", "")
            print(f"[{i}] {author}{' (icon=' + icon[-30:] + ')' if icon else ''}")
            print(f"    {title}")
            print(f"    {_truncate(desc, 80)}")
            for f in e.get("fields", []):
                print(f"    | {f['name']}: {_truncate(f['value'], 60)}")
        print()
        print(f"=== TRIGGER CHECK ===")
        print(f"pending={pending}, in_progress={in_prog}, done={done}, contexts_collected={len(done_contexts)}")
        print(f"state: {json.dumps(state, indent=2, ensure_ascii=False)}")
        return 0

    if args.force:
        # T30 — auto-mode flag 偵測：擋住 production auto-mode 誤用 --force（per Antigravity 踩坑）
        # auto-mode 應走 --mode all 走內部 idle gate / cooldown / baseline 三層保險
        # 真要 milestone notify 帶 --bypass-auto-mode-guard 顯式例外
        auto_mode_flag = STATE_DIR / "_auto_mode.flag"
        if auto_mode_flag.exists() and not args.bypass_auto_mode_guard:
            print(f"[notify] ⚠ AUTO MODE 偵測到（{auto_mode_flag.name} 存在）— 拒絕 --force")
            print(f"[notify]   理由：auto-mode 應走 `--mode all` 走 idle gate / cooldown / baseline 三層保險")
            print(f"[notify]   要 milestone broadcast 加 `--bypass-auto-mode-guard` 例外")
            print(f"[notify]   testing 想用 force：先 `rm {auto_mode_flag}` 退出 auto-mode")
            return 0  # silent skip (treat as no-op)

        urls, source = _read_webhook_urls(config)
        if not urls:
            print("[notify] ✗ no webhook URL")
            print("[notify]   設定方法：python notify_discord.py --set-webhook URL [--verify]")
            print("[notify]   或：python notify_discord.py --add-webhook URL  # broadcast 多頻道")
            print("[notify]   或：export PROMPTQUEUE_DISCORD_WEBHOOK=URL")
            return 1
        pending = sum(1 for s in states.values() if s.get("status") == "pending")
        in_prog = sum(1 for s in states.values() if s.get("status") in ("claimed", "in_progress"))
        done = sum(1 for s in states.values() if s.get("status") == "done")
        state = _load_state()
        done_contexts = _collect_done_contexts(events, state.get("last_notify_event_seq", -1))
        if not done_contexts:
            all_done = _collect_done_contexts(events, -1)
            if all_done:
                done_contexts = all_done[-2:]
        stats = {"pending": pending, "in_progress": in_prog, "done": done}
        content, embeds = _build_payload(stats, done_contexts, config)
        qi_username, qi_avatar = _resolve_queue_idle_identity(done_contexts, config)
        
        # prepend a test prefix to content to indicate force test
        if content.startswith("🎯"):
            content = "🧪 **[Force Send Test]** " + content[1:]
        else:
            content = "🧪 **[Force Send Test]** " + content
            
        any_ok, results = _send(urls, content, username=qi_username, avatar_url=qi_avatar, embeds=embeds)
        for uid, ok, err in results:
            print(f"[notify]   {uid}: {'OK' if ok else err}")
        print(f"[notify] force send → any_ok={any_ok} (source={source})")
        return 0 if any_ok else 1

    # ---- 一般跑：依 --mode dispatch 各 stream（互斥鎖 + trigger coalescing — TOCTOU/convoy fix）----
    rc = 0
    with _MirrorRunLock() as aLock:
        if not aLock.acquired and not aLock.bypass and _MIRROR_LOCK_PATH.exists():
            # 有健康 holder 在跑 → 留 pending flag 秒退（holder 收尾前會補跑一輪撿走本次觸發的訊息）
            try:
                _MIRROR_PENDING_PATH.touch()
            except OSError:
                pass
            print("[notify] busy — coalesced into running holder (pending flag set)")
            return 0
        for _pass in range(3):   # holder 補跑上限，防極端持續觸發下無限循環
            try:
                _MIRROR_PENDING_PATH.unlink(missing_ok=True)
            except OSError:
                pass
            if args.mode in ("queue-idle", "all"):
                sent, reason = notify_if_queue_idle(states, events, config)
                print(f"[notify queue-idle] sent={sent}, reason={reason}")
            if args.mode in ("tavern", "all"):
                n_sent, reason = notify_tavern_messages(config)
                print(f"[notify tavern] sent={n_sent}, reason={reason}")
                # T-TREASURY (Tim 2026-07-15 拍板方案 C): treasury ledger pull adapter 跟 tavern stream 同 run
                n_sent, reason = notify_treasury_entries(config)
                print(f"[notify treasury] sent={n_sent}, reason={reason}")
            if args.mode in ("wake", "all"):
                n_sent, reason = notify_wake_signals(config)
                print(f"[notify wake] sent={n_sent}, reason={reason}")
            if not _MIRROR_PENDING_PATH.exists():
                break
            print(f"[notify] pending flag 偵測到後到觸發 — 補跑 pass {_pass + 2}")
    return rc


if __name__ == "__main__":
    import sys
    sys.exit(main())
