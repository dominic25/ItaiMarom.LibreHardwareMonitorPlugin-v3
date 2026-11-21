using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// An <see cref="IHardwareSensorSource" /> backed by another machine's LibreHardwareMonitor Remote Web
/// Server.
/// </summary>
/// <remarks>
/// <para>
/// Remote hardware is read-only: the Remote Web Server publishes readings, not controls, so
/// <see cref="ReadControls" /> is empty and a write is reported as a sensor that is no longer there.
/// That is deliberate rather than a missing feature - a fan on another machine is not a fan this plugin
/// can safely drive.
/// </para>
/// <para>
/// The HTTP call never happens on the polling thread. Once <see cref="Open"/> is called, a background
/// loop owns the remote address and publishes the newest tree; <see cref="Read"/> only returns what that
/// loop last fetched. That is what stops a slow or sleeping machine from holding the local poll behind a
/// five-second timeout.
/// </para>
/// <para>
/// Failures back off - one attempt, then one second, two, four, up to a minute - and move the machine
/// through <see cref="RemoteSourceState.Degraded"/> to <see cref="RemoteSourceState.Offline"/>. A
/// successful read resets both. The state is exposed through <see cref="IRemoteHealthSource"/> so the
/// issue list, the window and the variables can all say "unreachable" instead of showing a dash with no
/// explanation.
/// </para>
/// <para>
/// When the remote source definition includes credentials, an <c>Authorization: Basic</c> header is
/// added to each request. The password is never logged.
/// </para>
/// </remarks>
public sealed class RemoteHardwareSource : IHardwareSensorSource, IRemoteHealthSource
{
	/// <summary>Waits after successive failures. The last value repeats once the array runs out.</summary>
	private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30, 60];

	/// <summary>Failures in a row before the machine is called unreachable rather than flaky.</summary>
	private const int OfflineAfterFailures = 3;

	/// <summary>
	/// A remote is not polled at the local loop's finest rate. A ten-millisecond interval is meant for
	/// on-board sensors, not for an HTTP request to another PC.
	/// </summary>
	private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromSeconds(1);

	/// <summary>
	/// A published tree older than this is no longer offered to the loop as a current reading. It is well
	/// above the HTTP timeout so one slow but successful fetch does not make a sensor blink out and back.
	/// </summary>
	private static readonly TimeSpan MinimumSnapshotLifetime = TimeSpan.FromSeconds(10);

	/// <summary>
	/// How often a remote whose sensors are not watched is still fetched for its health. Every configured
	/// remote publishes online/status variables, so it has to be probed even when no reading is bound; a
	/// slow probe keeps the old "an idle remote costs almost nothing" property.
	/// </summary>
	private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(30);

	private readonly RemoteSourceDefinition _definition;
	private readonly ILogger _logger;
	private readonly HttpClient _http;
	private readonly bool _ownsClient;
	private readonly string? _authHeader;
	private readonly object _sync = new();

	/// <summary>Serialises a manual refresh against the background loop's refresh.</summary>
	private readonly SemaphoreSlim _refreshing = new(1, 1);

	/// <summary>Wakes the loop early for a retry or a rescan. One outstanding wake is enough.</summary>
	private readonly SemaphoreSlim _wake = new(0, 1);

	private IReadOnlyCollection<string>? _hardwareFilter;
	private bool _hasFilter;
	private IReadOnlyList<HardwareSensor> _snapshot = [];
	private DateTimeOffset _snapshotAt;
	private TimeSpan _pollingInterval = TimeSpan.FromSeconds(1);
	private CancellationTokenSource? _loopCancellation;
	private Task? _loop;
	private bool _open;
	private RemoteSourceHealth _health = RemoteSourceHealth.Unknown;
	private int _failures;
	private long _retryAt;
	private long _lastAttempt;
	private bool _forceRefresh;
	private bool _disposed;

	public RemoteHardwareSource(RemoteSourceDefinition definition, ILogger logger)
		: this(definition, logger, CreateClient(), ownsClient: true)
	{
	}

	internal RemoteHardwareSource(
		RemoteSourceDefinition definition,
		ILogger logger,
		HttpClient client,
		bool ownsClient = false)
	{
		_definition = definition ?? throw new ArgumentNullException(nameof(definition));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
			.ForContext("RemoteSource", definition.Name);
		_http = client ?? throw new ArgumentNullException(nameof(client));
		_ownsClient = ownsClient;

		// Pre-compute the Basic Auth header if credentials are provided
		if (!string.IsNullOrEmpty(definition.UserName))
		{
			var credentials = Convert.ToBase64String(
				Encoding.ASCII.GetBytes($"{definition.UserName}:{definition.Password ?? string.Empty}"));
			_authHeader = $"Basic {credentials}";
		}
	}

	/// <inheritdoc />
	public event EventHandler<RemoteHealthChangedEventArgs>? RemoteHealthChanged;

	/// <summary>This machine's reachability right now.</summary>
	public RemoteSourceHealth Health
	{
		get
		{
			lock (_sync)
			{
				return _health;
			}
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<RemoteSourceStatus> RemoteStatuses => [Status()];

	/// <inheritdoc />
	public void Open()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_open = true;

			// The loop starts once and idles while the source is closed. Starting a second one on a
			// Close/Open cycle would leave two timers racing over the same HTTP client.
			if (_loop is null)
			{
				var cancellation = new CancellationTokenSource();
				_loopCancellation = cancellation;
				_loop = Task.Run(() => PollLoopAsync(cancellation), CancellationToken.None);
			}
		}

		Wake();
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
			_hardwareFilter = null;
			_hasFilter = false;
			_snapshot = [];
			_snapshotAt = default;
		}

		// Wake the loop so it notices it is closed instead of finishing an interval first.
		Wake();
	}

	/// <inheritdoc />
	public void SetPollingInterval(TimeSpan interval)
	{
		var normalized = interval < MinimumPollingInterval ? MinimumPollingInterval : interval;
		var running = false;

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_pollingInterval = normalized;
			running = _loop is not null && _open;
		}

		// A shorter interval should not wait out the old one before it takes effect. Without a loop
		// there is nothing to wake, and a permit would only buy an extra fetch when it starts.
		if (running)
		{
			Wake();
		}
	}

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
	{
		lock (_sync)
		{
			_hardwareFilter = hardwareFilter;
			_hasFilter = true;

			if (_disposed || !Wanted(hardwareFilter) || _snapshot.Count == 0)
			{
				return [];
			}

			return DateTimeOffset.UtcNow - _snapshotAt > SnapshotLifetime
				? []
				: _snapshot;
		}
	}

	/// <inheritdoc />
	/// <remarks>
	/// A rescan is user-initiated and expects the newest tree when it returns, so it fetches once here
	/// rather than only waking the loop. It also ignores the watched-hardware filter: the sensor window
	/// rescanning wants to see everything the machine has, including a sensor nobody has ticked yet.
	/// </remarks>
	public void Rescan()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_hardwareFilter = null;
			_retryAt = 0;
			_forceRefresh = true;
		}

		RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
	}

	/// <inheritdoc />
	public bool TryRetryRemote(string key)
	{
		if (!string.Equals(key, _definition.Key, StringComparison.Ordinal))
		{
			return false;
		}

		lock (_sync)
		{
			_retryAt = 0;
			_forceRefresh = true;
		}

		Wake();
		return true;
	}

	/// <inheritdoc />
	public IReadOnlyList<SensorIdentifier> ReadControls() => [];

	/// <inheritdoc />
	public bool SetControl(string sensorId, float value) => false;

	/// <inheritdoc />
	public bool ResetControl(string sensorId) => false;

	/// <inheritdoc />
	public void Dispose()
	{
		CancellationTokenSource? cancellation;

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_open = false;
			_snapshot = [];
			cancellation = _loopCancellation;
			_loopCancellation = null;
			_loop = null;
		}

		cancellation?.Cancel();

		if (_ownsClient)
		{
			_http.Dispose();
		}

		// The two semaphores are deliberately not disposed: the background loop may still be finishing a
		// request, and disposing a semaphore under the task that is about to release it turns a clean
		// shutdown into an ObjectDisposedException. They own no unmanaged state worth reclaiming.
	}

	/// <summary>
	/// Fetches the remote tree once and publishes it. This is what the background loop calls; it is also
	/// what a test calls to move the source without waiting for a timer.
	/// </summary>
	internal async Task<bool> RefreshAsync(CancellationToken cancellationToken)
	{
		lock (_sync)
		{
			if (_disposed || Environment.TickCount64 < _retryAt)
			{
				return false;
			}
		}

		await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			lock (_sync)
			{
				if (_disposed || Environment.TickCount64 < _retryAt)
				{
					return false;
				}

				// A fetch is happening now: the health clock starts here, and a forced retry has been
				// satisfied. It is set before the request so a request that gets cancelled still counts
				// as an attempt and does not make the loop spin.
				_lastAttempt = Environment.TickCount64;
				_forceRefresh = false;
			}

			using var request = new HttpRequestMessage(HttpMethod.Get, _definition.Url);

			// Add Basic Auth header if credentials are configured
			if (_authHeader is not null)
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeader["Basic ".Length..]);
			}

			// The body is read here rather than off the live stream: the response has to be complete
			// before a partial tree can be mistaken for a smaller machine.
			using var response = await _http
				.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
				.ConfigureAwait(false);

			if (response.StatusCode is not HttpStatusCode.OK)
			{
				throw new HttpRequestException(
					$"The remote source answered {(int)response.StatusCode} {response.ReasonPhrase}.");
			}

			await using var stream = await response.Content
				.ReadAsStreamAsync(cancellationToken)
				.ConfigureAwait(false);
			var sensors = RemoteSensorParser.Parse(stream, _definition);

			if (cancellationToken.IsCancellationRequested)
			{
				return false;
			}

			RemoteSourceHealth health;
			RemoteSourceState previous;
			lock (_sync)
			{
				if (_disposed)
				{
					return false;
				}

				previous = _health.State;
				_snapshot = sensors;
				_snapshotAt = DateTimeOffset.UtcNow;
				_failures = 0;
				_retryAt = 0;
				_health = new RemoteSourceHealth(
					RemoteSourceState.Online,
					_snapshotAt,
					null,
					0);
				health = _health;
			}

			Publish(health, previous);
			return true;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			RecordFailure(exception);
			return false;
		}
		finally
		{
			_refreshing.Release();
		}
	}

	private async Task PollLoopAsync(CancellationTokenSource cancellation)
	{
		var token = cancellation.Token;

		try
		{
			while (!token.IsCancellationRequested)
			{
				bool open;
				lock (_sync)
				{
					if (_disposed)
					{
						break;
					}

					open = _open;
				}

				if (!open)
				{
					// Closed: park until Open wakes us. The loop stays alive so a Close/Open cycle does
					// not race a second loop over the same client.
					try
					{
						await _wake.WaitAsync(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						break;
					}

					continue;
				}

				bool wanted;
				bool fetch;
				lock (_sync)
				{
					// A watched remote is fetched at the configured rate. An unwatched one is still
					// fetched for its health, but only every HealthPollInterval; that is what lets an
					// unticked remote become Offline and be retried. No consumer has spoken yet before
					// the first Read, so that counts as unwatched rather than "wants everything".
					wanted = _hasFilter && Wanted(_hardwareFilter);
					var now = Environment.TickCount64;
					fetch = _forceRefresh
						|| wanted
						|| _failures > 0
						|| now - _lastAttempt >= (long)HealthPollInterval.TotalMilliseconds;
				}

				if (fetch)
				{
					try
					{
						await RefreshAsync(token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						break;
					}
					catch (Exception exception)
					{
						// RefreshAsync handles a failed read itself; this is the last-resort case where it
						// could not. The loop must survive one bad attempt.
						_logger.Debug(exception, "The remote source loop caught an unexpected failure.");
					}
				}

				TimeSpan delay;
				lock (_sync)
				{
					// Wait out whatever the last failure scheduled; otherwise a watched remote follows the
					// polling rate and an unwatched one waits only for its next health probe. A timer that
					// fires a moment early lands on a tiny remaining wait rather than a whole extra step.
					var now = Environment.TickCount64;
					if (_retryAt > now)
					{
						delay = TimeSpan.FromMilliseconds(_retryAt - now);
					}
					else if (_hasFilter && Wanted(_hardwareFilter))
					{
						delay = _pollingInterval;
					}
					else
					{
						var remaining = (long)HealthPollInterval.TotalMilliseconds - (now - _lastAttempt);
						delay = TimeSpan.FromMilliseconds(Math.Max(250, remaining));
					}
				}

				try
				{
					await _wake.WaitAsync(delay, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
		}
		finally
		{
			cancellation.Dispose();
		}
	}

	private void RecordFailure(Exception exception)
	{
		RemoteSourceHealth health;
		RemoteSourceState previous;
		int failures;

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			previous = _health.State;
			failures = ++_failures;

			// The first failure keeps the machine online if it has ever answered: one slow poll is not an
			// outage. Three in a row is what the user is told to act on.
			var state = failures >= OfflineAfterFailures
				? RemoteSourceState.Offline
				: _health.HasBeenOnline
					? RemoteSourceState.Degraded
					: RemoteSourceState.Unknown;

			_retryAt = Environment.TickCount64
				+ (BackoffSeconds[Math.Min(failures - 1, BackoffSeconds.Length - 1)] * 1000L);

			_health = new RemoteSourceHealth(
				state,
				_health.LastSuccessAt,
				exception.Message,
				failures);
			health = _health;
		}

		if (failures == 1)
		{
			_logger.Warning(
				exception,
				"The remote source {RemoteSource} could not be read; retrying with a backoff.",
				_definition.Name);
		}
		else
		{
			_logger.Debug(
				exception,
				"The remote source {RemoteSource} is still unavailable.",
				_definition.Name);
		}

		Publish(health, previous);
	}

	private void Wake()
	{
		try
		{
			_wake.Release();
		}
		catch (SemaphoreFullException)
		{
			// A wake is already pending; one is enough.
		}
	}

	private void Publish(RemoteSourceHealth health, RemoteSourceState previous)
	{
		// The event is only about a state transition, not every retry.
		if (health.State == previous)
		{
			return;
		}

		try
		{
			RemoteHealthChanged?.Invoke(this, new RemoteHealthChangedEventArgs(Status()));
		}
		catch (Exception exception)
		{
			// A subscriber that throws must not turn a successful read into a failure or take the
			// background loop down.
			_logger.Debug(exception, "A remote health subscriber failed.");
		}
	}

	private RemoteSourceStatus Status()
	{
		lock (_sync)
		{
			return new RemoteSourceStatus(_definition.Key, _definition.Name, _definition.Url, _health);
		}
	}

	private bool Wanted(IReadOnlyCollection<string>? hardwareFilter) =>
		hardwareFilter is not { Count: > 0 }
		|| hardwareFilter.Any(_definition.MatchesHardwareFilter);

	private TimeSpan SnapshotLifetime
	{
		get
		{
			var lifetime = _pollingInterval * 2;
			return lifetime < MinimumSnapshotLifetime ? MinimumSnapshotLifetime : lifetime;
		}
	}

	private static HttpClient CreateClient()
	{
		var handler = new SocketsHttpHandler
		{
			// LibreHardwareMonitor compresses its response only when asked, and the plugin asks whether
			// the user knows it or not: a machine full of sensors is a large document.
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
			ConnectTimeout = TimeSpan.FromSeconds(5),
			PooledConnectionLifetime = TimeSpan.FromMinutes(2),
		};

		return new HttpClient(handler)
		{
			Timeout = TimeSpan.FromSeconds(5),
		};
	}
}
