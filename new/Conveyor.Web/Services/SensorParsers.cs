using System.Globalization;
using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

public static class SensorParsers
{
    public static Dimension? ParseDimension(string frame)
    {
        var value = frame.Trim().Replace("Q00003", "", StringComparison.Ordinal);
        value = value.Trim('\u0002', '\u0003', '\r', '\n', ' ');
        if (value.Length != 16) return null;
        if (!int.TryParse(value[..4], out var status) ||
            !decimal.TryParse(value.AsSpan(4, 4), NumberStyles.Number, CultureInfo.InvariantCulture, out var length) ||
            !decimal.TryParse(value.AsSpan(8, 4), NumberStyles.Number, CultureInfo.InvariantCulture, out var width) ||
            !decimal.TryParse(value.AsSpan(12, 4), NumberStyles.Number, CultureInfo.InvariantCulture, out var height)) return null;
        return new Dimension(length / 10m, width / 10m, height / 10m, status);
    }

    public static decimal? ParseWeight(string frame, string protocol)
    {
        var value = frame.Trim('\u0002', '\u0003', '\r', '\n', ' ');
        value = protocol switch
        {
            "Fixed16From0" when value.Length >= 11 => value[..11].Trim(),
            "Fixed16From1" when value.Length >= 11 => value.Substring(1, 10).Trim(),
            _ => value
        };
        var numeric = new string(value.Where(character => char.IsDigit(character) || character is '.' or '-' or ',').ToArray())
            .Replace(',', '.');
        return decimal.TryParse(numeric, NumberStyles.Number, CultureInfo.InvariantCulture, out var weight) ? weight : null;
    }
}
