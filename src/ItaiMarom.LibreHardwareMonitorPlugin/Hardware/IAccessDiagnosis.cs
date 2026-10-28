namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// What to tell a user about the readings this process cannot reach, and whether starting an elevated
/// helper would change that.
/// </summary>
/// <param name="Note">
/// The sentence to show the user, or null when every reading is reachable. It is the same sentence the
/// sensor window prints and the issue list carries, so the two cannot disagree about what is missing.
/// </param>
/// <param name="CanElevate">
/// Whether the missing readings are missing because this process is not elevated - the one case a helper
/// launched through UAC can fix. A driver that is not installed, or one that is installed but will not
/// open even for an administrator, is not something elevation can help with.
/// </param>
public readonly record struct AccessDiagnosis(string? Note, bool CanElevate)
{
	/// <summary>The ordinary answer: this process can read everything.</summary>
	public static AccessDiagnosis Reachable => new(null, CanElevate: false);

	/// <summary>Whether there is anything to tell the user.</summary>
	public bool IsReachable => Note is null;
}

/// <summary>
/// The seam over the machine's driver state, in the same shape <see cref="IHardwareSensorSource"/> is the
/// seam over the hardware library: the real answer in production, a scripted one in a test that has to
/// know what a warning looks like.
/// </summary>
public interface IAccessDiagnosis
{
	/// <summary>What is missing right now, and whether elevation would help.</summary>
	AccessDiagnosis Diagnose();
}

/// <summary>
/// The real diagnosis. When an elevated helper is serving the hardware, its answer is the one that
/// counts: this process cannot open the driver itself, so asking it would always report the readings as
/// missing even though the helper is reading them.
/// </summary>
/// <remarks>
/// The local answer is cached for a few seconds. Working it out opens the driver's device and reads the
/// service registry, and it is asked from UI paths - the sensor window and the action's error message -
/// where doing that on every access would make a slow read look like a slow window. The elevated answer
/// is never cached: it is a field read, and it has to follow the helper connecting or dropping at once.
/// </remarks>
public sealed class KernelAccessDiagnosis : IAccessDiagnosis
{
	/// <summary>
	/// How long a local answer is reused. Long enough that filling in the sensor window does not reopen the
	/// driver for every field, short enough that installing the driver is noticed without a restart.
	/// </summary>
	private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

	private readonly IHardwareSensorSource? _source;
	private readonly object _sync = new();

	private AccessDiagnosis _cached;
	private long _cachedUntil;
	private bool _hasCached;

	public KernelAccessDiagnosis(IHardwareSensorSource? source = null) => _source = source;

	/// <inheritdoc />
	public AccessDiagnosis Diagnose()
	{
		if (_source is IHardwareElevation { IsElevated: true } elevation)
		{
			return new AccessDiagnosis(elevation.ElevatedAccessNote, CanElevate: false);
		}

		lock (_sync)
		{
			var now = Environment.TickCount64;
			if (_hasCached && now < _cachedUntil)
			{
				return _cached;
			}

			_cached = KernelAccess.Diagnose();
			_cachedUntil = now + (long)CacheLifetime.TotalMilliseconds;
			_hasCached = true;
			return _cached;
		}
	}
}
