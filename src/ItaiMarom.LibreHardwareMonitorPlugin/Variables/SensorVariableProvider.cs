using System.Globalization;
using ItaiMarom.LibreHardwareMonitorPlugin.Hardware;
using MacroDeck.Localization;
using MacroDeck.Sdk.Variables;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Variables;

/// <summary>
/// Serves the plugin's variables: the selected sensors as eager variables, and the whole sensor tree as a
/// browsable catalog.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches the hardware library. Values come out of the cache
/// <see cref="HardwareMonitor"/> fills on its own thread, so a read is a dictionary lookup and never a
/// SMART query, and the session is never made to wait for a device. The one exception is the catalog:
/// browsing it is a deliberate, user-initiated act, so it is allowed to ask the monitor to enumerate.
/// </para>
/// <para>
/// The provider keeps no state of its own beyond a cache of that enumeration, so a read for a sensor the
/// plugin has never heard of answers "unavailable" rather than throwing - which is what the host expects
/// for a reference read back out of a profile that has since had the hardware removed.
/// </para>
/// </remarks>
public sealed class SensorVariableProvider : IVariableProvider, IDisposable
{
	/// <summary>How long a catalog enumeration is reused before the next browse asks the hardware again.</summary>
	private static readonly TimeSpan CatalogLifetime = TimeSpan.FromSeconds(30);

	/// <summary>Most entries served in one catalog page, whatever the host asks for.</summary>
	private const int MaxPageSize = 200;

	private readonly HardwareMonitor _monitor;
	private readonly SensorSelection _selection;
	private readonly SemaphoreSlim _tableGate = new(1, 1);

	private CatalogSnapshot? _snapshot;

	private IReadOnlyList<SensorVariable>? _eager;
	private IReadOnlyList<SensorIdentifier>? _eagerFor;

	public SensorVariableProvider(HardwareMonitor monitor, SensorSelection selection)
	{
		_monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
		_selection = selection ?? throw new ArgumentNullException(nameof(selection));
	}

	/// <summary>
	/// How often the loop reads the hardware, and therefore how often the host should read a variable.
	/// </summary>
	/// <remarks>
	/// Read live rather than captured: the settings screen can change the polling rate at run time, and
	/// the next definition the host asks for has to carry the new rate. Declaring it is what stops the
	/// host from falling back to its own five-second default for an eager variable.
	/// </remarks>
	private TimeSpan RefreshInterval => _monitor.Options.PollingInterval;

	/// <inheritdoc />
	/// <remarks>
	/// Empty until the plugin is configured or a Macro Deck 2 configuration has been migrated, and
	/// <see cref="VariablesDependOnConfiguration"/> says so. The catalog is how a user reaches every other
	/// sensor. Each definition carries the loop's polling interval, because the host otherwise reads an
	/// eager variable on its own five-second default and a widget would update slower than the hardware
	/// is polled.
	/// </remarks>
	public IReadOnlyList<VariableDefinition> Variables
	{
		get
		{
			var interval = RefreshInterval;
			return EagerVariables().Select(variable => variable.EagerDefinition(interval)).ToArray();
		}
	}

	/// <inheritdoc />
	public bool VariablesDependOnConfiguration => true;

	/// <inheritdoc />
	public bool SupportsCatalog => true;

	/// <inheritdoc />
	public bool SupportsSearch => true;

	/// <inheritdoc />
	/// <remarks>
	/// A read stays a dictionary lookup: the value comes from the loop's cache, and the range a fan's
	/// control accepts comes with the same sample. A cancellation token has nowhere to go here on purpose -
	/// answering from a dictionary is not work worth cancelling, and doing it synchronously is what keeps a
	/// read off the session's critical path.
	/// </remarks>
	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		if (!TryResolveSensor(localId, out var sensor)
			|| !_monitor.TryGetReading(sensor, out var value, out var control))
		{
			return new ValueTask<VariableReading>(VariableReading.Unavailable);
		}

