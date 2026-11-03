namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The part of a hardware source whose remote list can change while the plugin is running. The settings
/// screen writes it, and the next poll is what picks it up.
/// </summary>
public interface IRemoteSourceController
{
	/// <summary>
	/// Replaces the remote sources. A source that was removed is disposed once no read is using it; the
	/// local source is untouched.
	/// </summary>
	void ReplaceRemoteSources(IReadOnlyList<RemoteSourceDefinition> remotes);
}
