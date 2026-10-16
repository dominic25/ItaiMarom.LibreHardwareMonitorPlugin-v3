using System.Globalization;
using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using ItaiMarom.LibreHardwareMonitorPlugin.Migration;
using ItaiMarom.LibreHardwareMonitorPlugin.Variables;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// The plugin's settings screen: which sensors to declare as variables, and how often to read them.
/// </summary>
/// <remarks>
/// <para>
/// This is the Macro Deck 2 plugin's configuration dialog carried over. The old dialog was a checkbox
/// tree of hardware, then sensor type, then sensor, with a polling-rate slider under it. Macro Deck 3 has
/// no tree control, so the tree is flattened into one multi-select whose labels keep the old grouping -
/// <c>CPU &#8250; Temperature &#8250; CPU Core #1</c> - and the slider becomes a number field with the
/// same bounds, 10 to 10000 milliseconds.
/// </para>
/// <para>
/// Macro Deck 2 made the user pick before the plugin did anything. Here the picker decides only which
/// sensors are declared as variables up front - every other sensor is still reachable through the
/// catalog - but a button that names one by hand resolves only once that sensor has been declared here
/// or carried over by the migration.
/// </para>
/// <para>
/// What is stored is the sensors' full identity, not the picked resource ids, for the same reason the
/// migration stores it: the integration has to be able to declare their variables at startup without
/// probing the hardware first. The resource ids are only what the multi-select round-trips through.
/// </para>
/// </remarks>
internal sealed class LibreHardwareMonitorConfigFlow : IConfigFlow
{
	/// <summary>The one step, also the id the host submits back.</summary>
	internal const string StepId = "sensors";

	/// <summary>The field the picked resource ids are submitted under.</summary>
	internal const string SensorsFieldName = "sensor_selection";

	/// <summary>The field the polling interval, in milliseconds, is submitted under.</summary>
	internal const string PollingFieldName = "polling_interval";

	/// <summary>
	/// The name of the configuration entry this flow saves, written once in the plugin's own language:
	/// the host stores it as the entry's name and the user renames it from there.
	/// </summary>
	private const string EntryTitle = "Libre Hardware Monitor";

	/// <summary>The old plugin's slider bounds, reproduced.</summary>
	internal const double MinimumPollingMilliseconds = 10;
	internal const double MaximumPollingMilliseconds = 10_000;

	private readonly HardwareMonitor _hardware;
	private readonly SensorSelection _selection;
	private readonly ILogger _logger;

	/// <summary>The sensors known when the step was built, keyed by the resource id the picker stores.</summary>
	private IReadOnlyDictionary<string, SensorIdentifier>? _knownSensors;

	public LibreHardwareMonitorConfigFlow(HardwareMonitor hardware, SensorSelection selection, ILogger logger)
	{
		_hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
		_selection = selection ?? throw new ArgumentNullException(nameof(selection));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<LibreHardwareMonitorConfigFlow>();
	}

