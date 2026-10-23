using System.Globalization;
using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Migration;

/// <summary>
/// One line of the Macro Deck 2 plugin's <c>requestedSensors</c> setting: the device the user picked the
/// sensor from, its kind, and the token its variable name was built from.
/// </summary>
/// <param name="Hardware">
/// The top-level device's name as LibreHardwareMonitor reported it to the old plugin, for example
/// <c>Motherboard</c>. It is what tells two identical sensors on two identical devices apart. Empty when
/// the settings file did not carry it, in which case the token alone decides.
/// </param>
/// <param name="SensorType">The kind of reading, for example <c>Temperature</c>. Redundant with the token, kept because the old plugin stored it.</param>
/// <param name="Sensor">
/// The token the old plugin stored, <c>{SensorName}_{SensorType}</c>, for example
/// <c>CPU Core #1_Temperature</c>. It is a display name, not an identifier, which is why it is matched
/// rather than trusted.
/// </param>
/// <remarks>
/// <para>
/// The old plugin serialized a <c>List&lt;(string hardware, string type, string sensor)&gt;</c> with
/// Newtonsoft.Json, which writes a value tuple's public fields - <c>Item1</c>, <c>Item2</c>,
/// <c>Item3</c> - because the tuple element names are only metadata. That is the shape to expect. The
/// parser also accepts the named spelling <c>hardware</c>/<c>type</c>/<c>sensor</c> and a plain array,
/// because the file being read was written by somebody else's code on somebody else's machine, and
/// refusing it over a spelling change would throw away the only record of what the user had selected.
/// </para>
/// <para>
/// Nothing here throws. A migration that fails on a malformed setting leaves the user with nothing,
/// which is worse than migrating the lines that did parse.
/// </para>
/// </remarks>
public sealed record LegacySensorRequest(string Hardware, string SensorType, string Sensor)
{
	/// <summary>The stored token reduced the way the old plugin reduced it before comparing.</summary>
	public string CanonicalSensor => SensorIdentifier.ToLegacyName(Sensor);

	/// <summary>
	/// Whether this request is the one that names <paramref name="sensor"/>.
	/// </summary>
	/// <remarks>
	/// The old plugin updated every sensor whose token matched, inside every top-level hardware whose
	/// name was in the request list. Both halves are kept: the token alone would let a request for one
	/// graphics card's <c>GPU Core_Load</c> claim the other card's as well.
	/// </remarks>
	public bool Matches(SensorIdentifier sensor)
	{
		ArgumentNullException.ThrowIfNull(sensor);

		if (!string.Equals(CanonicalSensor, sensor.LegacyVariableName, StringComparison.Ordinal))
		{
			return false;
		}

		return Hardware.Length == 0
			|| string.Equals(Hardware, sensor.RootHardwareName, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Parses the stored setting. Returns an empty list for anything that is not a list of usable
	/// requests, including <see langword="null"/> and the empty string the old plugin wrote when it had
	/// nothing saved.
	/// </summary>
	public static IReadOnlyList<LegacySensorRequest> Parse(string? stored)
	{
		if (string.IsNullOrWhiteSpace(stored))
		{
			return [];
		}

		try
		{
			using var document = JsonDocument.Parse(Unwrap(stored));
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				return [];
			}

			var requests = new List<LegacySensorRequest>();
			foreach (var element in document.RootElement.EnumerateArray())
			{
				if (TryRead(element, out var request))
				{
					requests.Add(request);
				}
			}

			return requests;
		}
		catch (JsonException)
		{
			return [];
		}
	}

	/// <summary>
	/// Step past a value that was stored as a JSON string holding JSON. The old plugin read its setting
	/// back with <c>PluginConfiguration.GetValue</c>, which is typed as a string, so a version of it may
	/// well have written the array quoted.
	/// </summary>
	private static string Unwrap(string stored)
	{
		var text = stored.Trim();

		// Twice is enough: a quoted value, and a quoted value that was quoted again.
		for (var pass = 0; pass < 2; pass++)
		{
			try
			{
				using var document = JsonDocument.Parse(text);
				if (document.RootElement.ValueKind != JsonValueKind.String
					|| document.RootElement.GetString() is not { } inner
					|| string.IsNullOrWhiteSpace(inner))
				{
					return text;
				}

				text = inner.Trim();
			}
			catch (JsonException)
			{
				return text;
			}
		}

		return text;
	}

	private static bool TryRead(JsonElement element, out LegacySensorRequest request)
	{
		string hardware;
		string type;
		string? sensor;

		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				hardware = Read(element, "Item1", "hardware", "Hardware");
				type = Read(element, "Item2", "type", "SensorType", "Type");
				sensor = Read(element, "Item3", "sensor", "Sensor");
				break;

			case JsonValueKind.Array:
				hardware = ReadAt(element, 0);
				type = ReadAt(element, 1);
				sensor = ReadAt(element, 2);
				break;

			case JsonValueKind.String:
				// Barely usable but worth taking: the token is what actually decides the match.
				hardware = string.Empty;
				type = string.Empty;
				sensor = element.GetString();
				break;

			default:
				request = null!;
				return false;
		}

		if (string.IsNullOrWhiteSpace(sensor))
		{
			request = null!;
			return false;
		}

		request = new LegacySensorRequest(hardware.Trim(), type.Trim(), sensor.Trim());
		return true;
	}

	/// <summary>The first of the candidate property names that is present and holds a string.</summary>
	private static string Read(JsonElement element, params string[] names)
	{
		foreach (var name in names)
		{
			if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
			{
				return value.GetString() ?? string.Empty;
			}
		}

		return string.Empty;
	}

	private static string ReadAt(JsonElement element, int index) =>
		index < element.GetArrayLength() && element[index].ValueKind == JsonValueKind.String
			? element[index].GetString() ?? string.Empty
			: string.Empty;

	/// <inheritdoc />
	public override string ToString() =>
		string.Create(CultureInfo.InvariantCulture, $"{Hardware}/{Sensor}");
}
