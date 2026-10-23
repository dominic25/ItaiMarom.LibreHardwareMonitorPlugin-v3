using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// One branch of the classic sensor window's tree: the sensors of one piece of hardware that report one
/// reading kind.
/// </summary>
/// <param name="RootName">
/// The name of the top-level hardware the sensors hang off, for example <c>CPU</c>. It is the name the
/// hardware library gives the device, which for a processor or a disk is the model it reports.
/// </param>
/// <param name="TypeName">The reading kind they report, for example <c>Temperature</c>.</param>
/// <param name="Sensors">The branch's sensors, in the order the window shows them.</param>
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
	IReadOnlyList<SensorIdentifier> Sensors)
{
	/// <summary>
	/// Groups sensors by the hardware they hang off and then by the reading kind they report, keeping the
	/// order they were reported in at both levels.
	/// </summary>
	internal static IReadOnlyList<SensorCategory> Build(IReadOnlyCollection<SensorIdentifier> sensors)
	{
		ArgumentNullException.ThrowIfNull(sensors);

		var branches = new Dictionary<(string Hardware, SensorType Type), List<SensorIdentifier>>();
		var order = new List<(string Hardware, SensorType Type)>();

		foreach (var sensor in sensors)
		{
			var branch = (Hardware: sensor.RootHardwareName, Type: sensor.SensorType);
			if (!branches.TryGetValue(branch, out var group))
			{
				branches[branch] = group = [];
				order.Add(branch);
			}

			group.Add(sensor);
		}

		return
		[
			.. order.Select(branch => new SensorCategory(
				branch.Hardware,
				branch.Type.ToString(),
				branches[branch])),
		];
	}
}
