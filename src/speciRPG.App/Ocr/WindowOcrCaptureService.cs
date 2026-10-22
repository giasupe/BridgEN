using System.Diagnostics;
using System.Runtime.InteropServices;
using speciRPG.App.Input;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using WinRT;

namespace speciRPG.App.Ocr;

/// <summary>Captures one selected window and recognizes a selected area using English OCR.</summary>
public sealed class WindowOcrCaptureService : IDisposable
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly TimeSpan RecognitionInterval = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ConfirmationInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RetryRecognitionInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PeriodicQualityCheckInterval = TimeSpan.FromSeconds(6);
    private const DirectXPixelFormat CapturePixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly ID3D11Device _nativeDevice;
    private readonly IDirect3DDevice _captureDevice;
    private readonly GraphicsCaptureItem _captureItem;
    private readonly OcrEngine _ocrEngine;
    private readonly OcrCaptureRegion? _captureRegion;
    private readonly object _lifecycleGate = new();
    private readonly OcrTextStabilizer _textStabilizer = new();
    private readonly OcrEngine?[] _recoveryEngines = new OcrEngine?[2];
    private readonly OcrTranslationReadiness _translationReadiness = new();
    private bool _needsConfirmation = true;
    private OcrImageTreatment _preferredTreatment = OcrImageTreatment.Original;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _captureSession;
    private long _lastRecognitionTimestamp;
    private long _lastRecoveryTimestamp;
    private long _lastQualitySweepTimestamp;
    private long _timedRevision = -1;
    private long _revisionStartedTimestamp;
    private TimeSpan _initialPassTotal;
    private int _isRecognizing;
    private int _isDisposed;
    private int _errorReported;
    private Windows.Graphics.SizeInt32 _frameSize;

    public WindowOcrCaptureService(WindowTarget target, OcrCaptureRegion? captureRegion)
    {
        if (captureRegion is not null && !captureRegion.IsValid)
        {
            throw new ArgumentException("OCRの読み取り範囲が正しくありません。", nameof(captureRegion));
        }

        _captureRegion = captureRegion;

        if (!GraphicsCaptureSession.IsSupported())
        {
            throw new InvalidOperationException("このWindows環境ではウィンドウ画面の取得に対応していません。Windows 10 バージョン1903以降が必要です。");
        }

        var english = new Language("en-US");
        _ocrEngine = OcrEngine.TryCreateFromLanguage(english)
            ?? throw new InvalidOperationException("Windowsの英語OCR機能が利用できません。Windowsの設定で英語（米国）のOCR言語を追加してください。");

        _nativeDevice = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);

        IDirect3DDevice? captureDevice = null;
        try
        {
            using var dxgiDevice = _nativeDevice.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var captureDevicePointer));
            try
            {
                captureDevice = MarshalInterface<IDirect3DDevice>.FromAbi(captureDevicePointer);
            }
            finally
            {
                Marshal.Release(captureDevicePointer);
            }

            _captureItem = CreateCaptureItemForWindow(target.Handle);
            _captureDevice = captureDevice;
            _frameSize = _captureItem.Size;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                captureDevice,
                CapturePixelFormat,
                2,
                _frameSize);
            _captureSession = _framePool.CreateCaptureSession(_captureItem);
            _framePool.FrameArrived += OnFrameArrived;
            _captureSession.StartCapture();
        }
        catch
        {
            _captureSession?.Dispose();
            _framePool?.Dispose();
            captureDevice?.Dispose();
            _nativeDevice.Dispose();
            throw;
        }
    }

    public event EventHandler<OcrTextRecognizedEventArgs>? TextRecognized;
    public event EventHandler? ContentChanging;
    public event EventHandler<OcrErrorEventArgs>? ErrorOccurred;

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            {
                return;
            }

            if (_framePool is not null)
            {
                _framePool.FrameArrived -= OnFrameArrived;
            }

            _captureSession?.Dispose();
            _captureSession = null;
            _framePool?.Dispose();
            _framePool = null;
        }

        _ = ReleaseGraphicsDevicesAfterProcessingAsync();
    }

    private static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr windowHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var interfaceId = GraphicsCaptureItemIid;
        var itemPointer = interop.CreateForWindow(windowHandle, ref interfaceId);
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Direct3D11CaptureFrame? frame = null;
        SoftwareBitmap? bitmap = null;
        var recognitionStarted = false;
        long frameStartedTimestamp = 0;
        try
        {
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    return;
                }

                frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastRecognitionTimestamp), now);
                var interval = Volatile.Read(ref _needsConfirmation) ? ConfirmationInterval : RecognitionInterval;
                if (elapsed < interval || Interlocked.CompareExchange(ref _isRecognizing, 1, 0) != 0)
                {
                    return;
                }

                recognitionStarted = true;
                frameStartedTimestamp = now;
                Interlocked.Exchange(ref _lastRecognitionTimestamp, now);
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width > 0 && contentSize.Height > 0
                && (contentSize.Width != _frameSize.Width || contentSize.Height != _frameSize.Height))
            {
                frame.Dispose();
                frame = null;
                _frameSize = contentSize;
                if (Volatile.Read(ref _isDisposed) == 0)
                {
                    sender.Recreate(_captureDevice, CapturePixelFormat, 2, _frameSize);
                }

                return;
            }

            bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            frame.Dispose();
            frame = null;

            if (Volatile.Read(ref _isDisposed) != 0)
            {
                return;
            }

            var captureTime = Stopwatch.GetElapsedTime(frameStartedTimestamp);
            var stageStartedTimestamp = Stopwatch.GetTimestamp();
            TimeSpan? cropTime = null;
            if (_captureRegion is not null)
            {
                var croppedBitmap = CropBitmap(bitmap, _captureRegion);
                bitmap.Dispose();
                bitmap = croppedBitmap;
                cropTime = Stopwatch.GetElapsedTime(stageStartedTimestamp);
            }

            stageStartedTimestamp = Stopwatch.GetTimestamp();
            var signature = OcrImagePreprocessor.CreateFrameSignature(bitmap);
            // Apply the selected treatment on every pass. Returning to the raw
            // image between recovery passes manufactured alternating readings.
            using var prepared = _preferredTreatment == OcrImageTreatment.Original
                ? null
                : OcrImagePreprocessor.CreateRecognitionBitmap(bitmap, _preferredTreatment);
            var preprocessingTime = Stopwatch.GetElapsedTime(stageStartedTimestamp);
            stageStartedTimestamp = Stopwatch.GetTimestamp();
            var result = await _ocrEngine.RecognizeAsync(prepared ?? bitmap);
            var recognitionTime = Stopwatch.GetElapsedTime(stageStartedTimestamp);
            stageStartedTimestamp = Stopwatch.GetTimestamp();
            var recognizedLines = WindowsOcrTextLayout.ReadLines(result);
            var text = string.Join(Environment.NewLine, recognizedLines);
            var layoutTime = Stopwatch.GetElapsedTime(stageStartedTimestamp);
            stageStartedTimestamp = Stopwatch.GetTimestamp();
            var sourceText = text;
            var contentChanged = _textStabilizer.HasContentChanged(signature, text);
            if (contentChanged && Volatile.Read(ref _isDisposed) == 0)
            {
                // Pixel motion by itself cannot invalidate a translation.
                // A new reading or confirmed small edit must support it.
                ContentChanging?.Invoke(this, EventArgs.Empty);
            }

            var hasSuspiciousLines = recognizedLines.Any(OcrTextQuality.ShouldRetry);
            var recoveryCheckTimestamp = Stopwatch.GetTimestamp();
            var lastRecovery = Interlocked.Read(ref _lastRecoveryTimestamp);
            var recoveryAge = lastRecovery == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(lastRecovery, recoveryCheckTimestamp);
            var lastQualitySweep = Interlocked.Read(ref _lastQualitySweepTimestamp);
            var qualitySweepAge = lastQualitySweep == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(lastQualitySweep, recoveryCheckTimestamp);
            var shouldRecover = contentChanged
                || string.IsNullOrWhiteSpace(text)
                || hasSuspiciousLines
                || lastQualitySweep == 0 || qualitySweepAge >= PeriodicQualityCheckInterval;
            var stabilityCheckTime = Stopwatch.GetElapsedTime(stageStartedTimestamp);
            TimeSpan? recoveryTime = null;
            if (shouldRecover
                && (lastRecovery == 0 || recoveryAge >= RetryRecognitionInterval))
            {
                Interlocked.Exchange(ref _lastRecoveryTimestamp, recoveryCheckTimestamp);
                Interlocked.Exchange(ref _lastQualitySweepTimestamp, recoveryCheckTimestamp);
                stageStartedTimestamp = Stopwatch.GetTimestamp();
                var best = await RecognizeBestTextAsync(bitmap, text, _preferredTreatment);
                text = best.Text;
                _preferredTreatment = best.Treatment;
                recoveryTime = Stopwatch.GetElapsedTime(stageStartedTimestamp);
            }

            stageStartedTimestamp = Stopwatch.GetTimestamp();
            var observation = _textStabilizer.Observe(signature, text, Stopwatch.GetElapsedTime(0));
            var isReadyForTranslation = _translationReadiness.Observe(observation, text,
                Stopwatch.GetElapsedTime(0, frameStartedTimestamp));
            Volatile.Write(ref _needsConfirmation,
                !isReadyForTranslation && !string.IsNullOrWhiteSpace(observation.Text));
            stabilityCheckTime += Stopwatch.GetElapsedTime(stageStartedTimestamp);
            if (_timedRevision != observation.Revision)
            {
                _timedRevision = observation.Revision;
                _revisionStartedTimestamp = frameStartedTimestamp;
                _initialPassTotal = Stopwatch.GetElapsedTime(frameStartedTimestamp);
            }
            OcrDiagnostics.Record(observation with { IsStable = isReadyForTranslation },
                sourceText, text, _preferredTreatment, contentChanged,
                result.Lines.Count, recognizedLines.Count);
            if (Volatile.Read(ref _isDisposed) == 0)
            {
                // Repeated frames also report visual confirmation, even when
                // the accepted text is unchanged.
                TextRecognized?.Invoke(this, new OcrTextRecognizedEventArgs(
                    observation.Text, observation.Revision, isReadyForTranslation, observation.UnchangedFor,
                    new OcrProcessingTimings(_revisionStartedTimestamp, captureTime, cropTime,
                        preprocessingTime, recognitionTime, layoutTime, recoveryTime,
                        stabilityCheckTime, Stopwatch.GetElapsedTime(frameStartedTimestamp), _initialPassTotal)));
            }
        }
        catch (Exception exception)
        {
            if (Volatile.Read(ref _isDisposed) == 0 && Interlocked.Exchange(ref _errorReported, 1) == 0)
            {
                ErrorOccurred?.Invoke(this, new OcrErrorEventArgs($"英語OCRでエラーが発生しました: {exception.Message}"));
            }
        }
        finally
        {
            bitmap?.Dispose();
            frame?.Dispose();
            if (recognitionStarted)
            {
                Interlocked.Exchange(ref _isRecognizing, 0);
            }
        }
    }

    private async Task<OcrReading> RecognizeBestTextAsync(
        SoftwareBitmap source,
        string currentText,
        OcrImageTreatment currentTreatment)
    {
        var bestText = currentText;
        var bestTreatment = currentTreatment;

        try
        {
            var treatments = Enum.GetValues<OcrImageTreatment>()
                .Where(treatment => treatment != currentTreatment).ToArray();
            var readings = new OcrReading?[treatments.Length];
            var workerCount = Math.Min(2, Math.Max(1, Environment.ProcessorCount / 2));
            // Each worker owns a bitmap and OCR engine. Do not call the same
            // WinRT OCR engine concurrently, or lock the same bitmap in parallel.
            using var firstSource = SoftwareBitmap.Copy(source);
            using var secondSource = workerCount > 1 ? SoftwareBitmap.Copy(source) : null;
            var nextIndex = -1;
            async Task ReadAlternativesAsync(SoftwareBitmap workerSource, int workerIndex)
            {
                OcrEngine? engine;
                try
                {
                    engine = _recoveryEngines[workerIndex] ??=
                        OcrEngine.TryCreateFromLanguage(new Language("en-US"));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    return;
                }
                if (engine is null) return;
                int index;
                while ((index = Interlocked.Increment(ref nextIndex)) < treatments.Length)
                {
                    try
                    {
                        using var variant = OcrImagePreprocessor.CreateRecognitionBitmap(workerSource, treatments[index]);
                        var result = await engine.RecognizeAsync(variant);
                        readings[index] = new OcrReading(WindowsOcrTextLayout.ReadText(result), treatments[index]);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Retain other variants if one image is unsupported.
                    }
                    if (Volatile.Read(ref _isDisposed) != 0) return;
                }
            }
            var firstWorker = Task.Run(() => ReadAlternativesAsync(firstSource, 0));
            var secondWorker = secondSource is null ? Task.CompletedTask
                : Task.Run(() => ReadAlternativesAsync(secondSource, 1));
            await Task.WhenAll(firstWorker, secondWorker);
            // If an extra engine was unavailable or a worker failed a variant,
            // retain the original sequential fallback rather than omit a candidate.
            for (var index = 0; index < readings.Length; index++)
            {
                if (readings[index] is not null || Volatile.Read(ref _isDisposed) != 0) continue;
                try
                {
                    using var variant = OcrImagePreprocessor.CreateRecognitionBitmap(source, treatments[index]);
                    var result = await _ocrEngine.RecognizeAsync(variant);
                    readings[index] = new OcrReading(WindowsOcrTextLayout.ReadText(result), treatments[index]);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The original image remains usable when a variant is unsupported.
                }
            }
            // Preserve enum order when comparing, so task completion order does
            // not change which text wins a tie.
            foreach (var reading in readings)
            {
                if (reading is not null && OcrTextQuality.IsBetterCandidate(reading.Text, bestText))
                {
                    bestText = reading.Text;
                    bestTreatment = reading.Treatment;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Keep the best result found so far if a variant could not be created.
        }

        return new OcrReading(bestText, bestTreatment);
    }

    private sealed record OcrReading(string Text, OcrImageTreatment Treatment);

    private async Task ReleaseGraphicsDevicesAfterProcessingAsync()
    {
        while (Volatile.Read(ref _isRecognizing) != 0)
        {
            await Task.Delay(15).ConfigureAwait(false);
        }

        _captureDevice.Dispose();
        _nativeDevice.Dispose();
    }

    private static SoftwareBitmap CropBitmap(SoftwareBitmap source, OcrCaptureRegion region)
    {
        if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            using var converted = SoftwareBitmap.Convert(
                source,
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            return CropBitmap(converted, region);
        }

        var sourceWidth = source.PixelWidth;
        var sourceHeight = source.PixelHeight;
        var left = Math.Clamp((int)Math.Floor(region.Left * sourceWidth), 0, sourceWidth - 1);
        var top = Math.Clamp((int)Math.Floor(region.Top * sourceHeight), 0, sourceHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling((region.Left + region.Width) * sourceWidth), left + 1, sourceWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Top + region.Height) * sourceHeight), top + 1, sourceHeight);
        var cropWidth = right - left;
        var cropHeight = bottom - top;
        var cropped = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            cropWidth,
            cropHeight,
            BitmapAlphaMode.Premultiplied);

        try
        {
            using var sourceBuffer = source.LockBuffer(BitmapBufferAccessMode.Read);
            using var destinationBuffer = cropped.LockBuffer(BitmapBufferAccessMode.Write);
            using var sourceReference = sourceBuffer.CreateReference();
            using var destinationReference = destinationBuffer.CreateReference();

            sourceReference.As<IMemoryBufferByteAccess>().GetBuffer(out var sourcePointer, out _);
            destinationReference.As<IMemoryBufferByteAccess>().GetBuffer(out var destinationPointer, out _);

            var sourcePlane = sourceBuffer.GetPlaneDescription(0);
            var destinationPlane = destinationBuffer.GetPlaneDescription(0);
            var rowLength = checked(cropWidth * 4);
            var row = new byte[rowLength];

            for (var y = 0; y < cropHeight; y++)
            {
                var sourceOffset = sourcePlane.StartIndex
                    + ((top + y) * sourcePlane.Stride)
                    + (left * 4);
                var destinationOffset = destinationPlane.StartIndex + (y * destinationPlane.Stride);
                Marshal.Copy(IntPtr.Add(sourcePointer, sourceOffset), row, 0, rowLength);
                Marshal.Copy(row, 0, IntPtr.Add(destinationPointer, destinationOffset), rowLength);
            }

            return cropped;
        }
        catch
        {
            cropped.Dispose();
            throw;
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr windowHandle, [In] ref Guid interfaceId);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}

public sealed class OcrTextRecognizedEventArgs(
    string text, long revision, bool isStable, TimeSpan unchangedFor, OcrProcessingTimings timings) : EventArgs
{
    public string Text { get; } = text;
    public long Revision { get; } = revision;
    public bool IsStable { get; } = isStable;
    public TimeSpan UnchangedFor { get; } = unchangedFor;
    public OcrProcessingTimings Timings { get; } = timings;
}

public sealed class OcrErrorEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}
