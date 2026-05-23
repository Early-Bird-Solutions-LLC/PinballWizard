namespace PinballWizard.Application.Linking;

/// <summary>
/// Shared utilities for document-to-game linking: slug normalization,
/// word-boundary matching, and edition extraction.
/// </summary>
public static class LinkingUtilities
{
    // Edition markers in priority order (longer strings first to avoid
    // "le" winning before "limited" when both appear).
    private static readonly (string Marker, string Canonical)[] EditionMarkers =
    [
        ("premium", "Premium"),
        ("limited", "Limited"),
        ("pro", "Pro"),
        ("le", "LE"),
        ("vault", "Vault"),
        ("ce", "CE"),
    ];

    /// <summary>
    /// Normalizes a string for slug-substring matching: lowercases, then replaces
    /// <c>_</c>, <c>-</c>, <c>.</c>, and runs of whitespace with single spaces.
    /// This preserves word boundaries for <see cref="IsWordBoundaryMatch"/> to work correctly.
    /// For example, <c>stranger-things</c>, <c>StrangerThings</c>, and <c>stranger_things</c>
    /// all normalize to <c>stranger things</c>.
    /// </summary>
    public static string NormalizeForMatch(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var lower = value.ToLowerInvariant();
        // Replace runs of separators and whitespace with single spaces
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastWasSeparator = false;
        foreach (var c in lower)
        {
            var isSeparator = c == '_' || c == '-' || c == '.' || char.IsWhiteSpace(c);
            if (isSeparator)
            {
                if (!lastWasSeparator)
                {
                    sb.Append(' ');
                }
                lastWasSeparator = true;
            }
            else
            {
                sb.Append(c);
                lastWasSeparator = false;
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Word-boundary match on already-normalized strings.
    /// Pads both sides with a space so short slugs like "tron" or "kiss"
    /// don't match mid-word (e.g., "tron" inside "electronic").
    /// Both <paramref name="normText"/> and <paramref name="normSlug"/> must
    /// already be <see cref="NormalizeForMatch"/> output.
    /// </summary>
    public static bool IsWordBoundaryMatch(string normText, string normSlug)
    {
        if (string.IsNullOrEmpty(normSlug) || string.IsNullOrEmpty(normText))
            return false;
        var paddedText = " " + normText + " ";
        var paddedSlug = " " + normSlug + " ";
        return paddedText.Contains(paddedSlug, StringComparison.Ordinal);
    }

    /// <summary>
    /// Scans <paramref name="normalizedText"/> for any edition marker anywhere in the
    /// string. Used when we have link_text but no slug position to anchor from.
    /// </summary>
    public static string? ExtractEditionFromText(string normalizedText)
    {
        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (normalizedText.Contains(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
    }

    /// <summary>
    /// Anchored edition extraction: finds the slug within <paramref name="normFilename"/>,
    /// then checks what immediately follows for an edition marker.
    /// Skips any leading space and checks if the next token starts with an edition marker.
    /// </summary>
    public static string? ExtractEdition(string normFilename, string normSlug)
    {
        var idx = normFilename.IndexOf(normSlug, StringComparison.Ordinal);
        if (idx < 0) return null;

        var afterSlug = idx + normSlug.Length;
        if (afterSlug >= normFilename.Length) return null;

        var tail = normFilename[afterSlug..];

        // Skip leading space if present
        if (tail.StartsWith(' '))
        {
            tail = tail[1..];
        }

        if (tail.Length == 0) return null;

        foreach (var (marker, canonical) in EditionMarkers)
        {
            if (tail.StartsWith(marker, StringComparison.Ordinal))
                return canonical;
        }
        return null;
    }

    /// <summary>
    /// Extracts the game slug from a URL of the form
    /// <c>https://sternpinball.com/game/{slug}[/...]</c>.
    /// </summary>
    public static string? ExtractGameSlugFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("game", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }
        return null;
    }
}
