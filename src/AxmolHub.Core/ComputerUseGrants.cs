namespace AxmolHub.Core;

/// <summary>Canonical application keys used by Computer Use grants.</summary>
internal static class ComputerUseGrants
{
    public static string ApplicationFromArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("application", out var value)
                && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? NormalizeKey(value.GetString())
                : "";
        }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    public static string ApplicationForTool(string? toolName, string? json)
    {
        if (toolName == ToolNames.ComputerUse) return ApplicationFromArguments(json);
        if (toolName != ToolNames.CaptureScreen || string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("target", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? NormalizeKey(value.GetString()) : "";
        }
        catch (System.Text.Json.JsonException) { return ""; }
    }

    public static bool IsComputerUseTool(string? name) => name is ToolNames.ComputerUse or ToolNames.CaptureScreen;

    public static string NormalizeKey(string? value)
        => (value ?? "").Trim().Normalize(System.Text.NormalizationForm.FormC);

    public static List<string> Normalize(IEnumerable<string>? values)
        => (values ?? [])
            .Select(NormalizeKey)
            .Where(value => value.Length is > 0 and <= 256)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool Contains(IEnumerable<string>? values, string? app)
    {
        var key = NormalizeKey(app);
        return key.Length > 0 && (values ?? []).Any(value =>
            string.Equals(NormalizeKey(value), key, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Add(List<string> values, string? app)
    {
        var key = NormalizeKey(app);
        if (key.Length is 0 or > 256 || Contains(values, key)) return false;
        values.Add(key);
        return true;
    }

    public static bool Remove(List<string> values, string? app)
    {
        var key = NormalizeKey(app);
        var index = values.FindIndex(value =>
            string.Equals(NormalizeKey(value), key, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        values.RemoveAt(index);
        return true;
    }
}
