namespace SharpAstro.Ser;

/// <summary>
/// Helpers for SER timestamps. A SER timestamp is encoded exactly like a .NET <see cref="DateTime"/>
/// tick count: the number of 100-nanosecond intervals since 0001-01-01 00:00:00.
/// </summary>
public static class SerTimestamp
{
    /// <summary>Largest valid tick value (== <see cref="DateTime.MaxValue"/>.Ticks).</summary>
    public static long MaxValidTicks => DateTime.MaxValue.Ticks;

    /// <summary>
    /// Converts a raw SER tick value to a UTC <see cref="DateTimeOffset"/>. Per Raoul Behrend's
    /// finding, only the low 62 bits are defined; the two reserved most-significant bits are masked
    /// off and the result is clamped into the representable range so malformed values never throw.
    /// </summary>
    public static DateTimeOffset FromTicks(long ticks) => new(SanitizeTicks(ticks), TimeSpan.Zero);

    /// <summary>Ticks for a point in time, expressed in UTC (the form SER stores in its trailer).</summary>
    public static long ToTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    internal static long SanitizeTicks(long ticks)
    {
        ticks &= 0x3FFF_FFFF_FFFF_FFFFL; // clear the 2 reserved MSBs (62-bit value)
        return Math.Clamp(ticks, 0, MaxValidTicks);
    }
}
