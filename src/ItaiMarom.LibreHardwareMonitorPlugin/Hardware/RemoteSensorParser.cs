using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Reads the tree LibreHardwareMonitor's Remote Web Server serves at <c>/data.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The wire shape is the library's own settings tree. The root is a synthetic node, its one child is the
/// machine, and below that hardware hangs type groups which in turn hold the sensors. Only hardware
/// nodes carry a <c>HardwareId</c> and only sensor nodes carry a <c>SensorId</c>, so the walk descends
/// through every node and recognises what each one is from the fields it has.
/// </para>
/// <para>
/// The formatted <c>Value</c> is text like <c>51.0 °C</c>; <c>RawValue</c> is the number without the
/// unit, which is what the plugin caches. The library can serialise <c>NaN</c> or an infinity for a
/// sensor with nothing to report, and those become "no reading" exactly as they do for local hardware.
/// </para>
/// </remarks>
internal static class RemoteSensorParser
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNameCaseInsensitive = true,
		NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip,
	};

	/// <summary>
	/// The first number in formatted text. A server can send the number formatted with either a dot or
	/// a comma as the decimal separator, depending on the culture it runs under.
	/// </summary>
	private static readonly Regex LeadingNumber = new(
		@"[-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][-+]?\d+)?",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>Parses one document into the sensors it describes, in the order the library listed them.</summary>
	public static IReadOnlyList<HardwareSensor> Parse(string json, RemoteSourceDefinition source)
	{
		ArgumentNullException.ThrowIfNull(json);
		ArgumentNullException.ThrowIfNull(source);

		var root = JsonSerializer.Deserialize<RemoteNode>(json, SerializerOptions);
		return root is null ? [] : Parse(root, source);
	}

	/// <summary>Parses one document from a stream, which is how a response is normally read.</summary>
	public static IReadOnlyList<HardwareSensor> Parse(Stream stream, RemoteSourceDefinition source)
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(source);

		var root = JsonSerializer.Deserialize<RemoteNode>(stream, SerializerOptions);
		return root is null ? [] : Parse(root, source);
	}

	private static List<HardwareSensor> Parse(RemoteNode root, RemoteSourceDefinition source)
	{
		var prefix = "/remote/" + source.Key;
		var sensors = new List<HardwareSensor>();

		// The machine on the other end runs the same hardware library, so it repeats an id in the same
		// cases this machine does. Settling it here keeps one remote sensor from answering for two.
		Walk(root, null, prefix, source, new UniqueSensorIds(), sensors);
		return sensors;
	}

	/// <summary>
	/// Walks one node and its children. A node with a hardware id opens (or continues) a hardware
	/// location; a node with a sensor id is a leaf; a node with neither is a grouping node - the document
	/// root and the machine node are both, which is why the walk recognises nodes rather than levels.
	/// </summary>
	private static void Walk(
		RemoteNode node,
		HardwareLocation? hardware,
		string prefix,
		RemoteSourceDefinition source,
		UniqueSensorIds ids,
		List<HardwareSensor> sensors)
	{
		var location = hardware;

		if (NormalizeIdentifier(node.HardwareId) is { } hardwareId)
		{
			var name = string.IsNullOrWhiteSpace(node.Text) ? "Hardware" : node.Text.Trim();
			location = hardware is null
				? HardwareLocation.Root(prefix + hardwareId, source.Qualify(name), source.Key)
				: hardware.Child(prefix + hardwareId, name);
		}

		if (location is not null
			&& NormalizeIdentifier(node.SensorId) is { } sensorId
			&& Enum.TryParse<SensorType>(node.Type, ignoreCase: true, out var kind))
		{
			var name = string.IsNullOrWhiteSpace(node.Text) ? sensorId : node.Text.Trim();
			sensors.Add(new HardwareSensor(
				ids.Apply(new SensorIdentifier(location, prefix + sensorId, name, kind)),
				ValueOf(node)));
		}

		if (node.Children is not { Count: > 0 } children)
		{
			return;
		}

		foreach (var child in children)
		{
			if (child is not null)
			{
				Walk(child, location, prefix, source, ids, sensors);
			}
		}
	}

	/// <summary>
	/// A sensor identifier from the document, with a leading slash, or null for the grouping nodes that
	/// carry none. The document's root id can be <c>/</c>, which is a grouping node and not a device.
	/// </summary>
	private static string? NormalizeIdentifier(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}

		var id = raw.Trim();
		if (id == "/")
		{
			return null;
		}

		return id.StartsWith('/') ? id : "/" + id;
	}

	/// <summary>
	/// The reading a sensor reports. The raw value is preferred because it is the number itself; text is
	/// a fallback for a document from a server that only sent the formatted form. Either can arrive as a
	/// number or as formatted text depending on the server's version - the Remote Web Server has shipped
	/// both - so both shapes are read here.
	/// </summary>
	private static float? ValueOf(RemoteNode node)
	{
		if (TryReadNumber(node.RawValue, out var raw))
		{
			return Finite(raw);
		}

		if (!TryReadNumber(node.Value, out var formatted))
		{
			return null;
		}

		return Finite(formatted);
	}

	/// <summary>Reads a node's <c>RawValue</c>, which is a JSON number in one version and text in another.</summary>
	private static bool TryReadNumber(JsonElement? element, out double value)
	{
		value = 0;

		if (element is not { } raw)
		{
			return false;
		}

		return raw.ValueKind switch
		{
			JsonValueKind.Number => raw.TryGetDouble(out value),
			JsonValueKind.String => TryReadNumber(raw.GetString(), out value),
			_ => false,
		};
	}

	/// <summary>
	/// The first number in formatted text such as <c>51.0 °C</c> or <c>0,696 V</c>. A named floating
	/// point literal, <c>NaN</c>, or an infinity has no number to read and is no reading.
	/// </summary>
	private static bool TryReadNumber(string? text, out double value)
	{
		value = 0;

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var match = LeadingNumber.Match(text);
		if (!match.Success)
		{
			return false;
		}

		var token = match.Value;

		// A comma is this server's decimal separator, not a thousands separator. A document that used
		// both is read as the culture-independent form it looks like.
		token = token.Contains('.')
			? token.Replace(",", string.Empty, StringComparison.Ordinal)
			: token.Replace(',', '.');

		return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
	}

	/// <summary>A finite <see cref="float" />, or null when the number cannot be one.</summary>
	private static float? Finite(double value)
	{
		if (!double.IsFinite(value))
		{
			return null;
		}

		var single = (float)value;
		return float.IsFinite(single) ? single : null;
	}

	/// <summary>One node of the document. Every member is optional because each kind of node uses its own.</summary>
	internal sealed class RemoteNode
	{
		public string? Text { get; set; }

		public string? HardwareId { get; set; }

		public string? SensorId { get; set; }

		public string? Type { get; set; }

		public JsonElement? RawValue { get; set; }

		public string? Value { get; set; }

		public List<RemoteNode?>? Children { get; set; }
	}
}
