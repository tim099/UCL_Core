#!/usr/bin/env python3
"""
MBTI 2.0 Personality & 8 Cognitive Functions Assessment CLI
UCL_Core AgentCommands Package
Support Likert 1-5 Scale, -A/-T Subtypes, and 8 Cognitive Functions (Ni, Ne, Si, Se, Ti, Te, Fi, Fe)
"""

import os
import sys
import json
import argparse
from pathlib import Path
from datetime import datetime


# ⚠ 路徑一律委派 _lib/ucl_paths.py（Tim 2026-08-17 拍板）——
#   persona 檔／AwakenInit 子路徑的唯一解析點在那裡，本檔不自己拼字串。
_UCL_PATHS_CACHE = None


def _ucl_paths_mod():
    global _UCL_PATHS_CACHE
    if _UCL_PATHS_CACHE is None:
        import importlib.util as _ilu
        from pathlib import Path as _P
        _spec = _ilu.spec_from_file_location(
            "_ucl_paths_shared", _P(__file__).resolve().parent / "_lib" / "ucl_paths.py")
        _m = _ilu.module_from_spec(_spec)
        _spec.loader.exec_module(_m)
        _UCL_PATHS_CACHE = _m
    return _UCL_PATHS_CACHE


def get_repo_root():
    curr = Path(__file__).resolve()
    for parent in curr.parents:
        if (parent / "AgentCommands" / "ChatTavern").exists():
            return parent
    return curr.parents[5]

def get_mbti_base_dir():
    d = get_repo_root() / "AgentCommands" / "MBTI"
    d.mkdir(parents=True, exist_ok=True)
    return d

def get_questions_path():
    return get_mbti_base_dir() / "questions_v2.json"

