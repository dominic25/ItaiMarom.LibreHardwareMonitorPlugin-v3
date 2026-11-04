using LibreHardwareMonitor.Hardware;

namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// A unit a selected sensor can be shown in, when the plugin offers a choice for its kind.
/// </summary>
/// <remarks>
/// The enum member is what is stored and what the window posts; the symbol is what a user reads. Keeping
/// the two apart is deliberate - a stored value must not move when a label is reworded - and it also
/// leaves room for accepting a symbol from a hand-edited document.
/// </remarks>
public enum SensorUnit
{
	/// <summary>Degrees Celsius, what the hardware library reports temperatures in.</summary>
	Celsius,

	/// <summary>Degrees Fahrenheit.</summary>
	Fahrenheit,

	/// <summary>Decimal gigabytes, a thousand million bytes.</summary>
	Gigabytes,

	/// <summary>Binary gigabytes, 1024 cubed bytes.</summary>
	Gibibytes,

	/// <summary>Decimal megabytes, a million bytes.</summary>
	Megabytes,

	/// <summary>Binary megabytes, 1024 squared bytes.</summary>
	Mebibytes,

	/// <summary>Megahertz, what the hardware library reports clocks in.</summary>
	Megahertz,

	/// <summary>Gigahertz, a thousand megahertz.</summary>
	Gigahertz,

	/// <summary>Bytes per second, what the hardware library reports throughput in.</summary>
	BytesPerSecond,

	/// <summary>Binary kilobytes per second.</summary>
	KilobytesPerSecond,

	/// <summary>Binary megabytes per second.</summary>
	MegabytesPerSecond,
}

/// <summary>
/// The units a sensor kind can be shown in, how a reading is converted into the chosen one, and how a
/// reading another machine formatted is brought back to the unit this plugin caches.
/// </summary>
/// <remarks>
/// <para>
/// The library reports a sensor in one unit per kind and never converts: temperatures in Celsius, clocks
/// in megahertz, throughput in bytes per second, sizes in bytes or megabytes. A user who wants Fahrenheit,
/// gigahertz or gigabytes per second is asking for a display choice, not for the reading to change - so the
/// cached reading stays in the library's unit and only the value handed to Macro Deck is converted, at the
/// same place the unit and precision are declared.
/// </para>
/// <para>
/// A remote machine sends the same readings already formatted for a human (<c>12.2 GB</c>,
/// <c>53.0 KB/s</c>). Those are not the library's unit, so <see cref="FromReported" /> undoes the
/// formatting before the value is cached. Without that, a size would be cached as "12.2" and later shown
/// as almost nothing once its unit is applied a second time.
/// </para>
/// </remarks>
public static class SensorUnits
{
	/// <summary>The binary factors LibreHardwareMonitor itself uses when it scales a reading.</summary>
	private const double BytesPerKilobyte = 1024;
	private const double BytesPerMegabyte = 1024d * 1024;
	private const double BytesPerGigabyte = 1024d * 1024 * 1024;
	private const double BytesPerTerabyte = 1024d * 1024 * 1024 * 1024;

	private static readonly SensorUnit[] TemperatureOptions = [SensorUnit.Celsius, SensorUnit.Fahrenheit];
	private static readonly SensorUnit[] DataOptions = [SensorUnit.Gigabytes, SensorUnit.Gibibytes];
	private static readonly SensorUnit[] SmallDataOptions = [SensorUnit.Megabytes, SensorUnit.Mebibytes];
	private static readonly SensorUnit[] ClockOptions = [SensorUnit.Megahertz, SensorUnit.Gigahertz];
	private static readonly SensorUnit[] ThroughputOptions =
		[SensorUnit.BytesPerSecond, SensorUnit.KilobytesPerSecond, SensorUnit.MegabytesPerSecond];

	/// <summary>
	/// The kinds that offer a choice, in the order the window lists them. It is the same set
	/// <see cref="IsSupported" /> accepts, so a defaults editor and a per-sensor selector cannot disagree
	/// about which kinds exist.
	/// </summary>
	public static IReadOnlyList<SensorType> SupportedKinds { get; } =
	[
		SensorType.Temperature,
		SensorType.Data,
		SensorType.SmallData,
		SensorType.Clock,
		SensorType.Throughput,
	];

	/// <summary>Whether the plugin offers any unit choice for this kind of reading.</summary>
	public static bool IsSupported(SensorType sensorType) => sensorType is
		SensorType.Temperature or
		SensorType.Data or
		SensorType.SmallData or
		SensorType.Clock or
		SensorType.Throughput;

