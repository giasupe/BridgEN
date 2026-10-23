using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BridgEN.App.Input;
using BridgEN.App.Ocr;
using BridgEN.App.Translation;
using BridgEN.App.Configuration;

namespace BridgEN.App;

public partial class MainWindow : Window
{
    private readonly GlobalKeyboardMonitor? _keyboardMonitor;
    private readonly DispatcherTimer _activeWindowTimer;
    private readonly OcrOverlayWindow _ocrOverlayWindow;
    private readonly OcrRegionSelectionWindow _ocrRegionSelectionWindow;
    private WindowOcrCaptureService? _ocrCaptureService;
    private OcrCaptureRegion? _ocrCaptureRegion;
    private ITranslationProvider? _translationProvider;
    private CancellationTokenSource? _translationCancellation;
    private string? _latestRecognizedText;
    private long _latestOcrRevision = -1;
    private bool _isOcrTextStable;
    private long _failedTranslationRevision = -1;
    private readonly TranslationResultCache _translationCache = new();
    private IntPtr _captureTargetHandle;
    private bool _ocrRequested;
    private int _detectedKeyCount;
    private bool _isRefreshingWindowList;
    private bool _isSelectingOcrRegion;
    private bool _isClosed;
    private TranslationMeasurement? _translationMeasurement;
    private readonly TranslationSettingsStore _settingsStore = new();
    private TranslationConnectionSettings _connectionSettings = new();

    public MainWindow()
    {
        InitializeComponent();
        ResetResults();
        _ocrOverlayWindow = new OcrOverlayWindow();
        _ocrRegionSelectionWindow = new OcrRegionSelectionWindow();
        _ocrRegionSelectionWindow.RegionSelected += OnOcrRegionSelected;
        _ocrRegionSelectionWindow.SelectionCancelled += OnOcrRegionSelectionCancelled;
        LoadConnectionSettings();

        _activeWindowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _activeWindowTimer.Tick += (_, _) =>
        {
            UpdateTargetWindowStatus();
            UpdateOcrCaptureState();
            if (_translationMeasurement?.State is "安定待ち" or "API応答待ち" or "受信中")
                UpdateTranslationTimingDisplay();
        };

        RefreshWindowList();
        _activeWindowTimer.Start();

        try
        {
            _keyboardMonitor = new GlobalKeyboardMonitor();
            _keyboardMonitor.KeyPressed += OnTrackedKeyPressed;
            KeyboardStatusText.Text = "監視中: Z / Enter";
        }
        catch (Win32Exception exception)
        {
            KeyboardStatusText.Text = $"キーボード監視を開始できませんでした: {exception.Message}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _activeWindowTimer.Stop();
        _ocrRequested = false;
        StopOcrCapture();
        _ocrOverlayWindow.Close();
        _ocrRegionSelectionWindow.Close();
        _keyboardMonitor?.Dispose();
        base.OnClosed(e);
    }

    private void OnTrackedKeyPressed(object? sender, TrackedKeyPressedEventArgs e)
    {
        var selectedWindow = TargetWindowComboBox.SelectedItem as WindowTarget;
        if (!WindowTargetProvider.IsActive(selectedWindow))
        {
            return;
        }

        _detectedKeyCount++;
        LastKeyText.Text = $"最後に検知したキー: {e.KeyName}（合計 {_detectedKeyCount} 回）";
        DetectedKeyCountText.Text = $"対象ウィンドウ内の入力回数: {_detectedKeyCount} 回";
    }

    private void RefreshWindowsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindowList();
    }

