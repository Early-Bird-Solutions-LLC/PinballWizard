using System.Globalization;
using PinballWizard.Application.Tournaments;

namespace PinballWizard.Infrastructure.Integrations.MatchPlay;

// Parses Match Play Ratings CDN CSV (latest-ratings.csv).
// Header (observed 2026-11): Name,User ID,IFPA ID,Rating,RD,Lower Bound,
// Last Rating Period (Data from: YYYY-MM-DD),Rating class
internal static class MatchPlayRatingsCsvParser
{
    public static IReadOnlyList<MatchPlayRatingRow> Parse(string csv)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(csv);

        using var reader = new StringReader(csv);
        var header = reader.ReadLine();
        if (header is null || !header.Contains("User ID", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Match Play Ratings CSV header missing 'User ID' — confirm MatchPlay:RatingsExportUrl " +
                "points at https://mp-ratings.sfo3.cdn.digitaloceanspaces.com/latest-ratings.csv");
        }

        var rows = new List<MatchPlayRatingRow>(capacity: 160_000);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseLine(line, out var row))
            {
                continue;
            }

            rows.Add(row);
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException(
                "Match Play Ratings CSV parsed zero rows — refuse empty index (fail closed).");
        }

        return rows;
    }

    private static bool TryParseLine(string line, out MatchPlayRatingRow row)
    {
        row = null!;
        var fields = SplitCsvLine(line);
        // Name, User ID, IFPA ID, Rating, RD, Lower Bound, Last Rating Period, Rating class
        if (fields.Count < 7)
        {
            return false;
        }

        var name = fields[0].Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            || userId <= 0)
        {
            return false;
        }

        int? ifpaId = null;
        if (!string.IsNullOrWhiteSpace(fields[2])
            && int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIfpa)
            && parsedIfpa > 0)
        {
            ifpaId = parsedIfpa;
        }

        if (!double.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
        {
            return false;
        }

        if (!double.TryParse(fields[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var rd))
        {
            return false;
        }

        if (!double.TryParse(fields[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var lowerBound))
        {
            return false;
        }

        var lastPeriod = string.IsNullOrWhiteSpace(fields[6]) ? null : fields[6].Trim();
        int? ratingClass = null;
        if (fields.Count > 7
            && int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rc))
        {
            ratingClass = rc;
        }

        row = new MatchPlayRatingRow(name, userId, ifpaId, rating, rd, lowerBound, lastPeriod, ratingClass);
        return true;
    }

    // Minimal CSV splitter that honors double-quoted fields with commas.
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>(8);
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }
}
