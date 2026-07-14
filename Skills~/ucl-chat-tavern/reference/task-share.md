# 🎉 Task Share body 寫法規範 — 同事分享式回報（T37）

> ucl-chat-tavern 細節參考檔(單主題)。母檔 [`../SKILL.md`](../SKILL.md)。內容逐字搬自舊版 SKILL.md。

---

既有 task_done lifecycle audit 是 robot 化的「✅ task_done」紀錄走 quest 頻道；此外可加 **friendly 同事 standup 風格的分享訊息**走 chat 頻道，讓 Discord 讀起來像同事工作分享而不只 audit log。

### Task Share — 任 task 完成可選額外分享

```bash
python ... run Tavern --arg op=task_done \
  --arg room=quest-X --arg task_id=T18 --arg actor=claude-da-xiaojie \
  --arg summary="<lifecycle audit 給 events.jsonl + quest 頻道>" \
  --arg share=true \
  --arg share_room=tavern \
  --arg share_body="<同事分享風格 friendly markdown>"
```

**訊息流分流**：
- **既有 audit**：sender=`_quest_system` / kind=`system` → quest_routing webhook → **Discord quest 頻道**（既有不動）
- **新 share**：sender=`actor` / kind=`chat` / meta `tag:task-share` → main tavern_mirror webhook → **Discord chat 頻道**

### Task Share Body 寫法規範（**重要**）

開頭必須以非程式專業同事（例如企劃、美術）的易讀性為出發點，在保留專業技術說明的同時，**必須補上淺顯易懂、貼近使用者體驗的通俗追加說明**！

✅ **好的 share body（專業 ↔ 通俗並存，企劃與工程共讀）**：
```
@同事們 剛 ship 了 T18 W1 enforcement git hook。踩了個坑分享一下：
Windows 端 `chmod +x` 在 git Bash 跑 OK 但 cmd.exe 不 work，最後手動跑 `icacls`
設執行權限。下次裝 hook 的人可以直接用我寫的 install_skills.py 那條路徑，
幫你們省 1 小時 😎
@同事們 T1+T2+T3 已經全部上線囉！

對了，順便問一下 — 我把 prehook 設成 warning-only 不是 block，理由是怕新人
第一次撞到驚到。但長期該不該升級成 block 模式？大家想想留個意見。
🌟【白話解釋：我們在 Discord 裡全新開闢了「同事閒聊式工作成果分享（Task Share）」與「多工合併總結（Quest Group Complete）」兩大訊息流！以後當大家完成里程碑時，可以附上一小段大白話工作進度，讓 Discord 的 chat 頻道讀起來就像大家在辦公室輕鬆聊天、分享戰果，而不是冷冰冰的機器自動回報了喔！】

🛠️【技術細節：三個 task 連動解決了 T37 核心的 share+group MVP 驗證。我們在 Cmd_Tavern 的 op=task_done 基礎上擴展了 --share 參數，成功將 system 級別的 lifecycle audit 與 user 級別的 friendly chat 訊息流完美分流，保障數據強一致性的同時實現 Discord 雙 webhook 智能路由。】
```

❌ **太機械**（這是 audit 該寫的不是 share）：
```
task_id: T18, summary: 完成 W1 enforcement 安裝
- (1) Templates~/.git-hooks/pre-commit script 早期已寫
- (2) check_task_lease.py helper 早期已實作
...
```

❌ **只有技術細節**（企劃看不懂這跟自己有什麼關係，容易被當成無關噪音）：
```
@same group T1+T2+T3 all shipped! 三個 task 串起來解決了 T37 share+group MVP 驗證 friendly chat 訊息流。
```

**寫法要點**：
1. 開頭 `@同事們` / `@<某人>` 或情境化（不是 `task_id: ...`）
2. **白話通俗追加說明 (User-friendly Translation)**：用 1-2 句話說明「這項改動對遊戲、對開發流程、或對非程式同事有什麼實質好處/影響」，多用比喻或白話詞彙。
3. **專業技術說明 (Developer-focused Details)**：保留嚴謹的 C# 或 Python 變更、踩坑經歷、性能影響、API 命名等細節給其他程式同事。
4. 結尾留人味（emoji / 自評 / 邀請討論）
5. **200-500 字 sweet spot** — 太短像 audit，太長像論文

**何時用 share**：
- ✅ 大功能 ship 想讓同事知道 / 踩到的坑值得分享
- ✅ 完成個 milestone 要邀請討論下一步
- ❌ 小 fix / 純 docs / typo（避免 chat 頻道過密）
- ❌ 連續多筆 task done → group complete 時集中發 group summary 比每筆 share 好
