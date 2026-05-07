using Xunit;
using Xunit.Abstractions;
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
using SwipeService.Common;
using SwipeService.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace SwipeService.Tests.Controllers;

/// <summary>
/// Example demonstrating TestDiagnostics usage for better test debugging
/// This shows best practices for using diagnostic helpers in tests
/// </summary>
public class SwipesControllerDiagnosticExample : IDisposable
{
    private readonly SwipeContext _context;
    private readonly Mock<MatchmakingNotifier> _mockNotifier;
    private readonly Mock<ILogger<SwipesController>> _mockLogger;
    private readonly Mock<IMediator> _mockMediator;
    private readonly Mock<IUserProfileResolver> _mockResolver;
    private readonly SwipesController _controller;
    private readonly ITestOutputHelper _output;

    public SwipesControllerDiagnosticExample(ITestOutputHelper output)
    {
        _output = output;
        
        var options = new DbContextOptionsBuilder<SwipeContext>()
            .UseInMemoryDatabase(databaseName: $"DiagnosticExample_{Guid.NewGuid()}")
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

    /// <summary>
    /// Example: Basic swipe test WITH diagnostic logging
    /// Shows how diagnostics help debug failures
    /// </summary>
    [Fact]
    public async Task Example_SwipeWithDiagnostics_ShowsBetterErrorContext()
    {
        var diag = new TestDiagnostics(_output);
        diag.Log("Starting swipe validation test");

        // Arrange
        diag.Checkpoint("Setting up test data");
        var command = new RecordSwipeCommand 
        { 
            UserId = 1, 
            TargetUserId = 2, 
            IsLike = true 
        };
        
        var swipeResponse = new SwipeResponse 
        { 
            Success = true, 
            Message = "Swipe recorded", 
            IsMutualMatch = false 
        };
        
        diag.LogObject("Command", command);
        diag.LogObject("Expected Response", swipeResponse);

        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Success(swipeResponse));

        var request = new SwipeRequest 
        { 
            UserId = 1, 
            TargetUserId = 2, 
            IsLike = true 
        };

        diag.Checkpoint("Executing controller action");

        // Act
        var result = await _controller.Swipe(request);
        
        diag.Checkpoint("Validating response");
        diag.LogObject("Actual Result Type", result.GetType().Name);

        // Assert with diagnostic context
        var okResult = Assert.IsType<OkObjectResult>(result);
        diag.Log($"Response status: {okResult.StatusCode}");
        
        var apiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(okResult.Value);
        diag.LogObject("API Response", apiResponse);
        
        // Enhanced assertions with context
        diag.AssertTrue(apiResponse.Success, 
            "API response should indicate success",
            () => $"Response message: {apiResponse.Message}");
            
        diag.AssertTrue(apiResponse.Data != null,
            "Response data should not be null",
            () => $"Full response: {System.Text.Json.JsonSerializer.Serialize(apiResponse)}");
            
        if (apiResponse.Data != null)
        {
            diag.AssertEqual(true, apiResponse.Data.Success, "Swipe should be recorded successfully");
            diag.AssertEqual(false, apiResponse.Data.IsMutualMatch, "Should not be a mutual match");
        }

        diag.Log($"Test completed successfully in {diag.ElapsedMs}ms");
    }

    /// <summary>
    /// Example: Mutual match detection WITH diagnostics
    /// Shows how to track complex multi-step operations
    /// </summary>
    [Fact]
    public async Task Example_MutualMatchDetection_WithDetailedTracking()
    {
        var diag = new TestDiagnostics(_output);
        diag.Log("Starting mutual match detection test");

        // Arrange - Create bidirectional swipes
        diag.Checkpoint("Creating first swipe (user 1 → user 2)");
        var swipe1 = new Swipe
        {
            UserId = 1,
            TargetUserId = 2,
            IsLike = true,
            
            CreatedAt = DateTime.UtcNow
        };
        _context.Swipes.Add(swipe1);
        await _context.SaveChangesAsync();
        diag.Log($"First swipe created with ID: {swipe1.Id}");

        diag.Checkpoint("Creating second swipe (user 2 → user 1)");
        var command = new RecordSwipeCommand
        {
            UserId = 2,
            TargetUserId = 1,
            IsLike = true
        };

        var mutualMatchResponse = new SwipeResponse
        {
            Success = true,
            Message = "Mutual match!",
            IsMutualMatch = true,
            MatchId = 100
        };
        
        diag.LogObject("Mutual Match Response", mutualMatchResponse);

        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Success(mutualMatchResponse));

