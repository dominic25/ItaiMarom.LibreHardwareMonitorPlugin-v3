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
/// A failing remote must never delay the local poll behind it. The call is synchronous because the
/// polling seam is synchronous, so a dead address is retried on a backoff instead of on every interval:
/// one attempt, then one second, two, four, up to a minute. A successful read resets that.
/// </para>
/// <para>
/// When the remote source definition includes credentials, an <c>Authorization: Basic</c> header is
/// added to each request. The password is never logged.
/// </para>
/// </remarks>
public sealed class RemoteHardwareSource : IHardwareSensorSource
{
	/// <summary>Waits after successive failures. The last value repeats once the array runs out.</summary>
	private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30, 60];

	private readonly RemoteSourceDefinition _definition;
	private readonly ILogger _logger;
	private readonly HttpClient _http;
	private readonly bool _ownsClient;
	private readonly string? _authHeader;
	private readonly object _sync = new();

	private int _failures;
	private long _retryAt;
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
	public void Open()
	{
		// Nothing to open: the server owns its hardware and is already serving when the plugin starts.
	}

	/// <inheritdoc />
	public void Close()
	{
		// Nothing to close on the other machine.
	}

	/// <inheritdoc />
	public IReadOnlyList<HardwareSensor> Read(IReadOnlyCollection<string>? hardwareFilter)
	{
		// The polling loop hands every source the watched set. A remote whose label none of the watched
		// hardware belongs to is skipped before any network call, so an idle remote costs nothing.
		if (hardwareFilter is { Count: > 0 }
			&& !hardwareFilter.Any(_definition.MatchesHardwareFilter))
		{
			return [];
		}

		lock (_sync)
		{
			if (_disposed)
			{
				return [];
			}

			var now = Environment.TickCount64;
			if (now < _retryAt)
			{
				return [];
			}

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, _definition.Url);

				// Add Basic Auth header if credentials are configured
				if (_authHeader is not null)
				{
					request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _authHeader["Basic ".Length..]);
				}

				// The body is read here rather than off the live stream: the seam is synchronous, and a
				// server that stalls halfway through the document would otherwise hold the whole loop
				// past the timeout that was supposed to bound it.
				using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead);

				if (response.StatusCode is not HttpStatusCode.OK)
				{
					throw new HttpRequestException(
						$"The remote source answered {(int)response.StatusCode} {response.ReasonPhrase}.");
				}

				using var stream = response.Content.ReadAsStream();
				var sensors = RemoteSensorParser.Parse(stream, _definition);

				_failures = 0;
				_retryAt = 0;
				return sensors;
			}
			catch (Exception exception)
			{
				_failures = Math.Min(_failures + 1, BackoffSeconds.Length);
				_retryAt = now + (BackoffSeconds[_failures - 1] * 1000L);

				if (_failures == 1)
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

				return [];
			}
		}
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
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
		}

		if (_ownsClient)
		{
			_http.Dispose();
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
