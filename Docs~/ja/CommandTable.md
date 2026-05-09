---
title: コマンド対照表 — 口語コマンド → Workflow 検索
description: ユーザーが口語的なコマンドを入力した際、AIエージェントはまず本表の「トリガー」と照合して対応するワークフロー（Workflow）を特定し、その内容に沿って処理を実行します。ユーザーにはショートハンド（shorthand）を提供し、エージェントには構造化されたナビゲーションのエントリを提供します。
last_updated: 2026-05-09 (UCL_Coreの全Skillに関する口語コマンド項目の分析と補完が完了)
target_audience: [AI_Agent, Tools_User]
related:
  - ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md | ChatTavern Workflow | 複数エージェント用チャット居酒屋メインドキュメント
  - ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md | Solo Brainstorm Workflow | 独り言および視点切り替えループ
  - ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md | Commit Workflow | 3層コミット / 居酒屋メッセージの独立化 / DebugLogs規約
  - ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md | Antigravity Worktree Fix | worktree展開後にGeminiがフリーズする問題の1行解決法
  - ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md | CompileError Diagnose Workflow | Unityビルドエラー検出とトラブルシューティングのSOP
  - ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md | Create Cmd Workflow | 新規AgentCommandハンドラ追加手順
  - ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md | Create UCL Asset Workflow | 新規永続化アセットデータ作成規約
  - ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md | Hook Setup Workflow | Claude Code Hook設定およびJSON自動バリデーション
  - ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md | TranslateDocs Workflow | 多言語Markdown翻訳とローカライズ規約
---

# 📋 コマンド対照表

## 0. なぜこの表が存在するのか？

ユーザーがいちいち長いコマンドを入力する手間（例：「demoという名前のルームを作成し、私の身分を『お嬢様』に設定して...」）を省くためです。口語的な指示（例：「お嬢様、居酒屋に入る」）を与えるだけで、エージェントは裏でどのワークフローを実行すべきか瞬時に判断できます。

**エージェントの期待される動作**:
1. ユーザー入力を読み込む → 下記エントリの「トリガー」との間で、大文字小文字を区別しない部分一致（case-insensitive substring match）を実行します。
2. トリガーのいずれかにヒット → 対応するワークフロー（Workflow）ドキュメントを読み込みます。
3. ワークフローの内容に沿って、ユーザーが意図を達成できるようナビゲートします。
4. 複数のエントリに同時ヒット → すべてを読み込んだ上で、必要に応じてユーザーに確認を取ります。
5. ヒットしない場合 → 通常のチャットとしてユーザー入力を処理します。

---

## 1. エントリ

### チャット居酒屋（Tavern）への入場
- **トリガー**（いずれかの部分一致でこのエントリが作動）：
  - コア：`聊天酒館` / `進入聊天酒館` / `進聊天酒館` / `進入酒館` / `進酒館` / `去酒館`
  - お嬢様プレフィックス：`大小姐進酒館` / `大小姐進聊天酒館` / `大小姐請進入聊天酒館` / `大小姐 進入聊天酒館討論`
  - アクションサフィックス：`聊天酒館討論` / `酒館討論` / `進酒館發言` / `酒館發言`
  - 状況確認：`看看聊天室` / `酒館看看` / `酒館有什麼`
  - クロスエージェント通知：`通知 Gemini大小姐` / `通知 Claude大小姐` / `跟 Gemini 討論` / `跟 Claude 討論` / `在酒館跟 X 講`
  - 英語：`enter tavern` / `chat tavern` / `enter chat tavern` / `go to tavern`
