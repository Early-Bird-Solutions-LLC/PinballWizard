namespace PinballWizard.Application.Sync;

// Stern's live document title for /game/iron-maiden/ is
// "Iron Maiden Game Page - Stern Pinball". The franchise is "Iron Maiden".
// Leaving the chrome in place makes the title fail the subtitle-superset
// check against "Iron Maiden: Legacy of the Beast", so the era rule never
// runs and the slug fast path keeps iron-maiden on the 1981 machine (#596).
public static class SternPageTitle
{
    private static readonly string[] SiteSuffixes =
    [
        " | Stern Pinball",
        " - Stern Pinball",
    ];

    private const string GamePageSuffix = " Game Page";

    public static string WithoutChrome(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var trimmed = title.Trim();
        while (true)
        {
            var next = StripOnce(trimmed);
            if (next.Length == trimmed.Length) return trimmed;
            trimmed = next;
        }
    }

    private static string StripOnce(string trimmed)
    {
        foreach (var suffix in SiteSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length].Trim();
                break;
            }
        }

        if (trimmed.EndsWith(GamePageSuffix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^GamePageSuffix.Length].Trim();

        return trimmed;
    }
}
