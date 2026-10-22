using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using speciRPG.App.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Windows.Storage;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0) return await InspectImageAsync(args[0]);
        const string passage = "Rehabilitation training plan:\nAfter the subject is awake, please be sure\nto instruct the subject to complete\nthe rehabilitation training under surveillance";
        const string nextText = "Do not enter.\nThe training room is closed.";
        // Render fixtures while still on the STA thread. No user windows, network,
        // game memory, or API credentials are used by this integration check.
        var frames = Enumerable.Range(0, 3).Select(index => Render(passage, index)).ToArray();
        using var nextFrame = Render(nextText, 0);
        try
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                ?? throw new InvalidOperationException("Install Windows en-US OCR before running this check.");
            var bestText = string.Empty;
            var selected = OcrImageTreatment.Original;
            foreach (var treatment in Enum.GetValues<OcrImageTreatment>())
            {
                using var prepared = OcrImagePreprocessor.CreateRecognitionBitmap(frames[0], treatment);
                var reading = await engine.RecognizeAsync(prepared);
                var text = WindowsOcrTextLayout.ReadText(reading);
                if (OcrTextQuality.IsBetterCandidate(text, bestText)) { bestText = text; selected = treatment; }
            }
            if (!bestText.Contains("Rehabilitation", StringComparison.OrdinalIgnoreCase)
                || !bestText.Contains("subject", StringComparison.OrdinalIgnoreCase))
                throw new Exception("The generated English fixture was not read by Windows OCR.");

            var stabilizer = new OcrTextStabilizer();
            var ready = 0;
            for (var pass = 0; pass < 8; pass++)
            {
                var source = frames[pass % frames.Length];
                using var prepared = OcrImagePreprocessor.CreateRecognitionBitmap(source, selected);
                var reading = await engine.RecognizeAsync(prepared);
                var text = WindowsOcrTextLayout.ReadText(reading);
                var result = stabilizer.Observe(OcrImagePreprocessor.CreateFrameSignature(source), text, TimeSpan.FromMilliseconds(pass * 800));
                if (result.IsStable && result.UnchangedFor >= TimeSpan.FromSeconds(1)) ready++;
                Console.WriteLine($"frame={pass} treatment={selected} stable={result.IsStable} reason={result.Reason} visualChanged={result.VisualChanged}");
            }
            if (ready < 3) throw new Exception("The actual OCR stream did not settle over the generated moving background.");

            using var nextPrepared = OcrImagePreprocessor.CreateRecognitionBitmap(nextFrame, selected);
            var nextReading = await engine.RecognizeAsync(nextPrepared);
            var next = stabilizer.Observe(OcrImagePreprocessor.CreateFrameSignature(nextFrame),
                WindowsOcrTextLayout.ReadText(nextReading), TimeSpan.FromSeconds(8));
            if (!next.Text.Contains("closed", StringComparison.OrdinalIgnoreCase) || next.IsStable)
                throw new Exception("The real OCR reading did not invalidate the old dialogue.");
            Console.WriteLine($"PASS Windows en-US OCR: {ready}/8 frames ready; next passage replaces the old text.");
            await InspectImageAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "tutorial-controls.png"));
            return 0;
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static async Task<int> InspectImageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(System.IO.Path.GetFullPath(path));
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? throw new InvalidOperationException("Windows en-US OCR is required.");
        var bestText = string.Empty;
        var bestTreatment = OcrImageTreatment.Original;
        var highContrastVerified = false;
        foreach (var treatment in Enum.GetValues<OcrImageTreatment>())
        {
            using var prepared = OcrImagePreprocessor.CreateRecognitionBitmap(bitmap, treatment);
            var result = await engine.RecognizeAsync(prepared);
            var lines = WindowsOcrTextLayout.ReadLines(result);
            var text = string.Join(Environment.NewLine, lines);
            Console.WriteLine($"{treatment}: OCR blocks={result.Lines.Count}, visual rows={lines.Count}, score={OcrTextQuality.Score(text):F2}");
            foreach (var line in lines) Console.WriteLine($"  {line}");
            if (OcrTextQuality.IsBetterCandidate(text, bestText)) { bestText = text; bestTreatment = treatment; }
            if (treatment == OcrImageTreatment.HighContrast)
            {
                var expected = new[] { "Hold [Arrow keys] to move", "Hold [Shift] to run", "Tap [Z/Space] to Investigate or talk" };
                if (!lines.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
                    throw new Exception("The provided tutorial screenshot did not reconstruct into its three instruction rows.");
                highContrastVerified = true;
            }
        }
        if (!highContrastVerified || bestText.Split(Environment.NewLine).Length != 3
            || !bestText.Contains("Hold [Shift] to run", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Candidate selection lost the reconstructed tutorial layout.");
        Console.WriteLine($"PASS supplied tutorial screenshot: selected={bestTreatment}; 3 rows with matching key labels and actions.");
        return 0;
    }

    private static SoftwareBitmap Render(string text, int phase)
    {
        const int width = 1000, height = 240;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(28, 30, 35)), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(Brushes.LightGray, null, new Rect(850 + phase * 35, 10, 20, 210));
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 28, Brushes.White, 1.0) { LineHeight = 42 };
            drawing.DrawText(formatted, new Point(20, 20));
            for (var y = 0; y < height; y += 4)
                drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(45, 0, 0, 0)), null, new Rect(0, y, width, 1));
        }
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var pixels = new byte[width * height * 4];
        rendered.CopyPixels(pixels, width * 4, 0);
        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        bitmap.CopyFromBuffer(writer.DetachBuffer());
        return bitmap;
    }
}
