using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using TripEx.Api.Auth;
using TripEx.Api.Data;
using TripEx.Api.Services;

// ── Serilog: daily-rolling file logs in <app>/logs/ ──
// File pattern: logs/tripex-YYYYMMDD.log (new file every day, keep last 30 days)
var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logsDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logsDir, "tripex-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50 * 1024 * 1024, // 50 MB per file cap
        rollOnFileSizeLimit: true,
        shared: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// Redirect Console.WriteLine / Console.Error.WriteLine into Serilog
// so that all existing [OCI] / [OCR] / [OCR-LOG] logs land in the daily file.
Console.SetOut(new SerilogTextWriter(isError: false));
Console.SetError(new SerilogTextWriter(isError: true));

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

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
    options.UseSqlServer(connectionString));

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

var app = builder.Build();

// ── Ensure database tables exist ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TripExDbContext>();
    
    // Run init-db.sql to create missing tables — split by GO batches
    var initSqlPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "init-db.sql");
    if (!File.Exists(initSqlPath))
        initSqlPath = Path.Combine(AppContext.BaseDirectory, "Data", "init-db.sql");
    
    if (File.Exists(initSqlPath))
    {
        var sql = File.ReadAllText(initSqlPath);
        var batches = System.Text.RegularExpressions.Regex.Split(sql, @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("PRINT")) continue;
            try
            {
                db.Database.ExecuteSqlRaw(trimmed);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Batch note: {ex.Message}");
            }
        }
        Console.WriteLine("✅ Database tables verified/created from init-db.sql");
    }
    else
    {
        Console.WriteLine("⚠️ init-db.sql not found, trying EnsureCreated...");
        try { db.Database.EnsureCreated(); } catch { }
    }
}

// ── Ensure storage directory exists ──
var storagePath = app.Configuration["Storage:LocalPath"]
    ?? Environment.GetEnvironmentVariable("STORAGE_PATH")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");
Directory.CreateDirectory(storagePath);

// ── Middleware ──
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
