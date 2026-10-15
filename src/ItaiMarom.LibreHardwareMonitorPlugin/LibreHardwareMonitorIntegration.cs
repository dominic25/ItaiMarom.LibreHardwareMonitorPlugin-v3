using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin;

/// <summary>
/// The plugin's single integration.
/// </summary>
/// <remarks>
/// <para>
/// The Macro Deck 2 plugin this replaces exposed no actions at all - it was a pure variable provider that
/// pushed hardware sensor values into Macro Deck. The rewrite keeps that shape: the read-only
/// <see cref="Actions"/> list stays empty.
/// </para>
/// <para>
/// What is already here is the half that used to sit inside the plugin's own <c>DoWork</c> loop: the
/// hardware. <see cref="HardwareMonitor"/> reads it on its own background thread and caches the newest
/// value of every sensor a variable is bound to, so nothing on the path that answers Macro Deck ever waits
/// on a sensor.
/// </para>
/// </remarks>
public sealed class LibreHardwareMonitorIntegration : IPluginIntegration, IAsyncDisposable
{
	private readonly ILogger _logger;
	private readonly HardwareMonitor _hardware;

	// Built by DI, so anything the container knows can be taken here.
	public LibreHardwareMonitorIntegration(ILogger logger)
	{
		_logger = logger.ForContext<LibreHardwareMonitorIntegration>();
		_hardware = new HardwareMonitor(
			new LibreHardwareMonitorSource(),
			HardwareMonitorOptions.Default,
			logger);
	}

	/// <inheritdoc />
	/// <remarks>
	/// The Macro Deck 2 plugin had no actions, so neither does this: everything it offered was a variable.
	/// </remarks>
	public IReadOnlyList<IActionDefinition> Actions { get; } = [];

	/// <summary>
	/// Starts the hardware loop. Runs once the session is established, and again after a non-resume
	/// reconnect or a configuration change, so it is written to be safe to run again on a running monitor.
	/// </summary>
	public Task InitializeAsync(IIntegrationContext context)
	{
		_hardware.Start();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Stops the loop without tearing the monitor down, because initialization may well follow this and
	/// expects to get the same object back.
	/// </summary>
	public Task ShutdownAsync() => _hardware.StopAsync();

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		await _hardware.DisposeAsync().ConfigureAwait(false);
	}
}
