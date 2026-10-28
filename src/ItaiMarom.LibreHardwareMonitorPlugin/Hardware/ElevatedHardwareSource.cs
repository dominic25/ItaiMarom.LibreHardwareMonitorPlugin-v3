using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The source the plugin actually polls: the local hardware library until the user asks for elevated
/// readings, and the elevated helper's pipe after that.
/// </summary>
/// <remarks>
/// <para>
/// The swap is the whole point. A process that is not an administrator cannot open the driver the
/// readings need, and a process that <em>is</em> an administrator prompts once at every start. This source
/// starts local, lets the issue list start an elevated copy of the plugin on demand, and then hands every
/// call to the helper. The plugin's polling loop, cache and variable surface do not know the difference.
/// </para>
/// <para>
/// Every call is serialised by <c>_sync</c>, including the switch itself. That is what lets the local
/// hardware be closed before the helper opens its own: the polling loop can be in the middle of a read
/// when the user presses the button, and the read has to finish before the driver is handed over.
/// </para>
/// <para>
/// A helper that dies is not a dead plugin. A broken pipe drops back to the local source, which still
/// reads everything Windows itself exposes, and the issue list goes back to offering elevation.
/// </para>
/// </remarks>
internal sealed class ElevatedHardwareSource : IHardwareSensorSource, IHardwareElevation
{
	private readonly IHardwareSensorSource _local;
	private readonly IHardwareHostLauncher _launcher;
	private readonly ILogger _logger;
	private readonly object _sync = new();

	/// <summary>Serialises two attempts to start a helper, so one UAC prompt is not answered by two.</summary>
	private readonly SemaphoreSlim _enabling = new(1, 1);

	private volatile HardwareHostConnection? _connection;
	private bool _localOpen;
	private bool _remoteOpen;
	private volatile string? _elevatedAccessNote;
	private bool _disposed;

	public ElevatedHardwareSource(
		IHardwareSensorSource local,
		IHardwareHostLauncher launcher,
		ILogger logger)
	{
		_local = local ?? throw new ArgumentNullException(nameof(local));
		_launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<ElevatedHardwareSource>();
	}

	/// <inheritdoc />
	public bool IsElevated => _connection is not null;

	/// <inheritdoc />
	public string? ElevatedAccessNote
	{
		get
		{
			// A lock-free read on purpose: the diagnosis asks this from UI paths, and the lock it used to
			// take is the same one a read holds across a pipe round trip. The connection is published
			// only after its note is set, so a non-null connection has the note that belongs to it.
			var connection = _connection;
			return connection is null ? null : _elevatedAccessNote;
		}
	}

	/// <inheritdoc />
	public async Task<ElevationResult> EnableAsync(CancellationToken cancellationToken = default)
	{
		await _enabling.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return new ElevationResult(ElevationOutcome.Failed, "The plugin is shutting down.");
				}

