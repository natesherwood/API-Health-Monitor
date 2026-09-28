namespace RateMyApi.Core.Stats;

public static class Percentile
{
    /// <summary>
    /// Discrete percentile with exactly PostgreSQL's <c>percentile_disc</c> semantics: the first
    /// value, in ascending order, whose position fraction is &gt;= <paramref name="fraction"/>
    /// (row = ceil(fraction * n), at least 1). Nulls are ignored, as the SQL aggregate does.
    /// Returns null when there are no non-null values.
    /// </summary>
    public static int? Disc(IEnumerable<int?> values, double fraction)
    {
        if (double.IsNaN(fraction) || fraction < 0 || fraction > 1)
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Fraction must be within [0, 1].");

        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (sorted.Length == 0)
            return null;

        Array.Sort(sorted);
        // Same double arithmetic as percentile_disc_final: rownum = (int64) ceil(percentile * rowcount).
        var row = (long)Math.Ceiling(fraction * sorted.Length);
        return sorted[Math.Max(row, 1) - 1];
    }

    public static int? Disc(IEnumerable<int> values, double fraction) =>
        Disc(values.Select(v => (int?)v), fraction);

    public static int? P95(IEnumerable<int?> values) => Disc(values, 0.95);
}
