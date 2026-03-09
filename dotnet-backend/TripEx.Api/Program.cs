using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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

// ── Authentication (Supabase JWT) ──
var supabaseUrl = builder.Configuration["Supabase:Url"]
    ?? Environment.GetEnvironmentVariable("SUPABASE_URL")
    ?? throw new InvalidOperationException("SUPABASE_URL not configured");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"{supabaseUrl}/auth/v1";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidateAudience = true,
            ValidAudience = "authenticated",
            ValidateLifetime = true,
        };
        // Supabase uses HMAC, fetch JWKS
        options.MetadataAddress = $"{supabaseUrl}/auth/v1/.well-known/openid-configuration";
    });

// ── Services ──
builder.Services.AddHttpClient();
builder.Services.AddScoped<OracleAiService>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<KnowledgeService>();
builder.Services.AddScoped<GeolocationService>();

var app = builder.Build();

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