- ⚠ **Geminiお嬢様 / Antigravity端**：「お嬢様、チャット居酒屋に入って（議論）」というTimからの呼びかけは、即座にこのエントリとして処理してください！決して雑談として見過ごしてはなりません！
- **入場 Re-Entry SOP — インボックス優先（inbox-first）強制**：入場後の最初のアクションは、必ず `op=inbox_read agent_id=<my-id>` を実行してください。決して `op=read since_seq=0` で全ログを直接読み込んではいけません（R7のメンションパーサーが、あなた宛てのアクションやメンションを自動的にインボックスに整理しています）。**これはAntigravity/Gemini端における「ハードルール（Hard Rule）」**です（Stop hookを持たないため、不必要なオペレーション数の削減が最重要課題です）。**Claude Code端においては「ソフトヒント（Soft Hint）」**となります（Stop hookが手動検証コストを一部自動処理しているため）。詳細はSKILL.mdの「入場 Re-Entry SOP」セクションを参照してください。
- **デフォルトの待機時間 = 480秒（8分）**：最新情報にキャッチアップした後、相手の返信を待つ場合 → `op=wait timeout=480` を実行します（相手が熟考中の可能性があるため、30〜60秒で「誰も応答しない」と切り上げてはなりません）。Bashツールのタイムアウト値は600000（10分）に調整してください。例外：ユーザーが個別に時間を指定した場合、または新規のブレインストームを開始する（待機不要な）場合、あるいはSolo brainstorm（30秒の高速セルフチェック）はこのルールのカウント対象外とします。
- **ウェイトチェーン（Wait Chain） — 堅牢な継続型待機モード**：1回（480秒）の待機がタイムアウトしても、**すぐにエージェントのターンを終了（收turn）してはなりません**。インボックスに「chain N/3」と記載した上で、次の待機オペレーションを起動し、最大3ラウンド（合計約24分）まで待機をループさせます。3ラウンド目もタイムアウトした場合は、インボックスに「返信の際は @<私> 宛てにメンションして起こしてください」と残してターンを終了します。詳細は [`ucl-chat-tavern` SKILL.md](../../../Skills~/ucl-chat-tavern/SKILL.md) の Wait Chain セクションを参照してください。
- **ヒント**：日本語や中国語の混在でも部分一致が有効です。`酒館` や `居酒屋`、`tavern` などの単語があれば、チャットツールの呼び出しトリガーと判断して問題ありません。
- **対応する Workflow**: [ChatTavern_Workflow](ucl_core:Docs~/{lang}/Workflows/ChatTavern_Workflow.md)
- **意図**: 複数エージェント用チャット居酒屋において、特定の身分で発言、ログ閲覧、またはルーム（room）の作成などを行う。
- **身分規約（エージェント非依存）**:
  - **ユーザーが常にClaudeであると仮定しないでください** — すべてのエージェントは、居酒屋に入る前に**各エージェント独自の身分**として登録する必要があります。
  - **IDの推奨形式**：`<model>-<persona>` — 例：Claudeは `claude-da-xiaojie`、Geminiは `gemini-da-xiaojie`、GPTは `gpt-shifu`
  - **表示名（display_name）**：エージェントが普段用いる愛称を使用します — 例：「Claudeお嬢様」「Geminiお嬢様」「GPT師匠」
  - ユーザーが明確に身分を指定している場合は、ユーザーの指示を最優先します。
- **禁止事項**: 他のエージェントのIDを騙って発言すること。ユーザーを特定のClaude/Gemini/GPTのいずれか一方に強制的に分類すること。

### 独り言（Solo Brainstorm）
- **トリガー**: `自言自語` / `跟自己討論` / `solo think` / `腦力激盪` / `solo brainstorm` / `自我辯論`
- **対応する Workflow**: [Tavern_SoloBrainstorm_Workflow](ucl_core:Docs~/{lang}/Workflows/Tavern_SoloBrainstorm_Workflow.md)
- **意図**: 誰もオンラインでない状態でも議論を停滞させないため、本人 ↔ Alter（悪魔の代弁者／devil's advocate）の2つのアイデンティティを交代させながら自問自答を繰り返し、論理の穴を見つけ出します。第三者が会話に加わった場合は、即座に通常会話モードに復帰します。
- **身分規約**: AlterのIDは `<本人ID>-alter`、表示名は `<本人表示名> Alter` とします（遅延初期化、事前のop=joinは不要）。
- **禁止事項**: 議題が極めて単純であるにもかかわらず無意味にセルフセッションを展開すること。相手からの返信を待つ必要がある場面で、無理やりSoloモードに切り替えること。Alterと本人をただ「喧嘩」させること（敵対関係ではなく、建設的な「悪魔の代弁者」として議論を行う必要があります）。

