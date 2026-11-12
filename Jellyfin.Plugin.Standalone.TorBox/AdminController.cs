using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Standalone.TorBox;

/// <summary>
/// The two buttons on the settings page: check the key, run a sync now. Both
/// are admin-only, because both act on the server's files and the user's key.
/// </summary>
[ApiController]
[Route("StandaloneTorbox")]
[Authorize(Policy = MediaBrowser.Common.Api.Policies.RequiresElevation)]
public sealed class AdminController : ControllerBase
{
    private readonly StandaloneSync _sync;
    private readonly DebridClientFactory _clients;

    /// <summary>Initializes a new instance of the <see cref="AdminController"/> class.</summary>
    /// <param name="sync">The scheduled task.</param>
    /// <param name="clients">The provider factory.</param>
    public AdminController(StandaloneSync sync, DebridClientFactory clients)
    {
        _sync = sync;
        _clients = clients;
    }

    /// <summary>Checks the configured debrid key.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The health, as a message for the page.</returns>
    [HttpPost("probe")]
    public async Task<IActionResult> Probe(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.DebridApiKey))
        {
            return BadRequest(new { ok = false, message = "Enter your debrid API key first." });
        }

        var client = _clients.Create(configuration);
        if (client is null)
        {
            return BadRequest(new { ok = false, message = DebridClientFactory.UnsupportedMessage(configuration.Provider) });
        }

        var health = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var message = health switch
        {
            Providers.ProviderHealth.Healthy => "Key works.",
            Providers.ProviderHealth.InvalidKey => "That key was rejected. Check it and try again.",
            Providers.ProviderHealth.RateLimited => "The account is rate limited. Try again in a few minutes.",
            _ => "Could not reach the provider. Check the server's connection.",
        };
        return Ok(new { ok = health == Providers.ProviderHealth.Healthy, message });
    }

    /// <summary>Runs a sync immediately.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts, or why nothing changed.</returns>
    [HttpPost("sync")]
    public async Task<IActionResult> RunSync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return BadRequest(new { ok = false, message = "Standalone is not loaded." });
        }

        var outcome = await _sync.RunAsync(configuration, cancellationToken).ConfigureAwait(false);
        var message = outcome.Ok
            ? $"Added {outcome.Result!.Added}, updated {outcome.Result.Updated}, removed {outcome.Result.Removed}, unchanged {outcome.Result.Unchanged}, skipped {outcome.Result.Skipped}."
            : outcome.Error ?? "The sync could not run.";
        return Ok(new { ok = outcome.Ok, message });
    }
}
