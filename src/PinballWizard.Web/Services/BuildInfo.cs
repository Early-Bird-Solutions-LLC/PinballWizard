using System.Globalization;
using Microsoft.Extensions.Hosting;

namespace PinballWizard.Web.Services;

// Build-time identity for the web image.
//
// Reads PINWIZ_BUILD_SHA and PINWIZ_BUILD_TIME from IConfiguration (backed by
// environment variables promoted by the Dockerfile ARG → ENV chain in the CI
// image build). Local dev (no build args set) degrades visibly per Invariant #17:
// ShortSha = "local", BuildTimeUtc = null — never a fabricated version string.
//
// Registration: singleton in Program.cs. IConfiguration and IHostEnvironment are
// standard ASP.NET Core host services resolved by the DI container.
public sealed class BuildInfo
{
    // First 7 hex chars of the git SHA, or "local" when the env var is absent/empty.
    public string ShortSha { get; }

    // Parsed UTC deploy timestamp, or null when the env var is absent, empty, or
    // unparseable (e.g. before CI wires the ARGs — never throws, always degrades).
    public DateTimeOffset? BuildTimeUtc { get; }

    // ASP.NET host environment name (e.g. "Production", "Development").
    public string Environment { get; }

    public BuildInfo(IConfiguration configuration, IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        var sha = configuration["PINWIZ_BUILD_SHA"];
        ShortSha = !string.IsNullOrWhiteSpace(sha)
            ? sha[..Math.Min(7, sha.Length)]
            : "local";

        var time = configuration["PINWIZ_BUILD_TIME"];
        BuildTimeUtc = !string.IsNullOrWhiteSpace(time) &&
                       DateTimeOffset.TryParse(
                           time,
                           CultureInfo.InvariantCulture,
                           DateTimeStyles.AssumeUniversal,
                           out var parsed)
            ? parsed
            : null;

        Environment = hostEnvironment.EnvironmentName;
    }
}
