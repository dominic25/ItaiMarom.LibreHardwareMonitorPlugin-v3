using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Sdk.Migration;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Migration;

/// <summary>
/// Takes over a Macro Deck 2 installation of the old plugin: the sensors the user had asked for become
/// the sensors this plugin declares as variables.
/// </summary>
/// <remarks>
/// <para>
/// The old plugin had no actions - it only ever published variables - so nothing is claimed on the action
/// side and every button belonging to another plugin is left exactly as the host found it. What is
/// claimed is the old plugin's settings file, and what is read out of it is <c>requestedSensors</c>: the
/// list of sensors whose variables a Macro Deck 2 layout will still be referring to by name.
/// </para>
/// <para>
/// The result is one configuration entry rather than one per sensor. The user is not being asked to
/// configure anything - the migration already knows the answer - so the entry is a record of the answer,
/// read once at startup by the integration that acts on it.
/// </para>
/// </remarks>
public sealed class MacroDeck2Migration : IIntegrationMigration
{
	/// <summary>
	/// The integration the migrated entry belongs to. Must match <c>id</c> in manifest.json, or the host
	/// will store the entry against an integration that does not exist.
	/// </summary>
	public const string IntegrationId = "itaimarom.libre-hardware-monitor";

	/// <summary>
	/// The old plugin's settings file, which Macro Deck 2 names <c>{author}_{plugin name}</c>, lowercased.
	/// Here that is the author <c>ItaiMarom</c> and the plugin name <c>Libre Hardware Monitor Plugin</c>.
	/// </summary>
	public const string SettingsSource = "itaimarom_libre hardware monitor plugin";

	/// <summary>The title the migrated configuration entry is shown under.</summary>
	public const string ConfigurationTitle = "Macro Deck 2 sensors";

	private const string RequestedSensorsSetting = "requestedSensors";
	private const string PollingRateSetting = "pollingRate";

	private readonly HardwareMonitor _hardware;
	private readonly ILogger _logger;

	public MacroDeck2Migration(HardwareMonitor hardware, ILogger logger)
	{
		_hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<MacroDeck2Migration>();
	}

	/// <inheritdoc />
	public MigrationSource Source => MigrationSource.MacroDeck2;

	/// <inheritdoc />
	public IReadOnlyList<string> ClaimedActionSources { get; } = [];

	/// <inheritdoc />
	public IReadOnlyList<string> ClaimedSettingsSources { get; } = [SettingsSource];

	/// <summary>
	/// Always nothing: the old plugin contributed no actions, so there is no action of its own to
	/// translate. A button it never owned is not this migration's to rewrite.
	/// </summary>
	public Task<ActionMigrationResult?> MigrateActionAsync(ForeignAction action, CancellationToken cancellationToken = default) =>
		Task.FromResult<ActionMigrationResult?>(null);

	/// <summary>
	/// Resolves the stored sensor names against the hardware as it is now and hands the result back as a
	/// single configuration entry.
	/// </summary>
	public async Task<IReadOnlyList<MigratedConfiguration>> MigrateConfigurationAsync(
		ForeignPluginSettings settings,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var requests = LegacySensorRequest.Parse(Setting(settings, RequestedSensorsSetting));
		if (requests.Count == 0)
		{
			_logger.Information("No Macro Deck 2 sensors were found to migrate.");
			return [];
		}

		IReadOnlyList<SensorIdentifier> available;
		try
		{
			// Resolving here is what keeps the plugin from having to probe the hardware again at every
			// startup just to learn which variables it has.
			available = await _hardware.EnumerateSensorsAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "The hardware could not be enumerated, so no Macro Deck 2 sensors were migrated.");
			return [];
		}

		var sensors = LegacySensorSelection.Select(requests, available);
		if (sensors.Count == 0)
		{
			// An entry that names nothing would look configured and do nothing.
			_logger.Warning(
				"None of the {RequestedCount} Macro Deck 2 sensors are present on this machine; nothing was migrated.",
				requests.Count);
			return [];
		}

		_logger.Information(
			"Migrated {MigratedCount} of {RequestedCount} Macro Deck 2 sensors.",
			sensors.Count,
			requests.Count);

		var selection = new MigratedSensorSelection(sensors, PollingInterval(settings));
		return
		[
			new MigratedConfiguration(
				IntegrationId,
				ConfigurationTitle,
				selection.ToValues(),
				new Dictionary<string, MigratedSecret>(StringComparer.Ordinal)),
		];
	}

	/// <summary>
	/// The old plugin's polling rate, as a usable interval, or <see langword="null"/> when it never had
	/// one or the stored value makes no sense. It is clamped to the monitor's own bounds later.
	/// </summary>
	private static TimeSpan? PollingInterval(ForeignPluginSettings settings) =>
		long.TryParse(Setting(settings, PollingRateSetting), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
			&& milliseconds > 0
			? TimeSpan.FromMilliseconds(milliseconds)
			: null;

	/// <summary>
	/// One value out of the foreign settings, matched without regard to case: the file was written by
	/// another application and the exact casing of a key is not something to bet the migration on.
	/// </summary>
	private static string? Setting(ForeignPluginSettings settings, string key)
	{
		if (settings.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
		{
			return value;
		}

		foreach (var pair in settings.Settings)
		{
			if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
			{
				return pair.Value;
			}
		}

		return null;
	}
}