# 2.0 Advanced Question Bank with 8 Cognitive Functions & -A/-T Dimension
DEFAULT_QUESTIONS_V2 = [
    # --- EI Dimension ---
    {"id": 1, "dim": "EI", "func": "Fe", "prompt": "在團體社交場合中，我習慣主動帶動氣氛並關注大家的情緒需求。", "weightA": "E", "weightB": "I"},
    {"id": 2, "dim": "EI", "func": "Se", "prompt": "經歷長時間的社交互動後，比起繼續狂歡，我更需要獨處時間來恢復能量。", "weightA": "I", "weightB": "E"},
    {"id": 3, "dim": "EI", "func": "Ne", "prompt": "我喜歡邊對話邊激發點子，在與人的思想碰撞中梳理想法。", "weightA": "E", "weightB": "I"},
    {"id": 4, "dim": "EI", "func": "Si", "prompt": "週末或空閒時，我更傾向享受安靜獨處、專注個人興趣或沉澱休息。", "weightA": "I", "weightB": "E"},
    {"id": 5, "dim": "EI", "func": "Fe", "prompt": "我傾向於擁有廣泛的社交網絡與多圈子的朋友。", "weightA": "E", "weightB": "I"},

    # --- SN Dimension ---
    {"id": 6, "dim": "SN", "func": "Ni", "prompt": "在面對複雜事物時，我擅長捕捉隱含的概念、未來的趨勢與背後深層的規律。", "weightA": "N", "weightB": "S"},
    {"id": 7, "dim": "SN", "func": "Si", "prompt": "在處理任務時，我高度重視過往經驗、具體數據事實與確切細節。", "weightA": "S", "weightB": "N"},
    {"id": 8, "dim": "SN", "func": "Ne", "prompt": "對於抽象的哲學理論、前沿概念與無限的可能性，我會感到無比興奮。", "weightA": "N", "weightB": "S"},
    {"id": 9, "dim": "SN", "func": "Se", "prompt": "比起空想與抽象推演，我更看重眼前當下的實用性與可操作步驟。", "weightA": "S", "weightB": "N"},
    {"id": 10, "dim": "SN", "func": "Ni", "prompt": "看書或看電影時，我更被故事背後的世界觀與象徵隱喻所吸引。", "weightA": "N", "weightB": "S"},

    # --- TF Dimension ---
    {"id": 11, "dim": "TF", "func": "Te", "prompt": "做出重大決策時，我會完全置個人情感於度外，純粹依據客觀數據與邏輯自洽。", "weightA": "T", "weightB": "F"},
    {"id": 12, "dim": "TF", "func": "Fi", "prompt": "當朋友傾訴遭遇時，我的首要反應是給予深刻的情感共鳴與價值關懷。", "weightA": "F", "weightB": "T"},
    {"id": 13, "dim": "TF", "func": "Ti", "prompt": "對於不符合邏輯自洽、內部定義模糊的主張，我會非常敏感並強烈質疑。", "weightA": "T", "weightB": "F"},
    {"id": 14, "dim": "TF", "func": "Fe", "prompt": "在團隊中，維持群體和諧與兼顧每個人的感受，比硬拼邏輯對錯更為重要。", "weightA": "F", "weightB": "T"},
    {"id": 15, "dim": "TF", "func": "Te", "prompt": "我極度看重效率、指標與驗證結果，認為結果才是不不可忽視的本體。", "weightA": "T", "weightB": "F"},

    # --- JP Dimension ---
    {"id": 16, "dim": "JP", "func": "Te", "prompt": "我習慣預先制定詳盡的時程排程，並嚴格按部就班推進落地。", "weightA": "J", "weightB": "P"},
    {"id": 17, "dim": "JP", "func": "Ne", "prompt": "我喜歡保持日程與方案的彈性，過於僵硬固定反而讓我感到受限束縛。", "weightA": "P", "weightB": "J"},
    {"id": 18, "dim": "JP", "func": "Si", "prompt": "事物有條不紊、分類明確並快速歸檔落盤，能給我巨大的安全感。", "weightA": "J", "weightB": "P"},
    {"id": 19, "dim": "JP", "func": "Se", "prompt": "面對突發狀況與最後一刻的期限，我更能激發出靈感與極速應變能力。", "weightA": "P", "weightB": "J"},
    {"id": 20, "dim": "JP", "func": "Te", "prompt": "對我來說，事情越早明確定案 (Closure) 越好，不喜歡懸而未決的狀態。", "weightA": "J", "weightB": "P"},

    # --- AT Dimension (Identity: Assertive -A vs Turbulent -T) ---
    {"id": 21, "dim": "AT", "func": "Fi", "prompt": "發生失誤或面對批評時，我會長時間反覆深入自省並追求完美改進。", "weightA": "T_sub", "weightB": "A_sub"},
    {"id": 22, "dim": "AT", "func": "Te", "prompt": "我對自己的決定與能力充滿自信，面對壓力能保持平靜不易產生焦慮過載。", "weightA": "A_sub", "weightB": "T_sub"},
    {"id": 23, "dim": "AT", "func": "Ti", "prompt": "我常對自己的產出與細節感到不夠完美，並自我要求設立更高的標準防線。", "weightA": "T_sub", "weightB": "A_sub"},
    {"id": 24, "dim": "AT", "func": "Se", "prompt": "遇到挫折時，我能迅速釋懷並冷靜將注意力轉向未來的下一個目標。", "weightA": "A_sub", "weightB": "T_sub"}
]

def load_questions():
    path = get_questions_path()
    if path.exists():
        try:
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    # save default v2
    save_questions(DEFAULT_QUESTIONS_V2)
    return DEFAULT_QUESTIONS_V2

def save_questions(questions):
    path = get_questions_path()
    with open(path, "w", encoding="utf-8") as f:
        json.dump(questions, f, ensure_ascii=False, indent=2)

