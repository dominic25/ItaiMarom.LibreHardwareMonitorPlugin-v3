using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Stable identity of one LibreHardwareMonitor sensor: the hardware that exposes it, its own library
/// identifier and name, and its kind. Everything the plugin caches, configures or migrates is keyed by
/// this.
/// </summary>
/// <param name="Hardware">
/// The owning hardware. The Macro Deck 2 plugin only ever looked at top-level hardware, so a
/// motherboard's super-I/O sensors - the ones that carry the voltages and fan speeds - were invisible to
/// it. Walking the whole tree is why the hardware carries a <c>Parent/Child</c> path.
/// </param>
/// <param name="Id">
/// The hardware library's own sensor identifier, for example <c>/lpc/nct6776f/0/temperature/1</c>. It is
/// the one part of a sensor that is both unique and unchanged across restarts, so it - not the display
/// name - is what the generated variable id is derived from.
/// </param>
/// <param name="SensorName">The library's sensor name, for example <c>CPU Core #1</c>.</param>
/// <param name="SensorType">The kind of reading, which also decides how the value is formatted.</param>
public sealed record SensorIdentifier(
	HardwareLocation Hardware,
	string Id,
	string SensorName,
	SensorType SensorType)
{
	/// <summary>Replaces every character Macro Deck 2 refused to put in a variable name.</summary>
	private static readonly Regex LegacyUnsafeCharacters =
		new("[^A-Za-z0-9]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>
	/// Reduces any text to the shape Macro Deck 2 used for a variable name: every character outside
	/// <c>[A-Za-z0-9]</c> becomes an underscore and the result is lowercased.
	/// </summary>
	/// <remarks>
	/// Public because the Macro Deck 2 migration has to reduce a token it read out of a foreign settings
	/// file the same way, and a second copy of this rule is a second thing that can drift.
	/// </remarks>
	public static string ToLegacyName(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return LegacyUnsafeCharacters.Replace(text, "_").ToLowerInvariant();
	}

	/// <summary>
	/// The token the Macro Deck 2 plugin stored in its <c>requestedSensors</c> configuration, and the
	/// text its variable names were built from: <c>{SensorName}_{SensorType}</c>.
	/// </summary>
	public string LegacyToken => $"{SensorName}_{SensorType}";

	/// <summary>The name of the top-level hardware this sensor hangs off.</summary>
	public string RootHardwareName => Hardware.RootName;

	/// <summary>
	/// The declared local id of the eager variable for this sensor: derived from the library's stable
	/// identifier, so it survives a restart and a rename. Unique per sensor unless two identifiers
	/// sanitise alike, which <see cref="Variables.SensorVariableNaming"/> then settles.
	/// </summary>
	public string VariableId => VariableNaming.ToDeclaredId(Id);

	/// <summary>
	/// The catalog resource id for this sensor. This SDK build's catalog ids may carry any character but
	/// the <c>::</c> separator, whitespace and control characters, so the library identifier - which is
	/// already unique, stable and slash-separated - passes through readable.
	/// </summary>
	public string ResourceId => VariableNaming.ToResourceId(Id);

	/// <summary>What a user sees in the variable browser, for example <c>CPU Core #1 (Temperature)</c>.</summary>
	public string DisplayName => $"{SensorName} ({SensorType})";

	/// <summary>
	/// Macro Deck 2's variable name reproduced exactly: the legacy token with every non-alphanumeric
	/// character turned into an underscore, lowercased. It keeps runs of underscores - <c>CPU Core #1</c>
	/// at <c>Temperature</c> is <c>cpu_core__1_temperature</c>, not <c>cpu_core_1_temperature</c> - which
	/// is exactly why it is kept separate from <see cref="VariableName"/>.
	/// </summary>
	/// <remarks>
	/// The old plugin lowercased with the <em>current culture</em>, so a Turkish machine turned an "I"
	/// into a dotless "ı". This uses the invariant culture instead: a name is derived once and then has to
	/// mean the same thing on every machine that reads a shared button layout.
	/// </remarks>
	public string LegacyVariableName => ToLegacyName(LegacyToken);

	/// <summary>
	/// The canonical Macro Deck 3 variable name for this sensor: the legacy name where the host still
	/// accepts it verbatim, so an upgraded button keeps working, and a canonicalized fallback for the
	/// names the host would reject - chiefly the ones the old plugin produced starting with a digit, such
	/// as <c>3vsb_voltage</c>.
	/// </summary>
	public string VariableName
	{
		get
		{
			var legacy = LegacyVariableName;
			if (VariableNaming.IsValid(legacy))
			{
				return legacy;
			}

			var canonical = VariableNaming.Sanitize(LegacyToken);
			if (VariableNaming.IsValid(canonical))
			{
				return canonical;
			}

			// Unreachable for any real sensor - a sensor type is always letters - but a variable name has
			// to be legal whatever the hardware library reports.
			var qualified = VariableNaming.Sanitize($"{Hardware.Path} {LegacyToken}");
			return VariableNaming.IsValid(qualified) ? qualified : "v_sensor";
		}
	}

	/// <summary>
	/// Whether a user's search text names this sensor: its own name, what Macro Deck calls it, its kind,
	/// or the device and tree path it lives on. An absent search matches everything.
	/// </summary>
	/// <remarks>
	/// Shared by the variable catalog's search and the fan picker. Two copies of "what a search matches"
	/// would eventually answer the same query differently in the two places, and the difference would look
	/// like a bug in the hardware rather than in the plugin.
	/// </remarks>
	public bool Matches(string? search)
	{
		if (string.IsNullOrWhiteSpace(search))
		{
			return true;
		}

		return SensorName.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| VariableName.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| SensorType.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
			|| Hardware.RootName.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| Hardware.Path.Contains(search, StringComparison.OrdinalIgnoreCase);
	}
}
