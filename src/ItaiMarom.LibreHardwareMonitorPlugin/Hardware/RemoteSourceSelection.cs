namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The remote sources the plugin is currently configured with, shared by the integration that reads the
/// configuration and the settings flow that opens on it.
/// </summary>
/// <remarks>
/// Deliberately the same shape as <see cref="Variables.SensorSelection" />: a different list instance is
/// stored on every change, so a reader can tell by reference that its copy is stale, and a reader never
/// has to hold the lock while it walks the list.
/// </remarks>
public sealed class RemoteSourceSelection
{
	private readonly object _sync = new();
	private IReadOnlyList<RemoteSourceDefinition> _remotes = [];

	/// <summary>The configured remotes. Never null; empty until something is configured.</summary>
	public IReadOnlyList<RemoteSourceDefinition> Remotes
	{
		get
		{
			lock (_sync)
			{
				return _remotes;
			}
		}
	}

	/// <summary>Replaces the list. Remotes with the same address or key are already settled by the caller.</summary>
	public void Replace(IEnumerable<RemoteSourceDefinition>? remotes)
	{
		var replacement = remotes?.ToArray() ?? [];
		lock (_sync)
		{
			_remotes = replacement;
		}
	}
}
