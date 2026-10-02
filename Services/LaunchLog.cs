using System.Text.RegularExpressions;

namespace AnotherDSHL.Services;

internal static class LaunchLog
{
    public static string Sanitize(string line)
    {
        line = Regex.Replace(line, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        line = Regex.Replace(line, @"(?i)(authorization\s*[:=]\s*)(?:Bearer|Basic)\s+[^\s,;]+", "$1[已隐藏]");
        line = Regex.Replace(line, @"(https?://[^\s?#]+)\?[^\s]+", "$1?[参数已隐藏]", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"(?i)((?:token|api[_-]?key|authorization|password|_authToken)[""']?\s*[=:]\s*[""']?)[^\s,;""']+", "$1[已隐藏]");
        return line;
    }
}
