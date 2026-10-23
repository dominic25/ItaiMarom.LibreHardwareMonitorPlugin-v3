using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Owns the hardware library on a background loop and keeps the newest reading of every watched sensor.
/// </summary>
/// <remarks>
/// <para>
/// The loop is the only place the hardware library is touched. That is the whole point of the split: a
/// poll can block for as long as a disk takes to answer a SMART query, and the Macro Deck session is never
/// waiting on it. Readers call <see cref="TryGetReading"/> or <see cref="Snapshot"/> and get the last
/// value the loop wrote, out of a dictionary, without a lock ever being held across a call into the
/// hardware.
/// </para>
/// <para>
/// <see cref="Start"/> and <see cref="StopAsync"/> are both idempotent and both reversible, because an
/// integration is initialized again after a reconnect and having been initialized before must not stand
/// in the way of that.
/// </para>
/// </remarks>
public sealed class HardwareMonitor : IAsyncDisposable
{
	private readonly IHardwareSensorSource _source;
	private readonly ILogger _logger;

	/// <summary>
	/// How the loop is timed. Swapped rather than mutated, because the loop reads it between polls and
	/// reads one snapshot per iteration.
	/// </summary>
	private HardwareMonitorOptions _options;

	/// <summary>Serializes every call into the hardware library, which is not thread-safe.</summary>
	private readonly SemaphoreSlim _hardwareGate = new(1, 1);

	/// <summary>Guards the watched set and the reading cache. Never held across an await.</summary>
	private readonly object _sync = new();

	private readonly Dictionary<SensorIdentifier, Reading> _readings = [];
	private readonly HashSet<SensorIdentifier> _watched = [];

	private CancellationTokenSource? _cancellation;
	private Task? _loop;
	private bool _isOpen;
	private bool _disposed;

	/// <summary>
	/// What to tell the user about the readings this process cannot reach, or null when there is nothing
	/// to say. Worked out when the hardware is opened, which is the first moment the driver is reached.
	/// </summary>
	private string? _accessNote;

