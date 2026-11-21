using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Events;
using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Events;

/// <summary>
/// Turns the events the user configured into publications, once per poll.
/// </summary>
/// <remarks>
/// <para>
/// The host stores the configuration and hands it back through <see cref="IEventPublisher.GetBindings"/>.
/// This evaluator reads those bindings, watches the sensors they name through
/// <see cref="HardwareMonitor.SetEventSensors"/>, and keeps just enough state to publish on a transition:
/// a threshold that has been held long enough, a sensor that has gone quiet, a machine that crossed
/// between online and offline, the helper starting or stopping.
/// </para>
/// <para>
/// It runs on the polling thread, outside the hardware gate. A binding list is fetched every few seconds
/// rather than every poll, and sensors are enumerated only when the set of configured sensors changes or
/// one is missing, because both are host calls or full hardware reads.
/// </para>
/// </remarks>
internal sealed class SensorEventEvaluator
{
	/// <summary>How long a configured sensor is remembered before a missing one is looked up again.</summary>
	private static readonly TimeSpan SensorRefresh = TimeSpan.FromSeconds(30);

	private readonly HardwareMonitor _monitor;
	private readonly IRemoteHealthSource? _remoteHealth;
	private readonly IHardwareElevation? _elevation;
	private readonly ILogger _logger;
	private readonly ConcurrentDictionary<string, BindingState> _states = new(StringComparer.Ordinal);

	private IEventPublisher? _publisher;
	private IReadOnlyList<EventBinding> _bindings = [];
	private DateTimeOffset _bindingsAt = DateTimeOffset.MinValue;
	private Dictionary<string, SensorIdentifier> _sensors = new(StringComparer.Ordinal);
	private HashSet<string>? _wantedSensorIds;
	private DateTimeOffset _sensorsAt = DateTimeOffset.MinValue;
	private int _evaluating;

	public SensorEventEvaluator(
		HardwareMonitor monitor,
		IRemoteHealthSource? remoteHealth,
		IHardwareElevation? elevation,
		ILogger logger)
	{
		_monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
		_remoteHealth = remoteHealth;
		_elevation = elevation;
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
			.ForContext<SensorEventEvaluator>();
	}

	/// <summary>How often the configured bindings are re-read from the host.</summary>
	internal TimeSpan BindingRefresh { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>The clock tests can move instead of waiting for real time.</summary>
	internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

	/// <summary>
	/// Attaches the current session's publisher. A reconnect keeps the transition state, so a threshold
	/// that was already active does not fire again just because the host reconnected; a detach clears it,
	/// because there is no session to publish to and the next one is a new start.
	/// </summary>
	public void Attach(IEventPublisher? publisher)
	{
		Interlocked.Exchange(ref _publisher, publisher);
		_bindingsAt = DateTimeOffset.MinValue;

		if (publisher is null)
		{
			_bindings = [];
			_states.Clear();
			_wantedSensorIds = null;
			_monitor.SetEventSensors([]);
		}
	}

	/// <summary>Evaluates every configured event against the current cache. Never throws to the loop.</summary>
	public void Evaluate()
	{
		var publisher = Volatile.Read(ref _publisher);
		if (publisher is null || Interlocked.Exchange(ref _evaluating, 1) == 1)
		{
			return;
		}

		try
		{
			EvaluateCore(publisher);
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "The hardware events could not be evaluated.");
		}
		finally
		{
			Volatile.Write(ref _evaluating, 0);
		}
	}

	private void EvaluateCore(IEventPublisher publisher)
	{
		var now = Clock();
		RefreshBindingsIfNeeded(publisher, now);

		var bindings = _bindings.Where(IsOurs).ToArray();
		var wanted = bindings
			.Select(binding => Text(binding, HardwareEvents.SensorParameter))
			.Where(id => !string.IsNullOrWhiteSpace(id))
			.Select(id => id!)
			.ToHashSet(StringComparer.Ordinal);

		RefreshSensorsIfNeeded(wanted, now);
		_monitor.SetEventSensors(
			_sensors.Values.Where(sensor => wanted.Contains(sensor.ResourceId)));

		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var binding in bindings)
		{
			var key = KeyOf(binding);
			seen.Add(key);
			var state = _states.GetOrAdd(key, _ => new BindingState());

			switch (binding.EventId)
			{
				case HardwareEvents.ThresholdId:
					EvaluateThreshold(publisher, binding, state, now);
					break;
				case HardwareEvents.StoppedId:
					EvaluateStopped(publisher, binding, state, now);
					break;
				case HardwareEvents.RemoteOfflineId:
				case HardwareEvents.RemoteOnlineId:
					EvaluateRemote(publisher, binding, state);
					break;
				case HardwareEvents.ElevationChangedId:
					EvaluateElevation(publisher, binding, state);
					break;
			}
		}

