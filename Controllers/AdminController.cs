using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwipeService.Data;

namespace SwipeService.Controllers;

/// <summary>
/// Dev/staging-only administrative reset endpoints.
/// Used to wipe interaction data so a clean MVP demo can begin.
/// All endpoints reject calls in Production via IWebHostEnvironment guard.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly SwipeContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AdminController> _logger;

    public AdminController(SwipeContext context, IWebHostEnvironment env, ILogger<AdminController> logger)
    {
        _context = context;
        _env = env;
        _logger = logger;
    }

    private bool IsResetAllowed() =>
        _env.IsDevelopment() || _env.IsStaging() || _env.EnvironmentName == "Demo";

    /// <summary>Wipe all swipes and matches. Dev/Staging/Demo only.</summary>
    [HttpDelete("swipes")]
    public async Task<IActionResult> ResetAllSwipes()
    {
        if (!IsResetAllowed())
        {
            _logger.LogWarning("Admin reset rejected: environment={Env} is not dev/staging/demo", _env.EnvironmentName);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Admin reset disabled in this environment." });
        }

        var swipes = await _context.Swipes.ToListAsync();
        var matches = await _context.Matches.ToListAsync();
        var swipeCount = swipes.Count;
        var matchCount = matches.Count;

        _context.Matches.RemoveRange(matches);
        _context.Swipes.RemoveRange(swipes);
        await _context.SaveChangesAsync();

        _logger.LogWarning(
            "[FINDING] High AdminReset: cleared {SwipeCount} swipes and {MatchCount} matches by {User}",
            swipeCount, matchCount, User.Identity?.Name ?? "unknown");

        return Ok(new
        {
            message = "Swipes and matches cleared.",
            deletedSwipes = swipeCount,
            deletedMatches = matchCount,
            environment = _env.EnvironmentName,
        });
    }

    /// <summary>
    /// Targeted purge: deletes ONLY bot-generated swipes and matches (IsBotGenerated=true).
    /// Real-user interactions are never touched. Dev/Staging/Demo only.
    /// </summary>
    [HttpDelete("bot-swipe-data")]
    public async Task<IActionResult> ResetBotSwipes([FromQuery] int olderThanHours = 0)
    {
        if (!IsResetAllowed())
        {
            _logger.LogWarning("Admin reset rejected: environment={Env} is not dev/staging/demo", _env.EnvironmentName);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Admin reset disabled in this environment." });
        }

        // Optional TTL filter: only purge bot rows older than N hours (keeps a tester's active convo).
        var cutoff = olderThanHours > 0 ? DateTime.UtcNow.AddHours(-olderThanHours) : (DateTime?)null;
        var botSwipes = await _context.Swipes
            .Where(s => s.IsBotGenerated && (cutoff == null || s.CreatedAt < cutoff)).ToListAsync();
        var botMatches = await _context.Matches
            .Where(m => m.IsBotGenerated && (cutoff == null || m.CreatedAt < cutoff)).ToListAsync();
        var swipeCount = botSwipes.Count;
        var matchCount = botMatches.Count;

        _context.Matches.RemoveRange(botMatches);
        _context.Swipes.RemoveRange(botSwipes);
        await _context.SaveChangesAsync();

        _logger.LogWarning(
            "[FINDING] Medium AdminReset: cleared {SwipeCount} bot swipes and {MatchCount} bot matches by {User}",
            swipeCount, matchCount, User.Identity?.Name ?? "unknown");

        return Ok(new
        {
            message = "Bot-generated swipes and matches cleared.",
            deletedSwipes = swipeCount,
            deletedMatches = matchCount,
            environment = _env.EnvironmentName,
        });
    }

    /// <summary>
    /// Idempotent upsert of bot profile mappings from bot-service. Called on bot-service
    /// startup so the messaging match check always has correct keycloakId → profileId
    /// mappings (eliminates the manual mapping-repair step when bots 403 on messages).
    /// Bot profile IDs are distinct from human profile IDs, so deleting by ProfileId here
    /// can only ever remove a bot's own stale row.
    /// </summary>
    [HttpPost("sync-bot-mappings")]
    public async Task<IActionResult> SyncBotMappings([FromBody] BotMappingSyncRequest request, CancellationToken ct = default)
    {
        if (!IsResetAllowed())
        {
            _logger.LogWarning("Admin reset rejected: environment={Env} is not dev/staging/demo", _env.EnvironmentName);
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Admin reset disabled in this environment." });
        }

        if (request?.Mappings == null || request.Mappings.Count == 0)
        {
            return Ok(new { synced = 0, total = 0 });
        }

        var synced = 0;
        foreach (var m in request.Mappings)
        {
            if (m.ProfileId <= 0 || string.IsNullOrWhiteSpace(m.KeycloakId)) continue;

            // Remove any stale row for this bot (either wrong keycloakId or wrong profileId),
            // then insert the authoritative mapping. Raw SQL avoids EF change-tracker issues
            // (ProfileId is the entity key and must never be modified in-memory).
            await _context.Database.ExecuteSqlRawAsync(
                "DELETE FROM UserProfileMappings WHERE UserId = {0} OR ProfileId = {1}",
                new object[] { m.KeycloakId, m.ProfileId }, ct);
            await _context.Database.ExecuteSqlRawAsync(
                "INSERT INTO UserProfileMappings (ProfileId, UserId, CreatedAt) VALUES ({0}, {1}, UTC_TIMESTAMP())",
                new object[] { m.ProfileId, m.KeycloakId }, ct);
            synced++;
        }

        _logger.LogInformation("Synced {Synced}/{Total} bot profile mappings", synced, request.Mappings.Count);
        return Ok(new { synced, total = request.Mappings.Count });
    }
}

public class BotMappingSyncRequest
{
    public List<BotMappingItem> Mappings { get; set; } = new();
}

public class BotMappingItem
{
    public int ProfileId { get; set; }
    public string KeycloakId { get; set; } = string.Empty;
}
