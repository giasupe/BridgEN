# BridgENの構造と処理の流れ

BridgEN（旧speciRPG）は、ゲーム画面の画像を英語の文字列に変換し、その英文を翻訳APIへ送り、日本語をゲームの上に表示するWindowsアプリです。.NET 10とWPFを使用しています。内部のプロジェクト名はspeciRPGのままです。

配布版は`RelayTranslationProvider`からCloudflare Workerへ接続します。`Configuration/`はサーバーURLと暗号化した招待トークンの保存を担当します。`server/worker/`に中継API・招待トークン検証・Durable Objectによる利用上限を実装しています。`scripts/publish-win.ps1`でBridgEN.exeを含む自己完結型ZIPを作成できます。詳しくは[配布手順](distribution.md)を参照してください。

## ディレクトリ構造

```text
rpg-screen-translator/
├─ speciRPG.sln                         プロジェクトをまとめるソリューション
├─ run.bat                              ビルドしてからアプリを起動
├─ README.md                            導入方法、現在の仕様、開発上の課題
├─ docs/
│  └─ architecture.md                    この解説
├─ src/
│  └─ speciRPG.App/
│     ├─ speciRPG.App.csproj             .NET・WPF・外部ライブラリの設定
│     ├─ App.xaml / App.xaml.cs          アプリの起動入口と共通設定
│     ├─ AssemblyInfo.cs                WPFのテーマ参照などの設定
│     ├─ MainWindow.xaml                クライアント画面の部品と配置
│     ├─ MainWindow.xaml.cs             操作・OCR・翻訳・表示をつなぐ進行役
│     ├─ Input/
│     │  ├─ WindowTarget.cs             ウィンドウの一覧取得・前面判定
│     │  └─ GlobalKeyboardMonitor.cs    Z / Enterの押下を監視
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
   ├─ speciRPG.Ocr.Regression/           文章の揺れ・行の組み直しなどの確認用
   │  ├─ Program.cs
   │  └─ speciRPG.Ocr.Regression.csproj
   └─ speciRPG.Ocr.WindowsSmoke/         Windows英語OCRと画像補正の確認用
      ├─ Program.cs
      ├─ speciRPG.Ocr.WindowsSmoke.csproj
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

`WindowTarget.cs`で取得した一覧からゲームを選び、`OcrRegionSelectionWindow`で会話欄をドラッグします。選んだ範囲は`OcrCaptureRegion`に保存します。

### 2. 開始して、前面のゲームだけを読む

開始ボタンを押すと、`MainWindow.xaml.cs`が選択したゲームの前面状態を確認します。ゲームが前面になると`WindowOcrCaptureService`を作り、Windows Graphics Captureを開始します。

前面状態は約250msごとに確認します。ゲームが前面でなくなるとキャプチャと進行中の翻訳を止め、オーバーレイを隠します。完成済みの結果と計測値はクライアントで確認できます。

APIキーが設定されている場合、開始時に認証不要のHEADリクエストでAPIホストへの接続準備も始めます。文章を生成するAPIはこの段階では呼びません。

### 3. 画面を取得して範囲を切り出す

`WindowOcrCaptureService`が画像を取得し、選択した範囲を切り出します。

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

`OcrTextStabilizer`が、同じ台詞の読み取りの揺れと、新しい台詞・文字送りを区別します。近い候補が行き来する場合は良い候補を保持します。

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

`GlobalKeyboardMonitor`はZとEnterを監視し、ゲームが前面にある場合の押下回数を数えます。現在はOCRや翻訳を始めるきっかけには使っていません。
