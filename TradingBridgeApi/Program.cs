// Program.cs
using System.Reflection;
using System.Text;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

using TradingBridgeApi;
using TradingBridgeApi.Auth;
using TradingBridgeApi.Services.Live;
using TradingBridgeApi.Services.Strategy.Arbitrage;
using TradingBridgeApi.Services.Strategy.OpenDoor;
using TradingBridgeApi.Services.Strategy.Chrono;
using TradingBridgeApi.StrategyCommon;
using TradingBridgeApi.StrategyCommon.Signals;

// ✅ NEW: handlers + registry (Arbitrage-only for now)
using TradingBridgeApi.StrategyCommon.Handlers;

// ✅ GitHub signals source
using TradingBridgeApi.Signals;

// ---- Axion paths bootstrap (AppData root only; signals are from GitHub now) ----
AxionPaths.InitFromEnvOrDefaults();

var builder = WebApplication.CreateBuilder(args);

// (optional) expose resolved paths in config for debugging/DI usage
builder.Configuration["Axion:AppDataRoot"] = AxionPaths.AppDataRoot;
builder.Configuration["Axion:AllowlistPath"] = AxionPaths.AllowlistPath;
builder.Configuration["Axion:UsersDbPath"] = AxionPaths.UsersDbPath;

// Controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
var allowedOrigins = new[]
{
    "http://localhost:5173",
    "http://localhost:3000",
    "https://devion.vercel.app",
};

