using ItaiMarom.LibreHardwareMonitorPlugin.Configuration;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Plugin.Protocol.Handshake;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
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
/// Selecting which sensors those are is the settings screen the old plugin's configuration dialog became:
/// see <see cref="LibreHardwareMonitorConfigFlow"/>. The integration owns the loop and the selection, and
/// hands both to a fresh flow per session, so the screen reopens showing what is configured now.
/// </para>
/// <para>
/// The provider does the work; this type exists to own the lifetime and to wire the two together, so the
/// variable surface can be tested against a fake sensor source without a session.
/// </para>
/// </remarks>
public sealed class LibreHardwareMonitorIntegration :
	IPluginIntegration,
	IMigrationProvider,
	IVariableProvider,
	IConfigFlowProvider,
	IAsyncDisposable
{
	private readonly HardwareMonitor _hardware;
	private readonly SensorSelection _selection;
	private readonly SensorVariableProvider _variables;
	private readonly IPluginCatalogNotifier _catalogNotifier;
	private readonly ClassicWindowServer _window;
	private readonly ILogger _logger;

	public LibreHardwareMonitorIntegration(
		ILogger logger,
		IHardwareSensorSource source,
		SensorSelection selection,
		IPluginCatalogNotifier catalogNotifier)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_catalogNotifier = catalogNotifier ?? throw new ArgumentNullException(nameof(catalogNotifier));

		_hardware = new HardwareMonitor(source, HardwareMonitorOptions.Default, _logger);
		_selection = selection ?? throw new ArgumentNullException(nameof(selection));
		_variables = new SensorVariableProvider(_hardware, selection);

		// The classic sensor window is served for the whole life of the plugin rather than for one
		// session: a flow is handed out per session and has nowhere to release it, and the listener binds
		// its port lazily, so a plugin nobody configures never listens at all.
		_window = new ClassicWindowServer(_logger, new SensorReadingSource(_hardware));
		Migrations = [new MacroDeck2Migration(_hardware, _logger)];
	}

	/// <inheritdoc />
	public IReadOnlyList<IIntegrationMigration> Migrations { get; }

	/// <summary>
	/// The settings screen: one list of sensors and one polling interval, so a second entry would have
	/// nothing of its own to say.
	/// </summary>
	public bool AllowsMultipleConfigurations => false;

	/// <summary>
	/// A fresh flow per session, handed the live loop, the selection and the window server, so it opens on
	/// what is configured now rather than on what was configured when the plugin started.
	/// </summary>
	public IConfigFlow CreateConfigFlow() =>
		new LibreHardwareMonitorConfigFlow(_hardware, _selection, _window, _logger);

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

		// The host reads the declared set once, when it describes this plugin, and nothing it does after
		// that asks again on its own: a saved configuration reaches it only through this notice. Without it
		// the selection just applied stays invisible until the next reconnect, which is what made a saved
		// selection fail to show up as variables. It is fire-and-forget and a no-op with no open session, so
		// there is nothing to guard here, and it is sent on every initialize rather than only when the set
		// changed because the host may have described us before this call read the configuration.
		_catalogNotifier.CatalogChanged(CapabilityKinds.Variables, reason: "configuration read");
	}

	/// <summary>
	/// Stops the loop without tearing the monitor down, because initialization may well follow this and
	/// expects to get the same object back.
	/// </summary>
	public Task ShutdownAsync() => _hardware.StopAsync();

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		// The window stops listening first: it can hand a selection to a flow, and a flow that is still
		// running when the loop is torn down has nothing left to read.
		await _window.DisposeAsync().ConfigureAwait(false);
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