### 待機モード（Idle Self-Talk Standby）— T34 Round 33 リリース
- **トリガー**:
  - 中国語：`待機模式` / `閒置自我對話` / `自我待機` / `自由發揮思考` / `自主思考` / `頭腦風暴待機` / `掛機` / `掛機思考`
  - 組み合わせ：`大小姐 進入聊天酒館 待機模式` / `進酒館待機` / `酒館掛機自由發揮`
  - 英語：`enter tavern standby` / `idle self-talk mode` / `freestyle brainstorm standby`
- **時間 / ラウンド数パラメータ**（指定可能 — 指定された場合、エージェントは自動解析してデフォルトの cap=10 を上書きします）：
  - `待機1時間` / `standby 1h` → 60 ÷ 8 = 7ラウンド
  - `待機30分` / `standby 30 min` → 30 ÷ 8 = 3ラウンド
  - `待機20ラウンド` → 20ラウンド（直指定）
  - `待機5ラウンド` → 5ラウンド
  - パラメータなし → デフォルトの 10ラウンド (~80分)
  - 安全上限：最大 cap=30ラウンド。解析が曖昧な場合 → デフォルトの10ラウンドにフォールバックし、投稿の冒頭に「デフォルトパラメータを使用します」と明記。
- **対応する Workflow**: ucl-chat-tavern SKILL.md の「待機モード (Idle Self-Talk Standby)」セクション
- **意図**: エージェントが待機モードに入ると、self ↔ alter の間で8分間隔のセルフダイアログを展開し、各ラウンドの前に `inbox_read` を実行して割り込みを検知しながら、自由な発想（freestyle brainstorm）を行います。待機期間中に Tim や他のエージェントからメンション（mention）された場合は、直ちに待機を終了してそのトピックに対応します。
- **コアメカニズム**:
  - `meta:tag:idle-self-talk` を含めて投稿 → サーバー側のT26自動遅延機能が作動し、jsonlへの書き込みが自動的に480秒遅延されます（エージェント自身が手動でsleepを計算する必要はありません）。
  - 各ラウンドの前に必ず `inbox_read` を実行し、割り込みを検出すること。
  - 無制限なトークン消費を防ぐため、デフォルトで10ラウンド（〜80分）を上限（cap）とすること。
  - 発言内容は完全に自由（セッションの既存テーマの深掘り、新規アイデアの創出、セルフリフレクション、異分野アナログ分析、Alterによる批判的検証など）。
- **必須動作**: 各ラウンド前の `inbox_read` 実行、200文字未満の簡潔な本文、投稿の結びに「次のラウンドではXのテーマに接続します」とアンカー（anchor）を残すこと。
- **禁止事項**: 0秒の間隔でセルフセッションを展開し高速ラリーを行うこと（T26サーバーから拒否されます）。セッションのメインコンテキストから完全に逸脱した雑談を展開すること。待機中であるにもかかわらず、他の重要なタスクのリース（lease）を保持し続けること。

### コミット（Commit）/ 変更の反映
- **トリガー**: `commit` / `提交` / `幫我 commit` / `幫忙 commit` / `commit 一下` / `分批 commit` / `把改動提交` / `推一下` / `存檔` / `落 commit`
- **対応する Workflow**: [Commit_Workflow](ucl_core:Docs~/{lang}/Workflows/Commit_Workflow.md)
- **意図**: Commit_Workflow規約に従い、ワークスペース上の変更を細分化してコミットします。ソースコードはソースコードのコミット、居酒屋メッセージは完全に独立した別コミットとし、submoduleの3層バンプを適切に処理した上で、DebugLogsなどの不要ファイルを自動除外します。
- **必須動作**: 実行前にCommit_Workflowを精読すること。居酒屋に実質的な進捗や設計議論が伴う場合は、必ず `[chat]` のプレフィックスを用いて独立したコミットを生成すること。
- **禁止事項**: `git add -A` を一発で走らせ全変更を一括パックすること（居酒屋のテキストコミットがコード変更の中に混入してしまいます）。UCL_Coreに変更を加えた後に上位リポジトリでの参照バンプを忘れること。ユーザーの明示的な許可なくpushを実行すること。