builder.Services.AddCors(options =>
{
    options.AddPolicy("dev", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// ---- AUTH: Identity + SQLite(users.db) + JWT ----
builder.Services.AddDbContext<AxionIdentityDbContext>(opt =>
{
    opt.UseSqlite($"Data Source={AxionPaths.UsersDbPath}");
});

builder.Services
    .AddIdentity<IdentityUser, IdentityRole>(opt =>
    {
        opt.Password.RequireDigit = true;
        opt.Password.RequireLowercase = true;
        opt.Password.RequireUppercase = false;
        opt.Password.RequireNonAlphanumeric = false;
        opt.Password.RequiredLength = 8;
        opt.User.RequireUniqueEmail = true;
    })
    .AddEntityFrameworkStores<AxionIdentityDbContext>()
    .AddDefaultTokenProviders();

// ✅ IMPORTANT: For API endpoints we must NOT redirect to /Account/Login.
// Return 401/403 instead (avoids 302 Found for /api/*).
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events = new CookieAuthenticationEvents
    {
        OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        },
        OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddSingleton<AllowlistService>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddHostedService<BootstrapAdminHostedService>();

var jwtKey = builder.Configuration["Auth:Jwt:Key"] ?? "CHANGE_ME__use_long_random_secret_in_prod";
var jwtIssuer = builder.Configuration["Auth:Jwt:Issuer"] ?? "AxionLocal";
var jwtAudience = builder.Configuration["Auth:Jwt:Audience"] ?? "AxionLocal";

// ✅ Make JWT the default for API auth challenges, while Identity cookies still exist for web flows.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(opt =>
    {
        opt.RequireHttpsMetadata = false; // localhost over http
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

// ---- LIVE ----
builder.Services.AddSingleton<TradingAppClient>();
builder.Services.AddSingleton<UniverseService>();
builder.Services.AddSingleton<LiveSnapshotService>();

// =========================================================
// ✅ SIGNALS (STATIC) => GitHub RAW (NO local signals folder)
// =========================================================

// bind options from appsettings.json: Axion:Signals:GitHub
builder.Services.Configure<GitHubSignalsOptions>(
    builder.Configuration.GetSection("Axion:Signals:GitHub"));

// HttpClient for GitHub RAW downloads
builder.Services.AddHttpClient("github-raw", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Axion/1.0");
});

// Signals source
builder.Services.AddSingleton<ISignalsSource, GitHubSignalsSource>();

// ---- STATIC readers (must use ISignalsSource) ----
builder.Services.AddSingleton<ArbitrageFilesService>();
builder.Services.AddSingleton<OpenDoorFilesService>();
builder.Services.AddSingleton<ChronoFilesService>();

// ---- COMMON (signals join + eligibility + top + handlers + registry + signal service) ----
builder.Services.AddSingleton<StrategyJoiner>();
builder.Services.AddSingleton<EligibilityPolicy>();
builder.Services.AddSingleton<TopModePolicy>(); // ✅ already used inside arbitrage handler

// ✅ NEW: handler pattern (Arbitrage-only for now)
builder.Services.AddSingleton<IStrategySignalsHandler, ArbitrageSignalsHandler>();
builder.Services.AddSingleton<StrategyHandlerRegistry>();

// Router service (now depends only on registry)
builder.Services.AddSingleton<StrategySignalService>();

var app = builder.Build();

// Ensure Identity DB exists (no migrations required)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AxionIdentityDbContext>();
    db.Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("dev");

// app.UseHttpsRedirection(); // keep disabled for localhost over http

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Serve static files only if wwwroot exists (avoid warnings)
var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (Directory.Exists(wwwroot))
{
    app.UseStaticFiles();
}

app.MapControllers();

/* ===================== APP META (VERSION + HEALTH) ===================== */

static string GetAppVersion()
{
    // Prefer informational version (supports tags/commits)
    var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

    var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(info))
        return info.Trim();

    return asm.GetName().Version?.ToString() ?? "unknown";
}

// Version endpoint (Launcher can read this to display Version instead of "unknown")
app.MapGet("/version", () =>
{
    return Results.Ok(new
    {
        name = "TradingBridgeApi",
        version = GetAppVersion(),
        env = app.Environment.EnvironmentName,
        ts = DateTimeOffset.UtcNow
    });
});

// Health endpoint (Launcher should ping this; always returns 200 if app is alive)
app.MapGet("/health", (IOptions<GitHubSignalsOptions> gh) =>
{
    var o = gh.Value;

    // We do not perform remote GitHub fetch here (keep it fast/robust).
    // Just report whether token exists and what repo is configured.
    return Results.Ok(new
    {
        status = "ok",
        version = GetAppVersion(),
        env = app.Environment.EnvironmentName,
        ts = DateTimeOffset.UtcNow,
        signals = new
        {
            owner = o.Owner,
            repo = o.Repo,
            branch = o.Branch,
            hasToken = !string.IsNullOrWhiteSpace(o.Token)
        }
    });
});

/* ===================== DEBUG HELPERS ===================== */

app.MapGet("/__gh", (IOptions<GitHubSignalsOptions> opt) =>
{
    var o = opt.Value;
    return Results.Ok(new
    {
        o.Owner,
        o.Repo,
        o.Branch,
        hasToken = !string.IsNullOrWhiteSpace(o.Token),
        tokenLen = o.Token?.Length ?? 0,
        o.BasePath,
        o.CacheAllJsonl,
        o.CacheTtlDays,
        o.CacheIfSizeAtLeastBytes
    });
});

app.MapGet("/__whoami", (HttpContext ctx) =>
{
    var rawTarget = ctx.Features.Get<IHttpRequestFeature>()?.RawTarget;

    return Results.Ok(new
    {
        scheme = ctx.Request.Scheme,
        host = ctx.Request.Host.Value,
        pathBase = ctx.Request.PathBase.Value,
        path = ctx.Request.Path.Value,
        rawTarget
    });
});

app.MapGet("/__routes", (IEnumerable<EndpointDataSource> sources) =>
{
    var routes = sources
        .SelectMany(s => s.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(e => new
        {
            pattern = e.RoutePattern.RawText,
            order = e.Order,
            displayName = e.DisplayName
        })
        .OrderBy(x => x.pattern)
        .ToList();

    return Results.Ok(routes);
});

/// <summary>
/// Debug: open a signals file via ISignalsSource and return first line (or exception).
/// Usage:
///   /__sig?path=chrono/onefile.jsonl
/// </summary>
app.MapGet("/__sig", async (string path, ISignalsSource src, CancellationToken ct) =>
{
    try
    {
        path = (path ?? "").Trim().TrimStart('/');

        await using var s = await src.OpenReadAsync(path, ct);
        using var sr = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);

        var head = await sr.ReadLineAsync(ct);

        return Results.Ok(new
        {
            ok = true,
            path,
            head
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            ok = false,
            path,
            ex = ex.ToString()
        });
    }
});

/// <summary>
/// Debug: read first N lines (bounded) from a signals file.
/// Usage:
///   /__sighead?path=chrono/onefile.jsonl&lines=3
/// </summary>
app.MapGet("/__sighead", async (string path, int lines, ISignalsSource src, CancellationToken ct) =>
{
    lines = Math.Clamp(lines, 1, 20);
    path = (path ?? "").Trim().TrimStart('/');

    try
    {
        await using var s = await src.OpenReadAsync(path, ct);
        using var sr = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: false);

        var arr = new List<string>(lines);
        for (int i = 0; i < lines; i++)
        {
            var ln = await sr.ReadLineAsync(ct);
            if (ln is null) break;
            arr.Add(ln);
        }

        return Results.Ok(new
        {
            ok = true,
            path,
            lines = arr.Count,
            head = arr
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            ok = false,
            path,
            ex = ex.ToString()
        });
    }
});

app.Run();
