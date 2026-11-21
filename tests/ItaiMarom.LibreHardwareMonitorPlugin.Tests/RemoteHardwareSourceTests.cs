using System.Net;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The HTTP source behind one remote machine: what it asks for, what it publishes, and what it reports
/// when the other machine is not answering.
/// </summary>
/// <remarks>
/// The source polls in the background now, so a test drives it with <see cref="Fetch"/> rather than
/// waiting for a timer. <c>Read</c> is the cached answer the polling loop sees.
/// </remarks>
[TestFixture]
public sealed class RemoteHardwareSourceTests
{
	private static readonly RemoteSourceDefinition Source =
		new("Living Room", "http://living-room-pc:8085/data.json", "living_room");

	private const string Document =
		"""
		{"Children":[{"Text":"PC","HardwareId":"/","Children":[{"Text":"CPU","HardwareId":"/cpu","Children":[{"Text":"Load","SensorId":"/cpu/load/0","Type":"Load","RawValue":42}]}]}]}
		""";

	[Test]
	public void A_remote_read_returns_the_sensors_the_server_published()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		Assert.That(Fetch(source), Is.True);

		var sensors = source.Read(null);

		Assert.That(sensors.Single().Value, Is.EqualTo(42f));
		Assert.That(sensors.Single().Sensor.Id, Is.EqualTo("/remote/living_room/cpu/load/0"));
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[Test]
	public void The_request_goes_to_the_normalized_data_address()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		Fetch(source);

