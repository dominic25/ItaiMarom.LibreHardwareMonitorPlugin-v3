using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Stable identity of one LibreHardwareMonitor sensor: the hardware that exposes it, its name and its
/// kind. Everything the plugin caches, configures or migrates is keyed by this.
/// </summary>
/// <param name="HardwarePath">
/// The owning hardware's path, <c>Parent/Child</c> for sub-hardware. The Macro Deck 2 plugin only ever
/// looked at top-level hardware, so a motherboard's super-I/O sensors - the ones that carry the voltages
/// and fan speeds - were invisible to it. Walking the whole tree is why the path can have a slash in it.
/// </param>
/// <param name="SensorName">The library's sensor name, for example <c>CPU Core #1</c>.</param>
/// <param name="SensorType">The kind of reading, which also decides how the value is formatted.</param>
public sealed record SensorIdentifier(string HardwarePath, string SensorName, SensorType SensorType)
{
	/// <summary>Replaces every character Macro Deck 2 refused to put in a variable name.</summary>
	private static readonly Regex LegacyUnsafeCharacters =
		new("[^A-Za-z0-9]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	/// <summary>
	/// The token the Macro Deck 2 plugin stored in its <c>requestedSensors</c> configuration, and the
	/// text its variable names were built from: <c>{SensorName}_{SensorType}</c>.
	/// </summary>
	public string LegacyToken => $"{SensorName}_{SensorType}";

	/// <summary>The name of the top-level hardware this sensor hangs off.</summary>
	public string RootHardwareName
	{
		get
		{
			var separator = HardwarePath.IndexOf('/', StringComparison.Ordinal);
			return separator < 0 ? HardwarePath : HardwarePath[..separator];
		}
	}

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
	public string LegacyVariableName =>
		LegacyUnsafeCharacters.Replace(LegacyToken, "_").ToLowerInvariant();

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
			var qualified = VariableNaming.Sanitize($"{HardwarePath} {LegacyToken}");
			return VariableNaming.IsValid(qualified) ? qualified : "v_sensor";
		}
	}
}
