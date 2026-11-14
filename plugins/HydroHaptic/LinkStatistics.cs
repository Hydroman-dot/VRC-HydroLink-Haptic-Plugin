using System.Globalization;

namespace HydroHaptic;

internal readonly record struct LinkStatistics(bool Seen, uint Boot, ulong Received, ulong Missing, uint Age)
{
    internal double LossPercent => Received == 0 && Missing == 0 ? 0 : 100.0 * Missing / ((double)Received + Missing);
    internal static bool TryParse(string line, out LinkStatistics result)
    {
        result = default;
        var p = line.Split(' ');
        if (p.Length != 7 || p[0] != "LINK" || p[1] != "1") return false;
        var n = new ulong[5];
        for (int i = 0; i < n.Length; i++)
            if (!ulong.TryParse(p[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        if (n[0] > 1 || n[1] > uint.MaxValue || n[4] > uint.MaxValue ||
            (n[0] == 1 && n[2] == 0) || (n[0] == 0 && (n[2] != 0 || n[3] != 0))) return false;
        result = new(n[0] != 0, (uint)n[1], n[2], n[3], (uint)n[4]);
        return true;
    }
}
