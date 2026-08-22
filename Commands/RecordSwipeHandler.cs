using SwipeService.Metrics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SwipeService.Common;
using SwipeService.Data;
using SwipeService.Models;
using SwipeService.Services;

namespace SwipeService.Commands;

/// <summary>
/// Handles RecordSwipeCommand with integrated behavior analysis (T186 velocity + T189 circuit breaker).
/// </summary>
public class RecordSwipeHandler : IRequestHandler<RecordSwipeCommand, Result<SwipeResponse>>
{
    private readonly SwipeContext _context;
    private readonly MatchmakingNotifier _notifier;
    private readonly ILogger<RecordSwipeHandler> _logger;
    private readonly IRateLimitService? _rateLimitService;
    private readonly ISwipeBehaviorAnalyzer? _behaviorAnalyzer;
    private readonly SwipeServiceMetrics? _metrics;

    public RecordSwipeHandler(
        SwipeContext context,
        MatchmakingNotifier notifier,
        ILogger<RecordSwipeHandler> logger,
        IRateLimitService? rateLimitService = null,
        ISwipeBehaviorAnalyzer? behaviorAnalyzer = null,
        SwipeServiceMetrics? metrics = null)
    {
        _context = context;
        _notifier = notifier;
        _logger = logger;
        _rateLimitService = rateLimitService;
        _behaviorAnalyzer = behaviorAnalyzer;
        _metrics = metrics;
    }

    public async Task<Result<SwipeResponse>> Handle(RecordSwipeCommand request, CancellationToken cancellationToken)
    {
        try
        {
            // Business Rule: Cannot swipe on yourself
            if (request.UserId == request.TargetUserId)
            {
                return Result<SwipeResponse>.Failure("Cannot swipe on yourself");
            }

            // T189: Circuit breaker — check if user is in cooldown
            if (_behaviorAnalyzer != null)
            {
                var (inCooldown, cooldownUntil) = await _behaviorAnalyzer.CheckCooldownAsync(request.UserId);
                if (inCooldown)
                {
                    _logger.LogWarning("User {UserId} blocked by circuit breaker until {CooldownUntil}",
                        request.UserId, cooldownUntil);
                    return Result<SwipeResponse>.Failure(
                        $"Too many consecutive likes. Please wait until {cooldownUntil:HH:mm} UTC.");
                }
            }

            // Rate Limiting: Check daily swipe limits
            if (_rateLimitService != null)
            {
                var (isAllowed, remaining, limit) = await _rateLimitService.CheckDailyLimitAsync(request.UserId, request.IsLike);
                if (!isAllowed)
                {
                    _metrics?.RateLimited();
                    return Result<SwipeResponse>.Failure(
                        $"Daily {(request.IsLike ? "like" : "swipe")} limit reached ({limit} per day)");
                }
            }

            // T186: Velocity / suspicious pattern check before processing
            if (_behaviorAnalyzer != null)
            {
                var (isSuspicious, reason) = await _behaviorAnalyzer.IsSwipeSuspiciousAsync(request.UserId, request.IsLike);
                if (isSuspicious)
                {
                    _logger.LogWarning("Suspicious swipe from user {UserId}: {Reason}", request.UserId, reason);
                    // Shadow-log but still allow — the trust score penalty handles the consequence
                }
            }

            // Idempotency: Check if swipe with this idempotency key already exists
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                var existingIdempotentSwipe = await _context.Swipes
                    .Include(s => s.Match)
                    .FirstOrDefaultAsync(s => s.IdempotencyKey == request.IdempotencyKey, cancellationToken);

                if (existingIdempotentSwipe != null)
                {
                    // Return original result for idempotent retry
                    var idempotentResponse = new SwipeResponse
                    {
                        Success = true,
                        Message = "Swipe already processed (idempotent)",
                        IsMutualMatch = existingIdempotentSwipe.Match != null,
                        MatchId = existingIdempotentSwipe.Match?.Id ?? 0
                    };
                    return Result<SwipeResponse>.Success(idempotentResponse);
                }
            }

            // Business Rule: Check if swipe already exists (by user + target)
            var existingSwipe = await _context.Swipes
                .FirstOrDefaultAsync(s => s.UserId == request.UserId && s.TargetUserId == request.TargetUserId, cancellationToken);

            if (existingSwipe != null)
            {
                return Result<SwipeResponse>.Failure("Already swiped on this user");
            }

            // Create swipe record
            var swipe = new Swipe
            {
                UserId = request.UserId,
                TargetUserId = request.TargetUserId,
                IsLike = request.IsLike,
                CreatedAt = DateTime.UtcNow,
                IdempotencyKey = request.IdempotencyKey,
                IsBotGenerated = request.IsBotGenerated
            };

            _context.Swipes.Add(swipe);
            await _context.SaveChangesAsync(cancellationToken);
            _metrics?.SwipeProcessed();
            if (request.IsLike) _metrics?.Like(); else _metrics?.Pass();

            // Increment daily rate limit counter
            if (_rateLimitService != null)
            {
                await _rateLimitService.IncrementSwipeCountAsync(request.UserId);
            }

            // T186: Update behavioral stats after recording the swipe
            if (_behaviorAnalyzer != null)
            {
                try
                {
                    await _behaviorAnalyzer.UpdateStatsOnSwipeAsync(request.UserId, request.IsLike);
                }
                catch (Exception ex)
                {
                    // Non-critical: don't fail the swipe if stats update fails
                    _logger.LogError(ex, "Failed to update behavior stats for user {UserId}", request.UserId);
                }
            }

            var response = new SwipeResponse
            {
                Success = true,
                Message = "Swipe recorded successfully",
                IsMutualMatch = false
            };

            // Check for mutual match if it's a like
            if (request.IsLike)
            {
                var mutualSwipe = await _context.Swipes
                    .FirstOrDefaultAsync(s =>
                        s.UserId == request.TargetUserId &&
                        s.TargetUserId == request.UserId &&
                        s.IsLike, cancellationToken);

                if (mutualSwipe != null)
                {
                    var user1Id = Math.Min(request.UserId, request.TargetUserId);
                    var user2Id = Math.Max(request.UserId, request.TargetUserId);

                    var match = new Match
                    {
                        User1Id = user1Id,
                        User2Id = user2Id,
                        CreatedAt = DateTime.UtcNow,
                        // A match is bot-generated if either participant is a bot
                        IsBotGenerated = request.IsBotGenerated || mutualSwipe.IsBotGenerated
                    };

                    _context.Matches.Add(match);
                    await _context.SaveChangesAsync(cancellationToken);

                    response.IsMutualMatch = true;
                    response.MatchId = match.Id;
                    response.Message = "It's a match!";
                    _metrics?.MutualMatch();

                    // Notify matchmaking service
                    await _notifier.NotifyMatchmakingServiceAsync(request.UserId, request.TargetUserId);

                    _logger.LogInformation("Match created between users {UserId} and {TargetUserId}",
                        request.UserId, request.TargetUserId);
                }
            }

            return Result<SwipeResponse>.Success(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing swipe for user {UserId} on target {TargetUserId}",
                request.UserId, request.TargetUserId);
            return Result<SwipeResponse>.Failure($"Error processing swipe: {ex.Message}");
        }
    }
}