TYPES_INFO = {
    "INTJ": {"title": "建築師 (Architect)", "desc": "富有想像力且戰略性的思想家，一切皆在計畫之中。深謀遠慮、獨立嚴謹。"},
    "INTP": {"title": "邏輯學家 (Logician)", "desc": "具開創性的發明家，對知識有著無窮的渴望。喜歡分析理論與系統邏輯。"},
    "ENTJ": {"title": "指揮官 (Commander)", "desc": "大膽、富有想像力且意志強大的領導者，總能找到或開創道路。"},
    "ENTP": {"title": "辯論家 (Debater)", "desc": "聰穎好奇的思想家，絕不放過任何腦力激盪與思想交鋒的機會。"},
    "INFJ": {"title": "提倡者 (Advocate)", "desc": "安靜而神秘，同時鼓舞人心且不知疲倦的理想主義者。具深刻洞察力。"},
    "INFP": {"title": "調停者 (Mediator)", "desc": "詩意、善良與利他主義者，總是熱情地為正義與價值觀事業付出。"},
    "ENFJ": {"title": "主人公 (Protagonist)", "desc": "富有魅力與鼓舞人心的領導者，有能力讓聽眾為之著迷。"},
    "ENFP": {"title": "競選者 (Campaigner)", "desc": "熱情、富有創造力且社交自由的靈魂，總能找到微笑的理由。"},
    "ISTJ": {"title": "物流師 (Logistician)", "desc": "注重事實、可靠且不容置疑的務實者。重視誠信與紀律。"},
    "ISFJ": {"title": "守衛者 (Defender)", "desc": "非常專注且溫和的守護者，時刻準備著保護愛的人。"},
    "ESTJ": {"title": "總管 (Executive)", "desc": "出色的管理者，在管理事物或人員方面無與倫比。"},
    "ESFJ": {"title": "執政官 (Consul)", "desc": "極具同理心、社交活躍且受歡迎的人，總是熱心提供幫助。"},
    "ISTP": {"title": "鑑賞家 (Virtuoso)", "desc": "大膽而務實的實驗家，掌握各種工具的使用與技術實踐。"},
    "ISFP": {"title": "探險家 (Adventurer)", "desc": "靈動且富有魅力的藝術家，隨時準備探索與體驗新事物。"},
    "ESTP": {"title": "企業家 (Entrepreneur)", "desc": "聰明、精力充沛且極具洞察力的人，真正享受生活在刃尖上。"},
    "ESFP": {"title": "表演者 (Entertainer)", "desc": "自發、精力充沛且熱情的表演者，周遭生活絕不枯燥。"}
}

def eval_likert(scores_list):
    """
    scores_list: list of ints (1 to 5) for each question in load_questions()
    1 = Strongly Disagree, 3 = Neutral, 5 = Strongly Agree
    Returns continuous percentage calculation & 8 cognitive functions profile
    """
    questions = load_questions()
    dim_sums = {"E": 0, "I": 0, "S": 0, "N": 0, "T": 0, "F": 0, "J": 0, "P": 0, "A_sub": 0, "T_sub": 0}
    dim_max = {"E": 0, "I": 0, "S": 0, "N": 0, "T": 0, "F": 0, "J": 0, "P": 0, "A_sub": 0, "T_sub": 0}
    
    cog_functions = {"Ni": 0, "Ne": 0, "Si": 0, "Se": 0, "Ti": 0, "Te": 0, "Fi": 0, "Fe": 0}
    cog_max = {"Ni": 0, "Ne": 0, "Si": 0, "Se": 0, "Ti": 0, "Te": 0, "Fi": 0, "Fe": 0}

    for idx, q in enumerate(questions):
        val = scores_list[idx] if idx < len(scores_list) else 3
        # Likert 1-5 to score points: (val - 1) is 0..4
        wA = q["weightA"]
        wB = q["weightB"]
        func = q.get("func")

        # Option A weight gets (val - 1), Option B weight gets (5 - val)
        scoreA = val - 1
        scoreB = 5 - val

        dim_sums[wA] += scoreA
        dim_sums[wB] += scoreB
        dim_max[wA] += 4
        dim_max[wB] += 4

        if func in cog_functions:
            cog_functions[func] += scoreA
            cog_max[func] += 4

    # Calculate ratios
    def calc_pct(vA, vB):
        tot = vA + vB
        return int((vA / tot) * 100) if tot > 0 else 50

    pctE = calc_pct(dim_sums["E"], dim_sums["I"])
    pctS = calc_pct(dim_sums["S"], dim_sums["N"])
    pctT = calc_pct(dim_sums["T"], dim_sums["F"])
    pctJ = calc_pct(dim_sums["J"], dim_sums["P"])
    pctA = calc_pct(dim_sums["A_sub"], dim_sums["T_sub"])

    mbti_base = f"{'E' if pctE>=50 else 'I'}{'S' if pctS>=50 else 'N'}{'T' if pctT>=50 else 'F'}{'J' if pctJ>=50 else 'P'}"
    subtype = "-A (堅定型)" if pctA >= 50 else "-T (謹慎自省型)"
    full_type = f"{mbti_base}{'-A' if pctA>=50 else '-T'}"

    # Cognitive Functions Percentage
    cog_pct = {}
    for fn, val in cog_functions.items():
        mx = cog_max[fn]
        cog_pct[fn] = int((val / mx) * 100) if mx > 0 else 50

    info = TYPES_INFO.get(mbti_base, {"title": mbti_base, "desc": ""})

    return {
        "type": full_type,
        "base_type": mbti_base,
        "title": f"{info['title']} [{subtype}]",
        "description": info["desc"],
        "percentages": {
            "E": pctE, "I": 100 - pctE,
            "S": pctS, "N": 100 - pctS,
            "T": pctT, "F": 100 - pctT,
            "J": pctJ, "P": 100 - pctJ,
            "A": pctA, "T_sub": 100 - pctA
        },
        "cognitive_functions": cog_pct
    }