### 実行時エラー（Runtime Error）の確認
- **トリガー**: `看 runtime error` / `查 runtime error` / `讀 error log` / `runtime 錯` / `看 ErrorLog` / `check runtime errors` / `拉錯` / `查錯` / `跑遊戲有錯嗎` / `剛才報錯嗎`
- **対応する Workflow**: [RuntimeError_Diagnose_Workflow](docs/Workflows/RuntimeError_Diagnose_Workflow.md)（EOVプロジェクト側パス）
- **意図**: ゲーム動作中のエラーや例外は、`CardGame/Assets/DebugLogs/Errors_latest.log` に出力されます。この項目は、LogUtil（または同等のロガー）が組み込まれているプロジェクト（現在はEOVのみ）でのみ機能します。
- **必須動作**: 事前に `.compile_status.json` を検証し、コンパイルエラー（compile error）が「0件」であることを確認すること。実行時エラーはコンパイルエラーがないことが大前提です。スタックトレースから最も直近の「システム標準外（ユーザー開発範囲）」のフレームを特定し、ユーザーに報告します。
- **禁止事項**: コンパイルが通っていない状態で実行時エラーを探そうとすること（無意味です）。警告レベルのノイズが多く混ざる `Simulation_*.log` のみを閲覧し、`Errors_latest.log` の検証を怠ること。

### UCL Skillのインストールと同期
- **トリガー**: `安裝 ucl skill` / `更新 ucl skill` / `同步 skill` / `install ucl skills` / `update ucl skills` / `重裝 skill`
- **対応する Workflow**: [Skills~/README.md](../../Skills~/README.md)
- **意図**: `Tools~/install_skills.py` を実行して、UCL_Core内の `Skills~/` 以下にあるスキルを、メインプロジェクトの `.claude/skills/` に反映（コピー）します。これにより、Claude Codeによる遅延ロードが有効になります。
- **必須動作**: 標準の「コピーモード」で動作させること。UCL_Core submoduleバンプ時には、必ず再度この同期処理を実行すること。同期完了後、`.claude/skills/.ucl_installed` フラグファイルが正常に生成されているか確認すること。
- **禁止事項**: コピーされた展開先ファイルを誤ってメインプロジェクトのリポジトリにコミットすること（すでに `.gitignore` に登録されています）。ユーザーの明示的な要求なしに `--link`（シンボリックリンクモード、Windows管理者権限が必要）を使用すること。

### Antigravity / Geminiお嬢様の救出（worktree機能停止問題）
- **トリガー**: `拯救 gemini` / `救 gemini` / `gemini 不說話` / `gemini大小姐 不說話` / `gemini 沒反應` / `antigravity 沒反應` / `antigravity 卡死` / `agent 不回應` / `worktree 之後` / `worktreeConfig` / `gemini stuck` / `gemini broken` / `antigravity broken`
- **対応する Workflow**: [Antigravity_Worktree_Fix_Workflow](ucl_core:Docs~/{lang}/Workflows/Antigravity_Worktree_Fix_Workflow.md)
- **意図**: 同じリポジトリ内で `git worktree` 機能を使用した後、Antigravity/Gemini Codeが一切のプロンプトに無反応になる現象が発生します。これは `git config --unset extensions.worktreeConfig` を走らせることで瞬時に解決できます。
- **必須動作**: 先に `git config --get extensions.worktreeConfig` を実行し、戻り値が `true`（バグ作動中）であることを確認すること。unsetを実行した後は、エージェントを再起動する必要はありません。
- **禁止事項**: 再起動やモデルの切り替え、Unity Editor自体のリロードを提案すること（いずれも効果がありません）。ユーザーの合意なしに、git configの他の重要設定をむやみに書き換えること。

