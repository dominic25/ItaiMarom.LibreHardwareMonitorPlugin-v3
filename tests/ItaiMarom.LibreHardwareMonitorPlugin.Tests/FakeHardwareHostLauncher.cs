using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// A launcher that answers with a connection the test already built, so the switch between the local
/// source and the helper can be tested without a process or a UAC prompt.
/// </summary>
internal sealed class FakeHardwareHostLauncher : IHardwareHostLauncher
{
	/// <summary>What the next launch answers. A dismissed prompt by default, which changes nothing.</summary>
	public HardwareHostLaunchResult Result { get; set; } = HardwareHostLaunchResult.CancelledByUser;

	/// <summary>How many times the plugin asked Windows to start a helper.</summary>
	public int LaunchCount { get; private set; }

	/// <inheritdoc />
	public Task<HardwareHostLaunchResult> LaunchAsync(CancellationToken cancellationToken)
	{
		LaunchCount++;
		return Task.FromResult(Result);
	}
}
