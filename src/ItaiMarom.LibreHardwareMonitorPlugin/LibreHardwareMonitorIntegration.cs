using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin;

/// <summary>
/// The plugin's single integration.
/// </summary>
/// <remarks>
/// The Macro Deck 2 plugin this replaces exposed no actions at all - it was a pure variable provider
/// that pushed hardware sensor values into Macro Deck. The rewrite keeps that shape: the read-only
/// <see cref="Actions"/> list stays empty, and the value-producing capabilities
/// (<c>IVariableProvider</c>) and the sensor-selection configuration are added on top of this
/// skeleton.
/// </remarks>
public sealed class LibreHardwareMonitorIntegration : IPluginIntegration
{
    private readonly ILogger _logger;

    // Built by DI, so anything the container knows can be taken here.
    public LibreHardwareMonitorIntegration(ILogger logger)
    {
        _logger = logger.ForContext<LibreHardwareMonitorIntegration>();
    }

    /// <inheritdoc />
    public IReadOnlyList<IActionDefinition> Actions { get; } = [];

    /// <summary>
    /// Runs once the session is established, and again after a non-resume reconnect or a configuration
    /// change, so it has to be safe to run repeatedly against an already-initialized process.
    /// </summary>
    public Task InitializeAsync(IIntegrationContext context)
    {
        _logger.Information("Libre Hardware Monitor integration initialized.");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync() => Task.CompletedTask;
}