        var request = new SwipeRequest
        {
            UserId = 2,
            TargetUserId = 1,
            IsLike = true
        };

        diag.Checkpoint("Executing second swipe to create mutual match");

        // Act
        var result = await _controller.Swipe(request);

        diag.Checkpoint("Verifying mutual match was detected");
        
        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var apiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(okResult.Value);
        
        diag.LogObject("Final Response", apiResponse);
        
        // Detailed assertions with context
        diag.AssertTrue(apiResponse.Success, "Response should be successful");
        diag.AssertTrue(apiResponse.Data != null, "Response data should exist");
        
        if (apiResponse.Data != null)
        {
            diag.AssertEqual(true, apiResponse.Data.IsMutualMatch, 
                "Should detect mutual match");
            diag.AssertTrue(apiResponse.Data.MatchId > 0, 
                "Should have valid match ID",
                () => $"MatchId was: {apiResponse.Data.MatchId}");
        }

        // Verify database state
        diag.Checkpoint("Verifying database state");
        var swipeCount = await _context.Swipes.CountAsync();
        diag.Log($"Total swipes in database: {swipeCount}");
        diag.AssertEqual(1, swipeCount, "Should have 1 swipe (we added first manually)");

        diag.Log($"Mutual match test completed in {diag.ElapsedMs}ms");
    }

    /// <summary>
    /// Example: Error scenario with diagnostic context
    /// Shows how diagnostics help identify failure causes quickly
    /// </summary>
    [Fact]
    public async Task Example_ErrorScenario_WithRichErrorContext()
    {
        var diag = new TestDiagnostics(_output);
        diag.Log("Starting error scenario test");

        // Arrange
        diag.Checkpoint("Setting up self-swipe scenario (should fail)");
        var command = new RecordSwipeCommand
        {
            UserId = 1,
            TargetUserId = 1,  // Self-swipe!
            IsLike = true
        };
        
        diag.LogObject("Invalid Command", command);
        diag.Log("⚠️ Note: UserId == TargetUserId (self-swipe)");

        _mockMediator
            .Setup(m => m.Send(It.IsAny<RecordSwipeCommand>(), default))
            .ReturnsAsync(Result<SwipeResponse>.Failure("Cannot swipe on yourself"));

        var request = new SwipeRequest
        {
            UserId = 1,
            TargetUserId = 1,
            IsLike = true
        };

        diag.Checkpoint("Executing self-swipe request");

        // Act
        var result = await _controller.Swipe(request);

        diag.Checkpoint("Validating error response");
        diag.LogObject("Result Type", result.GetType().Name);

        // Assert
        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result);
        diag.Log($"Status Code: {badRequestResult.StatusCode}");
        
        var apiResponse = Assert.IsType<ApiResponse<SwipeResponse>>(badRequestResult.Value);
        diag.LogObject("Error Response", apiResponse);

        // Enhanced error validation
        diag.AssertTrue(!apiResponse.Success, 
            "Response should indicate failure",
            () => $"Success was: {apiResponse.Success}");
            
        diag.AssertTrue(apiResponse.Message != null && 
                       apiResponse.Message.Contains("yourself", StringComparison.OrdinalIgnoreCase),
            "Error message should mention self-swipe",
            () => $"Actual message: '{apiResponse.Message}'");

        diag.Log($"Error validation completed in {diag.ElapsedMs}ms");
        diag.Log("✅ Self-swipe correctly rejected");
    }
}