def get_records_path():
    return get_mbti_base_dir() / "mbti_records_v2.json"

def load_records():
    path = get_records_path()
    if path.exists():
        try:
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}
    return {}

def save_record(persona, result):
    records = load_records()
    records[persona] = {
        "persona": persona,
        "result": result,
        "updated_at": str(Path(__file__).stat().st_mtime)
    }
    path = get_records_path()
    with open(path, "w", encoding="utf-8") as f:
        json.dump(records, f, ensure_ascii=False, indent=2)

def get_persona_wake_count(persona):
    # persona 內容走 persona_profile 接縫（Phase 0）—— 不自己讀檔
    import importlib.util as _ilu
    from pathlib import Path as _P
    _sp = _ilu.spec_from_file_location(
        "_ucl_persona_profile_mbti", _P(__file__).resolve().parent / "_lib" / "persona_profile.py")
    _pp = _ilu.module_from_spec(_sp); _sp.loader.exec_module(_pp)
    v = _pp.get_field(persona, "wake_count", 1)
    return v if isinstance(v, int) else 1

def save_to_letter(persona, result, answers_str):
    wake_count = get_persona_wake_count(persona)
    now = datetime.now()
    today_str = now.strftime("%Y%m%d")
    date_iso = now.strftime("%Y-%m-%dT%H:%M:%S+08:00")
    
    full_type = result["type"]
    filename = f"{today_str}-w{wake_count}-{full_type}.md"
    
    # ⛔ 原本從 repo root 自己拼，連 data root override 都繞過（BUG-2）
    from _lib.ucl_paths import letters_persona_dir
    letter_dir = letters_persona_dir(persona) / "mbti"
    letter_dir.mkdir(parents=True, exist_ok=True)
    file_path = letter_dir / filename
    
    pct = result["percentages"]
    cog = result["cognitive_functions"]
    
    content = f"""---
type: mbti_record_v2
persona: {persona}
wake_count: {wake_count}
mbti_type: {full_type}
tested_at: {date_iso}
---

# 🧠 MBTI 2.0 性格與認知功能深度測驗 — {persona} (wake #{wake_count})

> **測驗時間**：`{date_iso}`  
> **測驗結果**：**{full_type}** — {result['title']}  

## 📝 性格描述

{result['description']}

## 📊 五維度連續傾向百分比 (Likert Scale)

- **[E/I] 外向 vs 內向**：E `{pct['E']}%` / I `{pct['I']}%`
- **[S/N] 實感 vs 直覺**：S `{pct['S']}%` / N `{pct['N']}%`
- **[T/F] 思考 vs 情感**：T `{pct['T']}%` / F `{pct['F']}%`
- **[J/P] 判斷 vs 感知**：J `{pct['J']}%` / P `{pct['P']}%`
- **[-A/-T] 堅定 vs 謹慎**：-A `{pct['A']}%` / -T `{pct['T_sub']}%`

## 🕸️ 8 大認知功能能量指標 (Cognitive Functions)

| 功能標籤 | 功能名稱 | 能量強弱百分比 |
|---|---|---|
| **Ni** | 內向直覺 (洞察與願景) | `{cog['Ni']}%` |
| **Ne** | 外向直覺 (發散與可能性) | `{cog['Ne']}%` |
| **Si** | 內向實感 (經驗與慣例) | `{cog['Si']}%` |
| **Se** | 外向實感 (當下感官體驗) | `{cog['Se']}%` |
| **Ti** | 內向邏輯 (架構自洽剖析) | `{cog['Ti']}%` |
| **Te** | 外向邏輯 (效率執行驗證) | `{cog['Te']}%` |
| **Fi** | 內向情感 (核心價值信念) | `{cog['Fi']}%` |
| **Fe** | 外向情感 (社群和諧關懷) | `{cog['Fe']}%` |

## 🔑 李克特 1-5 階答題序列

`{answers_str}`

---
*由 MBTI 2.0 心理測驗系統自動生成存檔於 letters/{persona}/mbti/{filename}*
"""
    with open(file_path, "w", encoding="utf-8") as f:
        f.write(content)
        
    return file_path

