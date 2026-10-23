# BridgENの構造と処理の流れ

BridgENは、ゲーム画面の画像を英語の文字列に変換し、その英文を翻訳APIへ送り、日本語をゲームの上に表示するWindowsアプリです。.NET 10とWPFを使用し、プロジェクト名・名前空間もBridgENに統一しています。

配布版は`RelayTranslationProvider`からCloudflare Workerへ接続します。`Configuration/`はサーバーURLと暗号化した招待トークンの保存を担当します。`server/worker/`に中継API・招待トークン検証・Durable Objectによる利用上限を実装しています。`scripts/publish-win.ps1`でBridgEN.exeを含む自己完結型ZIPを作成できます。詳しくは[配布手順](distribution.md)を参照してください。

## ディレクトリ構造

```text
BridgEN/
├─ BridgEN.sln                         プロジェクトをまとめるソリューション
├─ run.bat                              ビルドしてからアプリを起動
├─ README.md                            導入方法、現在の仕様、開発上の課題
├─ docs/
│  └─ architecture.md                    この解説
├─ src/
│  └─ BridgEN.App/
│     ├─ BridgEN.App.csproj             .NET・WPF・外部ライブラリの設定
│     ├─ App.xaml / App.xaml.cs          アプリの起動入口と共通設定
│     ├─ AssemblyInfo.cs                WPFのテーマ参照などの設定
│     ├─ MainWindow.xaml                クライアント画面の部品と配置
│     ├─ MainWindow.xaml.cs             操作・OCR・翻訳・表示をつなぐ進行役
│     ├─ Configuration/
│     │  ├─ OcrOperationSettings.cs     操作モードと実行キーの設定データ
│     │  ├─ OcrOperationSettingsStore.cs 操作設定の読込・保存
│     │  ├─ TranslationConnectionSettings.cs 翻訳接続の設定データ
│     │  └─ TranslationSettingsStore.cs 接続設定と暗号化したトークンの保存
│     ├─ Input/
│     │  ├─ WindowTarget.cs             ウィンドウの一覧取得・前面判定
│     │  ├─ GlobalKeyboardMonitor.cs    Z / Enterとマニュアル実行キーを監視
│     │  └─ ManualOcrHotkey.cs          実行キーと修飾キーの検証・表示
│     ├─ Ocr/
│     │  ├─ OcrCaptureRegion.cs         読む範囲の座標データ
│     │  ├─ OcrRegionSelectionWindow.xaml / .xaml.cs
│     │  │                             ドラッグで読む範囲を選ぶ画面
│     │  ├─ WindowOcrCaptureService.cs  ゲーム画面取得とOCR処理の進行役
│     │  ├─ OcrImageTreatment.cs        画像補正の種類
│     │  ├─ OcrImagePreprocessor.cs     拡大・コントラスト・二値化・走査線補正
│     │  ├─ OcrFrameSignature.cs        画像の変化を比べる特徴データ
│     │  ├─ WindowsOcrTextLayout.cs     Windows OCRの結果を単語座標へ変換
│     │  ├─ OcrTextLayout.cs            単語の座標から行と読み順を復元
│     │  ├─ OcrTextQuality.cs           認識候補の採点・再認識の必要性を判断
│     │  ├─ OcrTextSimilarity.cs        同じ台詞かを比べる文字列処理
│     │  ├─ OcrTextStabilizer.cs        候補の揺れを抑え、採用する英文を管理
│     │  ├─ OcrTranslationReadiness.cs  いつ翻訳へ進めるかを判断
│     │  ├─ OcrProcessingTimings.cs     各OCR工程の計測データ
│     │  ├─ OcrDiagnostics.cs           本文・画像を含まない診断ログ
│     │  └─ OcrOverlayWindow.xaml / .xaml.cs
│     │                                ゲーム上部に文字を重ねる画面
│     └─ Translation/
│        ├─ ITranslationProvider.cs     翻訳サービスの共通の呼び出し方と入力データ
│        ├─ GeminiTranslationProvider.cs Geminiへの送信・接続準備・APIエラー処理
│        ├─ GeminiStreamReader.cs       ストリーミングで届く文章の読み取り
│        └─ TranslationResultCache.cs   完成した訳を最大128件再利用
└─ tests/
   ├─ BridgEN.Ocr.Regression/           文章の揺れ・行の組み直しなどの確認用
   │  ├─ Program.cs
   │  └─ BridgEN.Ocr.Regression.csproj
   └─ BridgEN.Ocr.WindowsSmoke/         Windows英語OCRと画像補正の確認用
      ├─ Program.cs
      ├─ BridgEN.Ocr.WindowsSmoke.csproj
      └─ Fixtures/tutorial-controls.png 確認に使うゲーム画像
```

