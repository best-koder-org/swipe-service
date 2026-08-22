using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SwipeService.Commands;
using SwipeService.Common;
using SwipeService.Data;
using SwipeService.Models;
using SwipeService.Queries;
using SwipeService.Services;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwipeService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SwipesController : ControllerBase
    {
        private readonly SwipeContext _context;
        private readonly MatchmakingNotifier _notifier;
        private readonly ILogger<SwipesController> _logger;
        private readonly IMediator _mediator;
        private readonly IUserProfileResolver _profileResolver;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;

        public SwipesController(SwipeContext context, MatchmakingNotifier notifier, ILogger<SwipesController> logger, IMediator mediator, IUserProfileResolver profileResolver, IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _context = context;
            _notifier = notifier;
            _logger = logger;
            _mediator = mediator;
            _profileResolver = profileResolver;
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
        }

        // POST: Record a single swipe
        [HttpPost]
        [Authorize]
        public async Task<IActionResult> Swipe([FromBody] SwipeRequest request)
        {
            // Resolve the swiper's identity from the JWT.
            // The legacy `UserId` body field is ignored when a JWT is present —
            // this prevents callers from spoofing other users' swipes.
            var keycloakId = User.FindFirst("sub")?.Value
                          ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrWhiteSpace(keycloakId))
            {
                return Unauthorized(ApiResponse<SwipeResponse>.FailureResult("Missing 'sub' claim in JWT"));
            }

            // Forward the caller's bearer token to UserService for profile lookup.
            string? bearer = null;
            if (Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                var raw = authHeader.ToString();
                if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    bearer = raw.Substring("Bearer ".Length).Trim();
                }
            }
            if (string.IsNullOrWhiteSpace(bearer))
            {
                return Unauthorized(ApiResponse<SwipeResponse>.FailureResult("Missing bearer token"));
            }

            var profileId = await _profileResolver.ResolveProfileIdAsync(keycloakId, bearer, HttpContext.RequestAborted);
            if (profileId is null or 0)
            {
                // Fallback for internal bot callers: allow providing profile id via header when profile
                // resolution against UserService fails (dev-only convenience).
                if (Request.Headers.TryGetValue("X-Bot-ProfileId", out var headerVal) &&
                    int.TryParse(headerVal.ToString(), out var fallbackId) && fallbackId > 0)
                {
                    _logger.LogWarning("Profile resolution failed for keycloakId {KeycloakId}; using X-Bot-ProfileId fallback={FallbackId}", keycloakId, fallbackId);
                    profileId = fallbackId;
                }
                else
                {
                    return BadRequest(ApiResponse<SwipeResponse>.FailureResult("Could not resolve profile for caller"));
                }
            }

            // Self-healing: ensure the swiper has a profile mapping so the messaging
            // match check (MatchCheckController) can resolve this user later.
            await EnsureUserMappingAsync(keycloakId, profileId.Value, HttpContext.RequestAborted);

            // Translate Direction → IsLike when the modern field is supplied.
            var isLike = request.IsLike;
            if (!string.IsNullOrWhiteSpace(request.Direction))
            {
                isLike = request.Direction.Trim().ToLowerInvariant() switch
                {
                    "like" or "superlike" or "super_like" => true,
                    "pass" or "dislike" or "skip" => false,
                    _ => request.IsLike,
                };
            }

            // Bot-generated swipes carry the X-Bot-ProfileId header (bot-service always sends it).
            // Stamp the row so the targeted bot-data purge can identify it.
            var isBotGenerated = Request.Headers.ContainsKey("X-Bot-ProfileId");

            // Parse targetUserId (string from Flutter) to int profile ID.
            if (!int.TryParse(request.TargetUserId, out var targetProfileId))
            {
                return BadRequest(ApiResponse<SwipeResponse>.FailureResult(
                    $"Invalid TargetUserId '{request.TargetUserId}': expected a numeric profile ID."));
            }

            // ── Daily swipe limit gate (P1.5) ──
            var todayStart = DateTime.UtcNow.Date;
            var todaySwipeCount = await _context.Swipes
                .CountAsync(s => s.UserId == profileId.Value && s.CreatedAt >= todayStart);
            const int freeDailyLimit = 25;
            if (todaySwipeCount >= freeDailyLimit)
            {
                var isPremium = await CheckIsPremiumAsync(keycloakId);
                if (!isPremium)
                {
                    return StatusCode(402, ApiResponse<SwipeResponse>.FailureResult(
                        "Daily swipe limit reached. Upgrade to Premium for unlimited swipes."));
                }
            }

            var command = new RecordSwipeCommand
            {
                UserId = profileId.Value,
                TargetUserId = targetProfileId,
                IsLike = isLike,
                IdempotencyKey = request.IdempotencyKey,
                IsBotGenerated = isBotGenerated
            };

            var result = await _mediator.Send(command);

            if (result.IsFailure)
            {
                return BadRequest(ApiResponse<SwipeResponse>.FailureResult(result.Error!));
            }

            return Ok(ApiResponse<SwipeResponse>.SuccessResult(result.Value!));
        }

        // POST: Record multiple swipes in batch
        [HttpPost("batch")]
        public async Task<IActionResult> BatchSwipe([FromBody] BatchSwipeRequest request)
        {
            try
            {
                var responses = new List<SwipeResponse>();
                var matches = new List<Match>();

                foreach (var swipeAction in request.Swipes)
                {
                    // Parse string TargetUserId to int
                    if (!int.TryParse(swipeAction.TargetUserId, out var targetId))
                    {
                        responses.Add(new SwipeResponse
                        {
                            Success = false,
                            Message = $"Invalid TargetUserId '{swipeAction.TargetUserId}': expected numeric profile ID"
                        });
                        continue;
                    }

                    // Validate individual swipe
                    if (request.UserId == targetId)
                    {
                        responses.Add(new SwipeResponse
                        {
                            Success = false,
                            Message = $"Cannot swipe on yourself (Target: {swipeAction.TargetUserId})"
                        });
                        continue;
                    }

                    // Check if swipe already exists
                    var existingSwipe = await _context.Swipes
                        .FirstOrDefaultAsync(s => s.UserId == request.UserId && s.TargetUserId == targetId);

                    if (existingSwipe != null)
                    {
                        responses.Add(new SwipeResponse
                        {
                            Success = false,
                            Message = $"Already swiped on user {swipeAction.TargetUserId}"
                        });
                        continue;
                    }

                    var swipe = new Swipe
                    {
                        UserId = request.UserId,
                        TargetUserId = targetId,
                        IsLike = swipeAction.IsLike,
                        CreatedAt = DateTime.UtcNow
                    };

                    _context.Swipes.Add(swipe);

                    var response = new SwipeResponse
                    {
                        Success = true,
                        Message = $"Swipe recorded for user {swipeAction.TargetUserId}",
                        IsMutualMatch = false
                    };

                    // Check for mutual match if it's a like
                    if (swipeAction.IsLike)
                    {
                        var mutualSwipe = await _context.Swipes
                            .FirstOrDefaultAsync(s =>
                                s.UserId == targetId &&
                                s.TargetUserId == request.UserId &&
                                s.IsLike);

                        if (mutualSwipe != null)
                        {
                            var user1Id = Math.Min(request.UserId, targetId);
                            var user2Id = Math.Max(request.UserId, targetId);

                            var match = new Match
                            {
                                User1Id = user1Id,
                                User2Id = user2Id,
                                CreatedAt = DateTime.UtcNow
                            };

                            matches.Add(match);
                            _context.Matches.Add(match);

                            response.IsMutualMatch = true;
                            response.Message = $"It's a match with user {swipeAction.TargetUserId}!";
                        }
                    }

                    responses.Add(response);
                }

                await _context.SaveChangesAsync();

                // Notify matchmaking service for all matches
                foreach (var match in matches)
                {
                    await _notifier.NotifyMatchmakingServiceAsync(match.User1Id, match.User2Id);
                }

                return Ok(new { Responses = responses, TotalMatches = matches.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing batch swipes for user {UserId}", request.UserId);
                return StatusCode(500, new { Success = false, Message = "Internal server error" });
            }
        }

        // GET: Retrieve swipes by user with pagination and filtering
        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetSwipesByUser(int userId,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] bool? isLike = null)
        {
            var query = new GetSwipesByUserQuery
            {
                UserId = userId,
                Page = page,
                PageSize = pageSize,
                IsLike = isLike
            };

            var result = await _mediator.Send(query);

            if (result.IsFailure)
            {
                return StatusCode(500, ApiResponse<UserSwipeHistory>.FailureResult(result.Error!));
            }

            return Ok(ApiResponse<UserSwipeHistory>.SuccessResult(result.Value!));
        }

        // GET: Get matches for a user
        [HttpGet("matches/{userId}")]
        public async Task<IActionResult> GetMatchesForUser(int userId)
        {
            var query = new GetMatchesForUserQuery { UserId = userId };
            var result = await _mediator.Send(query);

            if (result.IsFailure)
            {
                return StatusCode(500, ApiResponse<List<MatchResult>>.FailureResult(result.Error!));
            }

            return Ok(ApiResponse<List<MatchResult>>.SuccessResult(result.Value!));
        }

        // GET: Retrieve users who liked a specific user
        [HttpGet("received-likes/{userId}")]
        public async Task<IActionResult> GetLikesReceivedByUser(int userId)
        {
            try
            {
                var likes = await _context.Swipes
                    .Where(s => s.TargetUserId == userId && s.IsLike)
                    .Select(s => new { UserId = s.UserId, LikedAt = s.CreatedAt })
                    .OrderByDescending(s => s.LikedAt)
                    .ToListAsync();

                return Ok(likes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving received likes for user {UserId}", userId);
                return StatusCode(500, new { Success = false, Message = "Internal server error" });
            }
        }

        // GET: Check if two users have a mutual match
        [HttpGet("match/{userId}/{targetUserId}")]
        public async Task<IActionResult> CheckMutualMatch(int userId, int targetUserId)
        {
            try
            {
                var user1Id = Math.Min(userId, targetUserId);
                var user2Id = Math.Max(userId, targetUserId);

                var match = await _context.Matches
                    .FirstOrDefaultAsync(m => m.User1Id == user1Id && m.User2Id == user2Id && m.IsActive);

                return Ok(new { IsMutualMatch = match != null, MatchId = match?.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking mutual match between {UserId} and {TargetUserId}", userId, targetUserId);
                return StatusCode(500, new { Success = false, Message = "Internal server error" });
            }
        }

        // DELETE: Unmatch users
        [HttpDelete("match/{userId}/{targetUserId}")]
        public async Task<IActionResult> Unmatch(int userId, int targetUserId)
        {
            var command = new UnmatchUsersCommand { UserId = userId, TargetUserId = targetUserId };
            var result = await _mediator.Send(command);

            if (result.IsFailure)
            {
                if (result.Error!.Contains("not found"))
                {
                    return NotFound(ApiResponse<object>.FailureResult(result.Error!));
                }
                return StatusCode(500, ApiResponse<object>.FailureResult(result.Error!));
            }

            return Ok(ApiResponse<object>.SuccessResult(new { Message = "Successfully unmatched" }));
        }

        // GET: Health check
        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            return Ok(new { Status = "Healthy", Service = "SwipeService", Timestamp = DateTime.UtcNow });
        }

        // GET: Get user profile mappings (ProfileId <-> Keycloak UUID)
        [HttpGet("user-mappings")]
        public async Task<IActionResult> GetUserMappings()
        {
            try
            {
                var mappings = await _context.UserProfileMappings
                    .Select(m => new { m.ProfileId, KeycloakUserId = m.UserId.ToString() })
                    .ToListAsync();
                return Ok(ApiResponse<object>.SuccessResult(mappings));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching user mappings");
                return StatusCode(500, "An error occurred while fetching user mappings");
            }
        }
        private record BillingStatusResponse(string UserId, string Tier, DateTime? ExpiresAt, bool IsPremium, int SparksBalance);

        /// <summary>
        /// Upsert the Keycloak-ID → Profile-ID mapping used by the messaging match check.
        /// Keeps the table self-healing for newly created profiles (humans + bots).
        /// ProfileId is the entity key, so it is NEVER modified — only missing mappings are
        /// inserted. Any failure is contained and the change tracker is reset so a failed
        /// upsert can never break the subsequent swipe save.
        /// </summary>
        private async Task EnsureUserMappingAsync(string keycloakId, int profileId, CancellationToken ct)
        {
            try
            {
                var existing = await _context.UserProfileMappings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.UserId == keycloakId, ct);
                if (existing != null)
                {
                    if (existing.ProfileId != profileId)
                    {
                        _logger.LogDebug(
                            "Mapping for {KeycloakId} already points to profile {Old}, ignoring {New}",
                            keycloakId, existing.ProfileId, profileId);
                    }
                    return; // never modify the key
                }

                // Avoid adding a row whose ProfileId key is already claimed.
                var byKey = await _context.UserProfileMappings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.ProfileId == profileId, ct);
                if (byKey != null)
                {
                    _logger.LogDebug("Profile {ProfileId} already mapped; skipping upsert", profileId);
                    return;
                }

                _context.UserProfileMappings.Add(new UserProfileMapping
                {
                    UserId = keycloakId,
                    ProfileId = profileId
                });
                await _context.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not upsert profile mapping for {KeycloakId}", keycloakId);
                // Reset the tracker so the swipe save below can never see a faulted entity.
                _context.ChangeTracker.Clear();
            }
        }

        private async Task<bool> CheckIsPremiumAsync(string keycloakId)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                var gatewayBase = _configuration["Gateway:BaseUrl"] ?? "http://localhost:8080";
                var apiKey = _configuration["InternalAuth:ApiKey"] ?? "";
                var req = new HttpRequestMessage(HttpMethod.Get, $"{gatewayBase}/api/billing/internal-status?userId={keycloakId}");
                req.Headers.Add("X-Internal-API-Key", apiKey);
                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return false;
                var json = await resp.Content.ReadFromJsonAsync<BillingStatusResponse>();
                return json?.IsPremium ?? false;
            }
            catch
            {
                return false;
            }
        }
    }
}
