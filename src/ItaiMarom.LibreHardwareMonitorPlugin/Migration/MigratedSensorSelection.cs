using System.Globalization;
using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Migration;

/// <summary>
/// The sensors the plugin is configured to declare as variables, how often to read them, and whether the
/// elevated helper should be started at startup: what a Macro Deck 2 migration leaves behind, and what the
/// plugin's own settings screen stores.
/// </summary>
/// <remarks>
/// <para>
/// The host's configuration entry is the only thing that survives a restart, so this is what the plugin
/// writes into it and reads back out. It has two writers - a migration taking over the old plugin's
/// settings, and the settings screen the user fills in - and one reader, so both paths have to agree on
/// the stored shape. One entry holds the whole selection: the thing being configured is one list, and
/// splitting it across an entry per sensor would turn a single read at startup into a round trip per
/// sensor.
/// </para>
/// <para>
/// <see cref="AutoElevate"/> is nullable only so that a document written before the setting existed - the
/// migration's, or any entry an older version left behind - reads back as "off" rather than as a missing
/// key that has to be repaired. The settings window is the only thing that writes it, and it writes the
/// answer either way; the issue's <em>Enable elevated readings</em> button deliberately writes nothing, so
/// pressing it is a one-off for the session and never changes what the next start does.
/// </para>
/// <para>
/// The list is stored as a JSON string inside a JSON value, which looks like one layer too many. It is
/// deliberate: a configuration value is read back with <c>GetStringAsync</c>, so a structured value would
/// come back as whatever text the host chose to represent it in. A string is the one shape the round trip
/// is specified to preserve.
/// </para>
/// </remarks>
public sealed record MigratedSensorSelection(
	IReadOnlyList<SensorIdentifier> Sensors,
	TimeSpan? PollingInterval,
	bool? AutoElevate = null)
{
	/// <summary>The value the sensor list is stored under.</summary>
	public const string SensorsKey = "sensors";

	/// <summary>The value the polling interval, in milliseconds, is stored under.</summary>
	public const string PollingRateKey = "polling_rate";

	/// <summary>
	/// The value the startup elevation preference is stored under, as <c>true</c> or <c>false</c>. Absent
	/// means off, which is what a migration and an entry written before the setting existed carry.
	/// </summary>
	public const string AutoElevateKey = "auto_elevate";

	/// <summary>Nothing migrated, or nothing that could be read back.</summary>
	public static MigratedSensorSelection Empty { get; } = new([], null);

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		// The text is read back by a later version of this plugin, so it is read back leniently.
		PropertyNameCaseInsensitive = true,
	};

	/// <summary>The values of the configuration entry a migration creates for this selection.</summary>
	public IReadOnlyDictionary<string, JsonElement> ToValues()
	{
		var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
		{
			[SensorsKey] = JsonSerializer.SerializeToElement(
				JsonSerializer.Serialize(Sensors.Select(MigratedSensor.From).ToArray(), SerializerOptions),
				SerializerOptions),
		};

		if (PollingInterval is { } interval)
		{
			values[PollingRateKey] = JsonSerializer.SerializeToElement(
				((long)interval.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
				SerializerOptions);
		}

		if (AutoElevate is { } autoElevate)
		{
			values[AutoElevateKey] = JsonSerializer.SerializeToElement(
				autoElevate ? "true" : "false",
				SerializerOptions);
		}

		return values;
	}

	/// <summary>
	/// The same selection as configuration values a flow completes with. A migration hands its values to
	/// the host as JSON; a flow hands them over as the plain strings <see cref="ConfigFlowValue"/> can
	/// carry, which is the one shape <see cref="IIntegrationConfig.GetStringAsync"/> is specified to give
	/// back unchanged.
	/// </summary>
	public IReadOnlyDictionary<string, ConfigFlowValue> ToConfigurationValues() =>
		ToValues().ToDictionary(
			pair => pair.Key,
			pair => ConfigFlowValue.Plain(pair.Value.GetString() ?? string.Empty),
			StringComparer.Ordinal);

	/// <summary>
	/// Reads back every selection the host holds for this integration. Never throws: a configuration that
	/// cannot be read costs the user their migrated variables, and failing initialization would cost them
	/// the plugin.
	/// </summary>
	public static async Task<MigratedSensorSelection> ReadAsync(
		IIntegrationConfig config,
		ILogger logger,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(logger);

		IReadOnlyList<ConfigEntrySnapshot> entries;
		try
		{
			entries = await config.GetEntriesAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			logger.Warning(exception, "The migrated sensor configuration could not be read.");
			return Empty;
		}

		var sensors = new List<SensorIdentifier>();
		var seen = new HashSet<SensorIdentifier>();
		TimeSpan? pollingInterval = null;
		bool? autoElevate = null;

		foreach (var entry in entries)
		{
			foreach (var sensor in ReadSensors(await ReadValueAsync(config, entry, SensorsKey, logger, cancellationToken).ConfigureAwait(false), logger))
			{
				if (seen.Add(sensor))
				{
					sensors.Add(sensor);
				}
			}

			if (pollingInterval is null
				&& ReadPollingInterval(await ReadValueAsync(config, entry, PollingRateKey, logger, cancellationToken).ConfigureAwait(false)) is { } interval)
			{
				pollingInterval = interval;
			}

			if (autoElevate is null
				&& ReadAutoElevate(await ReadValueAsync(config, entry, AutoElevateKey, logger, cancellationToken).ConfigureAwait(false)) is { } preference)
			{
				autoElevate = preference;
			}
		}

		return new MigratedSensorSelection(sensors, pollingInterval, autoElevate);
	}

	private static async Task<string?> ReadValueAsync(
		IIntegrationConfig config,
		ConfigEntrySnapshot entry,
		string key,
		ILogger logger,
		CancellationToken cancellationToken)
	{
		try
		{
			var value = await config.GetStringAsync(entry.Id, key, cancellationToken).ConfigureAwait(false);
			return string.IsNullOrWhiteSpace(value) ? null : value;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			// An entry that does not hold this value is the ordinary case - the polling interval is only
			// there when the old plugin had one - so this is not worth a warning.
			logger.Debug(exception, "A migrated configuration value named {Key} could not be read.", key);
			return null;
		}
	}

	private static List<SensorIdentifier> ReadSensors(string? stored, ILogger logger)
	{
		if (stored is null)
		{
			return [];
		}

		try
		{
			var sensors = new List<SensorIdentifier>();
			foreach (var migrated in JsonSerializer.Deserialize<MigratedSensor?[]>(stored, SerializerOptions) ?? [])
			{
				if (migrated is not null && migrated.TryRestore(out var sensor))
				{
					sensors.Add(sensor);
				}
				else
				{
					logger.Debug("A migrated sensor was dropped because it was not complete.");
				}
			}

			return sensors;
		}
		catch (JsonException exception)
		{
			logger.Warning(exception, "The migrated sensor list could not be parsed.");
			return [];
		}
	}

	private static TimeSpan? ReadPollingInterval(string? stored)
	{
		if (stored is null
			|| !long.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
			|| milliseconds <= 0)
		{
			return null;
		}

		return TimeSpan.FromMilliseconds(milliseconds);
	}

	private static bool? ReadAutoElevate(string? stored) =>
		bool.TryParse(stored, out var value) ? value : null;
}
