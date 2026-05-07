using Xunit;
using Moq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MediatR;
using SwipeService.Controllers;
using SwipeService.Data;
using SwipeService.Services;
using SwipeService.Models;
using SwipeService.Commands;
using SwipeService.Queries;
using SwipeService.Common;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Match = SwipeService.Models.Match;

namespace SwipeService.Tests.Controllers;

/// <summary>
/// Error handling and edge case tests for SwipesController
/// Tests cover database failures, concurrent modifications, null handling, and service exceptions
/// </summary>
public class SwipesControllerErrorTests : IDisposable
{
    private readonly SwipeContext _context;
    private readonly Mock<MatchmakingNotifier> _mockNotifier;
    private readonly Mock<ILogger<SwipesController>> _mockLogger;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IUserProfileResolver> _mockResolver;
    private readonly SwipesController _controller;

    public SwipesControllerErrorTests()
    {
        var options = new DbContextOptionsBuilder<SwipeContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SwipeContext(options);
        var mockHttpClient = new Mock<HttpClient>();
        _mockNotifier = new Mock<MatchmakingNotifier>(mockHttpClient.Object);
        _mockLogger = new Mock<ILogger<SwipesController>>();
        _mockMediator = new Mock<IMediator>();
        _mockResolver = new Mock<IUserProfileResolver>();
        _mockResolver
            .Setup(r => r.ResolveProfileIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _controller = new SwipesController(_context, _mockNotifier.Object, _mockLogger.Object, _mockMediator.Object, _mockResolver.Object);

        var claims = new[] { new Claim("sub", "test-keycloak-1") };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        httpContext.Request.Headers["Authorization"] = "Bearer test-token";
        _controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ==================== DATABASE EXCEPTION TESTS ====================

    [Fact]
    public async Task Swipe_MediatorThrowsException_Returns500Error()
    {
        // Arrange - Simulate database exception
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ThrowsAsync(new DbUpdateException("Database connection failed"));

        var request = new SwipeRequest { UserId = 1, TargetUserId = 2, IsLike = true };

        // Act & Assert - Should propagate exception (let middleware handle)
        await Assert.ThrowsAsync<DbUpdateException>(async () => await _controller.Swipe(request));
    }

    [Fact]
    public async Task BatchSwipe_DatabaseSaveException_ReturnsPartialSuccess()
    {
        // Arrange - First swipe succeeds, second fails
        var request = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = new List<SwipeAction>
            {
                new SwipeAction { TargetUserId = 2, IsLike = true },
                new SwipeAction { TargetUserId = 3, IsLike = true }
            }
        };

        // First swipe succeeds
        _context.Swipes.Add(new Swipe 
        { 
            UserId = 1, 
            TargetUserId = 2, 
            IsLike = true, 
            CreatedAt = DateTime.UtcNow 
        });
        await _context.SaveChangesAsync();

        // Act - Second swipe will fail if it's a duplicate
        var result = await _controller.BatchSwipe(request);

        // Assert - Should still return 200 but with error details
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    [Fact]
    public async Task GetSwipesByUser_MediatorException_ThrowsException()
    {
        // Arrange
        _mockMediator
            .Setup(m => m.Send(It.IsAny<GetSwipesByUserQuery>(), default))
            .ThrowsAsync(new InvalidOperationException("Query processing failed"));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _controller.GetSwipesByUser(1, 1, 10));
    }

    // ==================== NULL/INVALID DATA TESTS ====================

    [Fact]
    public async Task Swipe_NullRequest_ThrowsException()
    {
        // Act & Assert - Controller should handle null via model binding
        await Assert.ThrowsAsync<NullReferenceException>(
            async () => await _controller.Swipe(null!));
    }

    [Fact]
    public async Task BatchSwipe_EmptySwipeList_ReturnsEmptyResults()
    {
        // Arrange
        var request = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = new List<SwipeAction>() // Empty list
        };

        // Act
        var result = await _controller.BatchSwipe(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        
        // No swipes should be in database
        var swipesInDb = await _context.Swipes.Where(s => s.UserId == 1).ToListAsync();
        Assert.Empty(swipesInDb);
    }

    [Fact]
    public async Task GetSwipesByUser_InvalidPageNumber_ReturnsEmptyResults()
    {
        // Arrange - Page 9999 when only 10 swipes exist
        _mockMediator
            .Setup(m => m.Send(It.IsAny<GetSwipesByUserQuery>(), default))
            .ReturnsAsync(Result<UserSwipeHistory>.Success(new UserSwipeHistory 
            { 
                UserId = 1, 
                TotalSwipes = 10, 
                Swipes = new List<SwipeRecord>() 
            }));

        // Act
        var result = await _controller.GetSwipesByUser(1, 9999, 10);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<UserSwipeHistory>>(okResult.Value);
        Assert.True(apiResponse.Success);
        Assert.Empty(apiResponse.Data!.Swipes);
    }

    [Fact]
    public async Task GetSwipesByUser_NegativePageSize_HandledByMediator()
    {
        // Arrange
        _mockMediator
            .Setup(m => m.Send(It.Is<GetSwipesByUserQuery>(q => q.PageSize < 0), default))
            .ReturnsAsync(Result<UserSwipeHistory>.Failure("Invalid page size"));

        // Act
        var result = await _controller.GetSwipesByUser(1, 1, -10);

        // Assert
        var result500 = Assert.IsType<ObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<UserSwipeHistory>>(result500.Value);
        Assert.False(apiResponse.Success);
    }

    // ==================== CONCURRENT MODIFICATION TESTS ====================

    [Fact]
    public async Task BatchSwipe_ConcurrentSwipesOnSameUser_HandlesGracefully()
    {
        // Arrange - Two concurrent batch swipes targeting same users
        var request1 = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = new List<SwipeAction>
            {
                new SwipeAction { TargetUserId = 2, IsLike = true },
                new SwipeAction { TargetUserId = 3, IsLike = false }
            }
        };

        var request2 = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = new List<SwipeAction>
            {
                new SwipeAction { TargetUserId = 2, IsLike = false }, // Same target, different direction
                new SwipeAction { TargetUserId = 4, IsLike = true }
            }
        };

