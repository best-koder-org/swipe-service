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
/// T030: SwipeService controller tests for swipe recording and match detection
/// Tests cover swipe validation, idempotency, mutual match detection, and batch operations
/// </summary>
public class SwipesControllerTests : IDisposable
{
    private readonly SwipeContext _context;
    private readonly Mock<MatchmakingNotifier> _mockNotifier;
    private readonly Mock<ILogger<SwipesController>> _mockLogger;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IUserProfileResolver> _mockResolver;
    private readonly SwipesController _controller;

    public SwipesControllerTests()
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
        // Default: any caller resolves to profileId=1 (the historical swiper used in these tests).
        _mockResolver
            .Setup(r => r.ResolveProfileIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _controller = new SwipesController(_context, _mockNotifier.Object, _mockLogger.Object, _mockMediator.Object, _mockResolver.Object);

        // Authenticate the controller with a known sub claim + Authorization header.
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

    // ==================== SWIPE VALIDATION TESTS ====================

    [Fact]
    public async Task Swipe_ValidRequest_ReturnsOk()
    {
        // Arrange
        var command = new RecordSwipeCommand { UserId = 1, TargetUserId = 2, IsLike = true };
        var swipeResponse = new SwipeResponse { Success = true, Message = "Swipe recorded", IsMutualMatch = false };
        
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Success(swipeResponse));

        var request = new SwipeRequest { UserId = 1, TargetUserId = 2, IsLike = true };

        // Act
        var result = await _controller.Swipe(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(okResult.Value);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.True(apiResponse.Data.Success);
    }

    [Fact]
    public async Task Swipe_SelfSwipe_ReturnsBadRequest()
    {
        // Arrange - User trying to swipe on themselves
        var command = new RecordSwipeCommand { UserId = 1, TargetUserId = 1, IsLike = true };
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Failure("Cannot swipe on yourself"));

        var request = new SwipeRequest { UserId = 1, TargetUserId = 1, IsLike = true };

        // Act
        var result = await _controller.Swipe(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(badRequestResult.Value);
        Assert.False(apiResponse.Success);
        Assert.Contains("yourself", apiResponse.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Swipe_DuplicateSwipe_ReturnsBadRequest()
    {
        // Arrange - Swipe already exists
        _context.Swipes.Add(new Swipe 
        { 
            Id = 1, 
            UserId = 1, 
            TargetUserId = 2, 
            IsLike = true, 
            CreatedAt = DateTime.UtcNow 
        });
        await _context.SaveChangesAsync();

        var command = new RecordSwipeCommand { UserId = 1, TargetUserId = 2, IsLike = false };
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Failure("Already swiped on this user"));

        var request = new SwipeRequest { UserId = 1, TargetUserId = 2, IsLike = false };

        // Act
        var result = await _controller.Swipe(request);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(badRequestResult.Value);
        Assert.False(apiResponse.Success);
    }

    [Fact]
    public async Task Swipe_MutualLike_CreatesMatch()
    {
        // Arrange - User2 already liked User1
        _context.Swipes.Add(new Swipe 
        { 
            Id = 1, 
            UserId = 2, 
            TargetUserId = 1, 
            IsLike = true, 
            CreatedAt = DateTime.UtcNow 
        });
        await _context.SaveChangesAsync();

        var swipeResponse = new SwipeResponse 
        { 
            Success = true, 
            Message = "It's a match!", 
            IsMutualMatch = true 
        };
        
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Success(swipeResponse));

        var request = new SwipeRequest { UserId = 1, TargetUserId = 2, IsLike = true };

        // Act
        var result = await _controller.Swipe(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(okResult.Value);
        Assert.True(apiResponse.Success);
        Assert.NotNull(apiResponse.Data);
        Assert.True(apiResponse.Data.IsMutualMatch);
    }

    // ==================== BATCH SWIPE TESTS ====================

    [Fact]
    public async Task BatchSwipe_ValidRequests_ProcessesAll()
    {
        // Arrange - Multiple valid swipes
        var request = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = new List<SwipeAction>
            {
                new SwipeAction { TargetUserId = 2, IsLike = true },
                new SwipeAction { TargetUserId = 3, IsLike = false },
                new SwipeAction { TargetUserId = 4, IsLike = true }
            }
        };

        // Act
        var result = await _controller.BatchSwipe(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);
        
        // Verify all swipes were recorded
        var swipesInDb = await _context.Swipes.Where(s => s.UserId == 1).ToListAsync();
        Assert.Equal(3, swipesInDb.Count);
    }

    [Fact]
    public async Task BatchSwipe_MixedValidityTest_ReturnsPartialResults()
    {
        // Arrange - Mix of valid and invalid swipes
        var request = new BatchSwipeRequest
        {
            UserId = 1,
            Swipes = new List<SwipeAction>
            {
                new SwipeAction { TargetUserId = 2, IsLike = true },  // Valid
                new SwipeAction { TargetUserId = 1, IsLike = true },  // Invalid: self-swipe
                new SwipeAction { TargetUserId = 3, IsLike = false }, // Valid
            }
        };

        // Act
        var result = await _controller.BatchSwipe(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        
        // Only 2 valid swipes should be in DB
        var swipesInDb = await _context.Swipes.Where(s => s.UserId == 1).ToListAsync();
        Assert.Equal(2, swipesInDb.Count);
        Assert.DoesNotContain(swipesInDb, s => s.TargetUserId == 1); // Self-swipe rejected
    }

    // ==================== QUERY TESTS ====================

    [Fact]
    public async Task GetSwipesByUser_ValidUserId_ReturnsPagedResults()
    {
        // Arrange - Create Swipes for user
        var swipes = Enumerable.Range(1, 15)
            .Select(i => new Swipe 
            { 
                UserId = 1, 
                TargetUserId = i + 1, 
                IsLike = i % 2 == 0, 
                CreatedAt = DateTime.UtcNow.AddMinutes(-i) 
            })
            .ToList();
        
        _context.Swipes.AddRange(swipes);
        await _context.SaveChangesAsync();

        var query = new GetSwipesByUserQuery { UserId = 1, Page = 1, PageSize = 10 };
        var userSwipeHistory = new UserSwipeHistory 
        { 
            UserId = 1, 
            TotalSwipes = 15, 
            Swipes = swipes.Take(10).Select(s => new SwipeRecord 
            { 
                Id = s.Id,
                TargetUserId = s.TargetUserId, 
                IsLike = s.IsLike, 
                CreatedAt = s.CreatedAt 
            }).ToList() 
        };
        
        _mockMediator
            .Setup(m => m.Send(It.IsAny<GetSwipesByUserQuery>(), default))
            .ReturnsAsync(Result<UserSwipeHistory>.Success(userSwipeHistory));

        // Act
        var result = await _controller.GetSwipesByUser(1, 1, 10);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<UserSwipeHistory>>(okResult.Value);
        Assert.True(apiResponse.Success);
        Assert.Equal(15, apiResponse.Data!.TotalSwipes);
        Assert.Equal(10, apiResponse.Data.Swipes.Count);
    }

    [Fact]
    public async Task GetSwipesByUser_WithLikeFilter_ReturnsFilteredResults()
    {
        // Arrange - Create mix of likes and passes
        var swipes = new List<Swipe>
        {
            new Swipe { UserId = 1, TargetUserId = 2, IsLike = true, CreatedAt = DateTime.UtcNow },
            new Swipe { UserId = 1, TargetUserId = 3, IsLike = false, CreatedAt = DateTime.UtcNow },
            new Swipe { UserId = 1, TargetUserId = 4, IsLike = true, CreatedAt = DateTime.UtcNow },
        };
        
        _context.Swipes.AddRange(swipes);
        await _context.SaveChangesAsync();

        var query = new GetSwipesByUserQuery { UserId = 1, Page = 1, PageSize = 10, IsLike = true };
        var filterLikes = swipes.Where(s => s.IsLike).Select(s => new SwipeRecord 
        { 
            Id = s.Id,
            TargetUserId = s.TargetUserId, 
            IsLike = s.IsLike, 
            CreatedAt = s.CreatedAt 
        }).ToList();
        
        var userSwipeHistory = new UserSwipeHistory 
        { 
            UserId = 1, 
            TotalSwipes = 2, 
            Swipes = filterLikes
        };
        
        _mockMediator
            .Setup(m => m.Send(It.Is<GetSwipesByUserQuery>(q => q.IsLike == true), default))
            .ReturnsAsync(Result<UserSwipeHistory>.Success(userSwipeHistory));

        // Act
        var result = await _controller.GetSwipesByUser(1, 1, 10, true);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<UserSwipeHistory>>(okResult.Value);
        Assert.True(apiResponse.Success);
        Assert.All(apiResponse.Data!.Swipes, s => Assert.True(s.IsLike));
    }

    [Fact]
    public async Task GetMatchesForUser_ValidUserId_ReturnsMatches()
    {
        // Arrange - Create matches for user
        _context.Matches.AddRange(
            new Match { Id = 1, User1Id = 1, User2Id = 2, CreatedAt = DateTime.UtcNow, IsActive = true },
            new Match { Id = 2, User1Id = 1, User2Id = 3, CreatedAt = DateTime.UtcNow, IsActive = true }
        );
        await _context.SaveChangesAsync();

        var matchResults = new List<MatchResult>
        {
            new MatchResult { Id = 1, MatchedUserId = 2, MatchedAt = DateTime.UtcNow, IsActive = true },
            new MatchResult { Id = 2, MatchedUserId = 3, MatchedAt = DateTime.UtcNow, IsActive = true }
        };
        
        _mockMediator
            .Setup(m => m.Send(It.IsAny<GetMatchesForUserQuery>(), default))
            .ReturnsAsync(Result<List<MatchResult>>.Success(matchResults));

        // Act
        var result = await _controller.GetMatchesForUser(1);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<List<MatchResult>>>(okResult.Value);
        Assert.True(apiResponse.Success);
        Assert.Equal(2, apiResponse.Data!.Count);
    }

    [Fact]
    public async Task GetLikesReceivedByUser_ValidUserId_ReturnsLikes()
    {
        // Arrange - Other users liked User1
        _context.Swipes.AddRange(
            new Swipe { UserId = 2, TargetUserId = 1, IsLike = true, CreatedAt = DateTime.UtcNow },
            new Swipe { UserId = 3, TargetUserId = 1, IsLike = true, CreatedAt = DateTime.UtcNow },
            new Swipe { UserId = 4, TargetUserId = 1, IsLike = false, CreatedAt = DateTime.UtcNow } // Pass - should not be included
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.GetLikesReceivedByUser(1);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var likes = okResult.Value as IEnumerable<dynamic>;
        Assert.NotNull(likes);
        Assert.Equal(2, likes.Count()); // Only likes, not passes
    }

    // ==================== MATCH STATUS TESTS ====================

    [Fact]
    public async Task CheckMutualMatch_ExistingMatch_ReturnsTrue()
    {
        // Arrange - Match exists
        _context.Matches.Add(new Match 
        { 
            Id = 1, 
            User1Id = 1, 
            User2Id = 2, 
            CreatedAt = DateTime.UtcNow, 
            IsActive = true 
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _controller.CheckMutualMatch(1, 2);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic value = okResult.Value!;
        Assert.True((bool)value.GetType().GetProperty("IsMutualMatch")!.GetValue(value, null));
    }

    [Fact]
    public async Task CheckMutualMatch_NoMatch_ReturnsFalse()
    {
        // Act - No match exists in empty database
        var result = await _controller.CheckMutualMatch(1, 2);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic value = okResult.Value!;
        Assert.False((bool)value.GetType().GetProperty("IsMutualMatch")!.GetValue(value, null));
    }

    [Fact]
    public async Task Unmatch_ExistingMatch_ReturnsOk()
    {
        // Arrange
        _context.Matches.Add(new Match 
        { 
            Id = 1, 
            User1Id = 1, 
            User2Id = 2, 
            CreatedAt = DateTime.UtcNow, 
            IsActive = true 
        });
        await _context.SaveChangesAsync();

        _mockMediator
            .Setup(m => m.Send(It.IsAny<UnmatchUsersCommand>(), default))
            .ReturnsAsync(Result.Success());

        // Act
        var result = await _controller.Unmatch(1, 2);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<object>>(okResult.Value);
        Assert.True(apiResponse.Success);
    }

    [Fact]
    public async Task Unmatch_NonExistentMatch_ReturnsNotFound()
    {
        // Arrange
        _mockMediator
            .Setup(m => m.Send(It.IsAny<UnmatchUsersCommand>(), default))
            .ReturnsAsync(Result.Failure("Match not found"));

        // Act
        var result = await _controller.Unmatch(1, 999);

        // Assert
        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<object>>(notFoundResult.Value);
        Assert.False(apiResponse.Success);
        Assert.Contains("not found", apiResponse.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthCheck_ReturnsHealthy()
    {
        // Act
        var result = _controller.HealthCheck();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var value = okResult.Value;
        Assert.NotNull(value);
        
        var statusProp = value.GetType().GetProperty("Status");
        Assert.NotNull(statusProp);
        Assert.Equal("Healthy", statusProp.GetValue(value));
    }

    [Fact]
    public async Task Swipe_WithIdempotencyKey_ReturnsSameResultOnRetry()
    {
        // Arrange
        var idempotencyKey = Guid.NewGuid().ToString();
        var swipeRequest = new SwipeRequest
        {
            UserId = 1,
            TargetUserId = 2,
            IsLike = true,
            IdempotencyKey = idempotencyKey
        };

        var expectedResponse = new SwipeResponse
        {
            Success = true,
            Message = "Swipe recorded successfully",
            IsMutualMatch = false
        };

        _mockMediator.Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Success(expectedResponse));

        // Act - First request
        var firstResult = await _controller.Swipe(swipeRequest);
        var firstOkResult = Assert.IsType<OkObjectResult>(firstResult);
        var firstApiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(firstOkResult.Value);
        
        // Act - Second request with same idempotency key
        var secondResult = await _controller.Swipe(swipeRequest);
        var secondOkResult = Assert.IsType<OkObjectResult>(secondResult);
        var secondApiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(secondOkResult.Value);

        // Assert - Both responses should be successful  
        Assert.True(firstApiResponse.Success);
        Assert.True(secondApiResponse.Success);
        Assert.True(firstApiResponse.Data.Success);
        Assert.True(secondApiResponse.Data.Success);
    }

    [Fact]
    public async Task Swipe_WithoutIdempotencyKey_AllowsDuplicateProcessingAttempt()
    {
        // Arrange - First swipe without idempotency key
        var swipeRequest = new SwipeRequest
        {
            UserId = 1,
            TargetUserId = 2,
            IsLike = true
            // No IdempotencyKey
        };

        _mockMediator.SetupSequence(m => m.Send(It.Is<RecordSwipeCommand>(c => 
                c.UserId == 1 && c.TargetUserId == 2), default))
            .ReturnsAsync(Result<SwipeResponse>.Success(new SwipeResponse { Success = true }))
            .ReturnsAsync(Result<SwipeResponse>.Failure("Already swiped on this user"));

        // Act
        var firstResult = await _controller.Swipe(swipeRequest);
        var secondResult = await _controller.Swipe(swipeRequest);

        // Assert
        var firstOkResult = Assert.IsType<OkObjectResult>(firstResult);
        var firstApiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(firstOkResult.Value);
        Assert.True(firstApiResponse.Success);

        var secondBadResult = Assert.IsType<BadRequestObjectResult>(secondResult);
        var secondApiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(secondBadResult.Value);
        Assert.False(secondApiResponse.Success);
    }

    // ==================== JWT IDENTITY + DIRECTION TESTS (v0.1) ====================

    [Fact]
    public async Task Swipe_OverridesClientSuppliedUserId_WithJwtResolvedProfileId()
    {
        // Arrange — client tries to spoof another user's UserId in the body.
        // The controller MUST ignore the body's UserId and use the resolver's profileId (1).
        RecordSwipeCommand? captured = null;
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .Callback<IRequest<Result<SwipeResponse>>, CancellationToken>((cmd, _) => captured = (RecordSwipeCommand)cmd)
            .ReturnsAsync(Result<SwipeResponse>.Success(new SwipeResponse { Success = true }));

        var spoofed = new SwipeRequest { UserId = 9999, TargetUserId = 2, IsLike = true };

        // Act
        var result = await _controller.Swipe(spoofed);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(captured);
        Assert.Equal(1, captured!.UserId); // resolver returned 1, body's 9999 ignored
        Assert.Equal(2, captured.TargetUserId);
    }

    [Theory]
    [InlineData("like", true)]
    [InlineData("LIKE", true)]
    [InlineData("superlike", true)]
    [InlineData("super_like", true)]
    [InlineData("pass", false)]
    [InlineData("dislike", false)]
    [InlineData("skip", false)]
    public async Task Swipe_DirectionField_TakesPrecedenceOverIsLike(string direction, bool expectedIsLike)
    {
        // Arrange — Direction field is the modern Flutter contract. When supplied,
        // it must override the legacy IsLike boolean.
        RecordSwipeCommand? captured = null;
        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .Callback<IRequest<Result<SwipeResponse>>, CancellationToken>((cmd, _) => captured = (RecordSwipeCommand)cmd)
            .ReturnsAsync(Result<SwipeResponse>.Success(new SwipeResponse { Success = true }));

        // IsLike intentionally set to the opposite of the expected mapping
        var request = new SwipeRequest { TargetUserId = 2, IsLike = !expectedIsLike, Direction = direction };

        // Act
        await _controller.Swipe(request);

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(expectedIsLike, captured!.IsLike);
    }

    [Fact]
    public async Task Swipe_NoSubClaim_ReturnsUnauthorized()
    {
        // Arrange — strip JWT
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var request = new SwipeRequest { TargetUserId = 2, IsLike = true };

        // Act
        var result = await _controller.Swipe(request);

        // Assert
        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public async Task Swipe_ResolverReturnsNull_ReturnsBadRequest()
    {
        // Arrange — resolver fails to map keycloakId → profileId.
        _mockResolver.Reset();
        _mockResolver
            .Setup(r => r.ResolveProfileIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);

        var request = new SwipeRequest { TargetUserId = 2, IsLike = true };

        // Act
        var result = await _controller.Swipe(request);

        // Assert
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<SwipeResponse>>(bad.Value);
        Assert.False(resp.Success);
        Assert.Contains("profile", resp.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
