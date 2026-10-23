namespace BridgEN.App.Ocr;

/// <summary>Heuristics for deciding when to retry OCR and comparing alternate readings.</summary>
internal static class OcrTextQuality
{
    private const string ExpectedPunctuation = ".,!?;:'\"()-[]/";
    private const string TrimPunctuation = ".,!?;:'\"()[]{}";

    private static readonly HashSet<string> CommonEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "above", "after", "again", "all", "almost", "along", "already", "also", "always",
        "am", "an", "and", "another", "any", "are", "around", "as", "ask", "at", "away", "back",
        "be", "because", "become", "been", "before", "being", "below", "between", "both", "but", "by",
        "call", "came", "can", "cannot", "come", "could", "day", "did", "different", "do", "does",
        "doing", "done", "down", "each", "end", "enough", "even", "ever", "every", "few", "find",
        "first", "for", "found", "from", "get", "give", "go", "going", "good", "got", "great", "had",
        "has", "have", "he", "help", "her", "here", "hers", "him", "his", "how", "however", "i", "if",
        "in", "into", "is", "it", "its", "just", "keep", "know", "last", "later", "left", "let", "like",
        "little", "long", "look", "made", "make", "many", "may", "me", "might", "more", "most", "much",
        "must", "my", "name", "near", "never", "new", "next", "no", "not", "now", "of", "off", "on",
        "once", "one", "only", "or", "other", "our", "out", "over", "own", "people", "please", "right",
        "said", "same", "say", "see", "she", "should", "so", "some", "something", "still", "such", "take",
        "tell", "than", "that", "the", "their", "them", "then", "there", "these", "they", "thing", "think",
        "this", "those", "through", "time", "to", "too", "try", "two", "under", "until", "up", "us", "use",
        "very", "want", "was", "way", "we", "well", "were", "what", "when", "where", "which", "while",
        "who", "why", "will", "with", "without", "won't", "would", "yes", "you", "your", "you're",
        "adventure", "armor", "attack", "battle", "castle", "companion", "creature", "curse", "danger",
        "dark", "death", "demon", "dragon", "enemy", "escape", "fight", "fire", "forest", "friend", "gate",
        "ghost", "gold", "guard", "hero", "king", "kingdom", "knight", "life", "magic", "map", "monster",
        "power", "prince", "princess", "quest", "return", "save", "secret", "skill", "soul", "spell", "sword",
        "treasure", "weapon", "world", "wizard"
    };

    private static readonly HashSet<string> CommonHyphenatedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "first-class", "in-game", "long-term", "non-player", "real-time", "role-playing", "side-quest",
        "state-of-the-art", "turn-based", "well-being", "well-known"
    };

    public static bool ShouldRetry(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (text.Any(character => character > 127
            || (!char.IsLetterOrDigit(character)
                && !char.IsWhiteSpace(character)
                && !ExpectedPunctuation.Contains(character))))
        {
            return true;
        }

        var words = GetWords(text).ToArray();
        if (words.Length == 1 && words[0].Length >= 3)
        {
            return true;
        }

        return words.Any(IsSuspiciousWord);
    }

    /// <summary>
    /// Gives preference to readable English word boundaries and ordinary word shapes.
    /// This is only a tie-breaker: Windows OCR does not expose a confidence value.
    /// </summary>
    public static double Score(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return double.NegativeInfinity;
        }

        var score = 0d;
        foreach (var character in text)
        {
            if (char.IsAsciiLetter(character))
            {
                score += 0.15;
            }
            else if (char.IsAsciiDigit(character))
            {
                score += 0.05;
            }
            else if (char.IsWhiteSpace(character))
            {
                score += 0.4;
            }
            else if (ExpectedPunctuation.Contains(character))
            {
                score += 0.1;
            }
            else
            {
                score -= 3;
            }
        }

        foreach (var word in GetWords(text))
        {
            var normalized = word.Trim(TrimPunctuation.ToCharArray());
            if (normalized.Length == 0)
            {
                continue;
            }

            if (CommonEnglishWords.Contains(normalized))
            {
                score += 2;
            }
            // Ordinary long words and names are valid. Penalizing them made
            // deleting words such as "rehabilitation" improve the score.
            if (normalized.Length >= 24)
            {
                score -= 1 + ((normalized.Length - 23) * 0.4);
            }

            if (HasInternalUppercase(normalized))
            {
                score -= 1.5;
            }

            if (normalized.Contains('-') && !CommonHyphenatedWords.Contains(normalized))
            {
                score -= 1.5;
            }
        }

        return score;
    }

    public static bool IsBetterCandidate(string candidate, string current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        var currentScore = Score(current);
        return Score(candidate) > currentScore + Math.Max(1, Math.Abs(currentScore) * 0.02);
    }

    public static string Normalize(string text) =>
        string.Join(Environment.NewLine,
            text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(line => string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Where(line => line.Length > 0));

    private static bool IsSuspiciousWord(string word)
    {
        var normalized = word.Trim(TrimPunctuation.ToCharArray());
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.Length >= 24 && !CommonEnglishWords.Contains(normalized))
        {
            return true;
        }

        if (HasInternalUppercase(normalized))
        {
            return true;
        }

        return normalized.Contains('-') && !CommonHyphenatedWords.Contains(normalized);
    }

    private static bool HasInternalUppercase(string word)
    {
        for (var index = 1; index < word.Length; index++)
        {
            if (char.IsUpper(word[index]) && char.IsLower(word[0]))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
