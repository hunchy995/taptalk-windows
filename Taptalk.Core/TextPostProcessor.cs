namespace Taptalk.Core;

public static class TextPostProcessor
{
    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "um", "uh", "er", "ah", "hmm", "mmm", "like", "you know", "i mean",
        "sort of", "kind of", "basically", "actually", "literally", "okay", "so"
    };

    private static readonly HashSet<string> QuestionStarters = new(StringComparer.OrdinalIgnoreCase)
    {
        "who", "what", "where", "when", "why", "how", "is", "are", "was", "were",
        "do", "does", "did", "can", "could", "would", "will", "shall", "should",
        "may", "might", "have", "has", "had", "am"
    };

    private static readonly HashSet<string> EmphaticWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "please", "wow", "amazing", "great", "awesome", "love", "hate", "yes", "no"
    };

    private static readonly HashSet<string> ConfirmationEnders = new(StringComparer.OrdinalIgnoreCase)
    {
        "right", "okay", "correct"
    };

    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var cleaned = raw.Trim();

        // 1. Remove filler words
        foreach (var filler in Fillers)
        {
            cleaned = RegexReplace(cleaned, $@"\b{RegexEscape(filler)}\b", "", RegexOptions.IgnoreCase);
        }

        // 2. Remove repeated words
        cleaned = RegexReplace(cleaned, @"\b(\w+)\s+\1\b", "$1", RegexOptions.IgnoreCase);

        // 3. Clean whitespace
        cleaned = RegexReplace(cleaned, @"\s+", " ");
        cleaned = RegexReplace(cleaned, @"\s([.,!?;:])", "$1");

        // 4. Lowercase everything
        cleaned = cleaned.ToLowerInvariant();

        // 5. Capitalize standalone "i"
        cleaned = RegexReplace(cleaned, @"\bi\b", "I");

        // 6. Capitalize contractions
        cleaned = RegexReplace(cleaned, @"\bi'(m|ll|ve|d|re)\b", m => $"I'{m.Groups[1].Value}");

        // 7. Remove stray standalone punctuation
        cleaned = RegexReplace(cleaned, @"^[.,!?;:]+\s*", "");
        cleaned = RegexReplace(cleaned, @"\s*[.,!?;:]+$", "");

        // 8. Fix multiple punctuation
        cleaned = RegexReplace(cleaned, @"([!?.,])\1+", "$1");

        // 9. Remove periods mid-sentence (whisper artifact)
        cleaned = RegexReplace(cleaned, @"(?<=\w)\.(?=\s[a-z])", "");

        // 10. Clean comma artifacts
        cleaned = RegexReplace(cleaned, @",\.", ".");
        cleaned = RegexReplace(cleaned, @", ,", ",");

        // 11. Sentence case
        cleaned = cleaned.Length > 0 ? char.ToUpperInvariant(cleaned[0]) + cleaned[1..] : cleaned;

        // 12. Capitalize after sentence-ending punctuation
        cleaned = RegexReplace(cleaned, @"([.!?])\s+([a-z])",
            m => $"{m.Groups[1].Value} {char.ToUpperInvariant(m.Groups[2].Value[0])}{m.Groups[2].Value[1..]}");

        // 13. Smart ending punctuation
        var last = cleaned.LastOrDefault();
        if (char.IsLetter(last) && cleaned.Count(c => c == ' ') >= 2)
        {
            var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var firstWord = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            var lastWord = words.Length > 0 ? words[^1].ToLowerInvariant() : "";

            if (QuestionStarters.Contains(firstWord) || ConfirmationEnders.Contains(lastWord))
                cleaned += "?";
            else if (words.Any(w => EmphaticWords.Contains(w)))
                cleaned += "!";
            else
                cleaned += ".";
        }

        return cleaned.Trim();
    }

    private static string RegexReplace(string input, string pattern, string replacement, RegexOptions options = RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement, options);

    private static string RegexReplace(string input, string pattern, MatchEvaluator evaluator, RegexOptions options = RegexOptions.None)
        => System.Text.RegularExpressions.Regex.Replace(input, pattern, evaluator, options);

    private static string RegexEscape(string s) => System.Text.RegularExpressions.Regex.Escape(s);
}