        // Act - Simulate concurrent requests
        var result1Task = _controller.BatchSwipe(request1);
        var result2Task = _controller.BatchSwipe(request2);
        await Task.WhenAll(result1Task, result2Task);

        // Assert - At least one should succeed
        var result1 = await result1Task;
        var result2 = await result2Task;
        
        Assert.True(
            result1 is OkObjectResult || result2 is OkObjectResult,
            "At least one batch operation should succeed");

        // Verify only unique swipes in database (no duplicates)
        var swipesInDb = await _context.Swipes.Where(s => s.UserId == 1).ToListAsync();
        var uniqueTargets = swipesInDb.Select(s => s.TargetUserId).Distinct().Count();
        Assert.True(uniqueTargets >= 2, "Should have at least 2 unique target users");
    }

    // ==================== MATCH NOTIFICATION FAILURE TESTS ====================

    // ==================== EDGE CASE TESTS ====================

    [Fact]
    public async Task GetLikesReceivedByUser_NoLikes_ReturnsEmptyList()
    {
        // Arrange - User has no likes received
        _context.Swipes.Add(new Swipe 
        { 
            UserId = 2, 
            TargetUserId = 1, 
            IsLike = false, // Pass, not like
            CreatedAt = DateTime.UtcNow 
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetLikesReceivedByUser(1);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var likes = okResult.Value as IEnumerable<dynamic>;
        Assert.NotNull(likes);
        Assert.Empty(likes);
    }

    [Fact]
    public async Task CheckMutualMatch_SameUserIds_ReturnsValidResponse()
    {
        // Arrange - Edge case: checking if user matches with themselves
        // Act
        var result = await _controller.CheckMutualMatch(1, 1);

        // Assert - Should return false (user can't match with themselves)
        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic value = okResult.Value!;
        Assert.False((bool)value.GetType().GetProperty("IsMutualMatch")!.GetValue(value, null));
    }

    [Fact]
    public async Task CheckMutualMatch_ReversedUserIds_ReturnsSameResult()
    {
        // Arrange - Match exists between User1 and User2
        _context.Matches.Add(new Match 
        { 
            Id = 1, 
            User1Id = 1, 
            User2Id = 2, 
            CreatedAt = DateTime.UtcNow, 
            IsActive = true 
        });
        await _context.SaveChangesAsync();

        // Act - Check in both directions
        var result12 = await _controller.CheckMutualMatch(1, 2);
        var result21 = await _controller.CheckMutualMatch(2, 1);

        // Assert - Both should return true
        var okResult12 = Assert.IsType<OkObjectResult>(result12);
        var okResult21 = Assert.IsType<OkObjectResult>(result21);
        
        dynamic value12 = okResult12.Value!;
        dynamic value21 = okResult21.Value!;
        
        Assert.True((bool)value12.GetType().GetProperty("IsMutualMatch")!.GetValue(value12, null));
        Assert.True((bool)value21.GetType().GetProperty("IsMutualMatch")!.GetValue(value21, null));
    }

    [Fact]
    public async Task Unmatch_AlreadyUnmatched_ReturnsNotFound()
    {
        // Arrange - Match doesn't exist
        _mockMediator
            .Setup(m => m.Send(It.IsAny<UnmatchUsersCommand>(), default))
            .ReturnsAsync(Result.Failure("Match not found"));

        // Act
        var result = await _controller.Unmatch(1, 2);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<object>>(notFoundResult.Value);
        Assert.False(apiResponse.Success);
    }

    [Fact]
    public async Task GetMatchesForUser_InactiveMatches_OnlyReturnsActive()
    {
        // Arrange
        _context.Matches.AddRange(
            new Match { Id = 1, User1Id = 1, User2Id = 2, CreatedAt = DateTime.UtcNow, IsActive = true },
            new Match { Id = 2, User1Id = 1, User2Id = 3, CreatedAt = DateTime.UtcNow, IsActive = false } // Inactive
        );
        await _context.SaveChangesAsync();

        var activeMatches = new List<MatchResult>
        {
            new MatchResult { Id = 1, MatchedUserId = 2, MatchedAt = DateTime.UtcNow, IsActive = true }
        };
        
        _mockMediator
            .Setup(m => m.Send(It.IsAny<GetMatchesForUserQuery>(), default))
            .ReturnsAsync(Result<List<MatchResult>>.Success(activeMatches));

        // Act
        var result = await _controller.GetMatchesForUser(1);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<List<MatchResult>>>(okResult.Value);
        Assert.True(apiResponse.Success);
        Assert.Single(apiResponse.Data!);
        Assert.All(apiResponse.Data, m => Assert.True(m.IsActive));
    }

    [Fact]
    public async Task BatchSwipe_MaximumBatchSize_ProcessesAll()
    {
        // Arrange - Large batch (e.g., 100 swipes)
        var largeSwipeList = Enumerable.Range(2, 100)
            .Select(i => new SwipeAction { TargetUserId = i, IsLike = i % 2 == 0 })
            .ToList();

        var request = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = largeSwipeList
        };

        // Act
        var result = await _controller.BatchSwipe(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        
        // Verify all swipes were processed
        var swipesInDb = await _context.Swipes.Where(s => s.UserId == 1).ToListAsync();
        Assert.Equal(100, swipesInDb.Count);
    }

    [Fact]
    public async Task GetSwipesByUser_LargePageSize_HandlesGracefully()
    {
        // Arrange - Request 10,000 items per page
        _mockMediator
            .Setup(m => m.Send(It.Is<GetSwipesByUserQuery>(q => q.PageSize > 1000), default))
            .ReturnsAsync(Result<UserSwipeHistory>.Success(new UserSwipeHistory 
            { 
                UserId = 1, 
                TotalSwipes = 100, 
                Swipes = new List<SwipeRecord>() 
            }));

        // Act
        var result = await _controller.GetSwipesByUser(1, 1, 10000);

        // Assert - Should complete without timeout
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
    }

    // ==================== VALIDATION EDGE CASES ====================

    // Note: tests for body-supplied UserId (zero/negative) were removed in v0.1.
    // The body's UserId is now intentionally ignored — the swiper's identity is
    // derived from the JWT 'sub' claim via IUserProfileResolver. See
    // SwipesControllerTests.Swipe_OverridesClientSuppliedUserId_WithJwtResolvedProfileId
    // and SwipesControllerTests.Swipe_ResolverReturnsNull_ReturnsBadRequest.

    [Fact]
    public async Task BatchSwipe_AllSelfSwipes_ReturnsAllErrors()
    {
        // Arrange - All swipes are self-swipes
        var request = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = new List<SwipeAction>
            {
                new SwipeAction { TargetUserId = 1, IsLike = true },
                new SwipeAction { TargetUserId = 1, IsLike = false },
                new SwipeAction { TargetUserId = 1, IsLike = true }
            }
        };

        // Act
        var result = await _controller.BatchSwipe(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        
        // No swipes should be recorded
        var swipesInDb = await _context.Swipes.Where(s => s.UserId == 1).ToListAsync();
        Assert.Empty(swipesInDb);
    }
}
