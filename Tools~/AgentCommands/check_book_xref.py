#!/usr/bin/env python3
# 區塊職責：書內指路牌（cross-reference）檢查 —— 章號引用是否指到對的章、§ 引用是否存在。
# 物理意義：章節重編號之後，正文裡「回顧 ch6」這種指路牌不會自己跟著動，而它壞掉時
#          **讀起來完全通順** —— 失效樣子是讀者翻到錯的章，沒有任何一層會喊。
#          （TASK-0124：全書 8 格章號平移三個月沒有人發現，抓到它的是一次人工全書 review。）
# 數值影響：唯讀，一個字都不改；有 violation 回 exit 1。
# 設計取捨：
#   - **權威表是書自己的「章節對照表」**（ch12 §1 的 markdown 表格），不是寫死在本檔的清單 ——
#     寫死的那份會在改版時安靜過期，而那正是本檔要抓的那隻病。
#   - 兩層檢查：① 章號 ↔ 章名　② `chN §M` 的 § 在目標檔裡存不存在。
#     只比章名的第一層抓不到「章對 § 錯」那一族（TASK-0124 §H）。
#   - **原書（CB）的章號不比對**：判準是「標題裡沒有中日文字 ⇒ 那是英文原書的章名」，
#     再加上同一行出現 原書 / CB / 《Writing Effective Use Cases》 就跳過。
#   - `--selftest` 是**陽性對照**：塞一筆確定壞的引用進去，確認本工具真的會喊 ——
#     一個永遠回 0 violation 的檢查，跟一本沒有錯的書長得一模一樣。
import argparse
import os
import re
import sys

CJK = re.compile(r'[㐀-鿿぀-ヿ]')
REF_TITLE = re.compile(r'ch(\d+)\s*(?:「|『)([^」』]+)(?:」|』)')
REF_SECTION = re.compile(r'ch(\d+)\s*§\s*(\d+(?:\.\d+)?)')
TABLE_ROW = re.compile(r'^\|\s*(序|ch\d+)\s*\|\s*([^|]+?)\s*\|')
SECTION_HEAD = re.compile(r'^##\s*§\s*(\d+(?:\.\d+)?)')
# ⚠ 這裡刻意**不放**單獨的「CB」——「CB 的形式變異光譜本小姐留到 ch5「三種寫法」」這種句子
#   是在講**本書**的章，而句首有 CB ⇒ 拿 CB 當跳過條件會製造假陰（實測漏掉 001.txt:220 那格）。
#   英文原書的章名本來就沒有中日文字，那條判準已經夠用。
ORIGINAL_HINTS_TITLE = ('原書', 'Writing Effective Use Cases', 'ISBN')
# § 引用這一層要**更寬的跳過條件**：CB 的 §2.1 / Figure 2 這種指路也寫成 `ch2 §2.1`，
# 而它跟本書的 `ch2 §4` 在字面上一模一樣 —— 唯一分得出來的線索就在同一行的上下文。
ORIGINAL_HINTS_SECTION = ORIGINAL_HINTS_TITLE + ('CB', 'PART 1', 'PART1', 'Figure')


def norm(s):
    """把標題壓成可比對的形狀：去空白與標點、英文轉小寫。"""
    s = s.lower()
    return re.sub(r'[\s\-—–_:：,，、。()（）\[\]「」『』+*#]', '', s)


def load_chapters(book_dir):
    """檔名 000.txt … 012.txt ⇒ {章號: 路徑}，序章＝0。"""
    out = {}
    for name in sorted(os.listdir(book_dir)):
        m = re.fullmatch(r'(\d{3})\.txt', name)
        if m:
            out[int(m.group(1))] = os.path.join(book_dir, name)
    return out


def load_authority(chapters):
    """從書自己的「章節對照表」讀權威章名（表在最後一章）。"""
    if not chapters:
        return {}, None
    last = chapters[max(chapters)]
    table = {}
    with open(last, encoding='utf-8') as f:
        for line in f:
            m = TABLE_ROW.match(line)
            if not m:
                continue
            key, title = m.group(1), m.group(2)
            if title.strip() in ('主題', '---'):
                continue
            idx = 0 if key == '序' else int(key[2:])
            table[idx] = title.strip()
    for idx, path in chapters.items():
        if idx in table:
            continue
        with open(path, encoding='utf-8') as f:
            head = f.readline().strip()
        m = re.match(r'第[^\s]+章\s*[—–-]\s*(.+)', head)
        if m:
            table[idx] = m.group(1).strip()
    return table, last


def titles_match(ref_title, authority_title):
    a, b = norm(ref_title), norm(authority_title)
    if not a or not b:
        return False
    if a in b or b in a:
        return True
    sa, sb = set(a), set(b)
    return len(sa & sb) / max(1, len(sa | sb)) >= 0.4