    private void TargetWindowComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isRefreshingWindowList)
        {
            _latestRecognizedText = null;
            _ocrCaptureRegion = null;
            ResetResults();
            OcrRegionStatusText.Text = "範囲は未選択です。";
            _detectedKeyCount = 0;
            LastKeyText.Text = "最後に検知したキー: なし";
            DetectedKeyCountText.Text = "対象ウィンドウ内の入力回数: 0 回";
        }

        UpdateTargetWindowStatus();
        SelectOcrRegionButton.IsEnabled = !_isSelectingOcrRegion
            && TargetWindowComboBox.SelectedItem is WindowTarget;
        UpdateOcrCaptureState();
    }

    private async void SelectOcrRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isSelectingOcrRegion)
        {
            return;
        }

        if (TargetWindowComboBox.SelectedItem is not WindowTarget target)
        {
            OcrStatusText.Text = "先に一覧からゲームのウィンドウを選択してください。";
            return;
        }

        _isSelectingOcrRegion = true;
        SelectOcrRegionButton.IsEnabled = false;
        StopOcrCapture();
        _ocrOverlayWindow.Hide();
        OcrStatusText.Text = "対象ウィンドウを前面にしています。";
        var selectionStarted = false;
        try
        {
            if (!await WindowTargetProvider.TryActivateAsync(target))
            {
                OcrStatusText.Text = "対象ウィンドウを前面にできませんでした。ゲームを表示し、対象を選び直して再試行してください。";
                return;
            }

            if (_isClosed)
            {
                return;
            }

            if ((TargetWindowComboBox.SelectedItem as WindowTarget)?.Handle != target.Handle)
            {
                OcrStatusText.Text = "対象ウィンドウが変更されました。もう一度「読む範囲を選択」を押してください。";
                return;
            }

            Hide();
            if (!_ocrRegionSelectionWindow.ShowOverTarget(target.Handle))
            {
                OcrStatusText.Text = "範囲選択を表示できませんでした。ゲームが開いていることを確認して再試行してください。";
                return;
            }

            selectionStarted = true;
            OcrStatusText.Text = "ゲーム画面上をドラッグしてください。右クリックでキャンセルできます。";
        }
        finally
        {
            if (!selectionStarted && !_isClosed)
            {
                RestoreAfterOcrRegionSelection();
            }
        }
    }

    private void OnOcrRegionSelected(object? sender, OcrRegionSelectedEventArgs e)
    {
        _ocrCaptureRegion = e.Region;
        StopOcrCapture();
        _latestRecognizedText = null;
        ResetResults();
        OcrRegionStatusText.Text = "読み取り範囲を設定しました。";
        OcrStatusText.Text = _ocrRequested
            ? "読み取り範囲を更新しました。ゲームを前面にするとOCRを再開します。"
            : "範囲を選択しました。ゲームを前面にしてOCRを開始してください。";
        RestoreAfterOcrRegionSelection();
    }

    private void OnOcrRegionSelectionCancelled(object? sender, EventArgs e)
    {
        OcrStatusText.Text = "範囲選択をキャンセルしました。";
        RestoreAfterOcrRegionSelection();
    }

    private void RestoreAfterOcrRegionSelection()
    {
        _isSelectingOcrRegion = false;
        SelectOcrRegionButton.IsEnabled = TargetWindowComboBox.SelectedItem is WindowTarget;
        Show();
        Activate();
    }

    private void StartOcrButton_Click(object sender, RoutedEventArgs e)
    {
        if (TargetWindowComboBox.SelectedItem is not WindowTarget)
        {
            OcrStatusText.Text = "先に一覧からゲームのウィンドウを選択してください。";
            return;
        }

        if (_ocrCaptureRegion is null)
        {
            OcrStatusText.Text = "先に「読む範囲を選択」を押して、ゲーム画面上をドラッグしてください。";
            return;
        }

        if (_connectionSettings.UseRelay)
        {
            if (CreateTranslationProvider() is RelayTranslationProvider relay) _ = relay.WarmConnectionAsync();
        }
        else if (!string.IsNullOrWhiteSpace(GetConfiguredApiKey()))
            _ = GeminiTranslationProvider.WarmConnectionAsync();
        _ocrRequested = true;
        StartOcrButton.IsEnabled = false;
        StopOcrButton.IsEnabled = true;
        OcrStatusText.Text = "ゲームのウィンドウを前面にすると、英語OCRを開始します。";
        UpdateOcrCaptureState();
    }

    private void StopOcrButton_Click(object sender, RoutedEventArgs e)
    {
        _ocrRequested = false;
        StopOcrCapture();
        _ocrOverlayWindow.Hide();
        OcrStatusText.Text = "英語OCRを停止しました。";
        StartOcrButton.IsEnabled = true;
        StopOcrButton.IsEnabled = false;
    }

    private void RefreshWindowList()
    {
        var previousHandle = (TargetWindowComboBox.SelectedItem as WindowTarget)?.Handle;
        var windows = WindowTargetProvider.GetOpenWindows();
        _isRefreshingWindowList = true;
        try
        {
            TargetWindowComboBox.ItemsSource = windows;
            TargetWindowComboBox.SelectedItem = windows.FirstOrDefault(window => window.Handle == previousHandle);
        }
        finally
        {
            _isRefreshingWindowList = false;
        }

        if (TargetWindowComboBox.SelectedItem is null)
        {
            TargetWindowStatusText.Text = windows.Count == 0
                ? "選択できるウィンドウがありません。ゲームを開いてから一覧を更新してください。"
                : "対象ウィンドウを選択してください。";
        }

        UpdateTargetWindowStatus();
    }

    private void UpdateTargetWindowStatus()
    {
        if (TargetWindowComboBox.SelectedItem is not WindowTarget selectedWindow)
        {
            return;
        }

        TargetWindowStatusText.Text = WindowTargetProvider.IsActive(selectedWindow)
            ? $"対象がアクティブです。Z / Enter をカウントします: {selectedWindow.DisplayName}"
            : $"対象が前面にある間だけカウントします: {selectedWindow.DisplayName}";
    }

    private void UpdateOcrCaptureState()
    {
        // Do not capture the selection rectangle or translation overlay while dragging.
        if (_isSelectingOcrRegion)
        {
            return;
        }

        var target = TargetWindowComboBox.SelectedItem as WindowTarget;

        if (!_ocrRequested || target is null)
        {
            StopOcrCapture();
            _ocrOverlayWindow.Hide();
            return;
        }

        if (_ocrCaptureRegion is null)
        {
            StopOcrCapture();
            _ocrOverlayWindow.Hide();
            OcrStatusText.Text = "先に「読む範囲を選択」でOCR対象を決めてください。";
            return;
        }

        if (_ocrCaptureService is not null && _captureTargetHandle != target.Handle)
        {
            StopOcrCapture();
            _ocrOverlayWindow.Hide();
        }

        if (!WindowTargetProvider.IsActive(target))
        {
            StopOcrCapture();
            _ocrOverlayWindow.Hide();
            OcrStatusText.Text = "開始待ち: 選択したゲームのウィンドウを前面にしてください。";
            return;
        }

        if (_ocrCaptureService is not null)
        {
            _ocrOverlayWindow.UpdatePosition(target.Handle);
            return;
        }

        try
        {
            var captureService = new WindowOcrCaptureService(target, _ocrCaptureRegion);
            captureService.TextRecognized += OnOcrTextRecognized;
            captureService.ContentChanging += OnOcrContentChanging;
            captureService.ErrorOccurred += OnOcrErrorOccurred;
            _ocrCaptureService = captureService;
            _captureTargetHandle = target.Handle;
                    OcrStatusText.Text = "英語OCRを開始しました。英文が安定すると翻訳します。";
        }
        catch (Exception exception)
        {
            _ocrRequested = false;
            StartOcrButton.IsEnabled = true;
            StopOcrButton.IsEnabled = false;
            OcrStatusText.Text = exception.Message;
            StopOcrCapture();
        }
    }

    private void OnOcrTextRecognized(object? sender, OcrTextRecognizedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _ocrCaptureService)
                || !WindowTargetProvider.IsActive(TargetWindowComboBox.SelectedItem as WindowTarget))
            {
                return;
            }

            var recognizedText = e.Text?.Trim() ?? string.Empty;
            ShowOcrTimings(e.Timings);
            var isSameRevision = _latestOcrRevision == e.Revision;
            _latestOcrRevision = e.Revision;
            _isOcrTextStable = e.IsStable;
            RecognizedTextBox.Text = string.IsNullOrWhiteSpace(recognizedText)
                ? "画面から文字を認識できていません。"
                : recognizedText;

            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                _latestRecognizedText = null;
                CancelPendingTranslation();
                TranslationResultTextBox.Clear();
                _translationMeasurement = null;
                UpdateTranslationTimingDisplay();
                _ocrOverlayWindow.Hide();
                OcrStatusText.Text = "会話文を探しています。";
            }
            else if (TargetWindowComboBox.SelectedItem is WindowTarget target)
            {
                var isSameText = isSameRevision
                    && string.Equals(_latestRecognizedText, recognizedText, StringComparison.Ordinal);
                if (!isSameText)
                {
                    _latestRecognizedText = recognizedText;
                    CancelPendingTranslation();
                    TranslationResultTextBox.Clear();
                    _translationMeasurement = new TranslationMeasurement(
                        e.Timings.RevisionStartedTimestamp, Stopwatch.GetTimestamp(), e.Timings.InitialPassTotal);
                    UpdateTranslationTimingDisplay();
                }

                if (e.IsStable && _translationCache.TryGet(recognizedText, out var cachedTranslation))
                {
                    var displayStarted = Stopwatch.GetTimestamp();
                    TranslationResultTextBox.Text = cachedTranslation;
                    _ocrOverlayWindow.ShowRecognizedText(cachedTranslation, target.Handle);
                    OcrStatusText.Text = "日本語訳をゲーム画面の上部に表示しています。";
                    if (_translationMeasurement is { State: not ("完了" or "再利用") } cachedMeasurement)
                    {
                        cachedMeasurement.StabilityWait = Stopwatch.GetElapsedTime(cachedMeasurement.FirstResultTimestamp, displayStarted);
                        cachedMeasurement.Display = Stopwatch.GetElapsedTime(displayStarted);
                        cachedMeasurement.Total = Stopwatch.GetElapsedTime(cachedMeasurement.FirstFrameTimestamp);
                        cachedMeasurement.FirstDisplayTotal = cachedMeasurement.Total;
                        cachedMeasurement.State = "再利用";
                        UpdateTranslationTimingDisplay();
                    }
                    return;
                }

                if (!e.IsStable)
                {
                    CancelPendingTranslation();
                    _ocrOverlayWindow.ShowRecognizedText(recognizedText, target.Handle);
                    OcrStatusText.Text = "認識した文章が安定しているか確認しています。";
                    return;
                }

                if (isSameText && _translationCancellation is not null)
                {
                    OcrStatusText.Text = _translationMeasurement?.State == "受信中"
                        ? "日本語訳を受信しながら表示しています。"
                        : "翻訳の応答を待っています。";
                    return;
                }

                _ocrOverlayWindow.ShowRecognizedText(recognizedText, target.Handle);

                // Confirmation events repeat every OCR pass. An API failure must
                // not turn those events into repeated billable requests.
                if (_failedTranslationRevision == e.Revision) return;

                if (CreateTranslationProvider() is null)
                {
                    FinishTranslationMeasurement("接続設定待ち");
                    return;
                }
                // Each attempt owns its timings. A cancelled request can finish
                // later without changing the next attempt's measurements.
                if (_translationMeasurement is not { State: "安定待ち" })
                    _translationMeasurement = new TranslationMeasurement(
                        e.Timings.RevisionStartedTimestamp, Stopwatch.GetTimestamp(), e.Timings.InitialPassTotal);
                var cancellation = new CancellationTokenSource();
                _translationCancellation = cancellation;
                OcrStatusText.Text = "英文を確認しました。翻訳を開始します。";
                // The capture service now confirms the elapsed stability period.
                // Adding a second UI debounce here would duplicate that wait.
                _ = TranslateRecognizedTextAsync(recognizedText, target.Handle, cancellation,
                    _translationMeasurement!);
            }
        });
    }

    private async Task TranslateRecognizedTextAsync(
        string recognizedText,
        IntPtr targetHandle,
        CancellationTokenSource cancellation,
        TranslationMeasurement measurement)
    {
        try
        {
            if (!CanShowTranslation(recognizedText, targetHandle, cancellation))
            {
                return;
            }

            var request = new TranslationRequest(
                Text: recognizedText,
                SourceLanguage: "English",
                TargetLanguage: "Japanese",
                SpeakerName: null,
                CharacterMemory: null,
                RecentConversation: Array.Empty<string>());

            measurement.StabilityWait = Stopwatch.GetElapsedTime(measurement.FirstResultTimestamp);
            measurement.State = "API応答待ち";
            UpdateTranslationTimingDisplay();
            var apiStarted = Stopwatch.GetTimestamp();
            measurement.ApiStartedTimestamp = apiStarted;
            string translation;
            try
            {
                translation = await _translationProvider!.TranslateStreamingAsync(request, async partialText =>
                {
                    var receivedAt = Stopwatch.GetTimestamp();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!CanShowTranslation(recognizedText, targetHandle, cancellation)) return;
                        measurement.FirstText ??= Stopwatch.GetElapsedTime(apiStarted, receivedAt);
                        // Always show the first chunk. Coalesce bursts to keep the
                        // caption responsive without re-laying it out per token.
                        if (measurement.LastDisplayTimestamp is { } lastDisplay
                            && Stopwatch.GetElapsedTime(lastDisplay) < TimeSpan.FromMilliseconds(50)) return;
                        var displayStarted = Stopwatch.GetTimestamp();
                        TranslationResultTextBox.Text = partialText;
                        _ocrOverlayWindow.ShowRecognizedText(partialText, targetHandle);
                        measurement.Display = (measurement.Display ?? TimeSpan.Zero)
                            + Stopwatch.GetElapsedTime(displayStarted);
                        measurement.LastDisplayTimestamp = Stopwatch.GetTimestamp();
                        measurement.FirstDisplayTotal ??= Stopwatch.GetElapsedTime(measurement.FirstFrameTimestamp);
                        measurement.State = "受信中";
                        OcrStatusText.Text = "日本語訳を受信しながら表示しています。";
                        UpdateTranslationTimingDisplay();
                    });
                }, cancellation.Token);
            }
            finally
            {
                measurement.Api = Stopwatch.GetElapsedTime(apiStarted);
            }
            if (!CanShowTranslation(recognizedText, targetHandle, cancellation))
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(translation))
            {
                throw new InvalidOperationException("翻訳結果が空でした。");
            }

            var displayStarted = Stopwatch.GetTimestamp();
            TranslationResultTextBox.Text = translation;
            _ocrOverlayWindow.ShowRecognizedText(translation, targetHandle);
            _translationCache.Store(recognizedText, translation);
            OcrStatusText.Text = "日本語訳をゲーム画面の上部に表示しています。";
            measurement.Display = (measurement.Display ?? TimeSpan.Zero) + Stopwatch.GetElapsedTime(displayStarted);
            measurement.FirstDisplayTotal ??= Stopwatch.GetElapsedTime(measurement.FirstFrameTimestamp);
            measurement.Total = Stopwatch.GetElapsedTime(measurement.FirstFrameTimestamp);
            measurement.State = "完了";
            UpdateTranslationTimingDisplay();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // New OCR text, a focus change, or stopping OCR cancels obsolete work.
        }
        catch (Exception exception)
        {
            if (CanShowTranslation(recognizedText, targetHandle, cancellation))
            {
                _failedTranslationRevision = _latestOcrRevision;
                FinishTranslationMeasurement("失敗");
                TranslationResultTextBox.Clear();
                _ocrOverlayWindow.ShowRecognizedText(recognizedText, targetHandle);
                OcrStatusText.Text = $"翻訳に失敗しました: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_translationCancellation, cancellation))
            {
                _translationCancellation = null;
                if (measurement.State is "安定待ち" or "API応答待ち" or "受信中")
                {
                    FinishTranslationMeasurement("中断");
                }
            }

            if (ReferenceEquals(_translationMeasurement, measurement)) UpdateTranslationTimingDisplay();
            cancellation.Dispose();
        }
    }

    private bool CanShowTranslation(
        string recognizedText,
        IntPtr targetHandle,
        CancellationTokenSource cancellation)
    {
        return !cancellation.IsCancellationRequested
            && ReferenceEquals(_translationCancellation, cancellation)
            && string.Equals(_latestRecognizedText, recognizedText, StringComparison.Ordinal)
            && _ocrRequested
            && _isOcrTextStable
            && TargetWindowComboBox.SelectedItem is WindowTarget target
            && target.Handle == targetHandle
            && WindowTargetProvider.IsActive(target);
    }

    private void LoadConnectionSettings()
    {
        string? error = null;
        try
        {
            _connectionSettings = _settingsStore.Load() ?? new TranslationConnectionSettings();
            if (string.IsNullOrWhiteSpace(_connectionSettings.RelayUrl))
                _connectionSettings = _connectionSettings with { RelayUrl = TranslationSettingsStore.DefaultRelayUrl() };
        }
        catch (Exception exception)
        {
            _connectionSettings = new TranslationConnectionSettings();
            error = $"接続設定を読み込めませんでした。再登録してください: {exception.Message}";
        }
        ConnectionModeComboBox.SelectedIndex = _connectionSettings.UseRelay ? 0 : 1;
        RelayUrlTextBox.Text = _connectionSettings.RelayUrl;
        InviteTokenBox.Password = _connectionSettings.InviteToken;
        UpdateConnectionModeDisplay();
        ConnectionSettingsExpander.IsExpanded = error is not null || (_connectionSettings.UseRelay
            && (string.IsNullOrWhiteSpace(_connectionSettings.RelayUrl) || string.IsNullOrWhiteSpace(_connectionSettings.InviteToken)));
        ConnectionSettingsStatusText.Text = error ?? (_connectionSettings.UseRelay
            ? "配布されたサーバーURLと招待トークンを登録してください。トークンはこのWindowsユーザー用に暗号化して保存します。"
            : "開発用の直接接続です。Windows環境変数のGemini APIキーを使用します。");
    }

    private void ConnectionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectedIndex is also set while XAML is creating the remaining controls.
        if (!IsInitialized) return;
        UpdateConnectionModeDisplay();
        ConnectionSettingsStatusText.Text = "変更を反映するには「接続設定を保存」を押してください。";
    }

    private void UpdateConnectionModeDisplay()
    {
        var useRelay = ConnectionModeComboBox.SelectedIndex == 0;
        RelaySettingsPanel.Visibility = useRelay ? Visibility.Visible : Visibility.Collapsed;
        DeleteInviteTokenButton.Visibility = useRelay ? Visibility.Visible : Visibility.Collapsed;
        DirectConnectionHelpText.Visibility = useRelay ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveConnectionSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new TranslationConnectionSettings(ConnectionModeComboBox.SelectedIndex == 0,
                RelayUrlTextBox.Text.Trim(), InviteTokenBox.Password.Trim());
            if (settings.UseRelay) _ = new RelayTranslationProvider(settings.RelayUrl, settings.InviteToken);
            _settingsStore.Save(settings);
            ApplyConnectionSettings(settings);
            ConnectionSettingsStatusText.Text = "接続設定を保存しました。";
            OcrStatusText.Text = "接続設定を更新しました。ゲームを前面にすると再開します。";
        }
        catch (Exception exception)
        {
            ConnectionSettingsStatusText.Text = $"接続設定を保存できませんでした: {exception.Message}";
        }
    }

    private void DeleteInviteToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _connectionSettings with { InviteToken = string.Empty };
            _settingsStore.Save(settings);
            ApplyConnectionSettings(settings);
            InviteTokenBox.Clear();
            ConnectionModeComboBox.SelectedIndex = settings.UseRelay ? 0 : 1;
            RelayUrlTextBox.Text = settings.RelayUrl;
            ConnectionSettingsStatusText.Text = "保存した招待トークンを削除しました。";
        }
        catch (Exception exception)
        {
            ConnectionSettingsStatusText.Text = $"招待トークンを削除できませんでした: {exception.Message}";
        }
    }

    private void ApplyConnectionSettings(TranslationConnectionSettings settings)
    {
        StopOcrCapture();
        _ocrOverlayWindow.Hide();
        _translationProvider = null;
        _connectionSettings = settings;
        _latestRecognizedText = null;
        ResetResults();
    }

    private ITranslationProvider? CreateTranslationProvider()
    {
        if (_translationProvider is not null) return _translationProvider;
        try
        {
            if (_connectionSettings.UseRelay)
                return _translationProvider = new RelayTranslationProvider(_connectionSettings.RelayUrl, _connectionSettings.InviteToken);
            var apiKey = GetConfiguredApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                OcrStatusText.Text = "開発用の直接接続には、ユーザー環境変数 GEMINI_API_KEY または GOOGLE_API_KEY が必要です。";
                return null;
            }
            return _translationProvider = new GeminiTranslationProvider(apiKey);
        }
        catch (ArgumentException exception)
        {
            OcrStatusText.Text = $"「翻訳の接続設定」を登録してください: {exception.Message}";
            return null;
        }
    }

    private static string? GetConfiguredApiKey()
    {
        foreach (var variableName in new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" })
        {
            var processValue = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(processValue))
            {
                return processValue.Trim();
            }

            // Also read the user's saved variable. A running desktop app may have
            // inherited an older process environment from before the key was added.
            var userValue = Environment.GetEnvironmentVariable(
                variableName,
                EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(userValue))
            {
                return userValue.Trim();
            }
        }

        return null;
    }

    private void CancelPendingTranslation()
    {
        var cancellation = _translationCancellation;
        _translationCancellation = null;
        cancellation?.Cancel();
        if (cancellation is not null)
        {
            // Partial output is never retained as a completed translation.
            TranslationResultTextBox.Clear();
            _ocrOverlayWindow.Hide();
            FinishTranslationMeasurement("中断");
        }
    }

    private void OnOcrContentChanging(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _ocrCaptureService)) return;
            _isOcrTextStable = false;
            CancelPendingTranslation();
            OcrStatusText.Text = "画面の文字が変わりました。読み取りを確認しています。";
        });
    }

    private void OnOcrErrorOccurred(object? sender, OcrErrorEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _ocrCaptureService))
            {
                _isOcrTextStable = false;
                CancelPendingTranslation();
                FinishTranslationMeasurement("OCRエラー");
                OcrStatusText.Text = e.Message;
            }
        });
    }

    private void StopOcrCapture()
    {
        _isOcrTextStable = false;
        _latestOcrRevision = -1;
        _failedTranslationRevision = -1;
        CancelPendingTranslation();
        FinishTranslationMeasurement("中断");

        if (_ocrCaptureService is null)
        {
            _captureTargetHandle = IntPtr.Zero;
            return;
        }

        _ocrCaptureService.TextRecognized -= OnOcrTextRecognized;
        _ocrCaptureService.ContentChanging -= OnOcrContentChanging;
        _ocrCaptureService.ErrorOccurred -= OnOcrErrorOccurred;
        _ocrCaptureService.Dispose();
        _ocrCaptureService = null;
        _captureTargetHandle = IntPtr.Zero;
    }

    private void ResetResults()
    {
        RecognizedTextBox.Clear();
        TranslationResultTextBox.Clear();
        _translationCache.Clear();
        _translationMeasurement = null;
        ShowOcrTimings(null);
        UpdateTranslationTimingDisplay();
    }

    private static string Milliseconds(TimeSpan? duration) => duration is { } value
        ? $"{value.TotalMilliseconds:N1} ms" : "—";

    private void ShowOcrTimings(OcrProcessingTimings? timings)
    {
        OcrTimingsList.ItemsSource = new[]
        {
            new TimingRow("画面取得・コピー", Milliseconds(timings?.Capture)),
            new TimingRow("範囲切り出し", timings is null ? "—" : timings.Crop is null ? "省略" : Milliseconds(timings.Crop)),
            new TimingRow("画像補正・特徴抽出", Milliseconds(timings?.Preprocessing)),
            new TimingRow("OCR認識", Milliseconds(timings?.Recognition)),
            new TimingRow("行の組み直し", Milliseconds(timings?.Layout)),
            new TimingRow("補正比較・再認識", timings is null ? "—" : timings.Recovery is null ? "省略" : Milliseconds(timings.Recovery)),
            new TimingRow("品質・安定判定", Milliseconds(timings?.StabilityCheck)),
            new TimingRow("OCR処理全体", Milliseconds(timings?.Total))
        };
    }

    private void FinishTranslationMeasurement(string state)
    {
        if (_translationMeasurement is not { } measurement
            || measurement.State is not ("安定待ち" or "API応答待ち" or "受信中")) return;
        if (measurement.State == "安定待ち")
            measurement.StabilityWait = Stopwatch.GetElapsedTime(measurement.FirstResultTimestamp);
        else if (measurement.ApiStartedTimestamp is { } apiStarted && measurement.Api is null)
            measurement.Api = Stopwatch.GetElapsedTime(apiStarted);
        measurement.State = state;
        UpdateTranslationTimingDisplay();
    }

    private void UpdateTranslationTimingDisplay()
    {
        var measurement = _translationMeasurement;
        var wait = measurement?.State == "安定待ち"
            ? Stopwatch.GetElapsedTime(measurement.FirstResultTimestamp) : measurement?.StabilityWait;
        var api = measurement?.State is ("API応答待ち" or "受信中") && measurement.ApiStartedTimestamp is { } started
            ? Stopwatch.GetElapsedTime(started) : measurement?.Api;
        TranslationTimingsList.ItemsSource = new[]
        {
            new TimingRow("状態", measurement?.State ?? "未実行"),
            new TimingRow("初回OCR処理", Milliseconds(measurement?.InitialOcr)),
            new TimingRow("文章確認・安定待ち", Milliseconds(wait)),
            new TimingRow("API初回テキスト", measurement?.State == "再利用" ? "再利用" : Milliseconds(measurement?.FirstText)),
            new TimingRow("API受信全体", measurement?.State == "再利用" ? "再利用" : Milliseconds(api)),
            new TimingRow("表示更新（累計）", Milliseconds(measurement?.Display)),
            new TimingRow("取得→表示開始", Milliseconds(measurement?.FirstDisplayTotal)),
            new TimingRow("取得→翻訳完了", Milliseconds(measurement?.Total))
        };
    }

    private sealed record TimingRow(string Name, string Duration);

    private sealed class TranslationMeasurement(long firstFrameTimestamp, long firstResultTimestamp, TimeSpan initialOcr)
    {
        public long FirstFrameTimestamp { get; } = firstFrameTimestamp;
        public long FirstResultTimestamp { get; } = firstResultTimestamp;
        public TimeSpan InitialOcr { get; } = initialOcr;
        public long? ApiStartedTimestamp { get; set; }
        public TimeSpan? StabilityWait { get; set; }
        public TimeSpan? Api { get; set; }
        public TimeSpan? FirstText { get; set; }
        public TimeSpan? FirstDisplayTotal { get; set; }
        public long? LastDisplayTimestamp { get; set; }
        public TimeSpan? Display { get; set; }
        public TimeSpan? Total { get; set; }
        public string State { get; set; } = "安定待ち";
    }
}