		// A bound Slider takes its track from the reading rather than from the definition, so a fan's own
		// span travels with its value. The step is one percent because that is the resolution of a chip's
		// duty cycle: anything finer would be a position the hardware rounds away.
		return new ValueTask<VariableReading>(
			control is { } range
				? VariableReading.Of(value, range.Min, range.Max, 1)
				: VariableReading.Of(value));
	}

	/// <inheritdoc />
	/// <remarks>
	/// <para>
	/// Only a sensor the hardware library gave a control to is writable, and the definition it was declared
	/// under said so. The value is handed to the control without a second clamp: the hardware layer already
	/// holds it inside the range the chip reports, and the read that follows - not this answer - is what the
	/// host shows.
	/// </para>
	/// <para>
	/// "Not writable" is deliberately never the answer. This provider only declares the capability for a
	/// sensor that had one, so a write that arrives for a sensor without one is a control that went away,
	/// and the honest report is that it cannot be written right now rather than that it never could.
	/// </para>
	/// </remarks>
	public async ValueTask<VariableWriteResult> SetValueAsync(
		string localId,
		object? value,
		CancellationToken cancellationToken = default)
	{
		if (!TryReadNumber(value, out var number))
		{
			return VariableWriteResult.InvalidValue(Strings.Variables.Write.InvalidValue());
		}

		if (!TryResolveSensor(localId, out var sensor))
		{
			return VariableWriteResult.NotFound(Strings.Variables.Write.NotFound());
		}

		if (!sensor.Controllable)
		{
			return VariableWriteResult.Unavailable(Strings.Variables.Write.Unavailable());
		}

		bool applied;
		try
		{
			applied = await _monitor.SetControlAsync(sensor.Id, number, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// The write goes through the same gate as a poll, and a device that vanished while it was held
			// surfaces here. The next read is what says whether anything survived.
			return VariableWriteResult.Failed(Strings.Variables.Write.Failed());
		}

		return applied
			? VariableWriteResult.Applied()
			: VariableWriteResult.Unavailable(Strings.Variables.Write.Unavailable());
	}

	/// <inheritdoc />
	/// <remarks>
	/// This is where the working set is narrowed: the loop is told to read the eager sensors - which the
	/// host polls directly - plus whatever catalog resources are bound, and to forget the rest, so an
	/// unbound sensor stops costing a read rather than quietly going stale.
	/// </remarks>
	public async ValueTask<IReadOnlyList<VariableValue>> SubscribeAsync(
		IReadOnlyCollection<string> localIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(localIds);

		var watched = new HashSet<SensorIdentifier>(_selection.Sensors);
		if (localIds.Count > 0)
		{
			var table = await GetTableAsync(refresh: false, cancellationToken).ConfigureAwait(false);
			foreach (var localId in localIds)
			{
				if (table.TryGetSensor(localId, out var sensor))
				{
					watched.Add(sensor);
				}
			}
		}

		_monitor.SetWatchedSensors(watched);

		// Pushing is off, so the host reads what it needs; there is no benefit in handing back a snapshot
		// it is about to ask for one variable at a time anyway.
		return Array.Empty<VariableValue>();
	}

	/// <inheritdoc />
	/// <remarks>
	/// Deliberately permissive: any legal resource id resolves, even one for hardware that is not
	/// installed right now, because a reference read back out of a profile months later is worth keeping
	/// and reading as unavailable rather than being reported as broken.
	/// </remarks>
	public ValueTask<VariableDefinition?> ResolveAsync(string localId, CancellationToken cancellationToken = default)
	{
		var interval = RefreshInterval;
		foreach (var variable in EagerVariables())
		{
			if (string.Equals(variable.Id, localId, StringComparison.Ordinal))
			{
				return new ValueTask<VariableDefinition?>(variable.EagerDefinition(interval));
			}
		}

		if (!VariableNaming.IsValidResourceId(localId))
		{
			return ValueTask.FromResult<VariableDefinition?>(null);
		}

		// Already-enumerated hardware gives the description back verbatim - the unit, the precision, the
		// name it was offered under - so a restored binding looks exactly as it did when it was made.
		var table = Volatile.Read(ref _snapshot)?.Table;
		if (table is not null && table.TryGetSensor(localId, out var sensor))
		{
			return new ValueTask<VariableDefinition?>(CatalogDefinition(sensor, localId, parentId: null, interval));
		}

		// The sensor is not installed right now, but the binding survives - and it is polled at the
		// configured rate so it starts reporting as soon as the hardware answers again.
		return new ValueTask<VariableDefinition?>(
			VariableDefinition.OnDemand(localId, VariableType.Numeric) with { RefreshInterval = interval });
	}

	/// <inheritdoc />
	/// <remarks>
	/// A null <see cref="VariableCatalogQuery.ParentId"/> lists the hardware; a parent id lists that
	/// hardware's sensors; a search filters the whole tree and ignores the parent, because a search that
	/// only looked inside the folder the user happens to be in would be a strange search.
	/// </remarks>
	public async ValueTask<VariableCatalogPage> DiscoverAsync(
		VariableCatalogQuery query,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(query);

		SensorCatalogTable table;
		try
		{
			table = await GetTableAsync(refresh: true, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Enumerating hardware can fail - a driver that refuses to answer, a device pulled mid-read.
			// An empty page is the honest answer; the host shows a catalog with nothing in it.
			return VariableCatalogPage.Empty;
		}

		var offset = ParseOffset(query.ContinuationToken);
		var interval = RefreshInterval;

		if (!string.IsNullOrWhiteSpace(query.Search))
		{
			var matches = table.Sensors.Where(entry => entry.Sensor.Matches(query.Search)).ToArray();
			return Page(matches, query.PageSize, offset, entry => CatalogDefinition(entry.Sensor, entry.Id, entry.ParentId, interval));
		}

		if (string.IsNullOrEmpty(query.ParentId))
		{
			return Page(table.Hardware, query.PageSize, offset, HardwareDefinition);
		}

		return table.TryGetHardware(query.ParentId, out var node)
			? Page(node.Entries, query.PageSize, offset, entry => CatalogDefinition(entry.Sensor, entry.Id, node.Id, interval))
			: VariableCatalogPage.Empty;
	}

	/// <summary>Releases the gate that serializes catalog enumeration.</summary>
	public void Dispose() => _tableGate.Dispose();

	/// <summary>
	/// The eager variables for the current selection, computed once per selection rather than once per
	/// call. The two fields are not synchronized: a racing reader can only see a stale pairing, recompute
	/// from the same selection, and store the same answer. Only the names are cached - the definitions are
	/// built per call - so a changed polling interval is carried by the next definition without the cache
	/// having to be rebuilt.
	/// </summary>
	private IReadOnlyList<SensorVariable> EagerVariables()
	{
		var sensors = _selection.Sensors;
		if (_eager is { } cached && ReferenceEquals(sensors, _eagerFor))
		{
			return cached;
		}

		var assigned = SensorVariableNaming.Assign(sensors);

		_eagerFor = sensors;
		_eager = assigned;
		return assigned;
	}

	private bool TryResolveSensor(string localId, out SensorIdentifier sensor)
	{
		foreach (var variable in EagerVariables())
		{
			if (string.Equals(variable.Id, localId, StringComparison.Ordinal))
			{
				sensor = variable.Sensor;
				return true;
			}
		}

		// The catalog is only consulted from a snapshot: a read must not be the thing that triggers an
		// enumeration, or a single variable read could block on a disk. The host binds a resource before
		// it reads it, and binding subscribes, which builds the table.
		var table = Volatile.Read(ref _snapshot)?.Table;
		if (table is not null && table.TryGetSensor(localId, out var found))
		{
			sensor = found;
			return true;
		}

		sensor = null!;
		return false;
	}

	private async ValueTask<SensorCatalogTable> GetTableAsync(bool refresh, CancellationToken cancellationToken)
	{
		var snapshot = Volatile.Read(ref _snapshot);
		if (IsUsable(snapshot, refresh))
		{
			return snapshot!.Table;
		}

		await _tableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			snapshot = Volatile.Read(ref _snapshot);
			if (IsUsable(snapshot, refresh))
			{
				return snapshot!.Table;
			}

			var sensors = await _monitor.EnumerateSensorsAsync(cancellationToken).ConfigureAwait(false);
			var table = SensorCatalogTable.Build(sensors);
			Volatile.Write(ref _snapshot, new CatalogSnapshot(table, DateTimeOffset.UtcNow));
			return table;
		}
		finally
		{
			_tableGate.Release();
		}
	}

	private static bool IsUsable(CatalogSnapshot? snapshot, bool refresh) =>
		snapshot is not null
		&& (!refresh || DateTimeOffset.UtcNow - snapshot.BuiltAt < CatalogLifetime);

	private static VariableDefinition HardwareDefinition(SensorCatalogNode node) =>
		VariableDefinition.OnDemand(node.Id, VariableType.Numeric) with
		{
			Name = VariableNaming.Sanitize(node.Name),
			DisplayName = LocalizedText.FromLiteral(node.Name),
			IsContainer = true,
			IsBindable = false,
		};

	/// <summary>
	/// A catalog resource: on-demand, named the way a user already knows the sensor, writable when the
	/// hardware gave it a control, and polled at the loop's interval so a bound widget tracks the hardware.
	/// </summary>
	private static VariableDefinition CatalogDefinition(
		SensorIdentifier sensor,
		string resourceId,
		string? parentId,
		TimeSpan refreshInterval) =>
		SensorDefinition.Describe(
			sensor,
			VariableDefinition.OnDemand(resourceId, VariableType.Numeric),
			refreshInterval) with
		{
			// The host re-derives and de-duplicates a catalog name anyway, so the plain Macro Deck 2 name is
			// the best suggestion: it is what a user already knows the sensor as.
			Name = sensor.VariableName,
			ParentId = parentId,
		};

	private static VariableCatalogPage Page<T>(
		IReadOnlyList<T> items,
		int requestedPageSize,
		int offset,
		Func<T, VariableDefinition> select)
	{
		var size = requestedPageSize > 0 ? Math.Min(requestedPageSize, MaxPageSize) : MaxPageSize;
		var start = Math.Clamp(offset, 0, items.Count);
		var count = Math.Min(size, items.Count - start);

		var page = new List<VariableDefinition>(count);
		for (var index = start; index < start + count; index++)
		{
			page.Add(select(items[index]));
		}

		var next = start + count;
		return new VariableCatalogPage
		{
			Items = page,
			ContinuationToken = next < items.Count
				? next.ToString(CultureInfo.InvariantCulture)
				: null,
		};
	}

	private static int ParseOffset(string? continuationToken) =>
		int.TryParse(continuationToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) && offset > 0
			? offset
			: 0;

	/// <summary>
	/// Reads the value a Slider wrote, however the host serialized it - a number, a string, or nothing at
	/// all - and refuses anything that is not a finite number.
	/// </summary>
	private static bool TryReadNumber(object? value, out double number)
	{
		number = 0;

		if (value is null)
		{
			return false;
		}

		try
		{
			number = value switch
			{
				double wrote => wrote,
				float wrote => wrote,
				int wrote => wrote,
				long wrote => wrote,
				decimal wrote => (double)wrote,
				_ => Convert.ToDouble(value.ToString(), CultureInfo.InvariantCulture),
			};
		}
		catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
		{
			return false;
		}

		return double.IsFinite(number);
	}

	private sealed record CatalogSnapshot(SensorCatalogTable Table, DateTimeOffset BuiltAt);
}
