using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
	bool AutoElevate = false);

/// <summary>What the classic sensor window sent back when the user pressed Save.</summary>
internal sealed record ClassicWindowPayload(
	IReadOnlyList<string> ResourceIds,
	double? PollingMilliseconds,
	bool AutoElevate);

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

	/// <summary>Enough for any header we answer, small enough that a stranger cannot fill our memory.</summary>
	private const int MaximumHeadBytes = 16 * 1024;

	private const int MaximumBodyBytes = 1024 * 1024;

	/// <summary>Where the page asks for the readings it is showing, one refresh at a time.</summary>
	private const string ValuesPath = "/values";

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
			return key is not null && TryGetSession(key, out _)
				? await ReadingsAsync(cancellationToken).ConfigureAwait(false)
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

		return new HttpResponse(404, "Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found."));
	}

	private static bool IsReadingsRequest(HttpRequest request) =>
		string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
		&& (string.Equals(request.Target, ValuesPath, StringComparison.Ordinal)
			|| request.Target.StartsWith(ValuesPath + "?", StringComparison.Ordinal));

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
	private async Task<HttpResponse> ReadingsAsync(CancellationToken cancellationToken)
	{
		IReadOnlyDictionary<string, string?> readings;
		try
		{
			readings = await _readings.ReadingsAsync(cancellationToken).ConfigureAwait(false);
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
			form.ContainsKey("auto_elevate"));

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