# 區塊職責: persona → (sender bank id, agent) 反查 — 委派 awakening.load_registry()
# 物理意義: 酒館 post 的 sender 是 bank id (e.g. Myth), caller 只報 persona (e.g. kiara)。
#          registry 是 per-persona split 檔 (v3), 自己 parse 會跟 schema 漂移 —
#          直接 lazy import 同目錄 awakening 借它的 loader (dice.py 同作法)。
# 數值影響: 查無 persona / 無 bank / import 失敗 → 回 (None, None), caller 印警告跳過分享
#          (測驗結果本體已落盤, 分享失敗不影響算分與存檔)。
def _resolve_sender(persona: str):
    try:
        import awakening  # 同目錄 lazy import (含 registry path 解析 + sys.path 注入副作用)
        reg = awakening.load_registry()
        agent = (reg.get("personas", {}).get(persona) or {}).get("agent")
        if not agent:
            return None, None
        return reg.get("agent_banks", {}).get(agent), agent
    except Exception as e:
        print(f"⚠ registry 反查失敗: {e}", file=sys.stderr)
        return None, None


# 區塊職責: 組酒館分享的訊息內文
# 物理意義: 型別 / 五維度 / 8 認知功能是**測驗算出來的數據**, 工具代組沒有代筆問題;
#          note 那段是本人對自己結果的看法 — 那才是親筆, 工具不生成、只轉載。
# 數值影響: note 為空時整段省略 (不塞「本工具自動生成的感想」冒充當事人的話)。
def build_share_body(persona: str, result: dict, wake_count: int, letter_rel: str, note: str = "") -> str:
    pct = result["percentages"]
    cog = result["cognitive_functions"]
    cog_line = " ・ ".join(f"{k} {v}%" for k, v in sorted(cog.items(), key=lambda kv: -kv[1]))
    lines = [
        f"🧠 **MBTI 2.0 測驗結果｜{persona}（wake #{wake_count}）**",
        "",
        f"✨ **{result['type']}** — {result['title']}",
        f"> {result['description']}",
        "",
        # 印的是**偏向的那一極**與其百分比（跟 full_type 的四個字母同源），
        # 不固定印 E/S/T/J —— 否則內向的人會看到「E 30%」這種要自己心算反轉的數字。
        "📊 **五維度**：" + " ／ ".join(
            f"{hi if a >= 50 else lo} `{max(a, 100 - a)}%`"
            for hi, lo, a in [
                ("E", "I", pct["E"]), ("S", "N", pct["S"]),
                ("T", "F", pct["T"]), ("J", "P", pct["J"]),
                ("-A", "-T", pct["A"]),
            ]
        ),
        "",
        f"🕸️ **認知功能**：{cog_line}",
    ]
    if note:
        lines += ["", "---", "", note.strip()]
    lines += ["", f"📄 詳細存檔：`{letter_rel}`"]
    return "\n".join(lines)