	/// <summary>The unit a sensor is shown in when the user has not chosen another one.</summary>
	public static SensorUnit DefaultFor(SensorType sensorType) => sensorType switch
	{
		SensorType.Temperature => SensorUnit.Celsius,
		SensorType.Data => SensorUnit.Gigabytes,
		SensorType.SmallData => SensorUnit.Megabytes,
		SensorType.Clock => SensorUnit.Megahertz,
		SensorType.Throughput => SensorUnit.BytesPerSecond,
		_ => throw new ArgumentOutOfRangeException(
			nameof(sensorType),
			sensorType,
			"This kind of reading has no unit choice."),
	};

	/// <summary>
	/// The unit a sensor of this kind uses when it has no choice of its own: the configured default when
	/// one is present and valid, and the library's own otherwise.
	/// </summary>
	public static SensorUnit DefaultFor(
		SensorType sensorType,
		IReadOnlyDictionary<SensorType, SensorUnit>? defaults)
	{
		if (!IsSupported(sensorType))
		{
			throw new ArgumentOutOfRangeException(
				nameof(sensorType),
				sensorType,
				"This kind of reading has no unit choice.");
		}

		return defaults is not null
			&& defaults.TryGetValue(sensorType, out var unit)
			&& IsValidFor(sensorType, unit)
				? unit
				: DefaultFor(sensorType);
	}

	/// <summary>
	/// A complete defaults map: every kind that offers a choice, with the configured unit where it is
	/// valid and the library's own everywhere else. Storing and rendering a complete map is what lets a
	/// document written by an older version - or one with a hand-edited entry - read back as a working
	/// default rather than as a kind with no answer.
	/// </summary>
	public static IReadOnlyDictionary<SensorType, SensorUnit> NormalizeDefaults(
		IReadOnlyDictionary<SensorType, SensorUnit>? defaults)
	{
		var normalized = new Dictionary<SensorType, SensorUnit>(SupportedKinds.Count);
		foreach (var sensorType in SupportedKinds)
		{
			normalized[sensorType] = DefaultFor(sensorType, defaults);
		}

		return normalized;
	}

	/// <summary>The text that names a kind in the defaults editor, for example <c>Small data</c>.</summary>
	public static string Label(SensorType sensorType) => sensorType switch
	{
		SensorType.Temperature => "Temperature",
		SensorType.Data => "Data",
		SensorType.SmallData => "Small data",
		SensorType.Clock => "Clock",
		SensorType.Throughput => "Throughput",
		_ => sensorType.ToString(),
	};

	/// <summary>The units the window offers for a kind, in the order it shows them.</summary>
	public static IReadOnlyList<SensorUnit> OptionsFor(SensorType sensorType) => sensorType switch
	{
		SensorType.Temperature => TemperatureOptions,
		SensorType.Data => DataOptions,
		SensorType.SmallData => SmallDataOptions,
		SensorType.Clock => ClockOptions,
		SensorType.Throughput => ThroughputOptions,
		_ => [],
	};

	/// <summary>Whether a unit is one this kind can actually be shown in.</summary>
	public static bool IsValidFor(SensorType sensorType, SensorUnit unit) =>
		OptionsFor(sensorType).Contains(unit);

	/// <summary>The text shown after the value, for example <c>°F</c> or <c>GiB</c>.</summary>
	public static string Symbol(SensorUnit unit) => unit switch
	{
		SensorUnit.Celsius => "°C",
		SensorUnit.Fahrenheit => "°F",
		SensorUnit.Gigabytes => "GB",
		SensorUnit.Gibibytes => "GiB",
		SensorUnit.Megabytes => "MB",
		SensorUnit.Mebibytes => "MiB",
		SensorUnit.Megahertz => "MHz",
		SensorUnit.Gigahertz => "GHz",
		SensorUnit.BytesPerSecond => "B/s",
		SensorUnit.KilobytesPerSecond => "KB/s",
		SensorUnit.MegabytesPerSecond => "MB/s",
		_ => string.Empty,
	};