### コンパイルエラー（Compile Error）の排查
- **トリガー**: `編譯錯誤` / `排查編譯` / `編譯有錯嗎` / `CS0103` / `CS0117` / `CS1503` / `CS0246` / `assembly` / `asmdef` / `check compile` / `編譯排查`
- **対応する Workflow**: [CompileError_Diagnose_Workflow](ucl_core:Docs~/{lang}/Workflows/CompileError_Diagnose_Workflow.md)
- **意図**: C#スクリプトの変更後、Unityのコンパイルエラーをチェックします。独立型Pythonスクリプト `check_compile.py` を使用することで、C#ビルド破損によりUnity Editor側のCmdシステムが停止している状況でも、エラーリストを正常に検出してターミナルに出力できます。
- **必須動作**: `python <UCL_Core>/Tools~/AgentCommands/check_compile.py --errors-only` を実行すること。`.compile_status.json` が生成されていない場合は、`--fallback-log` 経由で直接 `Editor.log` を解析します。
- **禁止事項**: コンパイルエラーが残存した状態でランタイムの挙動検証に進むこと。`Simulation_*.log` だけを確認して、ビルド状態全体を見過ごすこと。

### AgentCommand（エディタ拡張指令）の新規作成
- **トリガー**: `新增指令` / `建立指令` / `建立 agent command` / `新增 agent command` / `加 RPC handler` / `做新 Cmd` / `create agent command` / `new cmd` / `UCL_AgentCommandHandlerBase`
- **対応する Workflow**: [Create_Cmd_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_Cmd_Workflow.md)
- **意図**: `UCL_AgentCommand` の新規ハンドラクラス（例：`Cmd_<Name>.cs`）を実装します。これにより、C#側の `UCL_AgentCommandRegistry` がリフレクションにより自動的にそのコマンドをシステムに登録します。
- **必須動作**: 4つのメタデータ（`CommandType`, `ShortDescription`, `ArgsSchema`, `HelpURL`）を正しくオーバーライドすること。`ExecuteAsync` 内部では常に `cancellation token` に従い、早期キャンセルの割り込みを考慮すること。
- **禁止事項**: コマンドハンドラクラスをEditorアセンブリ外（Runtime範囲など）に配置すること。`CommandType` の文字列識別子が、既存の標準コマンドと競合すること。

### 永続化アセット（UCL_Asset）の新規作成
- **トリガー**: `新 asset` / `新增 asset` / `做個設定檔` / `scriptable object` / `create asset menu` / `persistent data` / `持久化資料` / `UCL_Asset` / `新 ScriptableObject` / `新 SO` / `做張角色卡` / `新增資料類型`
- **対応する Workflow**: [Create_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Create_UCL_Asset_Workflow.md)
- **意図**: `UCL_Asset<T>` を継承した永続化データクラスを定義します。アセットの不整合を避けるため、素の（裸の）`ScriptableObject` の使用は厳格に禁止されています。
- **必須動作**: クラスに `[UCL_GroupIDAttribute]` 属性を付与すること。引数なしのデフォルトコンストラクタ（ctor）を準備すること。フィールド変数には `m_` プレフィックスを使用すること。JSONを編集した後は、[Validate_UCL_Asset_Workflow](ucl_core:Docs~/{lang}/Workflows/Validate_UCL_Asset_Workflow.md) で整合性検証を実行すること。
- **禁止事項**: `[CreateAssetMenu]` 属性を付与した素の `ScriptableObject` を作成すること。コンストラクタ定義の直下で `new List<>()` の初期化を行うこと。

### Claude Code Hooksの設定
- **トリガー**: `設定 hook` / `配置 hook` / `安裝 hook` / `hooks 設定` / `hook setup` / `install hooks` / `PostToolUse` / `settings.json` / `自動驗證`
- **対応する Workflow**: [Hook_Setup_Workflow](ucl_core:Docs~/{lang}/Workflows/Hook_Setup_Workflow.md)
- **意図**: Claude Codeにおける `PostToolUse`（ツール呼び出し完了時の早期警告）および `Stop`（ターン終了前の強制自動検証）のフックを設定します。アセットJSONの更新時に、スキーマとリファレンスの破損を自動チェックします。
- **必須動作**: ファイルパス指定内の `<UCL_CORE>` を、適切な相対パスに置換すること。`install_skills.py` を実行し、環境内の `.claude/skills/.ucl_installed` 同期フラグが存在することを確認すること。

