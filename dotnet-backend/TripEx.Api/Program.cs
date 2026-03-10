using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TripEx.Api.Auth;
using TripEx.Api.Data;
using TripEx.Api.Services;

var builder = WebApplication.CreateBuilder(args);

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
    options.UseNpgsql(connectionString));

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
