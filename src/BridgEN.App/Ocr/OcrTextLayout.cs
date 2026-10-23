using System.Text;

namespace BridgEN.App.Ocr;

internal sealed record OcrWordBox(string Text, double X, double Y, double Width, double Height)
{
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// Reconstructs horizontal visual rows instead of trusting OCR block order.
/// Windows OCR can report aligned instruction labels as independent columns.
/// </summary>
internal static class OcrTextLayout
{
    public static IReadOnlyList<string> BuildLines(IEnumerable<OcrWordBox> words)
    {
        var rows = new List<Row>();
        foreach (var word in words.Where(IsValid).OrderBy(word => word.CenterY).ThenBy(word => word.X))
        {
            Row? bestRow = null;
            var bestDistance = double.PositiveInfinity;
            foreach (var row in rows)
            {
                var height = Math.Min(word.Height, row.Height);
                var distance = Math.Abs(word.CenterY - row.CenterY);
                var overlap = Math.Min(word.Y + word.Height, row.CenterY + row.Height / 2)
                    - Math.Max(word.Y, row.CenterY - row.Height / 2);
                if (distance <= Math.Max(1, height * 0.5) && overlap >= height * 0.4
                    && distance / height < bestDistance)
                {
                    bestRow = row;
                    bestDistance = distance / height;
                }
            }

            if (bestRow is null) rows.Add(new Row(word));
            else bestRow.Add(word);
        }

        return rows.OrderBy(row => row.CenterY)
            .Select(row => JoinWords(row.Words.OrderBy(word => word.X).ThenBy(word => word.Y)))
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static bool IsValid(OcrWordBox word) =>
        !string.IsNullOrWhiteSpace(word.Text) && word.Width > 0 && word.Height > 0
        && double.IsFinite(word.X) && double.IsFinite(word.Y)
        && double.IsFinite(word.Width) && double.IsFinite(word.Height);

    private static string JoinWords(IEnumerable<OcrWordBox> words)
    {
        var text = new StringBuilder();
        foreach (var word in words)
        {
            var token = word.Text.Trim();
            if (text.Length > 0
                && !")]},.!?:;/".Contains(token[0])
                && !"([{/".Contains(text[^1]))
            {
                text.Append(' ');
            }
            text.Append(token);
        }
        return OcrTextQuality.Normalize(text.ToString());
    }

    private sealed class Row
    {
        public List<OcrWordBox> Words { get; } = [];
        public double CenterY { get; private set; }
        public double Height { get; private set; }

        public Row(OcrWordBox word) => Add(word);

        public void Add(OcrWordBox word)
        {
            Words.Add(word);
            // Medians prevent a tall bracket or a small descender from pulling
            // a row toward the next one and progressively merging adjacent rows.
            CenterY = Median(Words.Select(value => value.CenterY));
            Height = Median(Words.Select(value => value.Height));
        }

        private static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(value => value).ToArray();
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2;
        }
    }
}
