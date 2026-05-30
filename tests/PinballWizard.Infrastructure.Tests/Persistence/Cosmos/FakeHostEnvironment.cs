using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace PinballWizard.Infrastructure.Tests.Persistence.Cosmos;

internal sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = environmentName;
    public string ApplicationName { get; set; } = "test";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = "";
}