`bin/`と`obj/`はビルドで作られるファイルです。編集するコードとは分けて考えます。

## 最初に押さえる3つの担当

- `MainWindow.xaml.cs`は、操作・OCR・翻訳・表示をつなぐ担当です。
- `Ocr/`は、画像から「翻訳に使える英文」を作る担当です。
- `Translation/`は、英文から日本語を作り、再利用する担当です。

いずれも同じアプリの中で動きます。外部へ送るのは翻訳に必要な文字列で、画像はローカルで処理します。

## 処理の流れ

### 1. 起動して対象を選ぶ

`run.bat`が`dotnet build`を実行し、成功すると`dotnet run --no-build`で起動します。WPFの`App.xaml`から`MainWindow`が開きます。

`WindowTarget.cs`で取得した一覧からゲームを選びます。「読む範囲を選択」を押すと、`MainWindow.xaml.cs`がOCRと翻訳表示を一時停止し、`WindowTargetProvider.TryActivateAsync`でゲームを前面に移します。最小化されている場合は復元し、最大1秒間、復元と前面状態を確認します。成功した場合だけBridgENを隠し、`OcrRegionSelectionWindow`で会話欄をドラッグできるようにします。選択中はOCRを再開せず、選んだ範囲を`OcrCaptureRegion`に保存した後、BridgENへ戻ります。前面化に失敗した場合は範囲選択を開始せず、再試行の案内を表示します。

### 2. 開始して、前面のゲームだけを読む

`OcrOperationSettings`はオートマチックを初期値とし、実行キーはF8を初期値とします。変更は`OcrOperationSettingsStore`が`%LOCALAPPDATA%\BridgEN\ocr-settings.json`へ保存します。保存済みの選択は次回起動時にも使います。接続設定と招待トークンには触れません。

オートマチックで開始ボタンを押すと、`MainWindow.xaml.cs`が選択したゲームの前面状態を確認します。ゲームが前面になると`WindowOcrCaptureService`を作ります。イベント購読と現在のサービスへの登録を済ませてから`Start()`でWindows Graphics Captureを開始するため、最初の結果を受け取れます。

マニュアルで「待機を開始」を押した段階ではキャプチャサービスを作りません。`GlobalKeyboardMonitor`が実行キーを検出し、UIスレッドへ処理を予約します。フック内でOCRや通信は行いません。予約を実行するときにもモード・対象ウィンドウ・前面状態を確認し、マニュアル指定のサービスを作って開始します。サイズ変更用のフレームを除く最初の1枚にOCRと補正比較を適用し、結果を受け取るとキャプチャだけを解放します。文章の連続確認は省き、同じ翻訳・キャッシュ・ストリーミング表示の経路へ渡します。空読みやエラー時に自動再実行せず、次のキーを待ちます。結果が10秒以内に返らない場合もキャプチャを止めます。

キーの長押しはdown/upの状態で抑制します。OCR中の追加押下は予約しません。翻訳受信中の新しい押下は前の翻訳を取り消して新しい画面を読み取ります。モード変更・停止・対象変更・前面離脱でも進行中の処理を止め、古い結果が新しい処理へ混ざらないようにします。

前面状態は約250msごとに確認します。ゲームが前面でなくなるとキャプチャと進行中の翻訳を止め、オーバーレイを隠します。完成済みの結果と計測値はクライアントで確認できます。

APIキーが設定されている場合、開始時に認証不要のHEADリクエストでAPIホストへの接続準備も始めます。文章を生成するAPIはこの段階では呼びません。

