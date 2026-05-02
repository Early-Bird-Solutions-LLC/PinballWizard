using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

/// <summary>
/// Adapter that lets the Cosmos SDK use <c>System.Text.Json</c> instead
/// of its default Newtonsoft.Json serializer. The SDK's signed contract
/// is <see cref="CosmosSerializer"/>; this implementation delegates to
/// <see cref="JsonSerializer"/> with the supplied options.
/// </summary>
/// <remarks>
/// Why bother: the rest of the codebase already uses
/// <c>System.Text.Json</c> (catalog.json round-trip, all DTO
/// serialization). Using a single JSON stack means one set of
/// serialization rules (camelCase naming, null handling, etc.) applies
/// everywhere, and AOT-friendly source generators can be turned on later
/// without an additional Newtonsoft refactor.
/// </remarks>
public sealed class SystemTextJsonCosmosSerializer : CosmosSerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>Initializes a new serializer with the supplied options.</summary>
    public SystemTextJsonCosmosSerializer(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public override T FromStream<T>(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using (stream)
        {
            if (typeof(Stream).IsAssignableFrom(typeof(T)))
            {
                return (T)(object)stream;
            }

            return JsonSerializer.Deserialize<T>(stream, _options)!;
        }
    }

    /// <inheritdoc />
    public override Stream ToStream<T>(T input)
    {
        var memoryStream = new MemoryStream();
        JsonSerializer.Serialize(memoryStream, input, _options);
        memoryStream.Position = 0;
        return memoryStream;
    }
}
