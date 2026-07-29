using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Caisson.Drivers.MikroTik.IntegrationTests;

/// <summary>A minimal <see cref="IHostEnvironment"/> test double — the factory's fail-closed AllowUntrustedCertificate/Production gate needs one.</summary>
internal sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment(string environmentName = "Testing") => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }

    public string ApplicationName { get; set; } = "Caisson.Drivers.MikroTik.IntegrationTests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
