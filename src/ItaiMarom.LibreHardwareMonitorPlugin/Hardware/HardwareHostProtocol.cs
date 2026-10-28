using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>One request the plugin sends to the elevated hardware helper.</summary>
internal sealed class HardwareHostRequest
{
	/// <summary>One of <c>hello</c>, <c>open</c>, <c>close</c>, <c>read</c>, <c>controls</c>, <c>set</c>, <c>reset</c>, <c>ping</c> or <c>stop</c>.</summary>
	public required string Type { get; init; }

	/// <summary>The shared secret, sent only by <c>hello</c>.</summary>
	public string? Token { get; init; }

	/// <summary>The sensor a <c>set</c> or <c>reset</c> names.</summary>
	public string? SensorId { get; init; }

	/// <summary>The value a <c>set</c> writes.</summary>
	public float Value { get; init; }

	/// <summary>The top-level hardware a <c>read</c> is limited to, or null for the whole machine.</summary>
	public string[]? Filter { get; init; }
}

/// <summary>One answer from the elevated hardware helper.</summary>
internal sealed class HardwareHostResponse
{
	/// <summary>Whether the helper carried out the request.</summary>
	public bool Ok { get; init; }

	/// <summary>What went wrong, when <see cref="Ok"/> is false.</summary>
	public string? Error { get; init; }

	/// <summary>The sensors a <c>read</c> returned.</summary>
	public HardwareSensorDto[]? Sensors { get; init; }

	/// <summary>The controllable sensors a <c>controls</c> returned.</summary>
	public SensorIdentifierDto[]? Controls { get; init; }

	/// <summary>The answer a <c>set</c> or <c>reset</c> returned: false when the sensor is gone.</summary>
	public bool? Result { get; init; }

	/// <summary>What the helper still cannot read, or null when it can read everything. Set by <c>open</c>.</summary>
	public string? AccessNote { get; init; }
}

/// <summary>One sensor on the wire: identity, value and the range its control accepts.</summary>
internal sealed record HardwareSensorDto(
	SensorIdentifierDto Sensor,
	float? Value,
	ControlRangeDto? Control)
{
	public static HardwareSensorDto From(HardwareSensor sensor) => new(
		SensorIdentifierDto.From(sensor.Sensor),

		// A non-finite reading is the library saying it has nothing to report, which is what a null
		// value already means. It also cannot travel as JSON: serialising it throws, and a helper that
		// threw while answering a read would die and take the elevated readings with it.
		sensor.Value is { } value && float.IsFinite(value) ? value : null,

		// A span the chip cannot describe is no span at all, and a non-finite bound cannot travel either.
		sensor.Control is { } control && float.IsFinite(control.Min) && float.IsFinite(control.Max)
			? ControlRangeDto.From(control)
			: null);

	public HardwareSensor ToHardwareSensor() => new(
		Sensor.ToSensorIdentifier(),
		Value,
		Control?.ToControlRange());
}

/// <summary>The span a control accepts, on the wire.</summary>
internal sealed record ControlRangeDto(float Min, float Max)
{
	public static ControlRangeDto From(ControlRange range) => new(range.Min, range.Max);

	public ControlRange ToControlRange() => new(Min, Max);
}

/// <summary>
/// A sensor's identity on the wire. The library's own <see cref="SensorType"/> travels by name rather than
/// by number so a helper built against one library version cannot silently reinterpret a value.
/// </summary>
internal sealed record SensorIdentifierDto(
	HardwareLocationDto Hardware,
	string Id,
	string SensorName,
	string SensorType,
	bool Controllable)
{
	public static SensorIdentifierDto From(SensorIdentifier sensor) => new(
		HardwareLocationDto.From(sensor.Hardware),
		sensor.Id,
		sensor.SensorName,
		sensor.SensorType.ToString(),
		sensor.Controllable);

	public SensorIdentifier ToSensorIdentifier()
	{
		if (!Enum.TryParse<SensorType>(SensorType, ignoreCase: false, out var sensorType))
		{
			throw new InvalidDataException($"The hardware helper named an unknown sensor type: {SensorType}.");
		}

		return new SensorIdentifier(
			Hardware.ToHardwareLocation(),
			Id,
			SensorName,
			sensorType)
		{
			Controllable = Controllable,
		};
	}
}

/// <summary>One device in the hardware tree, on the wire.</summary>
internal sealed record HardwareLocationDto(string Id, string Name, string Path, string RootId, string RootName)
{
	public static HardwareLocationDto From(HardwareLocation location) => new(
		location.Id,
		location.Name,
		location.Path,
		location.RootId,
		location.RootName);

	public HardwareLocation ToHardwareLocation() => new(Id, Name, Path, RootId, RootName);
}

/// <summary>
/// The framing of the helper's named pipe: a four-byte little-endian length followed by that many bytes of
/// JSON. Both sides are the same assembly, so the contract is the records above rather than a public API.
/// </summary>
internal static class HardwareHostProtocol
{
	/// <summary>Enough for the largest sensor list on a machine, small enough that a bad length cannot exhaust memory.</summary>
	internal const int MaximumMessageBytes = 8 * 1024 * 1024;

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
		where T : class
	{
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(message);

		var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
		var length = new byte[sizeof(int)];
		BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);

		await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
		await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
		await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads one message. A zero-length payload, a length beyond <see cref="MaximumMessageBytes"/> or a
	/// payload that is not the expected record is a protocol failure rather than an empty request.
	/// </summary>
	public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
		where T : class
	{
		ArgumentNullException.ThrowIfNull(stream);

		var length = new byte[sizeof(int)];
		await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);

		var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(length);
		if (payloadLength <= 0 || payloadLength > MaximumMessageBytes)
		{
			throw new InvalidDataException($"The hardware helper sent a message of {payloadLength} bytes.");
		}

		var payload = new byte[payloadLength];
		await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);

		return JsonSerializer.Deserialize<T>(payload, Json)
			?? throw new InvalidDataException("The hardware helper sent an empty message.");
	}
}
