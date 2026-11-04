using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using LibreHardwareMonitor.Hardware;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Configuration;

/// <summary>
/// Everything the classic sensor window needs to open, and everything it hands back: what it shows, the
/// OAuth address it answers to and the state it must answer with.
/// </summary>
/// <param name="Values">
/// The reading of every sensor at the moment the window opened, keyed by resource id and null for a
/// sensor that had none. It is what the page is first drawn with; every refresh after that comes from
/// the plugin's <c>/values</c> route.
/// </param>
/// <param name="AutoElevate">
/// Whether the window opens with its <em>Start elevated readings at startup</em> box ticked, which is
/// the preference the plugin is running with.
/// </param>
/// <param name="RemoteSources">
/// The remote machines the window opens its editor on. They are read from and written to the same form
/// as the sensors, so one save carries the whole configuration.
/// </param>
/// <param name="DisplayUnits">
/// The unit each configured sensor is shown in, keyed by resource id. The window opens its selectors on
/// these so pressing Save without touching one cannot change the unit by accident.
/// </param>
/// <param name="UnitDefaults">
/// The unit each sensor kind uses when a sensor has no choice of its own. The window opens its defaults
/// editor on these, and a selector that is left on <em>Default</em> follows them.
/// </param>
internal sealed record ClassicWindowOpening(
	string Title,
	IReadOnlyList<SensorCategory> Categories,
	IReadOnlyList<string> SelectedResourceIds,
	double PollingMilliseconds,
	double MinimumPollingMilliseconds,
	double MaximumPollingMilliseconds,
	string RedirectUri,
	string State,
	string? Notice = null,
	IReadOnlyDictionary<string, string?>? Values = null,
	bool AutoElevate = false,
	IReadOnlyList<RemoteSourceDefinition>? RemoteSources = null,
	IReadOnlyDictionary<string, SensorUnit>? DisplayUnits = null,
	IReadOnlyDictionary<SensorType, SensorUnit>? UnitDefaults = null);

/// <summary>One remote machine as the window's form carried it back, before it is validated.</summary>
internal sealed record ClassicWindowRemote(string? Name, string? Url, string? Key, string? UserName = null, string? Password = null);

/// <summary>What the classic sensor window sent back when the user pressed Save.</summary>
internal sealed record ClassicWindowPayload(
	IReadOnlyList<string> ResourceIds,
	double? PollingMilliseconds,
	bool AutoElevate,
	IReadOnlyList<ClassicWindowRemote>? Remotes = null,
	/// <summary>
	/// The unit each configured sensor is shown in, keyed by resource id. A value of <c>null</c> means
	/// the user explicitly chose <em>Default</em> for that sensor, so its explicit unit choice (if any)
	/// should be cleared.
	/// </summary>
	IReadOnlyDictionary<string, SensorUnit?>? Units = null,
	IReadOnlyDictionary<SensorType, SensorUnit>? DefaultUnits = null);

/// <summary>
/// A loopback-only web server with one job: serve the Macro Deck 2 dialog rebuilt as a page, and collect
/// what the user did in it.
/// </summary>
/// <remarks>
/// <para>
/// The dialog cannot be a file on disk. The desktop shell only hands <c>http</c>, <c>https</c> and
/// <c>mailto</c> addresses to the system opener - anything else is refused silently - so the page has to
/// be reached over HTTP, and the plugin is the only thing here that can serve it. It binds
/// <c>127.0.0.1</c> on an ephemeral port, which needs no firewall prompt and no URL reservation, and
/// serves nothing but this one page.
/// </para>
/// <para>
/// The page does not carry the selection back in a URL. A callback is a <c>GET</c> whose whole line
/// Kestrel caps at 8 KB, which a machine with a few hundred sensors would sail past, so the selection is
/// <c>POST</c>ed here first and only a short key follows the redirect. The key is 128 random bits, which
/// is also what stops another local process from writing a configuration this plugin would believe.
/// </para>
/// </remarks>
internal sealed class ClassicWindowServer : IAsyncDisposable
{
	/// <summary>Where one window lives, and the key its selection comes back under.</summary>
	internal sealed record Opened(string Key, Uri PageUri);

	/// <summary>How long a window the user never finished is remembered for.</summary>
	private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

