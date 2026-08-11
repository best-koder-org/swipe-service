using SwipeService.Middleware;
using DatingApp.Shared.Middleware;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SwipeService.Data;
using SwipeService.Extensions;
using SwipeService.Services;
using SwipeService.Common;
using SwipeService.Models;
using System.Reflection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .Enrich.WithMachineName()
    .Enrich.WithCorrelationId()
    .Enrich.WithProperty("ServiceName", "SwipeService")
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{ServiceName}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path: "logs/swipe-service-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ServiceName}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}"
    ));

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Swipe Service API",
        Version = "v1",
        Description = "Swipe ingestion, idempotency, and rate limiting for matchmaking."
    });

    // JWT Authentication
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using Bearer scheme. Enter 'Bearer' [space] and then your token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });

    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }
});

// Configure MySQL database
builder.Services.AddDbContext<SwipeContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        new MySqlServerVersion(new Version(8, 0, 32)),
        mysqlOptions => mysqlOptions.EnableRetryOnFailure()
    ));

// Register SwipeService
builder.Services.AddScoped<SwipeService.Services.SwipeService>();

// Register rate limiting service and configuration
var swipeLimitsConfig = new SwipeService.Models.SwipeLimitsConfiguration();
builder.Configuration.GetSection("SwipeLimits").Bind(swipeLimitsConfig);
builder.Services.AddSingleton(swipeLimitsConfig);
builder.Services.AddScoped<SwipeService.Services.IRateLimitService, SwipeService.Services.RateLimitService>();

// T184-T191: Swipe behavior analysis configuration and services
builder.Services.Configure<SwipeBehaviorConfiguration>(
    builder.Configuration.GetSection("SwipeBehavior"));
builder.Services.AddScoped<ISwipeBehaviorAnalyzer, SwipeBehaviorAnalyzer>();
builder.Services.AddScoped<IBotDetectionService, BotDetectionHeuristics>();
builder.Services.AddHostedService<SwipeBehaviorRecalcService>();

// Internal API Key Authentication for service-to-service calls
builder.Services.AddScoped<InternalApiKeyAuthFilter>();
builder.Services.AddTransient<InternalApiKeyAuthHandler>();

// Register MatchmakingNotifier
builder.Services.AddHttpClient<MatchmakingNotifier>()
    .AddHttpMessageHandler<InternalApiKeyAuthHandler>();

// Register UserProfileResolver: HTTP-backed lookup keycloakId → profileId.
// Base URL comes from configuration (UserService:BaseUrl), with localhost fallback.
var userServiceBaseUrl = builder.Configuration["UserService:BaseUrl"] ?? "http://localhost:8082";
builder.Services.AddHttpClient<IUserProfileResolver, UserProfileResolver>(client =>
{
    client.BaseAddress = new Uri(userServiceBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Add CQRS with MediatR
builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));

// Add FluentValidation
builder.Services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());

builder.Services.AddKeycloakAuthentication(builder.Configuration);
builder.Services.AddAuthorization();
builder.Services.AddHealthChecks();
builder.Services.AddCorrelationIds();

// Configure OpenTelemetry for metrics and distributed tracing
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: "swipe-service",
                    serviceVersion: "1.0.0"))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("SwipeService")
        .AddPrometheusExporter())
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options =>
        {
            options.RecordException = true;
            options.Filter = (httpContext) =>
            {
                // Don't trace health checks and metrics endpoints
                var path = httpContext.Request.Path.ToString();
                return !path.Contains("/health") && !path.Contains("/metrics");
            };
        })
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation(options =>
        {
            options.SetDbStatementForText = true;
            options.EnrichWithIDbCommand = (activity, command) =>
            {
                activity.SetTag("db.query", command.CommandText);
            };
        }));

// Create custom meters for business metrics

// Register injectable business metrics
builder.Services.AddSingleton<SwipeService.Metrics.SwipeServiceMetrics>();

// CORS: config-driven origins — AllowAnyOrigin in dev, restricted in staging/production
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        if (allowedOrigins != null && allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

// Apply migrations on startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<SwipeContext>();
    if (dbContext.Database.IsRelational())
    {
        dbContext.Database.Migrate();
        Console.WriteLine("SwipeService: Applied database migrations");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Swipe Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseHttpsRedirection();

app.UseCorrelationIds();
app.UseGlobalExceptionHandling();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health");

// Prometheus metrics endpoint
app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();
