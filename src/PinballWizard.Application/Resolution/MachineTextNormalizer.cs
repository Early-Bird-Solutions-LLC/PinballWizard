using System.Globalization;
using System.Text;

namespace PinballWizard.Application.Resolution;

// The single normalizer for every text→machine match in the system (ADR-0054).
// It is intended to replace five divergent normalizers. Nothing consumes it yet — the six
// consumer migrations land later, and until they do the old normalizers remain in place.
//
// Folding '&' to "and" is the one deliberate behavioural delta from the normalizer it most
// directly supersedes (LinkingUtilities.NormalizeForMatch, which treats '&' as a separator).
// It is what will let us delete the &/and retry loop in MachineGroundingTool, which exists
// solely to bridge that inconsistency between two of our own normalizers.
public static class MachineTextNormalizer
{
    public static string Key(string? text) => string.Join(' ', Tokenize(text));

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var bounded = InsertTokenBoundaries(FoldDiacritics(text));

        var sb = new StringBuilder(bounded.Length + 8);
        foreach (var c in bounded)
        {
            // Apostrophes vanish rather than splitting: "Barry O's" → "barry os", so the
            // token survives as one word instead of degrading into a stray "s".
            if (c is '\'' or '’') continue;
            if (c == '&') { sb.Append(" and "); continue; }
            if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }
            sb.Append(' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string FoldDiacritics(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // Splits camelCase and letter/digit runs so "HotWheels" → "Hot Wheels" while the
    // already-joined "Hotwheels" is left alone (both forms occur in real AP filenames).
    private static string InsertTokenBoundaries(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0)
            {
                var p = s[i - 1];
                var boundary =
                    (char.IsLower(p) && char.IsUpper(c)) ||
                    (char.IsLetter(p) && char.IsDigit(c)) ||
                    (char.IsDigit(p) && char.IsLetter(c)) ||
                    (i + 1 < s.Length && char.IsUpper(p) && char.IsUpper(c) && char.IsLower(s[i + 1]));
                if (boundary) sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