		Assert.That(handler.LastUri, Is.EqualTo(new Uri("http://living-room-pc:8085/data.json")));
	}

	[Test]
	public void A_remote_whose_sensors_are_not_watched_is_not_returned_but_is_still_health_polled()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		// Its sensors are not bound, so none of them may be published...
		Assert.That(source.Read(["Office PC · CPU"]), Is.Empty);
		Assert.That(Fetch(source), Is.True);
		Assert.That(source.Read(["Office PC · CPU"]), Is.Empty);
		Assert.That(handler.Requests, Is.EqualTo(1));

		// ...but the machine is still probed for its health, which is what makes an offline/online
		// variable and its Retry action work for an unticked remote.
		Assert.That(source.Health.State, Is.EqualTo(RemoteSourceState.Online));

		// A watched read then serves the tree the health probe already fetched.
		Assert.That(source.Read(["Living Room · CPU"]), Has.Count.EqualTo(1));
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[Test]
	public void A_retry_for_an_unticked_remote_actually_fetches()
	{
		var handler = new StubHandler(_ => throw new HttpRequestException("unreachable"));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		source.Read(["Office PC · CPU"]);
		Assert.That(Fetch(source), Is.False);
		Assert.That(handler.Requests, Is.EqualTo(1));

		Assert.That(source.TryRetryRemote("living_room"), Is.True);
		Assert.That(Fetch(source), Is.False);

		// The retry bypassed the watched-sensor filter and issued a second request.
		Assert.That(handler.Requests, Is.EqualTo(2));
	}

	[Test]
	public void A_failing_remote_is_retried_on_a_backoff_rather_than_every_poll()
	{
		var handler = new StubHandler(_ => throw new HttpRequestException("unreachable"));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		Assert.That(Fetch(source), Is.False);
		Assert.That(Fetch(source), Is.False);
		Assert.That(Fetch(source), Is.False);

		// The first failure schedules a retry a second out; the poll that follows within that second must
		// not block on the same dead machine again.
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[Test]
	public void A_server_error_counts_as_a_failed_read()
	{
		var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		Assert.That(Fetch(source), Is.False);
		Assert.That(Fetch(source), Is.False);
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[Test]
	public void A_rescan_fetches_now_even_when_nothing_on_that_machine_is_watched()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		// The loop is only interested in a machine whose sensors are bound; a rescan from the window is
		// interested in everything, because that is how an unbound sensor becomes tickable.
		source.Read(["Office PC · CPU"]);
		source.Rescan();

		Assert.That(handler.Requests, Is.EqualTo(1));
		Assert.That(source.Read(null), Has.Count.EqualTo(1));
	}

	[Test]
	public async Task Opening_the_source_starts_the_background_poll()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);
		source.SetPollingInterval(TimeSpan.FromSeconds(1));

		source.Open();

		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
		while (source.Read(null).Count == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		Assert.That(source.Read(null), Has.Count.EqualTo(1));
		Assert.That(handler.Requests, Is.GreaterThanOrEqualTo(1));
	}

	[Test]
	public void A_disposed_remote_does_not_read_again()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = Create(handler, logger);

		source.Dispose();

		Assert.That(Fetch(source), Is.False);
		Assert.That(source.Read(null), Is.Empty);
		Assert.That(handler.Requests, Is.Zero);
	}

	[Test]
	public void A_remote_machine_has_no_controls_to_drive()
	{
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(new StubHandler(_ => Json(Document)), logger);

		// A fan on another machine is not a fan this plugin can safely drive.
		Assert.That(source.ReadControls(), Is.Empty);
		Assert.That(source.SetControl("/remote/living_room/cpu/load/0", 50), Is.False);
		Assert.That(source.ResetControl("/remote/living_room/cpu/load/0"), Is.False);
	}

	[Test]
	public void Health_moves_from_online_to_offline_and_back()
	{
		var failing = true;
		var handler = new StubHandler(_ => failing
			? throw new HttpRequestException("unreachable")
			: Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		var states = new List<RemoteSourceState>();
		source.RemoteHealthChanged += (_, args) => states.Add(args.Status.Health.State);

		Assert.That(Fetch(source), Is.False);
		Assert.That(source.Health.State, Is.EqualTo(RemoteSourceState.Unknown));

		// Each manual fetch has to clear the backoff the previous failure scheduled, exactly as the
		// background loop does by waiting out the delay.
		Assert.That(source.TryRetryRemote("living_room"), Is.True);
		Assert.That(Fetch(source), Is.False);
		Assert.That(source.Health.State, Is.EqualTo(RemoteSourceState.Unknown));

		// The third failure is what the user is told to act on.
		Assert.That(source.TryRetryRemote("living_room"), Is.True);
		Assert.That(Fetch(source), Is.False);
		Assert.That(source.Health.State, Is.EqualTo(RemoteSourceState.Offline));
		Assert.That(source.Health.LastError, Is.Not.Null);

		// A retry clears the backoff without waiting for it.
		failing = false;
		Assert.That(source.TryRetryRemote("living_room"), Is.True);
		Assert.That(Fetch(source), Is.True);
		Assert.That(source.Health.State, Is.EqualTo(RemoteSourceState.Online));
		Assert.That(source.Health.LastError, Is.Null);

		Assert.That(states, Is.EqualTo(new[]
		{
			RemoteSourceState.Offline,
			RemoteSourceState.Online,
		}));
	}

	[Test]
	public void A_remote_that_has_answered_once_is_degraded_before_it_is_offline()
	{
		var failing = false;
		var handler = new StubHandler(_ => failing
			? throw new HttpRequestException("unreachable")
			: Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		Assert.That(Fetch(source), Is.True);
		failing = true;

		Assert.That(Fetch(source), Is.False);
		Assert.That(source.Health.State, Is.EqualTo(RemoteSourceState.Degraded));
		Assert.That(source.Health.IsOnline, Is.True);

		Assert.That(source.Health.HasBeenOnline, Is.True);
		Assert.That(source.RemoteStatuses.Single().Key, Is.EqualTo("living_room"));
	}

	private static bool Fetch(RemoteHardwareSource source) =>
		source.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

	private static RemoteHardwareSource Create(StubHandler handler, ILogger logger) =>
		new(Source, logger, new HttpClient(handler), ownsClient: true);

	private static HttpResponseMessage Json(string json) =>
		new(HttpStatusCode.OK)
		{
			Content = new StringContent(json),
		};

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

		public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

		public int Requests { get; private set; }

		public Uri? LastUri { get; private set; }

		protected override HttpResponseMessage Send(
			HttpRequestMessage request,
			CancellationToken cancellationToken) =>
			Respond(request);

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken) =>
			Task.FromResult(Respond(request));

		private HttpResponseMessage Respond(HttpRequestMessage request)
		{
			Requests++;
			LastUri = request.RequestUri;
			return _respond(request);
		}
	}
}
