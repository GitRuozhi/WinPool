using System.Globalization;

namespace WinPool.App.Services;

/// <summary>Entry rules shared by integer capacity and count controls.</summary>
public static class IntegerInputPolicy
{
    public static bool ContainsOnlyDigits(string text) => text.All(c => c is >= '0' and <= '9');

    public static string Clamp(string text, long minimum, long? maximum, bool completeEmpty = false,
        bool emptyUsesMaximum = false)
    {
        minimum = Math.Max(0, minimum);
        var upper = Math.Max(minimum, maximum ?? long.MaxValue);
        if (text.Length == 0)
            return completeEmpty
                ? (emptyUsesMaximum && maximum.HasValue ? upper : minimum).ToString(CultureInfo.InvariantCulture)
                : text;
        if (!ContainsOnlyDigits(text)) return text;
        // Overflowing digit strings are above every supported bound, not malformed input.
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return upper.ToString(CultureInfo.InvariantCulture);
        var bounded = Math.Clamp(value, minimum, upper);
        return bounded == value ? text : bounded.ToString(CultureInfo.InvariantCulture);
    }
}
