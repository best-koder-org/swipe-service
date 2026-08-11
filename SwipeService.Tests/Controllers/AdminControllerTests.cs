using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SwipeService.Controllers;
using SwipeService.Data;
using SwipeService.Models;
using Xunit;
using SwipeMatch = SwipeService.Models.Match;

namespace SwipeService.Tests.Controllers;

public class AdminControllerTests : IDisposable
{
    private readonly SwipeContext _context;

    public AdminControllerTests()
    {
        var options = new DbContextOptionsBuilder<SwipeContext>()
            .UseInMemoryDatabase($"AdminReset_{Guid.NewGuid()}")
            .Options;
        _context = new SwipeContext(options);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private AdminController BuildController(string envName = "Development")
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(envName);
        var logger = Mock.Of<ILogger<AdminController>>();
        var ctrl = new AdminController(_context, env.Object, logger);
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return ctrl;
    }

    [Fact]
    public async Task ResetAllSwipes_WipesEverythingInDev()
    {
        _context.Swipes.Add(new Swipe { UserId = 1, TargetUserId = 2, IsLike = true, CreatedAt = DateTime.UtcNow });
        _context.Matches.Add(new SwipeMatch { User1Id = 1, User2Id = 2, CreatedAt = DateTime.UtcNow, IsActive = true });
        await _context.SaveChangesAsync();

        var result = await BuildController("Development").ResetAllSwipes();

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(0, await _context.Swipes.CountAsync());
        Assert.Equal(0, await _context.Matches.CountAsync());
    }

    [Fact]
    public async Task ResetAllSwipes_RejectsInProduction()
    {
        var result = await BuildController("Production").ResetAllSwipes();
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }
}
