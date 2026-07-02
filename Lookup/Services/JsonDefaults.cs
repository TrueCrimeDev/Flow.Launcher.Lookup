using System.Text.Json;

namespace Lookup.Services;

/// <summary>The plugin's one on-disk JSON dialect, shared by datasets and config:
/// snake_case keys, case-insensitive binding, comments and trailing commas allowed.</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions SnakeCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