def check(book_dir, extra_lines=None):
    chapters = load_chapters(book_dir)
    authority, table_file = load_authority(chapters)
    if not authority:
        return [('-', 0, 'FATAL', '找不到章節對照表（最後一章的 markdown 表格）—— 本工具沒有權威來源，拒絕回 0 violation')], {}

    sections = {}
    for idx, path in chapters.items():
        with open(path, encoding='utf-8') as f:
            sections[idx] = {SECTION_HEAD.match(l).group(1) for l in f if SECTION_HEAD.match(l)}

    violations = []
    stats = {'title_refs': 0, 'section_refs': 0, 'skipped_original': 0}
    for idx, path in sorted(chapters.items()):
        with open(path, encoding='utf-8') as f:
            lines = f.read().splitlines()
        if extra_lines and idx in extra_lines:
            lines = lines + extra_lines[idx]
        for lineno, line in enumerate(lines, 1):
            is_original_title = any(h in line for h in ORIGINAL_HINTS_TITLE)
            is_original_section = any(h in line for h in ORIGINAL_HINTS_SECTION)
            for m in REF_TITLE.finditer(line):
                target, title = int(m.group(1)), m.group(2)
                if not CJK.search(title) or is_original_title:
                    stats['skipped_original'] += 1
                    continue
                stats['title_refs'] += 1
                want = authority.get(target)
                if want is None:
                    violations.append((os.path.basename(path), lineno, 'ch 不存在',
                                       f'指向 ch{target}，而對照表沒有這一章'))
                elif not titles_match(title, want):
                    violations.append((os.path.basename(path), lineno, '章名不符',
                                       f'書上寫 ch{target}「{title}」，對照表的 ch{target} 是「{want}」'))
            for m in REF_SECTION.finditer(line):
                target, sec = int(m.group(1)), m.group(2)
                if is_original_section:
                    stats['skipped_original'] += 1
                    continue
                stats['section_refs'] += 1
                if target not in sections:
                    violations.append((os.path.basename(path), lineno, 'ch 不存在',
                                       f'§ 引用指向 ch{target}，而書裡沒有這一章'))
                elif sec not in sections[target]:
                    have = '／'.join(f'§{s}' for s in sorted(sections[target], key=float)) or '（無）'
                    violations.append((os.path.basename(path), lineno, '§ 不存在',
                                       f'指向 ch{target} §{sec}，而 ch{target} 只有 {have}'))
    return violations, stats


def render(violations, stats, book_dir):
    print(f'# 📐 book xref check — {book_dir}')
    print(f'- 章名引用 {stats.get("title_refs", 0)} 筆 ／ § 引用 {stats.get("section_refs", 0)} 筆'
          f' ／ 跳過（原書 CB 的章號）{stats.get("skipped_original", 0)} 筆')
    if not violations:
        print('- ✅ **0 violation**')
        return 0
    print(f'- ❌ **{len(violations)} violation**')
    for f, ln, kind, msg in violations:
        print(f'    · {f}:{ln}　[{kind}] {msg}')
    return 1


def selftest(book_dir):
    """陽性對照：餵一筆確定壞的引用，本工具必須喊；不喊就是尺壞了。"""
    bad = {0: ['（selftest）回顧 ch1「三種寫法」與 ch1 §99 —— 這兩格都是假的']}
    violations, _ = check(book_dir, extra_lines=bad)
    kinds = {v[2] for v in violations if v[0] == '000.txt'}
    ok = '章名不符' in kinds and '§ 不存在' in kinds
    print(f'# 🧪 selftest（陽性對照）：{"✅ 兩層都喊了" if ok else "❌ 沒喊 —— 這把尺量不到東西"}')
    for v in violations:
        if v[0] == '000.txt':
            print(f'    · {v[0]}:{v[1]}　[{v[2]}] {v[3]}')
    return 0 if ok else 2


def main():
    ap = argparse.ArgumentParser(description='書內章號／§ 指路牌檢查（唯讀）')
    ap.add_argument('book_dir', help='書的資料夾（含 000.txt…）')
    ap.add_argument('--selftest', action='store_true', help='陽性對照：確認本工具抓得到壞引用')
    a = ap.parse_args()
    if not os.path.isdir(a.book_dir):
        print(f'✗ 不是資料夾：{a.book_dir}', file=sys.stderr)
        return 2
    if a.selftest:
        return selftest(a.book_dir)
    violations, stats = check(a.book_dir)
    return render(violations, stats, a.book_dir)


if __name__ == '__main__':
    sys.exit(main())
