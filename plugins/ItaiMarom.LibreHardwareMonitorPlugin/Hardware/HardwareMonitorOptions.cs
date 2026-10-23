namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// How often the hardware is read and how long a reading stays usable.
/// </summary>
public sealed record HardwareMonitorOptions
{
	/// <summary>The fastest interval the loop will run at.</summary>
	public static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromMilliseconds(10);

	/// <summary>The slowest interval the loop will run at.</summary>
	public static readonly TimeSpan MaximumPollingInterval = TimeSpan.FromSeconds(60);

	/// <summary>
	/// How often the hardware tree is re-read. The old plugin's <c>pollingRate</c> setting, in
	/// milliseconds, defaulting to the 1000 it hardcoded.
	/// </summary>
	public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(1);

	/// <summary>
	/// How long a reading stays usable after the poll that produced it. Long enough that a single failed
	/// read - a disk that answered late, a sensor that returned null once - does not blank a variable,
	/// short enough that a device which really went away stops reporting a value that is no longer true.
	/// </summary>
	public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>The settings a plugin with no configuration in hand runs with.</summary>
	public static HardwareMonitorOptions Default { get; } = new();

	/// <summary>
	/// Clamps the interval into the supported range and keeps <see cref="StaleAfter"/> at or above it, so
	/// a configuration file someone edited by hand cannot produce a loop that spins or a reading that
	/// expires between two polls.
	/// </summary>
	public HardwareMonitorOptions Normalized()
	{
		var interval = Clamp(PollingInterval, MinimumPollingInterval, MaximumPollingInterval);

		return this with
		{
			PollingInterval = interval,
			StaleAfter = StaleAfter < interval ? interval : StaleAfter,
		};
	}

	private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) => value switch
	{
		_ when value < minimum => minimum,
		_ when value > maximum => maximum,
		_ => value,
	};
}
