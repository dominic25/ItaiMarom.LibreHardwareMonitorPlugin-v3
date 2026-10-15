using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

[TestFixture]
public sealed class IntegrationSmokeTests
{
    [Test]
    public void Integration_exposes_no_actions()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var integration = new LibreHardwareMonitorIntegration(logger);

        // The Macro Deck 2 plugin had no actions either: it only produced variables.
        Assert.That(integration.Actions, Is.Empty);
    }

    [Test]
    public async Task Initialize_and_shutdown_complete()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var integration = new LibreHardwareMonitorIntegration(logger);

        // The scaffolding integration ignores the context, so a null one is enough to smoke-test the
        // lifecycle. Once initialization consumes the context, this test gets a real one.
        Assert.DoesNotThrowAsync(async () =>
        {
            await integration.InitializeAsync(null!);
            await integration.ShutdownAsync();
        });
    }
}