	/// <summary>Enough for any header we answer, small enough that a stranger cannot fill our memory.
	/// Increased to 64KB to accommodate long query strings on the /values endpoint when many sensors
	/// have unit selectors (each adds ~100 chars to the query string).</summary>
	private const int MaximumHeadBytes = 64 * 1024;

	private const int MaximumBodyBytes = 1024 * 1024;

	/// <summary>Where the page asks for the readings it is showing, one refresh at a time.</summary>
	private const string ValuesPath = "/values";
	private const string TestRemotePath = "/test-remote";

	private const string HeaderTerminator = "\r\n\r\n";

	private static readonly byte[] EmptyBody = [];

	private readonly ILogger _logger;
	private readonly ISensorReadingSource _readings;
	private readonly object _sync = new();
	private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

	private TcpListener? _listener;
	private CancellationTokenSource? _stopping;
	private Task? _accepting;
	private Uri? _baseUri;
	private bool _disposed;

	public ClassicWindowServer(ILogger logger, ISensorReadingSource readings)
	{
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
			.ForContext<ClassicWindowServer>();
		_readings = readings ?? throw new ArgumentNullException(nameof(readings));
	}

	private sealed class Session
	{
		public required ClassicWindowOpening Opening { get; init; }

		public ClassicWindowPayload? Payload { get; set; }

		public DateTimeOffset OpenedAt { get; } = DateTimeOffset.UtcNow;
	}

	/// <summary>
	/// Opens a window for one configuration session, starting the listener the first time it is asked.
	/// Binds the port here rather than in the constructor so a plugin that is never configured never
	/// listens, and so a port that cannot be opened fails the step it belongs to instead of the process.
	/// </summary>
	public Opened Open(ClassicWindowOpening opening)
	{
		ArgumentNullException.ThrowIfNull(opening);

		var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
		var baseUri = EnsureStarted();

		lock (_sync)
		{
			ThrowIfDisposed();
			Prune();
			_sessions[key] = new Session { Opening = opening };
		}

		return new Opened(key, new Uri(baseUri, $"?s={key}"));
	}

	/// <summary>
	/// Takes the selection a window saved, once. A key that is unknown, or known but never saved, is
	/// refused rather than read as an empty selection, because an empty selection would quietly declare
	/// no variables at all.
	/// </summary>
	public bool TryTakePayload(string key, out ClassicWindowPayload? payload)
	{
		ArgumentNullException.ThrowIfNull(key);

		lock (_sync)
		{
			if (_sessions.TryGetValue(key, out var session) && session.Payload is { } saved)
			{
				_sessions.Remove(key);
				payload = saved;
				return true;
			}
		}

		payload = null;
		return false;
	}

