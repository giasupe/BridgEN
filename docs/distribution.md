# BridgEN ベータ版の公開手順

## 1. Cloudflareの作成画面

GitHubでこのプロジェクトを保存しているリポジトリを選び、次を設定します。

- Project name: `bridgen-api`
- Build command: `npm run check`（型チェック。依存関係はCloudflareがpackage-lock.jsonからインストール）
- Deploy command: `npx wrangler deploy`
- Enable Preview builds: OFF
- Protect with Cloudflare Access: 現段階ではOFF。Worker自身で個別の招待トークンを確認します。
- Advanced settings / Path: `/server/worker`
- API token: `Create new token`
- API token name: `bridgen-deploy`
- ビルド用のVariable name/value: 空欄

`wrangler.jsonc`はWorkers Freeで使えるSQLite-backed Durable Objectを作成します。有料プランへの自動アップグレードは行いません。WorkersとDurable Objectsにはそれぞれ無料枠上限があり、上限到達時は利用できなくなります。Geminiの利用料は別です。

GitHubへこのコードをpushした後にDeployします。この段階ではSecretがなくてもデプロイできますが、翻訳は503で拒否します。生成APIは呼びません。

## 2. Gemini APIキーの登録

Workerを開き、Settings → Variables & Secrets → Addを選びます。

- Type: `Secret`
- Variable name: `GEMINI_API_KEY`
- Value: 管理者のGemini APIキー

値はCloudflareの設定画面へ直接入力します。リポジトリ、ビルド変数、配布ZIPへ入れません。

## 3. 個別の招待トークンを作る

Windowsでは、管理者本人のPCでリポジトリ直下の`create-invite.bat`を実行します。Windows PowerShellを使うためNode.jsは不要です。Codexから実行した際のユーザープロファイルや画面が、管理者のWindows環境と同じとは限りません。トークンは手元のPCで発行し、そこに表示された保存先を使ってください。

```powershell
.\create-invite.bat owner
```

このコマンドはランダムな256bitのトークンを作り、値自体をコンソールに出さず、保存先だけを表示してフォルダーを開きます。

`create-invite.bat`をダブルクリックするか、引数なしで`.\create-invite.bat`を実行すると、その場で任意のUserIDを入力できます。`owner`や`friend01`は例であり、固定された名前ではありません。半角英数字・`-`・`_`で1～64文字、先頭は半角英数字にします。空欄・無効な入力・登録済みのIDの場合は、説明を表示して入力をやり直せます。予約語の`constructor`と`prototype`は使えません。

コマンドで直接指定する場合は`.\create-invite.bat -UserId giasupe`のように指定します。引数で指定したIDが無効・登録済みなら、既存のトークンを変更せず終了します。

batは起動したWindows PowerShellプロセスだけに`-ExecutionPolicy RemoteSigned`を指定します。ローカルの発行スクリプトを実行するための指定で、CurrentUserやLocalMachineのポリシーを恒久変更しません。組織のグループポリシーがある場合はそちらが優先されます。

Node.jsを使う従来の方法も残しています。リポジトリの`server/worker`で`node scripts/create-invite.mjs owner`を実行します。このNode版は同じIDの再発行で古いトークンを置き換えるため、既存のものを見る目的では実行しないでください。

- `*.token.txt`: その利用者だけへ渡す実際のトークン。
- `*.registration.json`: 今回の利用者だけの登録データ。
- `invitation-registry.json`: 全利用者の登録データ。トークンのSHA-256と利用者IDだけを含みます。

既存の統合登録ファイルを更新するときは、前の内容を`invitation-registry-識別子.backup.json`へ保存します。Cloudflareへ登録するのは最新の`invitation-registry.json`です。

保存先は`%LOCALAPPDATA%\BridgEN\admin\invites`です。GitHubや配布ZIPには含まれません。

Workerにもう1つSecretを追加します。

- Type: `Secret`
- Variable name: `INVITE_TOKENS_JSON`
- Value: `invitation-registry.json`の内容全体

新しい利用者を追加した後も、統合済みの`invitation-registry.json`でSecretを更新します。利用者は最大32人、登録JSONは5,000バイト以内に制限しています。トークンを失効させる場合は登録JSONからそのハッシュの項目を削除し、Secretを更新します。ローカルに登録JSONがない状態から発行した場合、Cloudflareにだけ存在する登録は自動取得されません。既存の配布先を維持するには、Cloudflareの現在の登録内容と新しい登録を統合してからSecretを更新してください。

生のトークンをGemini APIキー欄へ入れたり、登録JSONをアプリの招待トークン欄へ入れたりしないでください。

## 4. 接続する

Workerの公開URLに`/health`を付けて開きます。`service: BridgEN`と`status: ok`が返れば配置されています。healthはSecretの登録状態までは判定しません。