				if (_connection is not null)
				{
					return new ElevationResult(ElevationOutcome.Enabled, AccessNote: _elevatedAccessNote);
				}
			}

			var launched = await _launcher.LaunchAsync(cancellationToken).ConfigureAwait(false);
			if (launched.Outcome == HardwareHostLaunchOutcome.Cancelled)
			{
				return new ElevationResult(ElevationOutcome.Cancelled);
			}

			if (launched.Connection is not { } connection)
			{
				return new ElevationResult(
					ElevationOutcome.Failed,
					launched.Error ?? "The elevated hardware helper could not be started.");
			}

			HardwareHostConnection? discard = null;
			ElevationResult result;

			lock (_sync)
			{
				if (_disposed)
				{
					discard = connection;
					result = new ElevationResult(ElevationOutcome.Failed, "The plugin is shutting down.");
				}
				else if (_connection is not null)
				{
					// Another caller won the race while this one was waiting for UAC.
					discard = connection;
					result = new ElevationResult(ElevationOutcome.Enabled, AccessNote: _elevatedAccessNote);
				}
				else
				{
					result = SwitchTo(connection);
				}
			}

			discard?.Dispose();
			return result;
		}
		finally
		{
			_enabling.Release();
		}
	}

	/// <inheritdoc />
	public void Open()
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			if (_connection is { } connection)
			{
				try
				{
					EnsureRemoteOpen(connection);
					return;
				}
				catch (Exception exception) when (IsTransportFailure(exception))
				{
					DropRemote(connection);
				}
			}

			EnsureLocalOpen();
		}
	}

	/// <inheritdoc />
	public void Close()
	{
		lock (_sync)
		{
			if (_connection is { } connection)
			{
				if (_remoteOpen)
				{
					try
					{
						connection.Source.Close();
					}
					catch (Exception exception) when (IsTransportFailure(exception))
					{
						DropRemote(connection);
					}
					finally
					{
						_remoteOpen = false;
					}
				}

				return;
			}

			CloseLocal();
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			if (_connection is { } connection)
			{
				try
				{
					EnsureRemoteOpen(connection);
					return connection.Source.Read(hardwareFilter);
				}
				catch (Exception exception) when (IsTransportFailure(exception))
				{
					DropRemote(connection);
				}
			}

			EnsureLocalOpen();
			return _local.Read(hardwareFilter);
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<SensorIdentifier> ReadControls()
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			if (_connection is { } connection)
			{
				try
				{
					EnsureRemoteOpen(connection);
					return connection.Source.ReadControls();
				}
				catch (Exception exception) when (IsTransportFailure(exception))
				{
					DropRemote(connection);
				}
			}

			EnsureLocalOpen();
			return _local.ReadControls();
		}
	}

	/// <inheritdoc />
	public bool SetControl(string sensorId, float value)
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			if (_connection is { } connection)
			{
				try
				{
					EnsureRemoteOpen(connection);
					return connection.Source.SetControl(sensorId, value);
				}
				catch (Exception exception) when (IsTransportFailure(exception))
				{
					// A write cannot fall back: the local process cannot reach the control. Drop the
					// helper so reads recover, and let the caller report that the write did not happen.
					DropRemote(connection);
					throw;
				}
			}

			EnsureLocalOpen();
			return _local.SetControl(sensorId, value);
		}
	}

	/// <inheritdoc />
	public bool ResetControl(string sensorId)
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			if (_connection is { } connection)
			{
				try
				{
					EnsureRemoteOpen(connection);
					return connection.Source.ResetControl(sensorId);
				}
				catch (Exception exception) when (IsTransportFailure(exception))
				{
					DropRemote(connection);
					throw;
				}
			}

			EnsureLocalOpen();
			return _local.ResetControl(sensorId);
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		HardwareHostConnection? connection;

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			connection = _connection;
			_connection = null;
			_remoteOpen = false;
			_elevatedAccessNote = null;

			CloseLocal();
			_local.Dispose();
		}

		connection?.Dispose();

		// The semaphore is deliberately not disposed: a UAC prompt can still be open when the plugin
		// shuts down, and disposing it under the caller that is about to release it turns a clean
		// shutdown into an ObjectDisposedException. It owns no unmanaged state worth reclaiming here.
	}

	/// <summary>
	/// Closes the local hardware and opens the helper in its place, all while holding the lock every call
	/// goes through. If the helper cannot open, the local source is reopened so a failed elevation leaves
	/// the plugin exactly where it was.
	/// </summary>
	private ElevationResult SwitchTo(HardwareHostConnection connection)
	{
		var localWasOpen = _localOpen;
		if (_localOpen)
		{
			try
			{
				_local.Close();
			}
			catch (Exception exception)
			{
				_logger.Debug(exception, "The local hardware did not close before the elevated helper opened it.");
			}

			_localOpen = false;
		}

		try
		{
			connection.Source.Open();
		}
		catch (Exception exception)
		{
			// The helper never became the source, so nothing owns it: close it here or a failed
			// elevation would leave an elevated process running with nothing to talk to.
			_logger.Warning(exception, "The elevated hardware helper could not open the hardware.");
			connection.Dispose();
			ReopenLocal(localWasOpen);
			return new ElevationResult(ElevationOutcome.Failed, exception.Message);
		}

		// The note is published before the connection so the lock-free reader never sees a helper with no
		// note. The volatile write to _connection is what makes the note visible to that reader.
		UpdateAccessNote(connection);
		_connection = connection;
		_remoteOpen = true;
		_logger.Information("The elevated hardware helper is serving the hardware.");

		return new ElevationResult(ElevationOutcome.Enabled, AccessNote: _elevatedAccessNote);
	}

	/// <summary>
	/// Opens the local source again after a helper could not, so a failed elevation does not leave the
	/// plugin reading nothing.
	/// </summary>
	private void ReopenLocal(bool wasOpen)
	{
		if (!wasOpen || _localOpen)
		{
			return;
		}

		try
		{
			_local.Open();
			_localOpen = true;
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "The local hardware could not be reopened after a failed elevation.");
		}
	}

	private void EnsureLocalOpen()
	{
		if (!_localOpen)
		{
			_local.Open();
			_localOpen = true;
		}
	}

	private void EnsureRemoteOpen(HardwareHostConnection connection)
	{
		if (!_remoteOpen)
		{
			connection.Source.Open();
			_remoteOpen = true;
			UpdateAccessNote(connection);
		}
	}

	private void CloseLocal()
	{
		if (!_localOpen)
		{
			return;
		}

		try
		{
			_local.Close();
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "The local hardware did not close cleanly.");
		}
		finally
		{
			_localOpen = false;
		}
	}

	/// <summary>Forgets a helper that stopped answering and falls back to the local source.</summary>
	private void DropRemote(HardwareHostConnection connection)
	{
		if (!ReferenceEquals(_connection, connection))
		{
			return;
		}

		_logger.Warning("The elevated hardware helper stopped answering; falling back to the local readings.");
		_connection = null;
		_remoteOpen = false;
		_elevatedAccessNote = null;

		try
		{
			connection.Dispose();
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "The elevated hardware helper did not stop cleanly.");
		}
	}

	private void UpdateAccessNote(HardwareHostConnection connection) =>
		_elevatedAccessNote = (connection.Source as IAccessNoteSource)?.AccessNote;

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	/// <summary>
	/// Whether an exception means the pipe is gone rather than the hardware refusing a request. The
	/// distinction matters: a refused read is reported to the caller and the helper stays, a broken pipe
	/// is what makes the plugin fall back.
	/// </summary>
	private static bool IsTransportFailure(Exception exception) =>
		exception is IOException or ObjectDisposedException or TimeoutException or InvalidDataException;
}
