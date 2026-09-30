using System.Text.RegularExpressions;

namespace AnotherDSHL.Installer.Core;

public static class ReleaseVersion
{
    public static bool IsValid(string? value) => value is not null && Regex.IsMatch(value,
        @"^v?\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$") &&
        Version.TryParse(value.TrimStart('v').Split('+')[0].Split('-')[0], out _);

    public static int Compare(string left, string right)
    {
        static (Version Version, string[] Pre) Parse(string text)
        {
            if (!IsValid(text)) throw new InvalidDataException("版本号无效：" + text);
            var parts = text.TrimStart('v').Split('+')[0].Split('-', 2);
            var version = Version.Parse(parts[0]);
            return (new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision)),
                parts.Length == 2 ? parts[1].Split('.') : []);
        }
        var a = Parse(left); var b = Parse(right);
        var result = a.Version.CompareTo(b.Version);
        if (result != 0) return result;
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
            return a.Pre.Length == b.Pre.Length ? 0 : a.Pre.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            bool an = a.Pre[i].All(char.IsAsciiDigit), bn = b.Pre[i].All(char.IsAsciiDigit);
            var av = a.Pre[i].TrimStart('0'); var bv = b.Pre[i].TrimStart('0');
            result = an && bn ? (av.Length != bv.Length ? av.Length.CompareTo(bv.Length) : string.CompareOrdinal(av, bv))
                : an != bn ? an ? -1 : 1 : string.CompareOrdinal(a.Pre[i], b.Pre[i]);
            if (result != 0) return result;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }
}
