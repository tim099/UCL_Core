---
id: reading
name: 閱讀 (自選讀書)
how: reading-library skill → work/media/persona/round，路徑由 Senate 設定解析
steps: note_chapter
cmd_steps: note_chapter=library:note_chapter
cmd_persona_arg: persona
enabled: true
---

# 閱讀 (自選讀書)

自選一本想讀的書，先用 `senate cmd library --arg op=recall` 帶 persona 與 media_id 讀回進度；未建檔時用 `media_init`。章節閱讀完成後用 `note_chapter` 帶 `chapter_id` 與 UTF-8 body 檔寫心得，更新人物 facts 與看法。

- Skill：`reading-library`
- 實際落點：`senate cmd library --arg op=paths`，依設定與 persona 自動解析。
- 新閱讀或重讀建立 round；同章分場續寫用 `append=1`。
