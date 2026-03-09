using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TripEx.Api.Data;
using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// Handles user registration, login, and JWT token generation
/// </summary>
public class AuthService
{
    private readonly TripExDbContext _db;
    private readonly IConfiguration _config;

    public AuthService(TripExDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        // Check if email already exists
        var existing = await _db.Profiles.FirstOrDefaultAsync(p => p.Email == request.Email);
        if (existing != null)
            return new AuthResponse { Success = false, Error = "Email already registered" };

        var userId = Guid.NewGuid();
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password);

        // Create user credential
        _db.UserCredentials.Add(new UserCredential
        {
            UserId = userId,
            Email = request.Email,
            PasswordHash = passwordHash
        });

        // Create profile
        _db.Profiles.Add(new Profile
        {
            UserId = userId,
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.Email.Split('@')[0]
        });

        // Assign default role
        _db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            Role = "user"
        });

        await _db.SaveChangesAsync();

        var token = GenerateToken(userId, request.Email, "user");

        return new AuthResponse
        {
            Success = true,
            Token = token,
            UserId = userId.ToString(),
            Email = request.Email,
            DisplayName = request.DisplayName ?? request.Email.Split('@')[0]
        };
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var credential = await _db.UserCredentials
            .FirstOrDefaultAsync(c => c.Email == request.Email);

        if (credential == null || !BCrypt.Net.BCrypt.Verify(request.Password, credential.PasswordHash))
            return new AuthResponse { Success = false, Error = "Invalid email or password" };

        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == credential.UserId);
        var role = await _db.UserRoles.FirstOrDefaultAsync(r => r.UserId == credential.UserId);

        var token = GenerateToken(credential.UserId, credential.Email, role?.Role ?? "user");

        return new AuthResponse
        {
            Success = true,
            Token = token,
            UserId = credential.UserId.ToString(),
            Email = credential.Email,
            DisplayName = profile?.DisplayName ?? credential.Email
        };
    }

    private string GenerateToken(Guid userId, string email, string role)
    {
        var secret = _config["Jwt:Secret"]
            ?? Environment.GetEnvironmentVariable("JWT_SECRET")
            ?? throw new InvalidOperationException("JWT_SECRET not configured");

        var issuer = _config["Jwt:Issuer"] ?? "TripEx.Api";
        var audience = _config["Jwt:Audience"] ?? "TripEx.Client";
        var expirationHours = int.TryParse(_config["Jwt:ExpirationHours"], out var h) ? h : 24;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(expirationHours),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