### 3. 画面を取得して範囲を切り出す

`WindowOcrCaptureService`が画像を取得し、選択した範囲を切り出します。次の周期処理はオートマチックだけに適用します。マニュアルは押下後の1枚だけを使います。

- 文章を確認している間は、最短250ms間隔です。
- 確定後と文字がない場合は、通常400ms間隔です。今回、800msから変更しました。
- 前のOCRが終わっていない場合は、そのフレームで新しいOCRを始めません。

この間隔は処理開始の間隔です。画像の到着やOCR自体が遅ければ、設定どおりの周期にはなりません。

### 4. 画像を整えて英語を読む

`OcrImagePreprocessor`で画像の特徴を取り、現在選ばれている補正を適用します。Windowsのen-US OCRで英語を読み取ります。

初回・文章変更・疑わしい結果など、必要なときは他の補正も比較します。追加候補は最大2件ずつ並行処理し、`OcrTextQuality`で良い候補を選びます。補正の種類を通常読み取りでも使い続けることで、補正時だけ結果が変わる揺れを抑えます。

### 5. 単語を正しい行に並べる

Windows OCRの結果が列ごとに分かれる場合があるため、`WindowsOcrTextLayout`と`OcrTextLayout`で単語の位置を使い、上から下・左から右に並べ直します。行数は固定しません。

### 6. 採用する英文を決め、翻訳へ進めるか確認する

オートマチックでは`OcrTextStabilizer`が、同じ台詞の読み取りの揺れと、新しい台詞・文字送りを区別します。近い候補が行き来する場合は良い候補を保持します。マニュアルはこの連続確認と`OcrTranslationReadiness`を通さず、その1枚の認識結果を翻訳対象にします。

次に`OcrTranslationReadiness`で送信可能か確認します。

- 明瞭な文章が連続して完全一致し、別々の画像の取得時刻が250ms以上離れていれば、早く確定できます。
- 疑わしい文字や候補の揺れがある文章は、採用後約1秒確認します。
- 別の台詞へ変わった場合は、確認をやり直します。

背景の動きだけで確認待ちをやり直すことはありません。OCR結果は、この段階の採用文章をクライアントに表示します。

### 7. 翻訳を再利用するか、Geminiへ送る

確定した英文を`TranslationResultCache`で探します。完成済みの同じ英文があれば、APIを呼ばずに表示します。

未保存なら、`MainWindow.xaml.cs`が`ITranslationProvider`を通して、設定した翻訳プロバイダーを呼びます。配布版は`RelayTranslationProvider`から招待トークン付きでWorkerへ英文だけを送信し、Workerが認証・利用上限を確認してGeminiへ転送します。開発用の直接接続を選んだ場合のみ`GeminiTranslationProvider`が環境変数のAPIキーでGeminiへ接続します。

`ITranslationProvider`は「翻訳を頼む共通の窓口」です。将来別の翻訳サービスを追加するときは、この窓口に合わせて実装する方針です。

### 8. 日本語が届いた部分から表示する

`GeminiStreamReader`が届いた断片をつなぎ、`MainWindow.xaml.cs`へ通知します。翻訳結果欄と`OcrOverlayWindow`を受信中にも更新します。

正常に完了した訳だけをキャッシュへ保存します。別の文章へ切り替わった場合は古いリクエストをキャンセルし、その結果が後から届いても表示しません。

## 処理時間とキーボード監視

OCR側は`OcrProcessingTimings`、翻訳側は`MainWindow.xaml.cs`内の`TranslationMeasurement`で時間を管理します。左側は最新フレームのOCR処理、右側は現在の文章の初回OCRから翻訳表示までの処理です。

`GlobalKeyboardMonitor`はZとEnterの押下カウントと、マニュアル実行キーの検出を担当します。マニュアル実行キーはCtrl・Shift・Alt・Windowsキーとの組み合わせを指定できます。入力を消費せず、ゲームにもそのまま渡します。Z / Enterを実行キーに設定しない限り、従来のカウント自体がOCRを始めることはありません。
