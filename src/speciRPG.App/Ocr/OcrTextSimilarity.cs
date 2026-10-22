namespace speciRPG.App.Ocr;

/// <summary>Groups OCR variants without constructing text that was never read.</summary>
internal static class OcrTextSimilarity
{
    public static string Key(string text)
    {
        var key = new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        // Ellipses and question marks can be an entire RPG dialogue line.
        return key.Length > 0 ? key : new string(text.Where(character => !char.IsWhiteSpace(character)).ToArray());
    }

    public static bool IsSamePassage(string left, string right)
    {
        if (left == right) return true;
        var length = Math.Max(left.Length, right.Length);
        if (length == 0) return true;
        var maximumEdits = (int)(length * (length < 24 ? 0.15 : 0.28));
        if (Math.Abs(left.Length - right.Length) > maximumEdits) return false;

        var prefix = 0;
        while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix]) prefix++;
        var leftEnd = left.Length;
        var rightEnd = right.Length;
        while (leftEnd > prefix && rightEnd > prefix && left[leftEnd - 1] == right[rightEnd - 1])
        {
            leftEnd--;
            rightEnd--;
        }

        var a = left.AsSpan(prefix, leftEnd - prefix);
        var b = right.AsSpan(prefix, rightEnd - prefix);
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length) <= maximumEdits;
        if ((long)a.Length * b.Length > 1_000_000) return false;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var row = 1; row <= a.Length; row++)
        {
            current[0] = row;
            var smallest = row;
            for (var column = 1; column <= b.Length; column++)
            {
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (a[row - 1] == b[column - 1] ? 0 : 1));
                smallest = Math.Min(smallest, current[column]);
            }
            if (smallest > maximumEdits) return false;
            (previous, current) = (current, previous);
        }
        return previous[b.Length] <= maximumEdits;
    }
}
