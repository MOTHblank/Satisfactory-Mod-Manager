namespace SatisfactoryModManager;

internal sealed record FicsitInstallRequest(string ModId, string Version)
{
    public static bool TryParse(string argument, out FicsitInstallRequest request)
    {
        request = new FicsitInstallRequest(string.Empty, string.Empty);

        if (!Uri.TryCreate(argument, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, "smmanager", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "install", StringComparison.OrdinalIgnoreCase))
            return false;

        var query = ParseQuery(uri.Query);
        query.TryGetValue("modID", out var modId);
        if (string.IsNullOrWhiteSpace(modId))
            query.TryGetValue("modid", out modId);
        query.TryGetValue("version", out var version);

        if (!IsSafeValue(modId, 100))
            return false;
        if (!string.IsNullOrWhiteSpace(version) && !IsSafeValue(version, 100))
            return false;

        request = new FicsitInstallRequest(modId!, version ?? string.Empty);
        return true;
    }

    internal static bool IsSafeValue(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            return false;
        return value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '+');
    }

    internal static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            result[key] = value;
        }

        return result;
    }
}