	/// <summary>Stops listening and forgets every window still open.</summary>
	public ValueTask DisposeAsync()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return ValueTask.CompletedTask;
			}

			_disposed = true;
			_sessions.Clear();
			_stopping?.Cancel();
			_listener?.Stop();
		}

		var accepting = _accepting;
		return accepting is null ? ValueTask.CompletedTask : new ValueTask(accepting);
	}

	private Uri EnsureStarted()
	{
		lock (_sync)
		{
			ThrowIfDisposed();

			if (_baseUri is not null)
			{
				return _baseUri;
			}

			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();

			_stopping = new CancellationTokenSource();
			_listener = listener;
			_baseUri = new Uri($"http://{listener.LocalEndpoint}/");

			var token = _stopping.Token;
			_accepting = Task.Run(() => AcceptAsync(listener, token), CancellationToken.None);

			_logger.Information("The classic sensor window is being served from {WindowUri}.", _baseUri);
			return _baseUri;
		}
	}

	private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

	/// <summary>Takes connections until the listener stops, then hands each one to its own task.</summary>
	private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			TcpClient client;
			try
			{
				client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception exception) when (cancellationToken.IsCancellationRequested)
			{
				_logger.Debug(exception, "The classic sensor window stopped listening.");
				return;
			}
			catch (Exception exception)
			{
				_logger.Warning(exception, "The classic sensor window could not take a connection.");
				continue;
			}

			_ = Task.Run(() => ServeAsync(client, cancellationToken), CancellationToken.None);
		}
	}

	/// <summary>Answers one connection and closes it, never letting a bad request reach the caller.</summary>
	private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
	{
		using (client)
		{
			try
			{
				var stream = client.GetStream();
				var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
				var response = await RouteAsync(request, cancellationToken).ConfigureAwait(false);

				await WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// The window was closed while answering; there is nobody left to answer.
			}
			catch (Exception exception)
			{
				_logger.Warning(exception, "The classic sensor window could not answer a request.");
			}
		}
	}

	private sealed record HttpRequest(string Method, string Target, string Body);

	private sealed record HttpResponse(int Status, string Reason, string ContentType, byte[] Body, string? Location = null);

	/// <summary>
	/// Reads a request head and, for a <c>POST</c>, its body. Everything the page sends is
	/// <c>application/x-www-form-urlencoded</c>, which is ASCII, so reading the whole thing as text
	/// cannot split a character.
	/// </summary>
	private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		var buffer = new byte[2048];
		var collected = new StringBuilder();
		var headerEnd = -1;
		var expected = 0;

		while (true)
		{
			var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}

			collected.Append(Encoding.ASCII.GetString(buffer, 0, read));

			if (headerEnd < 0)
			{
				headerEnd = collected.ToString().IndexOf(HeaderTerminator, StringComparison.Ordinal);
				if (headerEnd >= 0)
				{
					expected = headerEnd + HeaderTerminator.Length + ReadContentLength(collected.ToString(0, headerEnd));
				}
			}

			if (headerEnd >= 0 && collected.Length >= expected)
			{
				break;
			}

			if (headerEnd < 0 && collected.Length > MaximumHeadBytes)
			{
				throw new InvalidOperationException("The classic sensor window received a head that was too large.");
			}

			if (collected.Length > MaximumHeadBytes + MaximumBodyBytes)
			{
				throw new InvalidOperationException("The classic sensor window received a request that was too large.");
			}
		}

		var text = collected.ToString();
		var bodyStart = headerEnd < 0 ? text.Length : headerEnd + HeaderTerminator.Length;
		var head = headerEnd < 0 ? text : text[..headerEnd];
		var lines = head.Split("\r\n", StringSplitOptions.None);
		var requestLine = lines.Length > 0 ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

		return new HttpRequest(
			requestLine.Length > 0 ? requestLine[0] : string.Empty,
			requestLine.Length > 1 ? requestLine[1] : string.Empty,
			text[bodyStart..Math.Min(text.Length, bodyStart + Math.Max(0, expected - bodyStart))]);
	}

	private static int ReadContentLength(string headers)
	{
		foreach (var line in headers.Split("\r\n", StringSplitOptions.None))
		{
			var separator = line.IndexOf(':');
			if (separator <= 0
				|| !string.Equals(line[..separator].Trim(), "Content-Length", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			return int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
				? Math.Max(0, Math.Min(length, MaximumBodyBytes))
				: 0;
		}

		return 0;
	}

	private async Task<HttpResponse> RouteAsync(HttpRequest request, CancellationToken cancellationToken)
	{
		var key = Query(request.Target, "s");

		// The readings the page is showing, which is the one route that reads the machine rather than the
		// selection. It answers only a window this plugin is still holding, so nothing else on the machine
		// can read the sensors through this port.
		if (IsReadingsRequest(request))
		{
			_logger.Debug("Values request: {Target}", request.Target);
			return key is not null && TryGetSession(key, out _)
				? await ReadingsAsync(QueryDefaults(request.Target), QueryUnits(request.Target), cancellationToken).ConfigureAwait(false)
				: ClosedWindow();
		}

		if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
			&& (string.Equals(request.Target, "/", StringComparison.Ordinal) || request.Target.StartsWith("/?", StringComparison.Ordinal)))
		{
			return key is not null && TryGetSession(key, out var session)
				? new HttpResponse(200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ClassicWindowPage.Render(session.Opening, key)))
				: ClosedWindow();
		}

		if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
			&& (string.Equals(request.Target, "/", StringComparison.Ordinal) || request.Target.StartsWith("/?", StringComparison.Ordinal)))
		{
			return key is not null && TryGetSession(key, out var session)
				? Save(session, key, ParseForm(request.Body))
				: ClosedWindow();
		}

		if (string.Equals(request.Target, "/favicon.ico", StringComparison.Ordinal))
		{
			return new HttpResponse(204, "No Content", "image/x-icon", EmptyBody);
		}

		if (IsTestRemoteRequest(request))
		{
			_logger.Debug("Test remote request: {Target}", request.Target);
			return key is not null && TryGetSession(key, out _)
				? await TestRemoteAsync(request, cancellationToken).ConfigureAwait(false)
				: ClosedWindow();
		}

		return new HttpResponse(404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found."));
	}

	private static bool IsReadingsRequest(HttpRequest request) =>
		string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
		&& (string.Equals(request.Target, ValuesPath, StringComparison.Ordinal)
			|| request.Target.StartsWith(ValuesPath + "?", StringComparison.Ordinal));

	private static bool IsTestRemoteRequest(HttpRequest request) =>
		string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
		&& (string.Equals(request.Target, TestRemotePath, StringComparison.Ordinal)
			|| request.Target.StartsWith(TestRemotePath + "?", StringComparison.Ordinal));

	/// <summary>
	/// Tests a remote connection by making a request to the provided URL with optional credentials.
	/// Returns a JSON response indicating success or failure, and on success includes the sensor list.
	/// </summary>
	private async Task<HttpResponse> TestRemoteAsync(HttpRequest request, CancellationToken cancellationToken)
	{
		var form = ParseForm(request.Body);
		var url = form.TryGetValue("url", out var urls) && urls.Count > 0 ? urls[0] : null;
		var userName = form.TryGetValue("username", out var users) && users.Count > 0 ? users[0] : null;
		var password = form.TryGetValue("password", out var passes) && passes.Count > 0 ? passes[0] : null;
		var key = form.TryGetValue("key", out var keys) && keys.Count > 0 ? keys[0] : null;

		if (string.IsNullOrWhiteSpace(url))
		{
			return JsonResponse(new { ok = false, message = "No URL provided" });
		}

		try
		{
			using var httpClient = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(10)
			};

			// Normalize URL like RemoteSourceAddress does
			if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
				!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
			{
				url = "http://" + url;
			}
			if (!url.EndsWith("/data.json", StringComparison.OrdinalIgnoreCase))
			{
				url = url.TrimEnd('/') + "/data.json";
			}

			using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);

			if (!string.IsNullOrEmpty(userName))
			{
				var credentials = Convert.ToBase64String(
					System.Text.Encoding.ASCII.GetBytes($"{userName}:{password ?? string.Empty}"));
				requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
			}

			using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

			if (response.IsSuccessStatusCode)
			{
				var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

				// Parse sensors from the response using the provided key (or a fallback)
				// Never allow "test" as a key - it's reserved for internal test dummy sources
				var effectiveKey = key;
				if (string.IsNullOrWhiteSpace(effectiveKey) || string.Equals(effectiveKey, "test", StringComparison.OrdinalIgnoreCase) || effectiveKey.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
				{
					effectiveKey = "remote";
				}
				var sensors = ParseRemoteSensors(content, effectiveKey);

				var sensorInfos = sensors.Select(s => new
				{
					resourceId = s.Sensor.ResourceId,
					name = s.Sensor.SensorName,
					type = s.Sensor.SensorType.ToString(),
					hardware = s.Sensor.Hardware.RootName,
					hardwarePath = s.Sensor.Hardware.Path,
					value = s.Value?.ToString("F1", CultureInfo.InvariantCulture),
					controllable = s.Sensor.Controllable
				}).ToArray();

				return JsonResponse(new
				{
					ok = true,
					message = "Connected successfully",
					sensors = sensorInfos,
					sensorCount = sensorInfos.Length
				});
			}
			else
			{
				return JsonResponse(new { ok = false, message = $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}" });
			}
		}
		catch (TaskCanceledException)
		{
			return JsonResponse(new { ok = false, message = "Connection timed out" });
		}
		catch (HttpRequestException ex)
		{
			return JsonResponse(new { ok = false, message = "Connection failed: " + ex.Message });
		}
		catch (Exception ex)
		{
			_logger.Warning(ex, "Test remote connection failed unexpectedly");
			return JsonResponse(new { ok = false, message = "Unexpected error: " + ex.Message });
		}
	}

	/// <summary>
	/// Parses sensors from a remote /data.json response using the same logic as RemoteHardwareSource.
	/// </summary>
	private static IReadOnlyList<HardwareSensor> ParseRemoteSensors(string json, string key)
	{
		try
		{
			// Use the provided key for the remote source so sensor resource IDs are namespaced correctly
			var dummySource = new RemoteSourceDefinition("Test", "http://test", key);
			return RemoteSensorParser.Parse(json, dummySource);
		}
		catch
		{
			return Array.Empty<HardwareSensor>();
		}
	}

	private static HttpResponse JsonResponse(object payload)
	{
		var json = System.Text.Json.JsonSerializer.Serialize(payload);
		return new HttpResponse(200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
	}

	/// <summary>
	/// One refresh of the window's value column: every sensor's reading as text, keyed by resource id, and
	/// null for a sensor that has none.
	/// </summary>
	/// <remarks>
	/// A read that fails answers with an empty set rather than an error. The page is polling, so the next
	/// refresh a second later is a better answer than a page that has to understand failures - and the
	/// hardware is allowed to be unreadable for a moment: a device being replaced is exactly the sort of
	/// thing a user is in this window to notice.
	/// </remarks>
	private async Task<HttpResponse> ReadingsAsync(
		IReadOnlyDictionary<SensorType, SensorUnit>? unitDefaults,
		IReadOnlyDictionary<string, SensorUnit?>? unitSelectors,
		CancellationToken cancellationToken)
	{
		IReadOnlyDictionary<string, string?> readings;
		try
		{
			readings = await _readings.ReadingsAsync(unitDefaults, unitSelectors, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			_logger.Warning(exception, "The classic sensor window could not read the sensors.");
			readings = new Dictionary<string, string?>(StringComparer.Ordinal);
		}

		var payload = readings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

		return new HttpResponse(
			200,
			"OK",
			"application/json; charset=utf-8",
			Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
	}

	/// <summary>What a window that is no longer open, or never was, is answered with.</summary>
	private static HttpResponse ClosedWindow() =>
		new(
			404,
			"Not Found",
			"text/plain; charset=utf-8",
			Encoding.UTF8.GetBytes("This window has already been closed."));

	/// <summary>
	/// Records what the user chose and sends the browser on to the host's OAuth callback, which is the
	/// one door back into the flow: the host tells the dialog the login finished, and the dialog resumes
	/// with the key below.
	/// </summary>
	private HttpResponse Save(Session session, string key, Dictionary<string, List<string>> form)
	{
		if (form.ContainsKey("cancel"))
		{
			return Redirect(session, BuildRedirect(session.Opening, "error", "User cancelled"));
		}

		session.Payload = new ClassicWindowPayload(
			form.TryGetValue("sensors", out var sensors)
				? sensors.Where(resourceId => !string.IsNullOrWhiteSpace(resourceId)).Distinct(StringComparer.Ordinal).ToArray()
				: [],
			ReadPolling(form),

			// A checkbox is submitted only while it is ticked, so its presence is the whole answer.
			form.ContainsKey("auto_elevate"),
			ReadRemotes(form),
			ReadUnits(form),
			ReadDefaultUnits(form));

		_logger.Information(
			"The classic sensor window saved {SensorCount} sensors.",
			session.Payload.ResourceIds.Count);

		return Redirect(session, BuildRedirect(session.Opening, "code", key));
	}

	private static string BuildRedirect(ClassicWindowOpening opening, string parameter, string value)
	{
		var separator = opening.RedirectUri.Contains('?') ? '&' : '?';
		return new StringBuilder(opening.RedirectUri)
			.Append(separator)
			.Append("state=")
			.Append(Uri.EscapeDataString(opening.State))
			.Append('&')
			.Append(parameter)
			.Append('=')
			.Append(Uri.EscapeDataString(value))
			.ToString();
	}

	private static HttpResponse Redirect(Session session, string location) =>
		new(
			302,
			"Found",
			"text/html; charset=utf-8",
			Encoding.UTF8.GetBytes(
				"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Returning to Macro Deck</title></head>"
				+ "<body><p>Returning to Macro Deck&hellip; You can close this window.</p></body></html>"),
			location);

	private static double? ReadPolling(Dictionary<string, List<string>> form)
	{
		if (!form.TryGetValue("polling", out var values)
			|| values.Count == 0
			|| !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
			|| !double.IsFinite(milliseconds))
		{
			return null;
		}

		return milliseconds;
	}

	/// <summary>
	/// The remote rows, three parallel lists in the form joined back into rows. A row with no address is
	/// the empty line the editor starts a new remote in, so it is dropped rather than treated as a
	/// configuration error the user has to clear.
	/// </summary>
	private static List<ClassicWindowRemote> ReadRemotes(Dictionary<string, List<string>> form)
	{
		var names = ValuesOf(form, "remote_name");
		var urls = ValuesOf(form, "remote_url");
		var usernames = ValuesOf(form, "remote_username");
		var passwords = ValuesOf(form, "remote_password");
		var keys = ValuesOf(form, "remote_key");

		var count = Math.Max(names.Count, Math.Max(urls.Count, Math.Max(usernames.Count, Math.Max(passwords.Count, keys.Count))));
		var remotes = new List<ClassicWindowRemote>(count);

		for (var index = 0; index < count; index++)
		{
			var url = At(urls, index);
			if (string.IsNullOrWhiteSpace(url))
			{
				continue;
			}

			remotes.Add(new ClassicWindowRemote(
				At(names, index),
				url,
				At(keys, index),
				At(usernames, index),
				At(passwords, index)));
		}

		return remotes;
	}

	/// <summary>
	/// The unit selectors, two parallel lists joined back into a sensor-to-unit map. The resource id
	/// travels in a hidden field beside its selector, so a unit can never be applied to the next row when
	/// one sensor has no choice to offer.
	/// </summary>
	/// <remarks>
	/// A value that is not a unit this version knows is ignored rather than stored: the sensor keeps the
	/// library's own unit, which is the safe answer and costs only the user's choice until they pick again.
	/// An <c>empty</c> value means the user chose <em>Default</em> for that sensor; the sensor is included
	/// with a <c>null</c> value so its explicit unit choice (if any) is cleared on save.
	/// </remarks>
	private static Dictionary<string, SensorUnit?> ReadUnits(Dictionary<string, List<string>> form)
	{
		var sensors = ValuesOf(form, "unit_sensor");
		var values = ValuesOf(form, "unit_value");
		var count = Math.Min(sensors.Count, values.Count);
		var units = new Dictionary<string, SensorUnit?>(count, StringComparer.Ordinal);

		for (var index = 0; index < count; index++)
		{
			var resourceId = sensors[index];
			if (string.IsNullOrWhiteSpace(resourceId))
			{
				continue;
			}

			var value = values[index];
			if (string.IsNullOrWhiteSpace(value))
			{
				// User explicitly chose Default - clear any explicit unit this sensor had.
				units[resourceId] = null;
			}
			else if (SensorUnits.TryParse(value, out var unit))
			{
				units[resourceId] = unit;
			}
		}

		return units;
	}

	/// <summary>
	/// The defaults editor, two parallel lists joined back into a kind-to-unit map. A kind this version
	/// does not offer, or a unit that does not suit its kind, is dropped; the page posts every kind, so a
	/// missing pair is a form that predates defaults rather than a kind the user cleared.
	/// </summary>
	private static Dictionary<SensorType, SensorUnit> ReadDefaultUnits(Dictionary<string, List<string>> form)
	{
		var kinds = ValuesOf(form, "default_unit_kind");
		var values = ValuesOf(form, "default_unit_value");
		var count = Math.Min(kinds.Count, values.Count);
		var defaults = new Dictionary<SensorType, SensorUnit>(count);

		for (var index = 0; index < count; index++)
		{
			if (Enum.TryParse<SensorType>(kinds[index], ignoreCase: true, out var sensorType)
				&& SensorUnits.IsSupported(sensorType)
				&& SensorUnits.TryParse(values[index], out var unit)
				&& SensorUnits.IsValidFor(sensorType, unit))
			{
				defaults[sensorType] = unit;
			}
		}

		return defaults;
	}

	/// <summary>
	/// The defaults the page is currently showing, carried on the readings request. It uses the same two
	/// parallel lists the form posts, so both paths read and validate them the same way. A request with no
	/// query string keeps the selection's defaults (older page or hand-written request). A request with an
	/// empty map means the user explicitly set every kind to <c>Default</c> and wants the library's own
	/// units, so we return an empty map rather than null.
	/// </summary>
	private static Dictionary<SensorType, SensorUnit>? QueryDefaults(string target)
	{
		var question = target.IndexOf('?');
		if (question < 0)
		{
			return null;
		}

		var defaults = ReadDefaultUnits(ParseForm(target[(question + 1)..]));
		return defaults;
	}

	/// <summary>
	/// The per-sensor unit selectors the page is currently showing, carried on the readings request. Uses
	/// the same parallel lists the form posts. A value of <c>null</c> means the user chose <em>Default</em>
	/// for that sensor. Sensors not in the map keep their saved selection (or default).
	/// </summary>
	private static Dictionary<string, SensorUnit?>? QueryUnits(string target)
	{
		var question = target.IndexOf('?');
		if (question < 0)
		{
			return null;
		}

		var sensors = ValuesOf(ParseForm(target[(question + 1)..]), "unit_sensor");
		var values = ValuesOf(ParseForm(target[(question + 1)..]), "unit_value");
		var count = Math.Min(sensors.Count, values.Count);
		if (count == 0)
		{
			return null;
		}

		var units = new Dictionary<string, SensorUnit?>(count, StringComparer.Ordinal);
		for (var index = 0; index < count; index++)
		{
			var resourceId = sensors[index];
			if (string.IsNullOrWhiteSpace(resourceId))
			{
				continue;
			}

			var value = values[index];
			if (string.IsNullOrWhiteSpace(value))
			{
				units[resourceId] = null;
			}
			else if (SensorUnits.TryParse(value, out var unit))
			{
				units[resourceId] = unit;
			}
		}

		return units.Count > 0 ? units : null;
	}

	private static List<string> ValuesOf(Dictionary<string, List<string>> form, string name) =>
		form.TryGetValue(name, out var values) ? values : [];

	private static string? At(List<string> values, int index) =>
		index < values.Count ? values[index] : null;

	private bool TryGetSession(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Session? session)
	{
		lock (_sync)
		{
			if (!_disposed && _sessions.TryGetValue(key, out var found) && DateTimeOffset.UtcNow - found.OpenedAt <= SessionLifetime)
			{
				session = found;
				return true;
			}
		}

		session = null;
		return false;
	}

	/// <summary>Drops windows nobody came back to, so a session abandoned in the dialog leaves nothing.</summary>
	private void Prune()
	{
		var cutoff = DateTimeOffset.UtcNow - SessionLifetime;
		foreach (var key in _sessions.Where(pair => pair.Value.OpenedAt < cutoff).Select(pair => pair.Key).ToArray())
		{
			_sessions.Remove(key);
		}
	}

	private static string? Query(string target, string name)
	{
		var question = target.IndexOf('?');
		if (question < 0)
		{
			return null;
		}

		foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = pair.IndexOf('=');
			var key = WebUtility.UrlDecode(separator < 0 ? pair : pair[..separator]);
			if (string.Equals(key, name, StringComparison.Ordinal))
			{
				return separator < 0 ? string.Empty : WebUtility.UrlDecode(pair[(separator + 1)..]);
			}
		}

		return null;
	}

	private static Dictionary<string, List<string>> ParseForm(string body)
	{
		var form = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			var separator = pair.IndexOf('=');
			var name = WebUtility.UrlDecode(separator < 0 ? pair : pair[..separator]);
			var value = WebUtility.UrlDecode(separator < 0 ? string.Empty : pair[(separator + 1)..]);

			if (!form.TryGetValue(name, out var values))
			{
				form[name] = values = [];
			}

			values.Add(value);
		}

		return form;
	}

	private static Task WriteAsync(NetworkStream stream, HttpResponse response, CancellationToken cancellationToken)
	{
		var head = new StringBuilder()
			.Append("HTTP/1.1 ")
			.Append(response.Status.ToString(CultureInfo.InvariantCulture))
			.Append(' ')
			.Append(response.Reason)
			.Append("\r\nContent-Type: ")
			.Append(response.ContentType)
			.Append("\r\nContent-Length: ")
			.Append(response.Body.Length.ToString(CultureInfo.InvariantCulture))
			.Append("\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");

		if (response.Location is not null)
		{
			head.Append("Location: ").Append(response.Location).Append("\r\n");
		}

		head.Append("\r\n");

		var header = Encoding.ASCII.GetBytes(head.ToString());
		var packet = new byte[header.Length + response.Body.Length];
		header.CopyTo(packet, 0);
		response.Body.CopyTo(packet, header.Length);

		return stream.WriteAsync(packet, cancellationToken).AsTask();
	}
}

