// Licensed to Shonkor under the MIT License.

using System.Text.Json;

namespace Shonkor.Plugin.TypeScript;

/// <summary>
/// Self-provisioned configuration for the Node sidecar, read from <c>sidecar.settings.json</c> in the
/// plugin folder. Deliberately tiny: the path to node (or null to auto-discover) and the per-request
/// parse timeout.
/// </summary>
internal sealed record SidecarSettings
{
    /// <summary>Absolute path to the node executable, or null to auto-discover (PATH, then common locations).</summary>
    public string? NodePath { get; init; }

    /// <summary>Per-request parse budget in seconds before a timeout diagnostic fires. Defaults to 30.</summary>
    public double? TimeoutSeconds { get; init; }

    /// <summary>The hard-coded fallback used when the settings file is absent or omits the value.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resolves the effective timeout with the same precedence spirit as
    /// <c>OllamaClientFactory.ApplyTimeout</c>: a configured positive value wins, otherwise the fallback.
    /// </summary>
    public TimeSpan ResolveTimeout() =>
        TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(TimeoutSeconds.Value) : DefaultTimeout;

    /// <summary>
    /// Loads settings from <paramref name="pluginDirectory"/>. A missing or malformed file is not fatal —
    /// the plugin must load and then degrade at runtime if needed — so defaults are returned instead.
    /// </summary>
    public static SidecarSettings Load(string pluginDirectory)
    {
        try
        {
            var path = Path.Combine(pluginDirectory, "sidecar.settings.json");
            if (!File.Exists(path)) return new SidecarSettings();
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<SidecarSettings>(json, JsonOptions);
            return parsed ?? new SidecarSettings();
        }
        catch
        {
            return new SidecarSettings();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
