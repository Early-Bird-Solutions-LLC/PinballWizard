using System.Text;
using System.Text.Json;
using PinballWizard.Domain.Abstractions;
using PinballWizard.Domain.Models;

namespace PinballWizard.Processor.Pipeline;

public sealed class JsonExtractor : IContentExtractor
{
    public string Name => "JsonExtractor";

    public bool CanExtract(string mimeType, string fileExtension)
        => mimeType is "application/json" || fileExtension is ".json";

    public async Task<ExtractionResult> ExtractAsync(Stream content, string filename, CancellationToken ct = default)
    {
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: ct);
        var root = document.RootElement;

        var sections = new List<TextSection>();
        var fullText = new StringBuilder();
        var metadata = new Dictionary<string, string>
        {
            ["extractor"] = Name,
            ["filename"] = filename
        };

        // Detect the source type from the JSON structure and extract accordingly
        if (IsOpdbRecord(root))
        {
            ExtractOpdb(root, sections, fullText, metadata);
        }
        else if (IsPinballMapRecord(root))
        {
            ExtractPinballMap(root, sections, fullText, metadata);
        }
        else if (IsIfpaRecord(root))
        {
            ExtractIfpa(root, sections, fullText, metadata);
        }
        else
        {
            ExtractGenericJson(root, sections, fullText, string.Empty);
        }

        return new ExtractionResult
        {
            Text = fullText.ToString(),
            Sections = sections,
            Metadata = metadata
        };
    }

    private static bool IsOpdbRecord(JsonElement root)
        => root.TryGetProperty("opdb_id", out _)
        || root.TryGetProperty("manufacturer", out _) && root.TryGetProperty("machine_type", out _);

    private static bool IsPinballMapRecord(JsonElement root)
        => root.TryGetProperty("location", out _) && root.TryGetProperty("machine_conditions", out _)
        || root.TryGetProperty("num_machines", out _);

    private static bool IsIfpaRecord(JsonElement root)
        => root.TryGetProperty("player_id", out _) || root.TryGetProperty("tournament_id", out _)
        || root.TryGetProperty("wppr_points", out _);

    private static void ExtractOpdb(JsonElement root, List<TextSection> sections, StringBuilder fullText, Dictionary<string, string> metadata)
    {
        metadata["sourceType"] = nameof(SourceType.OpdbApi);

        AppendField(root, "name", "Machine Name", sections, fullText);
        AppendField(root, "manufacturer", "Manufacturer", sections, fullText);
        AppendField(root, "machine_type", "Machine Type", sections, fullText);
        AppendField(root, "year", "Year", sections, fullText);
        AppendField(root, "theme", "Theme", sections, fullText);
        AppendField(root, "design_by", "Designed By", sections, fullText);
        AppendField(root, "art_by", "Art By", sections, fullText);
        AppendField(root, "dots_animation_by", "Dots/Animation By", sections, fullText);
        AppendField(root, "mechanical_by", "Mechanical By", sections, fullText);
        AppendField(root, "music_by", "Music By", sections, fullText);
        AppendField(root, "software_by", "Software By", sections, fullText);
        AppendField(root, "sound_by", "Sound By", sections, fullText);
        AppendField(root, "players", "Players", sections, fullText);
        AppendField(root, "description", "Description", sections, fullText);

        if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            var featureText = new StringBuilder();
            foreach (var feature in features.EnumerateArray())
            {
                featureText.AppendLine($"- {feature.GetString()}");
            }
            if (featureText.Length > 0)
            {
                var text = featureText.ToString().TrimEnd();
                sections.Add(new TextSection { Content = text, Heading = "Features", Level = 2 });
                fullText.AppendLine(text);
            }
        }
    }

    private static void ExtractPinballMap(JsonElement root, List<TextSection> sections, StringBuilder fullText, Dictionary<string, string> metadata)
    {
        metadata["sourceType"] = nameof(SourceType.PinballMapApi);

        AppendField(root, "name", "Location Name", sections, fullText);
        AppendField(root, "street", "Street", sections, fullText);
        AppendField(root, "city", "City", sections, fullText);
        AppendField(root, "state", "State", sections, fullText);
        AppendField(root, "zip", "ZIP", sections, fullText);
        AppendField(root, "country", "Country", sections, fullText);
        AppendField(root, "num_machines", "Number of Machines", sections, fullText);
        AppendField(root, "operator", "Operator", sections, fullText);

        if (root.TryGetProperty("machine_conditions", out var machines) && machines.ValueKind == JsonValueKind.Array)
        {
            foreach (var machine in machines.EnumerateArray())
            {
                var machineName = machine.TryGetProperty("name", out var n) ? n.GetString() : "Unknown";
                var condition = machine.TryGetProperty("condition", out var c) ? c.GetString() : "";
                var text = $"{machineName}: {condition}";
                sections.Add(new TextSection { Content = text, Heading = "Machine Conditions", Level = 2 });
                fullText.AppendLine(text);
            }
        }
    }

    private static void ExtractIfpa(JsonElement root, List<TextSection> sections, StringBuilder fullText, Dictionary<string, string> metadata)
    {
        metadata["sourceType"] = nameof(SourceType.IfpaApi);

        AppendField(root, "first_name", "First Name", sections, fullText);
        AppendField(root, "last_name", "Last Name", sections, fullText);
        AppendField(root, "city", "City", sections, fullText);
        AppendField(root, "state", "State", sections, fullText);
        AppendField(root, "country_name", "Country", sections, fullText);
        AppendField(root, "wppr_rank", "WPPR Rank", sections, fullText);
        AppendField(root, "wppr_points", "WPPR Points", sections, fullText);
        AppendField(root, "tournament_name", "Tournament Name", sections, fullText);
        AppendField(root, "event_name", "Event Name", sections, fullText);
        AppendField(root, "event_date", "Event Date", sections, fullText);
    }

    private static void ExtractGenericJson(JsonElement element, List<TextSection> sections, StringBuilder fullText, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path} > {property.Name}";
                    ExtractGenericJson(property.Value, sections, fullText, childPath);
                }
                break;

            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ExtractGenericJson(item, sections, fullText, $"{path}[{index}]");
                    index++;
                }
                break;

            default:
                var value = element.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var heading = string.IsNullOrEmpty(path) ? null : path;
                    var content = heading is not null ? $"{heading}: {value}" : value;
                    sections.Add(new TextSection { Content = content, Heading = heading, Level = 0 });
                    fullText.AppendLine(content);
                }
                break;
        }
    }

    private static void AppendField(JsonElement root, string propertyName, string heading, List<TextSection> sections, StringBuilder fullText)
    {
        if (!root.TryGetProperty(propertyName, out var value)) return;

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text)) return;

        var content = $"{heading}: {text}";
        sections.Add(new TextSection { Content = content, Heading = heading, Level = 2 });
        fullText.AppendLine(content);
    }
}
