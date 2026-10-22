using Windows.Media.Ocr;

namespace speciRPG.App.Ocr;

internal static class WindowsOcrTextLayout
{
    public static IReadOnlyList<string> ReadLines(OcrResult result)
    {
        var lines = OcrTextLayout.BuildLines(result.Lines.SelectMany(line => line.Words).Select(word =>
            new OcrWordBox(word.Text, word.BoundingRect.X, word.BoundingRect.Y,
                word.BoundingRect.Width, word.BoundingRect.Height)));

        // Preserve results from an engine which supplies text but no usable boxes.
        return lines.Count > 0 ? lines : result.Lines.Select(line => line.Text.Trim())
            .Where(line => line.Length > 0).ToArray();
    }

    public static string ReadText(OcrResult result) => string.Join(Environment.NewLine, ReadLines(result));
}