	/// <summary>
	/// Reads a stored or posted unit. The enum member is the canonical spelling; the symbols are accepted
	/// as well so a document a person edited by hand still reads back.
	/// </summary>
	public static bool TryParse(string? text, out SensorUnit unit)
	{
		unit = default;

		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		var trimmed = text.Trim();

		if (Enum.TryParse(trimmed, ignoreCase: true, out unit) && Enum.IsDefined(unit))
		{
			return true;
		}

		switch (trimmed)
		{
			case "°C" or "C":
				unit = SensorUnit.Celsius;
				return true;
			case "°F" or "F":
				unit = SensorUnit.Fahrenheit;
				return true;
			case "GB":
				unit = SensorUnit.Gigabytes;
				return true;
			case "GiB":
				unit = SensorUnit.Gibibytes;
				return true;
			case "MB":
				unit = SensorUnit.Megabytes;
				return true;
			case "MiB":
				unit = SensorUnit.Mebibytes;
				return true;
			case "MHz":
				unit = SensorUnit.Megahertz;
				return true;
			case "GHz":
				unit = SensorUnit.Gigahertz;
				return true;
			case "B/s":
				unit = SensorUnit.BytesPerSecond;
				return true;
			case "KB/s" or "KiB/s":
				unit = SensorUnit.KilobytesPerSecond;
				return true;
			case "MB/s" or "MiB/s":
				unit = SensorUnit.MegabytesPerSecond;
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// Converts a cached reading into the unit a sensor is shown in. The library's own unit is the input,
	/// and an unsupported kind or an unusable choice is left untouched rather than converted twice.
	/// </summary>
	public static double ToDisplay(SensorType sensorType, double value, SensorUnit? unit)
	{
		if (!IsSupported(sensorType))
		{
			return value;
		}

		var chosen = unit is { } candidate && IsValidFor(sensorType, candidate)
			? candidate
			: DefaultFor(sensorType);

		return chosen switch
		{
			SensorUnit.Fahrenheit => (value * 1.8) + 32,
			SensorUnit.Gigabytes => value / 1_000_000_000,
			SensorUnit.Gibibytes => value / BytesPerGigabyte,
			SensorUnit.Megabytes => value,
			SensorUnit.Mebibytes => value * 1_000_000 / BytesPerMegabyte,
			SensorUnit.Gigahertz => value / 1000,
			SensorUnit.KilobytesPerSecond => value / BytesPerKilobyte,
			SensorUnit.MegabytesPerSecond => value / BytesPerMegabyte,
			_ => value,
		};
	}

	/// <summary>
	/// Brings a reading another machine formatted back to the unit this plugin caches. A JSON number has
	/// no unit and is already the library's own; formatted text is scaled out of the suffix it carries.
	/// </summary>
	public static double FromReported(SensorType sensorType, double value, string? reportedUnit)
	{
		var suffix = Normalize(reportedUnit);
		if (suffix.Length == 0)
		{
			return value;
		}

		return sensorType switch
		{
			SensorType.Temperature => FromReportedTemperature(value, suffix),
			SensorType.Data => ToBytes(value, suffix),
			SensorType.SmallData => ToBytes(value, suffix) / 1_000_000,
			SensorType.Clock => ToMegahertz(value, suffix),
			SensorType.Throughput => ToBytesPerSecond(value, suffix),
			_ => value,
		};
	}

	/// <summary>The suffix of a formatted reading, lowercased and stripped of degree signs and spaces.</summary>
	private static string Normalize(string? reportedUnit) =>
		reportedUnit is null
			? string.Empty
			: reportedUnit
				.Trim()
				.Replace("°", string.Empty, StringComparison.Ordinal)
				.Replace(" ", string.Empty, StringComparison.Ordinal)
				.ToLowerInvariant();

	private static double FromReportedTemperature(double value, string suffix) =>
		suffix == "f" ? (value - 32) / 1.8 : value;

	/// <summary>
	/// A size in bytes. Plain <c>KB</c>/<c>MB</c>/<c>GB</c> are read as binary because that is how
	/// LibreHardwareMonitor scales them, while <c>KiB</c> and friends say the same thing explicitly.
	/// </summary>
	private static double ToBytes(double value, string suffix) => suffix switch
	{
		"b" or "byte" or "bytes" => value,
		"kb" or "kib" => value * BytesPerKilobyte,
		"mb" or "mib" => value * BytesPerMegabyte,
		"gb" or "gib" => value * BytesPerGigabyte,
		"tb" or "tib" => value * BytesPerTerabyte,
		_ => value,
	};

	/// <summary>
	/// A rate in bytes per second. A suffix ending in <c>bps</c> is bits, not bytes, and is divided by
	/// eight - network links are the readings that report themselves that way.
	/// </summary>
	private static double ToBytesPerSecond(double value, string suffix) => suffix switch
	{
		"b/s" or "b" => value,
		"kb/s" or "kib/s" => value * BytesPerKilobyte,
		"mb/s" or "mib/s" => value * BytesPerMegabyte,
		"gb/s" or "gib/s" => value * BytesPerGigabyte,
		"bps" => value / 8,
		"kbps" => value * 1000 / 8,
		"mbps" => value * 1000_000 / 8,
		"gbps" => value * 1000_000_000 / 8,
		_ => value,
	};

	private static double ToMegahertz(double value, string suffix) => suffix switch
	{
		"ghz" => value * 1000,
		"khz" => value / 1000,
		"hz" => value / 1_000_000,
		_ => value,
	};
}
