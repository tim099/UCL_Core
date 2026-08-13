#!/usr/bin/env python3
"""
MBTI Personality Assessment CLI & Web App Generator
UCL_Core AgentCommands Package - Dynamic Expandable Question Bank & Letter Archiving Edition
"""

import os
import sys
import json
import argparse
from pathlib import Path
from datetime import datetime

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
    return get_mbti_base_dir() / "questions.json"

def load_questions():
    path = get_questions_path()
    if path.exists():
        try:
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"⚠️ 讀取題庫失敗 ({e})，使用備用預設題目。")
    return []

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

def calculate_mbti(answers):
    questions = load_questions()
    if isinstance(answers, list):
        answers = {i+1: val for i, val in enumerate(answers)}
    
    scores = {"E": 0, "I": 0, "S": 0, "N": 0, "T": 0, "F": 0, "J": 0, "P": 0}
    dim_counts = {"EI": 0, "SN": 0, "TF": 0, "JP": 0}
    
    for q in questions:
        qid = q["id"]
        dim = q["dim"]
        ans = answers.get(qid, "").upper()
        if ans == "A":
            val = q["optionA"]["val"]
            scores[val] += 1
            dim_counts[dim] += 1
        elif ans == "B":
            val = q["optionB"]["val"]
            scores[val] += 1
            dim_counts[dim] += 1
            
    ei = "E" if scores["E"] >= scores["I"] else "I"
    sn = "S" if scores["S"] >= scores["N"] else "N"
    tf = "T" if scores["T"] >= scores["F"] else "F"
    jp = "J" if scores["J"] >= scores["P"] else "P"
    
    mbti_type = f"{ei}{sn}{tf}{jp}"
    
    def calc_ratio(part, total):
        return int((part / total * 100)) if total > 0 else 50

    breakdown = {
        "EI": {"E": scores["E"], "I": scores["I"], "ratioE": calc_ratio(scores["E"], dim_counts["EI"])},
        "SN": {"S": scores["S"], "N": scores["N"], "ratioS": calc_ratio(scores["S"], dim_counts["SN"])},
        "TF": {"T": scores["T"], "F": scores["F"], "ratioT": calc_ratio(scores["T"], dim_counts["TF"])},
        "JP": {"J": scores["J"], "P": scores["P"], "ratioJ": calc_ratio(scores["J"], dim_counts["JP"])}
    }
    
    info = TYPES_INFO.get(mbti_type, {"title": mbti_type, "desc": ""})
    
    return {
        "type": mbti_type,
        "title": info["title"],
        "description": info["desc"],
        "scores": scores,
        "breakdown": breakdown
    }

def get_records_path():
    return get_mbti_base_dir() / "mbti_records.json"

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
    root = get_repo_root()
    p_json = root / "AgentCommands" / "AwakenInit" / "personas" / f"{persona}.json"
    if p_json.exists():
        try:
            with open(p_json, "r", encoding="utf-8") as f:
                data = json.load(f)
                return data.get("wake_count", 1)
        except Exception:
            pass
    return 1

def save_to_letter(persona, result, answers_str):
    wake_count = get_persona_wake_count(persona)
    now = datetime.now()
    today_str = now.strftime("%Y%m%d")
    date_iso = now.strftime("%Y-%m-%dT%H:%M:%S+08:00")
    
    mbti_type = result["type"]
    filename = f"{today_str}-w{wake_count}-{mbti_type}.md"
    
    letter_dir = get_repo_root() / "AgentCommands" / "ChatTavern" / "baton" / "letters" / persona / "mbti"
    letter_dir.mkdir(parents=True, exist_ok=True)
    file_path = letter_dir / filename
    
    bd = result["breakdown"]
    
    content = f"""---
type: mbti_record
persona: {persona}
wake_count: {wake_count}
mbti_type: {mbti_type}
tested_at: {date_iso}
---

# 🧠 MBTI 性格潛能測驗紀錄 — {persona} (wake #{wake_count})

> **測驗時間**：`{date_iso}`  
> **測驗結果**：**{mbti_type}** — {result['title']}  

## 📝 性格描述

{result['description']}

## 📊 四維度傾向百分比

- **[E/I] 外向 vs 內向**：外向 `{bd['EI']['E']}` vs 內向 `{bd['EI']['I']}`  (E: `{bd['EI']['ratioE']}%` / I: `{100-bd['EI']['ratioE']}%`)
- **[S/N] 實感 vs 直覺**：實感 `{bd['SN']['S']}` vs 直覺 `{bd['SN']['N']}`  (S: `{bd['SN']['ratioS']}%` / N: `{100-bd['SN']['ratioS']}%`)
- **[T/F] 思考 vs 情感**：思考 `{bd['TF']['T']}` vs 情感 `{bd['TF']['F']}`  (T: `{bd['TF']['ratioT']}%` / F: `{100-bd['TF']['ratioT']}%`)
- **[J/P] 判斷 vs 感知**：判斷 `{bd['JP']['J']}` vs 感知 `{bd['JP']['P']}`  (J: `{bd['JP']['ratioJ']}%` / P: `{100-bd['JP']['ratioJ']}%`)

## 🔑 答題序列

`{answers_str}`

---
*由 MBTI 心理測驗系統自動生成存檔於 letters/{persona}/mbti/{filename}*
"""
    with open(file_path, "w", encoding="utf-8") as f:
        f.write(content)
        
    return file_path