現在の公開URLは`https://bridgen-api.gspe.workers.dev`、確認用URLは`https://bridgen-api.gspe.workers.dev/health`です。404と`error code: 1042`が返る場合は、まずSettings → Domains & Routesでworkers.devが有効か、Deploymentsに本番デプロイがあるか、URLが一致しているかを確認します。

BridgENの「翻訳の接続設定」で、中継サーバーを選び、WorkerのベースURL（`/health`や`/translate`を含めない）と`*.token.txt`の値を登録します。保存後にゲームのOCRを開始します。

サーバーは英語から日本語への翻訳だけを受け付けます。モデルとシステム指示はサーバー側で固定し、1回の英文は2,000文字、生成は最大2,048トークンです。受信は45秒で打ち切ります。正常終了していない訳を完成結果として保存しません。

## 利用上限と停止

`wrangler.jsonc`の初期値は、全体1,000回/日・60回/分、1人300回/日・20回/分です。1つのDurable Object内で予約を永続化してからGeminiへ進むため、異なる拠点からの同時利用でも全体上限を共有します。Durable Objectが使えない場合は翻訳も拒否します。

日次上限はUTC 00:00（日本時間09:00）で切り替わります。分単位の制限は固定の暦上の1分です。上限は料金ではなくリクエスト試行回数であり、入力長・出力長によりGeminiの費用は変わります。送信後のキャンセル・エラーも消費した扱いです。予算の厳密な金額保証ではありません。

上限変更はwrangler.jsoncの値を変えてpushします。緊急停止はWorkerの実行時変数`TRANSLATION_ENABLED`を文字列`false`へ設定します。再開はその変数を削除するか`true`へ変更します。これは認証を省略する設定ではありません。

Workerは本文・画像・生トークンを保存せず、コードからリクエスト本文をログ出力しません。運用途中でCloudflareのログ収集を追加する際にもAuthorizationや本文を記録しないでください。Google側の保持条件は別途利用中のAPIの設定・規約に従います。

## 5. Windows配布ZIP

リポジトリ直下から次を実行します。

```powershell
.\publish.bat
```

既定の公開URLは`src/BridgEN.App/bridgen.defaults.json`から読み込み、通常起動と配布ZIPの両方に反映されます。発行時だけ別の接続先にする場合は`.\scripts\publish-win.ps1 -RelayUrl https://別のWorker.workers.dev`を指定します。保存済みの接続先があればそちらを優先し、利用者が起動後に変更することもできます。

`artifacts/BridgEN-0.1.0-beta.2-win-x64.zip`と`.zip.sha256`が作られます。.NETを同梱し、`BridgEN.exe`と必要なDLL、公開URLだけの既定設定、利用説明を含みます。Geminiキーや招待トークンは含めません。WPFとWinRTの互換性のため初期版はトリミング・単一ファイル化を使いません。プロジェクト名・名前空間もBridgENに統一しています。

`SatelliteResourceLanguages`を`en;ja`に設定し、.NETやWPFの表示メッセージ用データは英語・日本語だけを同梱します。英語は通常DLL本体に含まれるため、独立した`en`フォルダーがない場合があります。この設定はOCRの認識言語やGeminiの翻訳言語を変更するものではありません。

## 6. GitHub Releases

`.github/workflows/release-windows.yml`は、`v0.1.0-beta.2`のようなタグをpushすると、そのタグのコードからWindows版を発行し、ZIPとSHA-256ファイルを添付してReleaseを公開します。タグとアプリのVersionが一致し、`docs/release-notes/版番号.md`が存在する必要があります。先に下書きへ添付し、アップロードされたファイルのサイズとSHA-256を確認してから公開します。`-beta.2`などの接尾辞がある版はPre-releaseにします。

新しい版を公開するときは、版番号とリリースノートを更新して開発ブランチへpushし、そのコミットに新しいタグを付けてpushします。すでに公開されたタグのReleaseは変更しません。

画面から手動で公開する場合は、次の手順を使います。

1. サーバー接続・OCR・ストリーミング・認証失敗・利用上限・キャンセルを確認する。
2. GitHubのReleasesで下書きを作り、タグを`v0.1.0-beta.2`とする。この版のTargetは、最新の変更をpushした`Feature/ManualMode`を選ぶ。
3. ZIPとSHA-256ファイルを添付する。GitHubが自動生成するSource code ZIPは、起動用の配布物ではありません。
4. Pre-releaseにし、対応環境・英語OCRの導入・利用上限・英文がGoogleへ送られることを記載する。
5. 招待した利用者へトークンを個別に渡してから公開する。トークンはRelease本文や公開Issueに書かない。

デプロイ・実API接続・公開前の動作確認が済むまでは、配布可能な完成版として扱いません。