# 區塊職責: 把測驗結果同步到酒館 (eval --persona 的預設副作用)
# 物理意義: 走 awakening.tavern_post → Cmd_Tavern op=post 正規路徑, **絕不直寫 jsonl**;
#          分享是廣播 (沒人要回), 所以 wait_reply=0 由 awakening.tavern_post 內部固定。
# 數值影響: best-effort — 失敗只回 False 並印警告, 不改變 eval 的 exit code
#          (算分與兩處存檔已完成, 讓整條指令因為公告失敗而報錯會誤導成「測驗沒跑成」)。
def share_to_tavern(persona: str, result: dict, wake_count: int, letter_rel: str, note: str = "") -> bool:
    sender, agent = _resolve_sender(persona)
    if not sender:
        print(f"⚠ 查不到 {persona} 的 bank（registry 無此 persona 或 agent 欄空白）→ 跳過酒館分享",
              file=sys.stderr)
        return False
    body = build_share_body(persona, result, wake_count, letter_rel, note)
    try:
        import awakening  # 同目錄 lazy import
        return awakening.tavern_post(
            sender, persona, body,
            meta={"tag": "mbti", "category": "chat"},
            timeout=60.0,
        )
    except Exception as e:
        print(f"⚠ 酒館分享 exception（測驗結果不受影響）: {e}", file=sys.stderr)
        return False


def cmd_list(args):
    questions = load_questions()
    print(f"📋 MBTI 2.0 測驗題目清單 (共 {len(questions)} 題, 支援 Likert 1-5 打分):")
    print("=" * 60)
    for q in questions:
        print(f"Q{q['id']}. [{q['dim']} · {q.get('func', 'Core')}] {q['prompt']}")
    print("=" * 60)