		foreach (var key in _states.Keys.Where(key => !seen.Contains(key)).ToArray())
		{
			_states.TryRemove(key, out _);
		}
	}

	private void EvaluateThreshold(
		IEventPublisher publisher,
		EventBinding binding,
		BindingState state,
		DateTimeOffset now)
	{
		if (SensorOf(binding) is not { } sensor
			|| Number(binding, HardwareEvents.ThresholdParameter) is not { } threshold
			|| !_monitor.TryGetReading(sensor, out var value))
		{
			return;
		}

		var below = string.Equals(
			Text(binding, HardwareEvents.DirectionParameter),
			HardwareEvents.DirectionBelow,
			StringComparison.OrdinalIgnoreCase);
		var dwell = TimeSpan.FromSeconds(Math.Max(0, Number(binding, HardwareEvents.DwellParameter) ?? 0));
		var hysteresis = Math.Max(0, Number(binding, HardwareEvents.HysteresisParameter) ?? 0);

		var condition = below ? value <= threshold : value >= threshold;

		if (condition)
		{
			state.Since ??= now;

			if (!state.Active && now - state.Since.Value >= dwell)
			{
				state.Active = true;
				Publish(publisher, binding.EventId, new Dictionary<string, object?>
				{
					[HardwareEvents.SensorParameter] = sensor.DisplayName,
					[HardwareEvents.PayloadValue] = value,
					[HardwareEvents.ThresholdParameter] = threshold,
					[HardwareEvents.PayloadUnit] = SensorFormatting.Unit(sensor.SensorType) ?? string.Empty,
					[HardwareEvents.MachineParameter] = MachineOf(sensor),
				});
			}

			return;
		}

		state.Since = null;

		if (!state.Active)
		{
			return;
		}

		// Hysteresis is what keeps a reading sitting on the line from firing on every other poll.
		var cleared = below
			? value > threshold + hysteresis
			: value < threshold - hysteresis;

		if (cleared)
		{
			state.Active = false;
		}
	}

	private void EvaluateStopped(
		IEventPublisher publisher,
		EventBinding binding,
		BindingState state,
		DateTimeOffset now)
	{
		if (SensorOf(binding) is not { } sensor)
		{
			return;
		}

		var grace = TimeSpan.FromSeconds(Math.Max(0, Number(binding, HardwareEvents.GraceParameter) ?? 30));

		if (_monitor.TryGetReading(sensor, out _))
		{
			state.LastSeen = now;
			state.StoppedFired = false;
			return;
		}

		if (state.LastSeen is not { } lastSeen || state.StoppedFired || now - lastSeen < grace)
		{
			return;
		}

		state.StoppedFired = true;
		Publish(publisher, binding.EventId, new Dictionary<string, object?>
		{
			[HardwareEvents.SensorParameter] = sensor.DisplayName,
			[HardwareEvents.PayloadSeconds] = (now - lastSeen).TotalSeconds,
			[HardwareEvents.MachineParameter] = MachineOf(sensor),
		});
	}

	private void EvaluateRemote(
		IEventPublisher publisher,
		EventBinding binding,
		BindingState state)
	{
		if (Text(binding, HardwareEvents.MachineParameter) is not { } key
			|| _remoteHealth?.RemoteStatuses.FirstOrDefault(status =>
				string.Equals(status.Key, key, StringComparison.Ordinal)) is not { } status)
		{
			return;
		}

		var online = status.Health.IsOnline;

		if (state.WasOnline is not { } wasOnline)
		{
			// The first observation is the starting point, not a transition.
			state.WasOnline = online;
			return;
		}

		if (binding.EventId == HardwareEvents.RemoteOfflineId && wasOnline && !online)
		{
			Publish(publisher, binding.EventId, new Dictionary<string, object?>
			{
				[HardwareEvents.MachineParameter] = status.Name,
				[HardwareEvents.PayloadUrl] = status.Url,
				[HardwareEvents.PayloadError] = status.Health.LastError ?? string.Empty,
			});
		}
		else if (binding.EventId == HardwareEvents.RemoteOnlineId && !wasOnline && online)
		{
			Publish(publisher, binding.EventId, new Dictionary<string, object?>
			{
				[HardwareEvents.MachineParameter] = status.Name,
				[HardwareEvents.PayloadUrl] = status.Url,
			});
		}

		state.WasOnline = online;
	}

	private void EvaluateElevation(IEventPublisher publisher, EventBinding binding, BindingState state)
	{
		var elevated = _elevation?.IsElevated ?? false;

		if (state.WasElevated is not { } wasElevated)
		{
			state.WasElevated = elevated;
			return;
		}

		if (wasElevated == elevated)
		{
			return;
		}

		state.WasElevated = elevated;
		Publish(publisher, binding.EventId, new Dictionary<string, object?>
		{
			[HardwareEvents.PayloadElevated] = elevated,
		});
	}

	private void RefreshBindingsIfNeeded(IEventPublisher publisher, DateTimeOffset now)
	{
		if (now - _bindingsAt < BindingRefresh)
		{
			return;
		}

		_bindingsAt = now;

		try
		{
			_bindings = publisher.GetBindings() ?? [];
		}
		catch (Exception exception)
		{
			// Keep the last known bindings; the host may be mid-reconnect.
			_logger.Debug(exception, "The configured hardware events could not be read.");
		}
	}

	private void RefreshSensorsIfNeeded(HashSet<string> wanted, DateTimeOffset now)
	{
		var changed = _wantedSensorIds is null || !_wantedSensorIds.SetEquals(wanted);
		var missing = wanted.Any(id => !_sensors.ContainsKey(id));
		var retryMissing = missing && now - _sensorsAt >= SensorRefresh;

		if (!changed && !retryMissing)
		{
			return;
		}

		_wantedSensorIds = wanted;
		_sensorsAt = now;

		if (wanted.Count == 0)
		{
			return;
		}

		try
		{
			var sensors = _monitor.EnumerateSensorsAsync().GetAwaiter().GetResult();
			var map = new Dictionary<string, SensorIdentifier>(StringComparer.Ordinal);
			foreach (var sensor in sensors)
			{
				map.TryAdd(sensor.ResourceId, sensor);
			}

			_sensors = map;
		}
		catch (Exception exception)
		{
			// Keep what was already resolved; a device that refused to answer is retried after the
			// refresh window rather than on every poll.
			_logger.Debug(exception, "The sensors for the configured events could not be enumerated.");
		}
	}

	private SensorIdentifier? SensorOf(EventBinding binding) =>
		Text(binding, HardwareEvents.SensorParameter) is { } id
		&& _sensors.TryGetValue(id, out var sensor)
			? sensor
			: null;

	private string MachineOf(SensorIdentifier sensor)
	{
		if (sensor.Hardware.Source is not { } source)
		{
			return Resolve(Strings.Events.ThisMachine());
		}

		return _remoteHealth?.RemoteStatuses
			.FirstOrDefault(status => string.Equals(status.Key, source, StringComparison.Ordinal))
			?.Name
			?? source;
	}

	private void Publish(
		IEventPublisher publisher,
		string eventId,
		IReadOnlyDictionary<string, object?> parameters)
	{
		// A shutdown or reconnect may have detached this publisher while the evaluation was running.
		if (!ReferenceEquals(Volatile.Read(ref _publisher), publisher))
		{
			return;
		}

		try
		{
			publisher.Publish(eventId, parameters);
		}
		catch (Exception exception)
		{
			_logger.Debug(exception, "The event {EventId} could not be published.", eventId);
		}
	}

	private static bool IsOurs(EventBinding binding) =>
		binding.EventId is
			HardwareEvents.ThresholdId or
			HardwareEvents.StoppedId or
			HardwareEvents.RemoteOfflineId or
			HardwareEvents.RemoteOnlineId or
			HardwareEvents.ElevationChangedId;

	private static string KeyOf(EventBinding binding) =>
		binding.EventId
		+ "|"
		+ string.Join(
			"|",
			binding.Parameters
				.OrderBy(pair => pair.Key, StringComparer.Ordinal)
				.Select(pair => pair.Key + "=" + (ValueText(pair.Value) ?? string.Empty)));

	private static string? Text(EventBinding binding, string name) =>
		binding.Parameters.TryGetValue(name, out var value) ? ValueText(value) : null;

	private static double? Number(EventBinding binding, string name) =>
		Text(binding, name) is { } text
		&& double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
		&& double.IsFinite(number)
			? number
			: null;

	private static string? ValueText(EventBindingValue value)
	{
		if (value.Value is not { } json)
		{
			return null;
		}

		return json.ValueKind switch
		{
			JsonValueKind.String => json.GetString(),
			JsonValueKind.Number => json.GetRawText(),
			JsonValueKind.True => bool.TrueString,
			JsonValueKind.False => bool.FalseString,
			JsonValueKind.Null or JsonValueKind.Undefined => null,
			_ => json.GetRawText(),
		};
	}

	private static string Resolve(LocalizedText text) =>
		Resolver.Resolve(text, CultureInfo.CurrentUICulture.Name) ?? string.Empty;

	private static readonly LocalizationResolver Resolver = CreateResolver();

	private static LocalizationResolver CreateResolver()
	{
		var catalogs = new LocalizationCatalogRegistry();
		catalogs.Register(Strings.LocalizationCatalog);

		return new LocalizationResolver(catalogs);
	}

	/// <summary>The small amount of state that makes an event a transition rather than a level.</summary>
	private sealed class BindingState
	{
		public DateTimeOffset? Since;
		public bool Active;

		public DateTimeOffset? LastSeen;
		public bool StoppedFired;

		public bool? WasOnline;
		public bool? WasElevated;
	}
}
