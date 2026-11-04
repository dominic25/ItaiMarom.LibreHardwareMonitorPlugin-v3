using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// One branch of the classic sensor window's tree: the sensors of one piece of hardware that report one
/// reading kind.
/// </summary>
/// <param name="RootName">
/// The name of the top-level hardware the sensors hang off, for example <c>CPU</c>. It is the name the
/// hardware library gives the device, which for a processor or a disk is the model it reports. For a
/// remote device this is the hardware's own name, without the machine label in front of it, because the
/// machine is the branch above it.
/// </param>
/// <param name="TypeName">The reading kind they report, for example <c>Temperature</c>.</param>
/// <param name="Sensors">The branch's sensors, in the order the window shows them.</param>
/// <param name="Source">
/// The key of the remote machine this branch belongs to, or null for the machine the plugin runs on. It is
/// what makes one top-level branch per remote machine possible while two machines can both have a
/// <c>CPU</c>.
/// </param>
/// <param name="MachineName">
/// The label of the remote machine, for the branch the window draws above its hardware; null for local
/// hardware.
/// </param>
/// <remarks>
/// <para>
/// The Macro Deck 2 dialog was a checkbox tree of hardware, then reading kind, then sensor, and this is
/// what that tree is built from: one branch per pair, with the pair's sensors under it.
/// </para>
/// <para>
/// The order is the hardware library's own, deliberately. The old dialog added a node the first time its
/// walk reached it, so a machine's devices and reading kinds come out in the order the library lists
/// them rather than in alphabetical order - and a name-sorted tree is the one thing a user who knows the
/// old dialog would notice immediately.
/// </para>
/// </remarks>
internal sealed record SensorCategory(
	string RootName,
	string TypeName,
	IReadOnlyList<SensorIdentifier> Sensors,
	string? Source = null,
	string? MachineName = null)
{
	/// <summary>
	/// Groups sensors by the machine they came from, then the hardware they hang off and the reading kind
	/// they report, keeping the order they were reported in at every level.
	/// </summary>
	/// <param name="sensors">The sensors the hardware source reported.</param>
	/// <param name="remotes">
	/// The configured remote machines, used to label their branch. A machine the list does not know is
	/// labelled from its sensors' qualified root name, so a tree is never left without a title.
	/// </param>
	internal static IReadOnlyList<SensorCategory> Build(
		IReadOnlyCollection<SensorIdentifier> sensors,
		IReadOnlyList<RemoteSourceDefinition>? remotes = null)
	{
		ArgumentNullException.ThrowIfNull(sensors);

		var labels = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var remote in remotes ?? [])
		{
			labels.TryAdd(remote.Key, remote.Name);
		}

		var branches = new Dictionary<(string? Source, string RootId, SensorType Type), List<SensorIdentifier>>();
		var order = new List<(string? Source, string RootId, SensorType Type, string RootName, string? MachineName)>();

		foreach (var sensor in sensors)
		{
			var source = sensor.Hardware.Source;
			var machineName = source is null ? null : LabelFor(sensor, labels);
			var rootName = source is null || machineName is null
				? sensor.RootHardwareName
				: WithoutMachine(sensor.RootHardwareName, machineName);
			var branch = (Source: source, RootId: sensor.Hardware.RootId, Type: sensor.SensorType);

			if (!branches.TryGetValue(branch, out var group))
			{
				branches[branch] = group = [];
				order.Add((branch.Source, branch.RootId, branch.Type, rootName, machineName));
			}

			group.Add(sensor);
		}

		return
		[
			.. order.Select(branch => new SensorCategory(
				branch.RootName,
				branch.Type.ToString(),
				branches[(branch.Source, branch.RootId, branch.Type)],
				branch.Source,
				branch.MachineName)),
		];
	}

	/// <summary>
	/// The label a remote machine is shown under: the configured name when it is known, otherwise the
	/// part of the qualified root name before the separator.
	/// </summary>
	private static string LabelFor(SensorIdentifier sensor, Dictionary<string, string> labels)
	{
		if (labels.TryGetValue(sensor.Hardware.Source!, out var label))
		{
			return label;
		}

		var root = sensor.RootHardwareName;
		var separator = root.IndexOf(RemoteSourceDefinition.NameSeparator, StringComparison.Ordinal);
		return separator > 0 ? root[..separator] : root;
	}

	/// <summary>
	/// The hardware's own name, with the machine label the parser put in front of it removed. A machine
	/// branch above it already says which PC it is, so repeating the label on every device would only add
	/// noise.
	/// </summary>
	private static string WithoutMachine(string rootName, string machineName)
	{
		var prefix = machineName + RemoteSourceDefinition.NameSeparator;
		return rootName.StartsWith(prefix, StringComparison.Ordinal)
			? rootName[prefix.Length..]
			: rootName;
	}
}