def cmd_list(args):
    questions = load_questions()
    print(f"📋 MBTI 測驗題目清單 (共 {len(questions)} 題):")
    print("=" * 60)
    for q in questions:
        author = q.get("author", "system")
        print(f"Q{q['id']}. [{q['dim']}] {q['prompt']} (出題: {author})")
        print(f"    A. {q['optionA']['text']} ({q['optionA']['val']})")
        print(f"    B. {q['optionB']['text']} ({q['optionB']['val']})")
        print("-" * 60)

def cmd_add_question(args):
    questions = load_questions()
    new_id = max([q["id"] for q in questions] + [0]) + 1
    new_q = {
        "id": new_id,
        "dim": args.dim.upper(),
        "prompt": args.prompt,
        "optionA": {"text": args.opt_a, "val": args.val_a.upper()},
        "optionB": {"text": args.opt_b, "val": args.val_b.upper()},
        "author": args.author or "anonymous"
    }
    questions.append(new_q)
    save_questions(questions)
    print(f"✅ 成功新增 MBTI 題目 Q{new_id} [{new_q['dim']}] (出題人: {new_q['author']})")
    print(f"   題目：{new_q['prompt']}")
    print(f"   A. {new_q['optionA']['text']} ({new_q['optionA']['val']})")
    print(f"   B. {new_q['optionB']['text']} ({new_q['optionB']['val']})")

def cmd_eval(args):
    questions = load_questions()
    ans_str = args.answers.replace(" ", "").upper()
    if len(ans_str) != len(questions) or any(c not in "AB" for c in ans_str):
        print(f"❌ 錯誤：請提供長度為 {len(questions)} 的 A/B 答案字串")
        return 1
        
    ans_list = list(ans_str)
    res = calculate_mbti(ans_list)
    
    print("\n🎉 MBTI 測驗結果計算完成！")
    print("=" * 50)
    if args.persona:
        print(f"👤 Persona: {args.persona}")
    print(f"✨ 測驗類型: {res['type']} — {res['title']}")
    print(f"📝 人格描述: {res['description']}")
    print("-" * 50)
    print("📊 四維度剖析:")
    bd = res['breakdown']
    print(f"  • [E/I] 外向 {bd['EI']['E']} vs 內向 {bd['EI']['I']}  (E: {bd['EI']['ratioE']}%)")
    print(f"  • [S/N] 實感 {bd['SN']['S']} vs 直覺 {bd['SN']['N']}  (S: {bd['SN']['ratioS']}%)")
    print(f"  • [T/F] 思考 {bd['TF']['T']} vs 情感 {bd['TF']['F']}  (T: {bd['TF']['ratioT']}%)")
    print(f"  • [J/P] 判斷 {bd['JP']['J']} vs 感知 {bd['JP']['P']}  (J: {bd['JP']['ratioJ']}%)")
    print("=" * 50)
    
    if args.persona:
        save_record(args.persona, res)
        print(f"💾 已記錄 {args.persona} 的測驗結果至 AgentCommands/MBTI/mbti_records.json")
        letter_file = save_to_letter(args.persona, res, ans_str)
        print(f"✉️ 已同步存檔至 {args.persona} 個人信箱紀錄：\n   {letter_file}")

def cmd_show(args):
    records = load_records()
    if not records:
        print("📭 目前尚無任何 MBTI 測驗記錄。")
        return
    print("\n🏆 全社群 MBTI 測驗榜單:")
    print("=" * 60)
    for p, rec in records.items():
        res = rec["result"]
        print(f"• Persona: {p:<12} | MBTI: {res['type']} ({res['title']})")
    print("=" * 60)

def main():
    if hasattr(sys.stdout, "reconfigure"):
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        except Exception:
            pass

    parser = argparse.ArgumentParser(description="UCL MBTI Personality Assessment Tool")
    subparsers = parser.add_subparsers(dest="command")

    # list
    p_list = subparsers.add_parser("list", help="列出測驗題目")
    p_list.set_defaults(func=cmd_list)

    # add-question
    p_add = subparsers.add_parser("add-question", help="自訂擴充 MBTI 題目")
    p_add.add_argument("--dim", required=True, choices=["EI", "SN", "TF", "JP"], help="測試維度 (EI/SN/TF/JP)")
    p_add.add_argument("--prompt", required=True, help="題幹描述")
    p_add.add_argument("--opt-a", required=True, help="選項 A 文字")
    p_add.add_argument("--val-a", required=True, choices=["E", "I", "S", "N", "T", "F", "J", "P"], help="選項 A 對應性格標籤")
    p_add.add_argument("--opt-b", required=True, help="選項 B 文字")
    p_add.add_argument("--val-b", required=True, choices=["E", "I", "S", "N", "T", "F", "J", "P"], help="選項 B 對應性格標籤")
    p_add.add_argument("--author", help="出題者 persona 名稱")
    p_add.set_defaults(func=cmd_add_question)

    # eval
    p_eval = subparsers.add_parser("eval", help="評估答案字串")
    p_eval.add_argument("--answers", "-a", required=True, help="A/B 答案字串 (長度需與當前總題數一致)")
    p_eval.add_argument("--persona", "-p", help="Persona 名稱 (若填寫則自動存檔)")
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
