using MacroDeck.Plugin.Hosting.Integrations.HostApis;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// Records what the integration would have told the host. The real notifier is fire-and-forget over a live
/// session, so a test cannot observe it; this stands in for it and makes the announcement an assertion.
/// </summary>
internal sealed class RecordingCatalogNotifier : IPluginCatalogNotifier
{
	public List<(string Kind, string? LocalId, string? Reason)> Announcements { get; } = [];

	public void CatalogChanged(string kind, string? localId = null, string? reason = null) =>
		Announcements.Add((kind, localId, reason));
}