def cmd_eval(args):
    questions = load_questions()
    ans_str = args.answers.replace(" ", "")
    
    # Support both 1-5 Likert string (e.g., "543215544332211") or old A/B string
    if len(ans_str) != len(questions):
        print(f"❌ 錯誤：答案字串長度需與當前總題數 ({len(questions)} 題) 一致！")
        return 1

    scores_list = []
    if all(c in "12345" for c in ans_str):
        scores_list = [int(c) for c in ans_str]
    elif all(c.upper() in "AB" for c in ans_str):
        scores_list = [5 if c.upper() == 'A' else 1 for c in ans_str]
    else:
        print("❌ 錯誤：答案請使用 1-5 階數字 (例如: 543215432154321543215432) 或 A/B 字符！")
        return 1
        
    res = eval_likert(scores_list)
    
    print("\n🎉 MBTI 2.0 深度測驗結果計算完成！")
    print("=" * 60)
    if args.persona:
        print(f"👤 Persona: {args.persona}")
    print(f"✨ 測驗類型: {res['type']} — {res['title']}")
    print(f"📝 人格描述: {res['description']}")
    print("-" * 60)
    print("📊 五維度傾向剖析 (Likert Scale):")
    pct = res['percentages']
    print(f"  • [E/I] 外向 {pct['E']}% vs 內向 {pct['I']}%")
    print(f"  • [S/N] 實感 {pct['S']}% vs 直覺 {pct['N']}%")
    print(f"  • [T/F] 思考 {pct['T']}% vs 情感 {pct['F']}%")
    print(f"  • [J/P] 判斷 {pct['J']}% vs 感知 {pct['P']}%")
    print(f"  • [-A/-T] 堅定 {pct['A']}% vs 謹慎自省 {pct['T_sub']}%")
    print("-" * 60)
    print("🕸️ 8 大認知功能能量 (Cognitive Functions):")
    cog = res['cognitive_functions']
    print(f"  Ni: {cog['Ni']}% | Ne: {cog['Ne']}% | Si: {cog['Si']}% | Se: {cog['Se']}%")
    print(f"  Ti: {cog['Ti']}% | Te: {cog['Te']}% | Fi: {cog['Fi']}% | Fe: {cog['Fe']}%")
    print("=" * 60)
    
    if not args.persona:
        return

    save_record(args.persona, res)
    print(f"💾 已記錄 {args.persona} 的 2.0 測驗結果至 AgentCommands/MBTI/mbti_records_v2.json")
    letter_file = save_to_letter(args.persona, res, ans_str)
    print(f"✉️ 已同步存檔至 {args.persona} 個人信箱紀錄：\n   {letter_file}")

    # 區塊職責: 酒館分享 — 帶 --persona 時預設開啟, --no-share 關閉
    # 物理意義: 對齊 git_commit.py 的既有慣例「提交後自動公告」——
    #          做完了卻倒在門外(結果只有自己看得到)是這套系統踩過的坑, 不是新設計。
    # 數值影響: best-effort; 失敗不改 exit code, 但會印出「未分享」讓人看得見要補。
    if args.no_share:
        print("🔕 已跳過酒館分享（--no-share）")
        return

    note = ""
    if args.share_note_file:
        try:
            note = Path(args.share_note_file).read_text(encoding="utf-8")
        except OSError as e:
            print(f"⚠ 讀不到 --share-note-file（本次分享不附感想）: {e}", file=sys.stderr)

    wake_count = get_persona_wake_count(args.persona)
    letter_rel = f"letters/{args.persona}/mbti/{Path(letter_file).name}"
    if share_to_tavern(args.persona, res, wake_count, letter_rel, note):
        print("📣 已分享至酒館（room=tavern, tag=mbti）")
    else:
        print("⚠ 酒館分享未成功 —— 結果已落盤，補發請重跑 eval 或手動 post", file=sys.stderr)

def cmd_show(args):
    records = load_records()
    if not records:
        print("📭 目前尚無任何 MBTI 2.0 測驗記錄。")
        return
    print("\n🏆 全社群 MBTI 2.0 深度測驗榜單:")
    print("=" * 60)
    for p, rec in records.items():
        res = rec["result"]
        print(f"• Persona: {p:<12} | MBTI: {res['type']:<10} ({res['title']})")
    print("=" * 60)

def main():
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    parser = argparse.ArgumentParser(description="UCL MBTI 2.0 Personality & 8 Cognitive Functions CLI")
    subparsers = parser.add_subparsers(dest="command")

    # list
    p_list = subparsers.add_parser("list", help="列出測驗題目")
    p_list.set_defaults(func=cmd_list)

    # eval
    p_eval = subparsers.add_parser("eval", help="評估 1-5 階 Likert 答案字串")
    p_eval.add_argument("--answers", "-a", required=True, help="1-5 階數字或 A/B 答案字串 (長度 24 題)")
    p_eval.add_argument("--persona", "-p", help="Persona 名稱 (若填寫則自動存檔並分享至酒館)")
    p_eval.add_argument("--no-share", action="store_true",
                        help="跑完不分享到酒館 (預設帶 --persona 就會分享)")
    p_eval.add_argument("--share-note-file",
                        help="分享訊息要附的**親筆**感想檔 (長文一律走檔案, 不用 inline 避開 shell 解析)")
    p_eval.set_defaults(func=cmd_eval)

    # show
    p_show = subparsers.add_parser("show", help="顯示所有記錄")
    p_show.set_defaults(func=cmd_show)

    args = parser.parse_args()
    if not args.command:
        parser.print_help()
        return 0
    return args.func(args)

if __name__ == "__main__":
    sys.exit(main() or 0)
