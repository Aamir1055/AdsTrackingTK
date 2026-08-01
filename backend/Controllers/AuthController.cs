using AdsTracking.Api.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using System.Security.Cryptography;

namespace AdsTracking.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly DbConnectionFactory _dbFactory;

    // Simple in-memory token store (cleared on app restart)
    private static readonly HashSet<string> ValidTokens = new();

    public AuthController(DbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Unauthorized(new { message = "Invalid username or password" });

        using var connection = _dbFactory.CreateConnection();
        await connection.OpenAsync();

        var hash = await connection.ExecuteScalarAsync<string>(
            "SELECT password_hash FROM dashboard_users WHERE username = @Username LIMIT 1",
            new { Username = request.Username });

        if (hash == null || !BCrypt.Net.BCrypt.Verify(request.Password, hash))
            return Unauthorized(new { message = "Invalid username or password" });

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ValidTokens.Add(token);

        return Ok(new { token });
    }

    [HttpGet("verify")]
    public IActionResult Verify([FromHeader(Name = "X-Dashboard-Token")] string? token)
    {
        if (!string.IsNullOrEmpty(token) && ValidTokens.Contains(token))
            return Ok(new { valid = true });

        return Unauthorized(new { valid = false });
    }

    public record LoginRequest(string Username, string Password);
}
