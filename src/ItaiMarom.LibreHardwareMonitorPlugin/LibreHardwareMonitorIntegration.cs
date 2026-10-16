using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Migration;
using MacroDeck.Sdk.Variables;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin;

/// <summary>
/// The plugin's integration: it owns the hardware polling loop and serves the variables read off it.
/// </summary>
/// <remarks>
/// <para>
/// It has no actions, because the Macro Deck 2 plugin had none - everything it offered was a variable -
/// and it serves variables two ways. The sensors the user selected, or that a migrated Macro Deck 2
/// configuration asked for, are declared as ordinary variables so the button that used
/// <c>{{cpu_core__1_temperature}}</c> still resolves; every other sensor is reachable on demand through
/// the catalog, without anything being declared up front.
/// </para>
/// <para>
/// The provider does the work; this type exists to own the lifetime and to wire the two together, so the
/// variable surface can be tested against a fake sensor source without a session.
/// </para>
/// </remarks>
public sealed class LibreHardwareMonitorIntegration : IPluginIntegration, IMigrationProvider, IVariableProvider, IAsyncDisposable
{
	private readonly HardwareMonitor _hardware;
	private readonly SensorSelection _selection;
	private readonly SensorVariableProvider _variables;
	private readonly ILogger _logger;

	public LibreHardwareMonitorIntegration(ILogger logger, IHardwareSensorSource source, SensorSelection selection)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));

		_hardware = new HardwareMonitor(source, HardwareMonitorOptions.Default, _logger);
		_selection = selection ?? throw new ArgumentNullException(nameof(selection));
		_variables = new SensorVariableProvider(_hardware, selection);
		Migrations = [new MacroDeck2Migration(_hardware, _logger)];
	}

	/// <inheritdoc />
	public IReadOnlyList<IIntegrationMigration> Migrations { get; }

	/// <inheritdoc />
	public IReadOnlyList<IActionDefinition> Actions { get; } = [];

	/// <inheritdoc />
	public IReadOnlyList<VariableDefinition> Variables => _variables.Variables;

	/// <inheritdoc />
	public bool VariablesDependOnConfiguration => _variables.VariablesDependOnConfiguration;

	/// <inheritdoc />
	public bool SupportsCatalog => _variables.SupportsCatalog;

	/// <inheritdoc />
	public bool SupportsSearch => _variables.SupportsSearch;

	/// <inheritdoc />
	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default) =>
		_variables.ReadAsync(localId, cancellationToken);

	/// <inheritdoc />
	public ValueTask<IReadOnlyList<VariableValue>> SubscribeAsync(
		IReadOnlyCollection<string> localIds,
		CancellationToken cancellationToken = default) =>
		_variables.SubscribeAsync(localIds, cancellationToken);

	/// <inheritdoc />
	public ValueTask<VariableDefinition?> ResolveAsync(string localId, CancellationToken cancellationToken = default) =>
		_variables.ResolveAsync(localId, cancellationToken);

	/// <inheritdoc />
	public ValueTask<VariableCatalogPage> DiscoverAsync(
		VariableCatalogQuery query,
		CancellationToken cancellationToken = default) =>
		_variables.DiscoverAsync(query, cancellationToken);

	/// <summary>
	/// Reads what a migration left behind, points the loop at those sensors and starts it. Runs once the
	/// session is established, and again after a non-resume reconnect or a configuration change, so it is
	/// written to be safe to run on a monitor that is already running.
	/// </summary>
	public async Task InitializeAsync(IIntegrationContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		// What the host holds is the whole of what this plugin is configured with, so it is read on every
		// initialize rather than cached: an entry the user removed has to stop being a variable.
		var migrated = await MigratedSensorSelection
			.ReadAsync(context.Config, _logger, CancellationToken.None)
			.ConfigureAwait(false);

		_selection.Replace(migrated.Sensors);
		_hardware.SetOptions(PollingOptions(migrated.PollingInterval));

		// The eager variables are the ones the host reads directly, without ever subscribing, so they are
		// the working set until a binding narrows or widens it.
		_hardware.SetWatchedSensors(_selection.Sensors);
		_hardware.Start();
	}

	/// <summary>
	/// Stops the loop without tearing the monitor down, because initialization may well follow this and
	/// expects to get the same object back.
	/// </summary>
	public Task ShutdownAsync() => _hardware.StopAsync();

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		_variables.Dispose();
		await _hardware.DisposeAsync().ConfigureAwait(false);
	}

	/// <summary>
	/// The plugin's timing with a migrated Macro Deck 2 polling rate applied. The monitor clamps it.
	/// </summary>
	private static HardwareMonitorOptions PollingOptions(TimeSpan? pollingInterval) =>
		pollingInterval is { } interval
			? HardwareMonitorOptions.Default with { PollingInterval = interval }
			: HardwareMonitorOptions.Default;
}
