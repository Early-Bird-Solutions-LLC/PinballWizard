using System.Text.Json;

namespace PinballWizard.Infrastructure.Integrations.PinTips;

public sealed record PinTip(int TipId, string OpdbId, string Category, int VoteTotal, string Text);

public static class PinTipsParser
{
    public static IReadOnlyList<PinTip> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "PinTips export is not a JSON array. Expected https://mp-data.sfo3.cdn.digitaloceanspaces.com/latest-pintips.json.");
        }

        var tips = new List<PinTip>();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("tipId", out var idEl) || !idEl.TryGetInt32(out var tipId))
            {
                continue;
            }

            var opdbId = ReadString(row, "opdbId");
            var text = ReadString(row, "text");
            if (string.IsNullOrWhiteSpace(opdbId) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var category = ReadString(row, "category");
            var votes = row.TryGetProperty("voteTotal", out var votesEl) && votesEl.TryGetInt32(out var n) ? n : 0;
            tips.Add(new PinTip(tipId, opdbId.Trim(), string.IsNullOrWhiteSpace(category) ? "general" : category.Trim(), votes, text.Trim()));
        }

        return tips;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
