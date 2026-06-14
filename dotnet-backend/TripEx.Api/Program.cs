using System.Text;
using log4net;
using log4net.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using TripEx.Api;
using TripEx.Api.Auth;
using TripEx.Api.Data;
using TripEx.Api.Services;

// ── log4net: daily-rolling file logs in <app>/logs/ ──
// File pattern: logs/tripex-YYYYMMDD.log (new file every day, keep last 30 days, 50MB cap).
// Config is loaded from log4net.config which sits next to the dll in publish output.
var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
try { Directory.CreateDirectory(logsDir); } catch { /* never fail startup over logs dir */ }

var log4netConfigPath = Path.Combine(AppContext.BaseDirectory, "log4net.config");
if (!File.Exists(log4netConfigPath))
    log4netConfigPath = Path.Combine(Directory.GetCurrentDirectory(), "log4net.config");

if (File.Exists(log4netConfigPath))
{
    var repo = LogManager.GetRepository(System.Reflection.Assembly.GetExecutingAssembly());
    XmlConfigurator.ConfigureAndWatch(repo, new FileInfo(log4netConfigPath));
}
else
{
    BasicConfigurator.Configure(LogManager.GetRepository(System.Reflection.Assembly.GetExecutingAssembly()));
}

// Redirect Console.WriteLine / Console.Error.WriteLine into log4net
// so that all existing [OCI] / [OCR] / [OCR-LOG] logs land in the daily file.
Console.SetOut(new Log4NetTextWriter(isError: false));
Console.SetError(new Log4NetTextWriter(isError: true));

// ── Global exception handlers — log crashes before IIS kills the process ──
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    Console.Error.WriteLine($"[FATAL] Unhandled AppDomain exception (isTerminating={e.IsTerminating}): {e.ExceptionObject}");

TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Console.Error.WriteLine($"[WARN] Unobserved Task exception (marking observed to prevent crash): {e.Exception}");
    e.SetObserved(); // prevents process crash from fire-and-forget tasks
};

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddLog4Net(log4netConfigPath);

// Prevent BackgroundService exceptions from stopping the entire host process.
// Without this, an unhandled exception in DbCleanupService (or any other BackgroundService)
// would trigger IIS Rapid Fail Protection and stop the app pool.
builder.Services.Configure<HostOptions>(opts =>
    opts.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// ── Configuration ──
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── CORS ──
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// ── Database ──
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? throw new InvalidOperationException("DATABASE_URL not configured");

builder.Services.AddDbContext<TripExDbContext>(options =>
    options.UseSqlServer(connectionString, sql =>
        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)));

// ── Authentication (JWT + API Key) ──
var jwtSecret = builder.Configuration["Jwt:Secret"]
    ?? Environment.GetEnvironmentVariable("JWT_SECRET")
    ?? throw new InvalidOperationException("JWT_SECRET not configured");

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "TripEx.Api";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "TripEx.Client";

var apiKey = builder.Configuration["ApiKey:Key"]
    ?? Environment.GetEnvironmentVariable("API_KEY")
    ?? ""; // Optional — if empty, API key auth is disabled

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "MultiAuth";
        options.DefaultChallengeScheme = "MultiAuth";
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    })
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", options =>
    {
        options.ApiKey = apiKey;
        options.ClientName = builder.Configuration["ApiKey:ClientName"] ?? "TripExClient";
    })
    .AddPolicyScheme("MultiAuth", "JWT or API Key", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            // If X-Api-Key header present, use API Key scheme
            if (context.Request.Headers.ContainsKey("X-Api-Key"))
                return "ApiKey";

            // Otherwise fall back to JWT
            return JwtBearerDefaults.AuthenticationScheme;
        };
    });

// ── Services ──
builder.Services.AddHttpClient();
builder.Services.AddScoped<OracleAiService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<KnowledgeService>();
builder.Services.AddScoped<GeolocationService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<FileStorageService>();
builder.Services.AddHostedService<DbCleanupService>();

var app = builder.Build();

Console.WriteLine($"🚀 [STARTUP] App built at {DateTime.Now:HH:mm:ss.fff}");

// ── Ensure storage directory exists (fast, safe) ──
var storagePath = app.Configuration["Storage:LocalPath"]
    ?? Environment.GetEnvironmentVariable("STORAGE_PATH")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");
try { Directory.CreateDirectory(storagePath); } catch (Exception ex) { Console.WriteLine($"⚠️ Storage dir: {ex.Message}"); }

// ── Database init runs in BACKGROUND (does not block IIS startup) ──
// IIS aborts the process if startup takes > 120s. SQL Server connection
// during cold-start can easily exceed that, so we defer DB init.
_ = Task.Run(async () =>
{
    try
    {
        await Task.Delay(2000); // let the host finish binding ports first
        Console.WriteLine($"🗄️ [DB-INIT] Starting database initialization at {DateTime.Now:HH:mm:ss.fff}");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TripExDbContext>();

        var initSqlPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "init-db.sql");
        if (!File.Exists(initSqlPath))
            initSqlPath = Path.Combine(AppContext.BaseDirectory, "Data", "init-db.sql");

        if (File.Exists(initSqlPath))
        {
            var sql = await File.ReadAllTextAsync(initSqlPath);
            var batches = System.Text.RegularExpressions.Regex.Split(sql, @"^\s*GO\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            foreach (var batch in batches)
            {
                var trimmed = batch.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("PRINT")) continue;
                try { await db.Database.ExecuteSqlRawAsync(trimmed); }
                catch (Exception ex) { Console.WriteLine($"⚠️ [DB-INIT] Batch note: {ex.Message}"); }
            }
            Console.WriteLine($"✅ [DB-INIT] Database verified in {sw.ElapsedMilliseconds}ms");
        }
        else
        {
            Console.WriteLine("⚠️ [DB-INIT] init-db.sql not found, trying EnsureCreated...");
            try { await db.Database.EnsureCreatedAsync(); } catch (Exception ex) { Console.WriteLine($"⚠️ [DB-INIT] EnsureCreated: {ex.Message}"); }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ [DB-INIT] Background DB init failed: {ex.Message}");
    }
});

// ── Middleware ──
// if (app.Environment.IsDevelopment())
// {
     app.UseSwagger();
     app.UseSwaggerUI();
// }

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
