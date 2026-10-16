using System.Globalization;
using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Migration;

/// <summary>
/// What a Macro Deck 2 migration leaves behind: the sensors to declare as variables, and how often the
/// old plugin was told to read them.
/// </summary>
/// <remarks>
/// <para>
/// The host's configuration entry is the only thing a migration can persist, so this is what the plugin
/// writes into it and reads back out. One entry holds the whole selection: the thing being configured is
/// one list, and splitting it across an entry per sensor would turn a single read at startup into a
/// round trip per sensor.
/// </para>
/// <para>
/// The list is stored as a JSON string inside a JSON value, which looks like one layer too many. It is
/// deliberate: a configuration value is read back with <c>GetStringAsync</c>, so a structured value would
/// come back as whatever text the host chose to represent it in. A string is the one shape the round trip
/// is specified to preserve.
/// </para>
/// </remarks>
public sealed record MigratedSensorSelection(IReadOnlyList<SensorIdentifier> Sensors, TimeSpan? PollingInterval)
{
	/// <summary>The value the sensor list is stored under.</summary>
	public const string SensorsKey = "sensors";

	/// <summary>The value the polling interval, in milliseconds, is stored under.</summary>
	public const string PollingRateKey = "polling_rate";

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

		return values;
	}

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
		}

		return new MigratedSensorSelection(sensors, pollingInterval);
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
}