	public HardwareMonitor(IHardwareSensorSource source, HardwareMonitorOptions options, ILogger logger)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_options = (options ?? throw new ArgumentNullException(nameof(options))).Normalized();
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<HardwareMonitor>();
	}

	/// <summary>Whether the polling loop is running.</summary>
	public bool IsRunning
	{
		get
		{
			lock (_sync)
			{
				return _loop is not null;
			}
		}
	}

	/// <summary>
	/// Replaces the set of sensors the loop reads and caches. Anything no longer watched is dropped, so a
	/// variable the user unbound stops answering instead of quietly going stale.
	/// </summary>
	public void SetWatchedSensors(IEnumerable<SensorIdentifier> sensors)
	{
		ArgumentNullException.ThrowIfNull(sensors);

		lock (_sync)
		{
			_watched.Clear();
			_watched.UnionWith(sensors);

			foreach (var key in _readings.Keys.Where(key => !_watched.Contains(key)).ToArray())
			{
				_readings.Remove(key);
			}
		}
	}

	/// <summary>How the loop is currently timed.</summary>
	public HardwareMonitorOptions Options => Volatile.Read(ref _options);

	/// <summary>
	/// What to tell the user about the readings this process cannot reach, or null when there is nothing
	/// to say. It is something to put beside the sensors rather than a failure of its own: everything
	/// that does not go through the hardware library's kernel driver still reads.
	/// </summary>
	public string? AccessNote => Volatile.Read(ref _accessNote);

	/// <summary>
	/// Replaces the loop's timing, for example with the polling rate a migrated configuration asks for.
	/// Takes effect at the loop's next iteration rather than at its next start, and is safe to call
	/// whether or not it is running.
	/// </summary>
	public void SetOptions(HardwareMonitorOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		Volatile.Write(ref _options, options.Normalized());
	}

	/// <summary>Starts the loop. Does nothing when it is already running.</summary>
	public void Start()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		lock (_sync)
		{
			if (_loop is not null)
			{
				return;
			}

			var cancellation = new CancellationTokenSource();
			_cancellation = cancellation;
			_loop = Task.Run(() => PollLoopAsync(cancellation.Token));
		}

		_logger.Information(
			"Hardware monitor started; polling every {PollingIntervalMs} ms.",
			Options.PollingInterval.TotalMilliseconds);
	}

	/// <summary>Stops the loop and releases the hardware. Does nothing when it is not running.</summary>
	public async Task StopAsync()
	{
		CancellationTokenSource? cancellation;
		Task? loop;

		lock (_sync)
		{
			cancellation = _cancellation;
			loop = _loop;
			_cancellation = null;
			_loop = null;
		}

		if (cancellation is null)
		{
			return;
		}

		await cancellation.CancelAsync().ConfigureAwait(false);

		if (loop is not null)
		{
			try
			{
				await loop.ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// The loop ended because it was asked to.
			}
		}

		cancellation.Dispose();

		await _hardwareGate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (_isOpen)
			{
				_source.Close();
				_isOpen = false;
			}
		}
		finally
		{
			_hardwareGate.Release();
		}

		_logger.Information("Hardware monitor stopped.");
	}

	/// <summary>Reads the newest value of one sensor, if the loop has a fresh one.</summary>
	public bool TryGetReading(SensorIdentifier sensor, out double value)
	{
		lock (_sync)
		{
			if (_readings.TryGetValue(sensor, out var reading))
			{
				value = reading.Value;
				return true;
			}
		}

		value = 0;
		return false;
	}

	/// <summary>A copy of every fresh reading, safe to walk without holding the cache.</summary>
	public IReadOnlyDictionary<SensorIdentifier, double> Snapshot()
	{
		lock (_sync)
		{
			return _readings.ToDictionary(pair => pair.Key, pair => pair.Value.Value);
		}
	}

	/// <summary>
	/// Reads every sensor once, off the loop's schedule, and hands back what each of them said. This is
	/// what opens the hardware when the loop is not running.
	/// </summary>
	/// <remarks>
	/// It reads the whole machine rather than the watched set, because its callers are the ones that have
	/// to show a user what is there before anything is watched: the sensor picker's tree, and the readings
	/// that tree puts beside each sensor.
	/// </remarks>
	public async Task<IReadOnlyList<HardwareSensor>> SampleAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		await _hardwareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			EnsureOpen();
			return _source.Read(null);
		}
		finally
		{
			_hardwareGate.Release();
		}
	}

	/// <summary>Reads every sensor once and keeps only what the sensor picker's tree is built from.</summary>
	public async Task<IReadOnlyList<SensorIdentifier>> EnumerateSensorsAsync(
		CancellationToken cancellationToken = default)
	{
		var samples = await SampleAsync(cancellationToken).ConfigureAwait(false);

		return [.. samples.Select(sample => sample.Sensor)];
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;

		await StopAsync().ConfigureAwait(false);

		await _hardwareGate.WaitAsync().ConfigureAwait(false);
		try
		{
			_source.Dispose();
		}
		finally
		{
			_hardwareGate.Release();
		}

		_hardwareGate.Dispose();
	}

	private async Task PollLoopAsync(CancellationToken cancellationToken)
	{
		try
		{
			await _hardwareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			try
			{
				// Opening probes every bus on the machine. Doing it here rather than in Start is what keeps
				// InitializeAsync from blocking the session on a driver that takes a second to answer.
				EnsureOpen();
			}
			finally
			{
				_hardwareGate.Release();
			}

			while (!cancellationToken.IsCancellationRequested)
			{
				// One snapshot per iteration, so the poll and the delay that follows it are timed by the
				// same options even if a new configuration arrives in between.
				var options = Options;

				try
				{
					await PollOnceAsync(options, cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception exception)
				{
					// One bad poll - a device that vanished mid-read, a driver that threw - must not take the
					// loop down and leave every variable frozen at whatever it last said.
					_logger.Warning(exception, "A hardware poll failed; keeping the previous readings.");
				}

				await Task.Delay(options.PollingInterval, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// Stopped by StopAsync.
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "The hardware polling loop stopped unexpectedly.");
		}
	}

	private async Task PollOnceAsync(HardwareMonitorOptions options, CancellationToken cancellationToken)
	{
		SensorIdentifier[] watched;
		lock (_sync)
		{
			if (_watched.Count == 0)
			{
				// Nothing is bound yet. Reading hardware nothing consumes would be pure cost.
				return;
			}

			watched = [.. _watched];
		}

		var filter = watched
			.Select(sensor => sensor.RootHardwareName)
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		IReadOnlyList<HardwareSensor> samples;
		await _hardwareGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			EnsureOpen();
			samples = _source.Read(filter);
		}
		finally
		{
			_hardwareGate.Release();
		}

		lock (_sync)
		{
			var sampledAt = DateTimeOffset.UtcNow;

			foreach (var sample in samples)
			{
				// A sample with no value holds no value: leave what the last poll saw rather than deleting
				// it, so a single null does not make a variable blink out and back within one interval.
				if (sample.Value is { } value && _watched.Contains(sample.Sensor))
				{
					_readings[sample.Sensor] = new Reading(value, sampledAt);
				}
			}

			// Past StaleAfter a reading is a claim about the past, and the value a variable reports is
			// supposed to be about now.
			var cutoff = sampledAt - options.StaleAfter;
			foreach (var key in _readings
				.Where(pair => pair.Value.SampledAt < cutoff)
				.Select(pair => pair.Key)
				.ToArray())
			{
				_readings.Remove(key);
			}
		}
	}

	private void EnsureOpen()
	{
		if (_isOpen)
		{
			return;
		}

		_source.Open();
		_isOpen = true;

		// Asked once per open, because the answer is about this machine and this process. It goes to the
		// log as well as to the window: the readings it names are absent rather than stale, and nothing
		// else in the plugin can tell the difference.
		var note = KernelAccess.Describe();
		Volatile.Write(ref _accessNote, note);

		if (note is not null)
		{
			_logger.Warning("{AccessNote}", note);
		}
	}

	/// <summary>One cached value and the moment the poll that produced it finished.</summary>
	private readonly record struct Reading(double Value, DateTimeOffset SampledAt);
}
