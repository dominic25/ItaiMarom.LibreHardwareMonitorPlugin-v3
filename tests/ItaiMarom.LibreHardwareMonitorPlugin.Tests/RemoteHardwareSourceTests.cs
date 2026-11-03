using System.Net;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using NUnit.Framework;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Tests;

/// <summary>
/// The HTTP source behind one remote machine: what it asks for, what it returns, and what it does when
/// the other machine is not answering.
/// </summary>
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

		source.Read(null);

		Assert.That(handler.LastUri, Is.EqualTo(new Uri("http://living-room-pc:8085/data.json")));
	}

	[Test]
	public void A_watched_hardware_that_belongs_to_another_machine_is_not_requested()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		Assert.That(source.Read(["Office PC · CPU"]), Is.Empty);
		Assert.That(handler.Requests, Is.Zero);

		Assert.That(source.Read(["Living Room · CPU"]), Has.Count.EqualTo(1));
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[Test]
	public void A_failing_remote_is_retried_on_a_backoff_rather_than_every_poll()
	{
		var handler = new StubHandler(_ => throw new HttpRequestException("unreachable"));
		using var logger = new LoggerConfiguration().CreateLogger();
		using var source = Create(handler, logger);

		Assert.That(source.Read(null), Is.Empty);
		Assert.That(source.Read(null), Is.Empty);
		Assert.That(source.Read(null), Is.Empty);

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

		Assert.That(source.Read(null), Is.Empty);
		Assert.That(source.Read(null), Is.Empty);
		Assert.That(handler.Requests, Is.EqualTo(1));
	}

	[Test]
	public void A_disposed_remote_does_not_read_again()
	{
		var handler = new StubHandler(_ => Json(Document));
		using var logger = new LoggerConfiguration().CreateLogger();
		var source = Create(handler, logger);

		source.Dispose();

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
