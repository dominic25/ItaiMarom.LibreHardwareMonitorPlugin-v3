using Serilog;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// Carries out the helper's requests against one hardware source. Kept separate from the pipe so the
/// protocol can be tested without a process, a pipe or an administrator.
/// </summary>
internal sealed class HardwareHostHandler : IDisposable
{
	private readonly IHardwareSensorSource _source;
	private readonly Func<string?> _accessNote;
	private readonly ILogger _logger;
	private readonly object _sync = new();

	private bool _open;
	private bool _disposed;

	public HardwareHostHandler(
		IHardwareSensorSource source,
		ILogger logger,
		Func<string?>? accessNote = null)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<HardwareHostHandler>();
		_accessNote = accessNote ?? (() => null);
	}

	/// <summary>Executes one request. A hardware failure is reported to the caller, not thrown into the pipe.</summary>
	public HardwareHostResponse Handle(HardwareHostRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		try
		{
			return request.Type switch
			{
				"open" => Open(),
				"close" => Close(),
				"read" => Read(request.Filter),
				"controls" => Controls(),
				"set" => Set(request.SensorId, request.Value),
				"reset" => Reset(request.SensorId),
				"ping" => new HardwareHostResponse { Ok = true },
				_ => Failure($"Unknown request: {request.Type}."),
			};
		}
		catch (Exception exception)
		{
			// The helper stays alive: one device that vanished or one driver that refused a read is a
			// result for this request, and the next poll is allowed to try again.
			_logger.Warning(exception, "The hardware helper could not carry out {RequestType}.", request.Type);
			return Failure(exception.Message);
		}
	}

	/// <summary>Closes the hardware and releases the source. Safe to call more than once.</summary>
	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;

			try
			{
				if (_open)
				{
					_source.Close();
					_open = false;
				}
			}
			catch (Exception exception)
			{
				_logger.Debug(exception, "The hardware helper could not close the hardware.");
			}

			_source.Dispose();
		}
	}

	private HardwareHostResponse Open()
	{
		lock (_sync)
		{
			if (!_open)
			{
				_source.Open();
				_open = true;
			}

			// The helper is the process that can answer this: it is elevated, so it can say whether the
			// driver is actually reachable now that the privilege is in the right place.
			return new HardwareHostResponse { Ok = true, AccessNote = _accessNote() };
		}
	}

	private HardwareHostResponse Close()
	{
		lock (_sync)
		{
			if (_open)
			{
				_source.Close();
				_open = false;
			}
		}

		return new HardwareHostResponse { Ok = true };
	}

	private HardwareHostResponse Read(string[]? filter)
	{
		lock (_sync)
		{
			EnsureOpen();
			var sensors = _source.Read(filter is { Length: > 0 } ? filter : null);
			return new HardwareHostResponse
			{
				Ok = true,
				Sensors = [.. sensors.Select(HardwareSensorDto.From)],
			};
		}
	}

	private HardwareHostResponse Controls()
	{
		lock (_sync)
		{
			EnsureOpen();
			var controls = _source.ReadControls();
			return new HardwareHostResponse
			{
				Ok = true,
				Controls = [.. controls.Select(SensorIdentifierDto.From)],
			};
		}
	}

	private HardwareHostResponse Set(string? sensorId, float value)
	{
		if (string.IsNullOrWhiteSpace(sensorId))
		{
			return Failure("No sensor was named.");
		}

		lock (_sync)
		{
			EnsureOpen();
			return new HardwareHostResponse { Ok = true, Result = _source.SetControl(sensorId, value) };
		}
	}

	private HardwareHostResponse Reset(string? sensorId)
	{
		if (string.IsNullOrWhiteSpace(sensorId))
		{
			return Failure("No sensor was named.");
		}

		lock (_sync)
		{
			EnsureOpen();
			return new HardwareHostResponse { Ok = true, Result = _source.ResetControl(sensorId) };
		}
	}

	/// <summary>Every operation may be the first one: the pipe does not promise an <c>open</c> before it.</summary>
	private void EnsureOpen()
	{
		if (!_open)
		{
			_source.Open();
			_open = true;
		}
	}

	private static HardwareHostResponse Failure(string error) => new() { Ok = false, Error = error };
}
