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
}