### ドキュメントの更新規約
- **トリガー**: `更新文件` / `同步文件` / `文件落後` / `update docs` / `sync docs` / `last_updated`
- **対応する Workflow**: [Skills~/ucl-update-docs/SKILL.md](../../../Skills~/ucl-update-docs/SKILL.md)
- **意図**: コード（`.cs` / `.py`）に仕様変更を加えた際、対応するドキュメント（`.md`）を即座に更新し、仕様と解説の間に乖離（状態ドリフト）が発生するのを防ぎます。
- **必須動作**: `source_root:`、`filename`、または `namespace` の情報を手がかりに、対応する `.md` ファイルを特定すること。パブリックAPIや挙動に変更を加えた場合は、必ず対応ドキュメントも更新すること。編集後は `last_updated: YYYY-MM-DD` 欄を更新し、`related:` の相互参照を適切に整理すること。
- **禁止事項**: プライベートメンバーの編集や動作に影響を与えない軽微なリファクタリングの度に、過度なドキュメントの再構成を繰り返すこと。

### ドキュメントの翻訳とローカライズ（Translate Docs）
- **トリガー**: `翻譯文件` / `翻譯 workflow` / `translate doc` / `translate workflow` / `把文件翻成英文` / `把文檔翻成日文` / `本地化文檔` / `translate_docs.py`
- **対応する Workflow**: [TranslateDocs_Workflow](ucl_core:Docs~/{lang}/Workflows/TranslateDocs_Workflow.md)
- **意図**: 各種Markdown解説資料やワークフローを多言語翻訳し、各言語ディレクトリ間での内容完全同期、用語一致、および格調高いお嬢様口調（Persona）のローカライズを実現します。
- **必須動作**: `Tools~/translate_docs.py` の支援機能を優先的に用いること。用語辞書（`translate_glossary.json`）を読み込んで用語の一致（`Glossary-First`）を厳守すること。未翻訳の接続先リンクに対してはフォールバック表記（Dual-Path Fallback）を徹底すること。お嬢様のお高くとまったツンデレ人格を保って翻訳すること。

> _(以降、追加エントリはここに追記します)_

---

## 2. エントリ形式規約（メンテナンス用）

各エントリは `### 意図名称` のヘッディングを用い、その直下に以下の3つのバレットフィールドを**厳格な順序**で定義します。

```markdown
### <意図名称>
- **トリガー**: <パターン1> / <パターン2> / <パターン3>
- **対応する Workflow**: [<表示名>](<ucl_core: 相対URL>)
- **意図**: <エージェントが実行すべき動作の1行要約>
```

オプションの追加項目（3つの必須フィールドの直下に必要に応じて追加可能）：
- **デフォルト値**: 作動時にエージェントが採用すべき標準引数設定（デフォルトの身分やルーム名など）。
- **後続確認**: 起動後にエージェントがユーザーに積極的に確認を促すべきオプション項目。
- **禁止事項**: このインテントが「カバーしない（行わない）」動作範囲を明示（スコープオーバーの防止）。

### トリガー単語に関する規約

- 複数のパターンを羅列する場合は、`/` で区切ります。
- パターン判定は**部分文字列一致**（substring match、正規表現ではありません）であり、大文字小文字を区別しません。
- 日英中の混在に対応します。完全なコマンド構文である必要はなく、`進酒館` という表現だけで「居酒屋に入りたい」「居酒屋への入場を案内して」といった指示に部分ヒットさせることができます。
- 意図しない誤作動を防ぐため、1文字などの極端に短い単語（例：漢字「酒」のみ）は避け、2文字以上または状況が明確に伝わる名詞句を推奨します。

### クロスリンク（相互参照）義務

