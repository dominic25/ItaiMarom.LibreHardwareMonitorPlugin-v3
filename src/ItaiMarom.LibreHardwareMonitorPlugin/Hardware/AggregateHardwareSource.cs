using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The source the polling loop actually sees: this machine's hardware plus every configured remote one.
/// </summary>
/// <remarks>
/// <para>
/// Local hardware stays authoritative for everything that can be changed. Only reads fan out to the
/// remote sources; controls are enumerated and written on this machine, because a remote server publishes
/// readings rather than a control surface.
/// </para>
/// <para>
/// The local source is wrapped rather than replaced, so an elevated helper is still the local source's
/// business: the elevation interface is forwarded to it, and the access note it reports reaches the issue
/// list unchanged.
/// </para>
/// <para>
/// A remote that throws or times out is isolated. Its failure is logged and the local readings and every
/// other remote still come back, which is what makes one unplugged machine an empty branch rather than a
/// plugin that has stopped reading anything.
/// </para>
/// </remarks>
public sealed class AggregateHardwareSource :
	IHardwareSensorSource,
	IHardwareElevation,
	IAccessNoteSource,
	IRemoteSourceController
{
	private readonly IHardwareSensorSource _local;
	private readonly ILogger _logger;
	private readonly Func<RemoteSourceDefinition, IHardwareSensorSource> _remoteFactory;
	private readonly object _sync = new();

	private IReadOnlyList<IHardwareSensorSource> _remotes = [];
	private bool _disposed;

	public AggregateHardwareSource(
		IHardwareSensorSource local,
		ILogger logger,
		Func<RemoteSourceDefinition, IHardwareSensorSource>? remoteFactory = null)
	{
		_local = local ?? throw new ArgumentNullException(nameof(local));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
			.ForContext<AggregateHardwareSource>();
		_remoteFactory = remoteFactory ?? (definition => new RemoteHardwareSource(definition, _logger));
	}

	/// <inheritdoc />
	public bool IsElevated => (_local as IHardwareElevation)?.IsElevated ?? false;

	/// <inheritdoc />
	public string? ElevatedAccessNote => (_local as IHardwareElevation)?.ElevatedAccessNote;

	/// <inheritdoc />
	public string? AccessNote => (_local as IAccessNoteSource)?.AccessNote;

	/// <inheritdoc />
	public Task<ElevationResult> EnableAsync(CancellationToken cancellationToken = default) =>
		(_local as IHardwareElevation)?.EnableAsync(cancellationToken)
		?? Task.FromResult(new ElevationResult(
			ElevationOutcome.Failed,
			"This hardware source cannot be elevated."));

	/// <inheritdoc />
	public void ReplaceRemoteSources(IReadOnlyList<RemoteSourceDefinition> remotes)
	{
		ArgumentNullException.ThrowIfNull(remotes);

		IReadOnlyList<IHardwareSensorSource> replaced;

		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			replaced = _remotes;
			_remotes = remotes.Select(CreateRemote).ToArray();
		}

		DisposeAll(replaced);
	}

	/// <inheritdoc />
	public void Open()
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			_local.Open();
		}
	}

	/// <inheritdoc />
	public void Close()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_local.Close();
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			var sensors = new List<HardwareSensor>(_local.Read(hardwareFilter));

			foreach (var remote in _remotes)
			{
				try
				{
					sensors.AddRange(remote.Read(hardwareFilter));
				}
				catch (Exception exception)
				{
					// One remote is one source. A fake or a future transport that throws is still not a
					// reason to stop reading this machine.
					_logger.Warning(exception, "A remote hardware source failed; its sensors are omitted.");
				}
			}

			return sensors;
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<SensorIdentifier> ReadControls()
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			return _local.ReadControls();
		}
	}

	/// <inheritdoc />
	public bool SetControl(string sensorId, float value)
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			return _local.SetControl(sensorId, value);
		}
	}

	/// <inheritdoc />
	public bool ResetControl(string sensorId)
	{
		lock (_sync)
		{
			ThrowIfDisposed();
			return _local.ResetControl(sensorId);
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		IHardwareSensorSource[] remotes;

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			remotes = [.. _remotes];
			_remotes = [];
		}

		foreach (var remote in remotes)
		{
			Dispose(remote);
		}

		_local.Dispose();
	}

	private IHardwareSensorSource CreateRemote(RemoteSourceDefinition definition)
	{
		try
		{
			return _remoteFactory(definition);
		}
		catch (Exception exception)
		{
			_logger.Warning(
				exception,
				"The remote source {RemoteSource} could not be created; it is being skipped.",
				definition.Name);

			return new UnavailableRemoteSource();
		}
	}

	private void DisposeAll(IEnumerable<IHardwareSensorSource> sources)
	{
		foreach (var source in sources)
		{
			Dispose(source);
		}
	}

	private void Dispose(IHardwareSensorSource source)
	{
		try
		{
			source.Dispose();
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "A remote hardware source did not stop cleanly.");
		}
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	/// <summary>A remote that could not even be constructed, so a bad row cannot remove the rest.</summary>
	private sealed class UnavailableRemoteSource : IHardwareSensorSource
	{
		public void Open()
		{
		}

		public void Close()
		{
		}

		public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter) => [];

		public IReadOnlyList<SensorIdentifier> ReadControls() => [];

		public bool SetControl(string sensorId, float value) => false;

		public bool ResetControl(string sensorId) => false;

		public void Dispose()
		{
		}
	}
}