	/// <inheritdoc />
	public async Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken) =>
		ConfigFlowResult.Step(await BuildStepAsync(cancellationToken).ConfigureAwait(false));

	/// <inheritdoc />
	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(input);

		if (!string.Equals(stepId, StepId, StringComparison.Ordinal))
		{
			return ConfigFlowResult.Error(
				await BuildStepAsync(cancellationToken).ConfigureAwait(false),
				Strings.Configuration.UnknownStep());
		}

		var sensors = await ResolveAsync(ReadSelection(input), cancellationToken).ConfigureAwait(false);
		var selection = new MigratedSensorSelection(sensors, ReadPollingInterval(input));

		_logger.Information("The sensor configuration was saved with {SensorCount} sensors.", sensors.Count);

		return ConfigFlowResult.Complete(EntryTitle, selection.ToConfigurationValues());
	}

	/// <summary>
	/// Builds the one step: every sensor the hardware reports as an option, the ones already configured
	/// preselected, and the polling interval the monitor is currently running at.
	/// </summary>
	private async Task<ConfigFlowStep> BuildStepAsync(CancellationToken cancellationToken)
	{
		var known = await IndexAsync(cancellationToken).ConfigureAwait(false);

		var options = known.Values
			.OrderBy(sensor => sensor.Hardware.RootName, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(sensor => sensor.SensorType.ToString(), StringComparer.Ordinal)
			.ThenBy(sensor => sensor.SensorName, StringComparer.CurrentCultureIgnoreCase)
			.Select(sensor => new ActionParameterOption
			{
				Value = sensor.ResourceId,
				Label = OptionLabel(sensor),
			})
			.ToArray();

		// Built by hand rather than with ActionParameter.MultiSelect: that factory has no default, the
		// property is init-only, and a migrated entry has no stored value for the field - so without a
		// default the picker would reopen empty and invite the user to save over their own selection.
		var sensors = new ActionParameter
		{
			Name = SensorsFieldName,
			Type = ActionParameterType.MultiSelect,
			Options = options,
			Label = Strings.Configuration.Sensors(),
			Description = Strings.Configuration.SensorsHint(),
			DefaultValue = _selection.Sensors
				.Select(sensor => sensor.ResourceId)
				.Where(known.ContainsKey)
				.Distinct(StringComparer.Ordinal)
				.ToArray(),
		};

		var polling = ActionParameter.Number(
			PollingFieldName,
			label: Strings.Configuration.PollingRate(),
			description: Strings.Configuration.PollingRateHint(),
			min: MinimumPollingMilliseconds,
			max: MaximumPollingMilliseconds,
			step: 1,
			defaultValue: CurrentPollingMilliseconds());

		return new ConfigFlowStep
		{
			StepId = StepId,
			Title = Strings.Configuration.Title(),
			Description = Strings.Configuration.Description(),
			Fields = [sensors, polling],
		};
	}

	/// <summary>
	/// Every sensor the hardware reports, keyed by resource id. Read once and kept for the session so a
	/// submit can turn the picked ids back into identities without reading the hardware again.
	/// </summary>
	private async Task<IReadOnlyDictionary<string, SensorIdentifier>> IndexAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<SensorIdentifier> sensors;
		try
		{
			sensors = await _hardware.EnumerateSensorsAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "The hardware could not be enumerated, so the sensor picker is empty.");
			sensors = [];
		}

		_knownSensors = sensors
			.GroupBy(sensor => sensor.ResourceId, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
		return _knownSensors;
	}

	/// <summary>
	/// Turns the picked resource ids back into the sensors they name, dropping any that are no longer
	/// present. Re-reads the hardware once if an id cannot be resolved, so a submit that follows a device
	/// being plugged in or removed does not silently lose the rest of the selection.
	/// </summary>
	private async Task<IReadOnlyList<SensorIdentifier>> ResolveAsync(
		List<string> resourceIds,
		CancellationToken cancellationToken)
	{
		if (resourceIds.Count == 0)
		{
			return [];
		}

		var known = _knownSensors ?? await IndexAsync(cancellationToken).ConfigureAwait(false);
		var resolved = Map(resourceIds, known);
		if (resolved.Count == resourceIds.Distinct(StringComparer.Ordinal).Count())
		{
			return resolved;
		}

		resolved = Map(resourceIds, await IndexAsync(cancellationToken).ConfigureAwait(false));
		if (resolved.Count != resourceIds.Distinct(StringComparer.Ordinal).Count())
		{
			_logger.Warning(
				"{MissingCount} of the {PickedCount} picked sensors are not present on this machine.",
				resourceIds.Distinct(StringComparer.Ordinal).Count() - resolved.Count,
				resourceIds.Count);
		}

		return resolved;
	}

	private static List<SensorIdentifier> Map(
		List<string> resourceIds,
		IReadOnlyDictionary<string, SensorIdentifier> known)
	{
		var seen = new HashSet<SensorIdentifier>();
		var sensors = new List<SensorIdentifier>(resourceIds.Count);
		foreach (var resourceId in resourceIds)
		{
			if (known.TryGetValue(resourceId, out var sensor) && seen.Add(sensor))
			{
				sensors.Add(sensor);
			}
		}

		return sensors;
	}

	/// <summary>The label the picker shows: the old tree's three levels, flattened onto one line.</summary>
	private static string OptionLabel(SensorIdentifier sensor) =>
		$"{sensor.Hardware.RootName} \u203A {sensor.SensorType} \u203A {sensor.SensorName}";

	/// <summary>The interval the loop is running at, clamped into the field's own bounds.</summary>
	private double CurrentPollingMilliseconds() =>
		Math.Clamp(
			_hardware.Options.PollingInterval.TotalMilliseconds,
			MinimumPollingMilliseconds,
			MaximumPollingMilliseconds);

	/// <summary>The picked resource ids out of what the host submitted.</summary>
	private static List<string> ReadSelection(IReadOnlyDictionary<string, object?> input) =>
		input.TryGetValue(SensorsFieldName, out var value)
			? ReadStrings(value)
			: [];

	/// <summary>
	/// Reads a multi-select value without assuming its runtime shape. Over the wire it is a JSON array,
	/// but a harness or a future host may hand it over as text or as an enumerable.
	/// </summary>
	private static List<string> ReadStrings(object? value) => value switch
	{
		JsonElement element => ReadJsonStrings(element),
		string text => ReadString(text),
		IEnumerable<string> values => Clean(values),
		System.Collections.IEnumerable values => ReadObjects(values),
		_ => [],
	};

	private static List<string> ReadJsonStrings(JsonElement element)
	{
		if (element.ValueKind == JsonValueKind.Array)
		{
			var texts = new List<string>();
			foreach (var item in element.EnumerateArray())
			{
				var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
				if (!string.IsNullOrWhiteSpace(text))
				{
					texts.Add(text);
				}
			}

			return texts;
		}

		return element.ValueKind == JsonValueKind.String ? Clean([element.GetString()]) : [];
	}

	private static List<string> ReadString(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return [];
		}

		// A single value the host chose to hand over as text rather than as an array.
		if (text.Length > 1 && text[0] == '[')
		{
			try
			{
				using var document = JsonDocument.Parse(text);
				return ReadJsonStrings(document.RootElement);
			}
			catch (JsonException)
			{
				// Not JSON after all; fall through and treat it as one value.
			}
		}

		return [text];
	}

	private static List<string> ReadObjects(System.Collections.IEnumerable values)
	{
		var texts = new List<string>();
		foreach (var item in values)
		{
			if (item?.ToString() is { } text && !string.IsNullOrWhiteSpace(text))
			{
				texts.Add(text);
			}
		}

		return texts;
	}

	/// <summary>The polling interval the field submitted, or nothing when it is missing or unusable.</summary>
	private static TimeSpan? ReadPollingInterval(IReadOnlyDictionary<string, object?> input)
	{
		if (!input.TryGetValue(PollingFieldName, out var value))
		{
			return null;
		}

		var milliseconds = value switch
		{
			JsonElement element => ReadJsonNumber(element),
			double number => number,
			float number => number,
			int number => number,
			long number => number,
			decimal number => (double)number,
			string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
			_ => null,
		};

		if (milliseconds is not { } interval
			|| interval <= 0
			|| double.IsNaN(interval)
			|| double.IsInfinity(interval))
		{
			return null;
		}

		return TimeSpan.FromMilliseconds(interval);
	}

	private static double? ReadJsonNumber(JsonElement element) => element.ValueKind switch
	{
		JsonValueKind.Number when element.TryGetDouble(out var value) => value,
		JsonValueKind.String when double.TryParse(
			element.GetString(),
			NumberStyles.Float,
			CultureInfo.InvariantCulture,
			out var value) => value,
		_ => null,
	};

	private static List<string> Clean(IEnumerable<string?> values)
	{
		var texts = new List<string>();
		foreach (var value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				texts.Add(value);
			}
		}

		return texts;
	}
}