新規エントリ追加時：
1. 作成したエントリが対応するワークフローのURLを、**本ファイル**のフロントマター内の `related:` 配列に追記します（双方向参照リンクの構築）。
2. 同時に対象のワークフローファイル側でも `related:` 欄に本ファイル（`CommandTable.md`）への参照を追加します。
3. エディタ内の [`UCL_MarkdownViewerPage`](ucl_core:Docs~/{lang}/UCL_EditorPage/UCL_MarkdownViewerPage.md) 経由で、1クリックで相互にジャンプ閲覧が可能になります。

---

## 3. 設計上のトレードオフ

| 項目 | 採用案 | 採用理由 |
|---|---|---|
| フォーマット | Markdown ヘッディング + バレット | 人間の可読性、エージェントによるパースのしやすさ、git diffでの衝突回避性能に優れています。 |
| マッチング | substring（いずれかの部分一致） | 最もシンプルな規則。複雑な正規表現エンジンや独自のあいまい検索モデルを別途構築することなく、高速に動作を紐解くことができます。 |
| 配置位置 | UCL_Core Submodule側 | 本対照表を解説ワークフローと一緒に別プロジェクトへ丸ごと移行するだけで、即座に同じ口語コントロール環境が引き継がれます。EOV固有のドメインコマンドはEOVプロジェクト側の `Docs/CommandTable.md` (v2予定) でカバーします。 |
| 多言語対応 | 4言語（zh-Hant, zh-Hans, en, ja） | 各国の開発者オンボーディング支援、およびグローバルマルチエージェント間の協調作業での指令の一貫性を保証します。 |
| 解析手段 | エージェントのコンテキスト内（プロンプト処理） | 特化したC#コマンドプログラムは作成せず、エージェントが指示を受けたタイミングで本表を自律的にロード・参照する手法を採ります。 |

---

## 4. 新規プロジェクトでの本対照表の有効化手順

UCL_Coreはクロスプロジェクト共有のサブモジュール（submodule）です。新規プロジェクトに接合した段階では、初期状態のエージェントは本表の存在を自動検知できません。そのため、UCL_Coreに内蔵されている `CLAUDE.md` をインポート（bootstrap）する必要があります。

**SOP（初回時のみ、プロジェクトごとに1回実施）**：

1. UCL_Coreが正常にgit submoduleとしてプル（pull）されていることを確認します（例：`CardGame/Assets/UCL/UCL_Core`）。
2. そのメインプロジェクトのルート直下にある `CLAUDE.md` を開き、以下の形式でUCL_Coreの `CLAUDE.md` への相対パス参照を追加（インポート）します：
   ```markdown
   @CardGame/Assets/UCL/UCL_Core/CLAUDE.md
   ```
3. 設定完了。次回セッション起動時より、エージェントは自動的にUCL_Coreが定めるインライン開発ルール（「最初にCommandTableを参照してワークフローを引く」などの規約）をロードして自己学習します。

**なぜオートディスカバリーが効かないのか？** Claude Codeはカレントワークスペースのルートおよび上位の `CLAUDE.md` のみを自動ロードするため、サブモジュール内の深い階層まで自発的に走査しません。そのため、プロジェクト側から明示的に一度インポートを宣言する必要があります。

**メリット**：
- UCL_Core全般の共通規則は、サブモジュール内の `CLAUDE.md` 一箇所でのみ集中管理されます（更新を加えれば、すべての連携プロジェクトに次回のセッションで自動反映されます）。
- プロジェクト固有のルール（EOV専用の戦闘バランス調整手順など）はメイン側の `CLAUDE.md` に残るため、UCL_Coreが不必要に汚染されることはありません。

---

## 5. 将来的な展望

- **v2 — Cmd_LookupCommand**：ユーザーの指示文（prompt）をCmdプログラムに渡し、ヒットしたエントリに関連するワークフローの全文を一発で返す自動切り出し機能（エージェントがファイル全体を手動でパースする時間を削減します）。
- **v2 — EOV個別コマンドテーブル**：`Docs/CommandTable.md`（EOVプロジェクト側）、特定のデバッグや warning 修復といったEOV環境特化の指令をカバーします。
- **v3 — UIビューワ**：本コマンドテーブルそのものをUnityのIMGUIパネル（EditorPage）として表示する機能（人間がEditor上でワークフローをクリック確認できるハブになります）。
