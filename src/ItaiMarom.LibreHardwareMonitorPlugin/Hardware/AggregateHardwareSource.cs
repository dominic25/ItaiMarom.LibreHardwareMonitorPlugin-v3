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
/// plugin that has stopped reading anything. Each remote polls itself in the background, so its HTTP
/// timeout never lands on this aggregate's caller.
/// </para>
/// </remarks>
public sealed class AggregateHardwareSource :
	IHardwareSensorSource,
	IHardwareElevation,
	IAccessNoteSource,
	IRemoteSourceController,
	IRemoteHealthSource
{
	private readonly IHardwareSensorSource _local;
	private readonly ILogger _logger;
	private readonly Func<RemoteSourceDefinition, IHardwareSensorSource> _remoteFactory;
	private readonly object _sync = new();

	private IReadOnlyList<RemoteEntry> _remotes = [];
	private bool _open;
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
	public event EventHandler<RemoteHealthChangedEventArgs>? RemoteHealthChanged;

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
	public Task<ElevationResult> DisableAsync(CancellationToken cancellationToken = default) =>
		(_local as IHardwareElevation)?.DisableAsync(cancellationToken)
		?? Task.FromResult(new ElevationResult(
			ElevationOutcome.Failed,
			"This hardware source cannot be elevated."));

	/// <inheritdoc />
	public IReadOnlyList<RemoteSourceStatus> RemoteStatuses
	{
		get
		{
			lock (_sync)
			{
				return [.. _remotes.Select(StatusOf)];
			}
		}
	}

	/// <inheritdoc />
	public bool TryRetryRemote(string key)
	{
		lock (_sync)
		{
			var entry = _remotes.FirstOrDefault(remote =>
				string.Equals(remote.Definition.Key, key, StringComparison.Ordinal));

			return entry is not null
				&& entry.Source is IRemoteHealthSource health
				&& health.TryRetryRemote(key);
		}
	}

	/// <inheritdoc />
	public void ReplaceRemoteSources(IReadOnlyList<RemoteSourceDefinition> remotes)
	{
		ArgumentNullException.ThrowIfNull(remotes);

		IReadOnlyList<RemoteEntry> replaced;

		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);

			replaced = _remotes;
			_remotes = remotes.Select(CreateRemote).ToArray();

			foreach (var entry in _remotes)
			{
				if (entry.Source is IRemoteHealthSource health)
				{
					health.RemoteHealthChanged += OnRemoteHealthChanged;
				}

				// A list saved while the loop is running has to start its new machines now; waiting for
				// the next Open would leave a newly added remote dark until a restart.
				if (_open)
				{
					entry.Source.Open();
				}
			}
		}

		DisposeAll(replaced);
	}

	/// <inheritdoc />
	public void SetPollingInterval(TimeSpan interval)
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_local.SetPollingInterval(interval);

			foreach (var remote in _remotes)
			{
				remote.Source.SetPollingInterval(interval);
			}
		}
	}

	/// <inheritdoc />
	public void Rescan()
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			_local.Rescan();

			foreach (var remote in _remotes)
			{
				remote.Source.Rescan();
			}
		}
	}

	/// <inheritdoc />
	public void Open()
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			_open = true;
			_local.Open();

			// A remote reads itself in the background now, so its loop starts and stops with the aggregate
			// rather than with the first Read. Opening is idempotent.
			foreach (var remote in _remotes)
			{
				remote.Source.Open();
			}
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

			_open = false;
			_local.Close();

			foreach (var remote in _remotes)
			{
				remote.Source.Close();
			}
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
					sensors.AddRange(remote.Source.Read(hardwareFilter));
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
		RemoteEntry[] remotes;

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_open = false;
			remotes = [.. _remotes];
			_remotes = [];
		}

		foreach (var remote in remotes)
		{
			Dispose(remote);
		}

		_local.Dispose();
	}

	private RemoteEntry CreateRemote(RemoteSourceDefinition definition)
	{
		try
		{
			return new RemoteEntry(definition, _remoteFactory(definition));
		}
		catch (Exception exception)
		{
			_logger.Warning(
				exception,
				"The remote source {RemoteSource} could not be created; it is being skipped.",
				definition.Name);

			return new RemoteEntry(definition, new UnavailableRemoteSource(definition));
		}
	}

	private RemoteSourceStatus StatusOf(RemoteEntry entry) =>
		entry.Source is IRemoteHealthSource health
			? health.RemoteStatuses.FirstOrDefault(status =>
				string.Equals(status.Key, entry.Definition.Key, StringComparison.Ordinal))
				?? new RemoteSourceStatus(
					entry.Definition.Key,
					entry.Definition.Name,
					entry.Definition.Url,
					RemoteSourceHealth.Unknown)
			: new RemoteSourceStatus(
				entry.Definition.Key,
				entry.Definition.Name,
				entry.Definition.Url,
				RemoteSourceHealth.Unknown);

	private void OnRemoteHealthChanged(object? sender, RemoteHealthChangedEventArgs args)
	{
		try
		{
			RemoteHealthChanged?.Invoke(this, args);
		}
		catch (Exception exception)
		{
			// The remote's own loop is the caller; a notification that throws must not stop it.
			_logger.Debug(exception, "A remote health subscriber failed.");
		}
	}

	private void DisposeAll(IEnumerable<RemoteEntry> entries)
	{
		foreach (var entry in entries)
		{
			Dispose(entry);
		}
	}

	private void Dispose(RemoteEntry entry)
	{
		if (entry.Source is IRemoteHealthSource health)
		{
			health.RemoteHealthChanged -= OnRemoteHealthChanged;
		}

		try
		{
			entry.Source.Dispose();
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "A remote hardware source did not stop cleanly.");
		}
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	private sealed record RemoteEntry(RemoteSourceDefinition Definition, IHardwareSensorSource Source);

	/// <summary>A remote that could not even be constructed, so a bad row cannot remove the rest.</summary>
	private sealed class UnavailableRemoteSource : IHardwareSensorSource, IRemoteHealthSource
	{
		private readonly RemoteSourceDefinition _definition;

		public UnavailableRemoteSource(RemoteSourceDefinition definition) => _definition = definition;

		public event EventHandler<RemoteHealthChangedEventArgs>? RemoteHealthChanged
		{
			add
			{
			}
			remove
			{
			}
		}

		public IReadOnlyList<RemoteSourceStatus> RemoteStatuses =>
		[
			new(
				_definition.Key,
				_definition.Name,
				_definition.Url,
				new RemoteSourceHealth(
					RemoteSourceState.Offline,
					null,
					"The remote source could not be created.",
					0)),
		];

		public bool TryRetryRemote(string key) =>
			string.Equals(key, _definition.Key, StringComparison.Ordinal);

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
