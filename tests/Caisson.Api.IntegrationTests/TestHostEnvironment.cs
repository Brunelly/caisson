using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Caisson.Api.IntegrationTests;

/// <summary>A minimal <see cref="IHostEnvironment"/> test double for the fail-closed startup guards.</summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string environmentName) => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }

    public string ApplicationName { get; set; } = "Caisson.Api.IntegrationTests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
