using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Variables;

/// <summary>A sensor in the catalog, with the resource id it is reached by and the node it hangs under.</summary>
public sealed record SensorCatalogEntry(SensorIdentifier Sensor, string Id, string ParentId);

/// <summary>
/// One piece of top-level hardware as a node of the catalog: the group a user opens before they see the
/// sensors.
/// </summary>
/// <remarks>
/// Sub-hardware is flattened into its parent on purpose. A motherboard's voltages and fan speeds come
/// from a super-I/O chip the user has never heard of, so they belong under "Motherboard"; the chip's own
/// name would only be a detour. The device each sensor came from is still recoverable, because it is part
/// of the sensor's identity.
/// </remarks>
public sealed record SensorCatalogNode(string Id, string Name, IReadOnlyList<SensorCatalogEntry> Entries);

/// <summary>
/// The whole sensor tree, flattened into the shape the variable catalog serves: one node per piece of
/// top-level hardware, each holding every sensor it and its sub-hardware expose.
/// </summary>
public sealed class SensorCatalogTable
{
	/// <summary>The table for a machine that reports nothing - a catalog with no nodes, not a failure.</summary>
	public static SensorCatalogTable Empty { get; } = new(
		[],
		[],
		new Dictionary<string, SensorIdentifier>(StringComparer.Ordinal),
		new Dictionary<string, SensorCatalogNode>(StringComparer.Ordinal));

	private readonly IReadOnlyDictionary<string, SensorIdentifier> _sensorsById;
	private readonly IReadOnlyDictionary<string, SensorCatalogNode> _nodesById;

	private SensorCatalogTable(
		IReadOnlyList<SensorCatalogNode> hardware,
		IReadOnlyList<SensorCatalogEntry> sensors,
		IReadOnlyDictionary<string, SensorIdentifier> sensorsById,
		IReadOnlyDictionary<string, SensorCatalogNode> nodesById)
	{
		Hardware = hardware;
		Sensors = sensors;
		_sensorsById = sensorsById;
		_nodesById = nodesById;
	}

	/// <summary>The top-level hardware nodes, in display order.</summary>
	public IReadOnlyList<SensorCatalogNode> Hardware { get; }

	/// <summary>Every sensor in the table, in display order, for searching across the whole tree.</summary>
	public IReadOnlyList<SensorCatalogEntry> Sensors { get; }

	/// <summary>Looks a sensor up by the resource id it was handed out under, or by its raw library id.</summary>
	public bool TryGetSensor(string resourceId, out SensorIdentifier sensor) =>
		_sensorsById.TryGetValue(resourceId, out sensor!);

	/// <summary>Looks a hardware node up by the resource id it was handed out under.</summary>
	public bool TryGetHardware(string resourceId, out SensorCatalogNode node) =>
		_nodesById.TryGetValue(resourceId, out node!);

	/// <summary>Builds the table from a fresh sensor tree.</summary>
	public static SensorCatalogTable Build(IReadOnlyList<SensorIdentifier> sensors)
	{
		ArgumentNullException.ThrowIfNull(sensors);

		// Resource ids are handed out from one pool so a sensor can never be reached at the id a hardware
		// node already answers to.
		var usedIds = new HashSet<string>(StringComparer.Ordinal);
		var byId = new Dictionary<string, SensorIdentifier>(StringComparer.Ordinal);
		var nodesById = new Dictionary<string, SensorCatalogNode>(StringComparer.Ordinal);
		var nodes = new List<SensorCatalogNode>();
		var entries = new List<SensorCatalogEntry>();

		var groups = sensors
			.Distinct()
			.GroupBy(sensor => sensor.Hardware.RootId, StringComparer.Ordinal)
			.OrderBy(group => group.First().Hardware.RootName, StringComparer.Ordinal);

		foreach (var group in groups)
		{
			var nodeId = UniqueResourceId(VariableNaming.ToResourceId(group.Key), usedIds);
			usedIds.Add(nodeId);

			var nodeEntries = new List<SensorCatalogEntry>();
			foreach (var sensor in group
				.OrderBy(item => item.Hardware.Path, StringComparer.Ordinal)
				.ThenBy(item => item.SensorType)
				.ThenBy(item => item.SensorName, StringComparer.Ordinal))
			{
				var id = UniqueResourceId(sensor.ResourceId, usedIds);
				usedIds.Add(id);

				byId[id] = sensor;
				var entry = new SensorCatalogEntry(sensor, id, nodeId);
				nodeEntries.Add(entry);
				entries.Add(entry);
			}

			var node = new SensorCatalogNode(nodeId, group.First().Hardware.RootName, nodeEntries);
			nodes.Add(node);
			nodesById[nodeId] = node;
		}

		return new SensorCatalogTable(nodes, entries, byId, nodesById);
	}

	private static string UniqueResourceId(string id, HashSet<string> used)
	{
		if (!used.Contains(id))
		{
			return id;
		}

		for (var suffix = 2; ; suffix++)
		{
			var tail = "-" + suffix.ToString(CultureInfo.InvariantCulture);
			var room = VariableNaming.MaxResourceIdLength - tail.Length;
			var head = id.Length > room ? id[..room] : id;
			var candidate = head + tail;

			if (VariableNaming.IsValidResourceId(candidate) && !used.Contains(candidate))
			{
				return candidate;
			}
		}
	}
}
