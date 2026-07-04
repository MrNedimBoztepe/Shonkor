// Licensed to Shonkor under the MIT License.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shonkor.Web.Services;

using static Shonkor.Web.EndpointHelpers;

namespace Shonkor.Web.Endpoints;

/// <summary>
/// Reads and writes the dashboard-editable AI/tool settings (Ollama URLs/models, embedding source,
/// streaming, semantic-C# default, enrichment batch sizes). Writes go to <c>appsettings.Local.json</c>
/// (loaded with reloadOnChange), so most changes take effect on the next request without a restart.
/// Writing changes server behaviour, so it is <b>loopback-only</b> and opt-in outside Development —
/// mirroring the filesystem-browser endpoint. Secrets are never exposed or written here.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        // GET /api/settings — current effective AI settings (loopback-only; non-secret operational config).
        app.MapGet("/api/settings", (HttpContext context, IConfiguration config) =>
        {
            if (!context.IsLoopback())
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return Results.Ok(SettingsStore.Read(config));
        });

        // POST /api/settings — persist a partial update. Loopback-only + opt-in outside Development.
        app.MapPost("/api/settings", async (AiSettings update, HttpContext context, IConfiguration config, IHostEnvironment env) =>
        {
            var allowWrite = config.GetValue<bool?>("Security:AllowSettingsWrite") ?? env.IsDevelopment();
            if (!allowWrite || !context.IsLoopback())
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Validate the small, closed value set before persisting.
            if (update.EmbeddingSource is { } src && src is not ("code" or "summary"))
            {
                return Results.BadRequest("Embedding source must be 'code' or 'summary'.");
            }
            if (update.EnrichmentBatchSize is { } bs && bs < 1)
            {
                return Results.BadRequest("Enrichment batch size must be >= 1.");
            }
            if (update.EnrichmentMaxParallelism is { } mp && mp < 1)
            {
                return Results.BadRequest("Enrichment parallelism must be >= 1.");
            }
            if (update.DriftReconcileIntervalSeconds is { } di && di < 0)
            {
                return Results.BadRequest("Drift interval must be >= 0.");
            }

            try
            {
                var localPath = Path.Combine(env.ContentRootPath, "appsettings.Local.json");
                SettingsStore.Write(localPath, update);
                // Don't read back from IConfiguration here — reloadOnChange is debounced, so a re-read could
                // race and echo pre-save values. Echo the accepted update (authoritative) and let the client
                // GET /api/settings later for the reloaded effective values.
                return Results.Ok(new
                {
                    saved = true,
                    applied = update,
                    note = "Saved. Most settings apply on the next request/enrichment cycle. " +
                           "Drift interval changes need a restart (the reconciliation worker is wired at startup)."
                });
            }
            catch (Exception ex)
            {
                return Fail("Failed to save settings.", ex);
            }
        });
    }
}
