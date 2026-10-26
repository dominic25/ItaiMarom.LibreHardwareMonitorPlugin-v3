namespace ItaiMarom.LibreHardwareMonitorPlugin.Hardware;

/// <summary>
/// The span a control accepts, copied from the hardware library's own minimum and maximum software value.
/// </summary>
/// <remarks>
/// A fan is offered as a percentage, and the host's Slider widget takes its track from the reading rather
/// than from the definition, so the number the chip actually accepts has to reach the reading somehow.
/// It travels with the sample because it comes from the same <c>IControl</c> the value does - asking for
/// it separately would mean a second walk of the hardware tree to learn something the first one already
/// had. It is not part of a sensor's identity and nothing keys on it.
/// </remarks>
/// <param name="Min">The lowest value <c>SetSoftware</c> will be held to, in percent.</param>
/// <param name="Max">The highest value <c>SetSoftware</c> will be held to, in percent.</param>
public readonly record struct ControlRange(float Min, float Max);
