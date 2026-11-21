namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>How a remote machine is currently answering.</summary>
public enum RemoteSourceState
{
	/// <summary>No answer yet. Not an outage: the first poll may simply not have happened.</summary>
	Unknown,

	/// <summary>The last read succeeded.</summary>
	Online,

	/// <summary>The last read failed, but not often enough to call the machine unreachable.</summary>
	Degraded,

	/// <summary>Enough consecutive reads failed that the machine is treated as unreachable.</summary>
	Offline,
}

/// <summary>
/// What the plugin knows about one remote machine's reachability. It is separate from the readings: a
/// remote can be offline while its last tree is still shown, and online while a particular sensor reports
/// no value.
/// </summary>
public readonly record struct RemoteSourceHealth(
	RemoteSourceState State,
	DateTimeOffset? LastSuccessAt,
	string? LastError,
	int ConsecutiveFailures)
{
	/// <summary>Before the first read: nothing has been tried yet.</summary>
	public static RemoteSourceHealth Unknown { get; } = new(RemoteSourceState.Unknown, null, null, 0);

	/// <summary>Whether the machine answered recently, including a single failed retry.</summary>
	public bool IsOnline => State is RemoteSourceState.Online or RemoteSourceState.Degraded;

	/// <summary>Whether the machine has answered at least once since the plugin started.</summary>
	public bool HasBeenOnline => LastSuccessAt is not null;
}

/// <summary>One remote machine as the health surfaces name it.</summary>
public sealed record RemoteSourceStatus(
	string Key,
	string Name,
	string Url,
	RemoteSourceHealth Health);

/// <summary>Raised when a remote machine starts or stops answering.</summary>
public sealed class RemoteHealthChangedEventArgs(RemoteSourceStatus status) : EventArgs
{
	/// <summary>The machine, and the state it just moved to.</summary>
	public RemoteSourceStatus Status { get; } = status;
}

/// <summary>
/// The part of a hardware source that can report every remote machine's reachability and retry one
/// immediately. The sensor window, the issue list and the variables all read this one answer.
/// </summary>
public interface IRemoteHealthSource
{
	/// <summary>Every configured remote machine, in the order it was configured.</summary>
	IReadOnlyList<RemoteSourceStatus> RemoteStatuses { get; }

	/// <summary>The status of one machine, or null when no configured remote has that key.</summary>
	RemoteSourceStatus? Find(string key) =>
		RemoteStatuses.FirstOrDefault(status => string.Equals(status.Key, key, StringComparison.Ordinal));

	/// <summary>
	/// Clears a machine's backoff and asks the next refresh to happen now. Returns false when no remote
	/// has that key.
	/// </summary>
	bool TryRetryRemote(string key);

	/// <summary>Raised when a machine moves between online and offline.</summary>
	event EventHandler<RemoteHealthChangedEventArgs>? RemoteHealthChanged;
}
