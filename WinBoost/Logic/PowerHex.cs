using System.Globalization;
using System.Text.RegularExpressions;

namespace WinBoost.Logic;

/// <summary>
/// powercfg /q output is localized, so only 0x######## tokens are used.
/// The last two hex values of a setting block are Current AC then Current DC.
/// </summary>
public static partial class PowerHex
{
    [GeneratedRegex(@"0x([0-9a-fA-F]{8})")]
    private static partial Regex HexRx();

    public static (uint? Ac, uint? Dc) ParseAcDc(string? block)
    {
        if (string.IsNullOrEmpty(block)) return (null, null);
        var hex = HexRx().Matches(block);
        if (hex.Count < 2) return (null, null);
        uint ac = uint.Parse(hex[^2].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        uint dc = uint.Parse(hex[^1].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (ac, dc);
    }
}
