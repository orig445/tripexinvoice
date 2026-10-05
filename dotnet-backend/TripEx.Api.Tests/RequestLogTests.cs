using System.Xml.Linq;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The request log names a request by its path only (Roi, 2026-10-05): ASP.NET's own request lines
/// wrote the full URL, which put the widget's conversation id (?sessionToken=) and the Zoho webhook
/// secret (a path segment) into the log every few seconds. See RequestLog.
/// </summary>
public class RequestLogTests
{
    [Theory]
    [InlineData("/DEV_AI/api/chat/updates", "/DEV_AI/api/chat/updates")]
    [InlineData("/DEV_AI/api/chat", "/DEV_AI/api/chat")]
    [InlineData("/DEV_AI/AI/Message/ClearSession", "/DEV_AI/AI/Message/ClearSession")]
    [InlineData("/DEV_AI/api/zoho/desk/thread/Zx9-secret-0123456789abcdef", "/DEV_AI/api/zoho/desk/thread/***")]
    [InlineData("/api/zoho/desk/thread/Zx9-secret-0123456789abcdef", "/api/zoho/desk/thread/***")]
    [InlineData("/DEV_AI/API/Zoho/Desk/Thread/Zx9-secret", "/DEV_AI/API/Zoho/Desk/Thread/***")]
    [InlineData("/api/zoho/desk/thread/Zx9-secret/extra", "/api/zoho/desk/thread/***/extra")]
    [InlineData("/api/zoho/desk/thread/", "/api/zoho/desk/thread/")]
    [InlineData("/DEV_AI/usage", "/DEV_AI/usage")]
    public void The_path_is_logged_with_the_webhook_secret_hidden(string path, string expected)
    {
        Assert.Equal(expected, RequestLog.SafePath(path));
    }

    [Theory]
    [InlineData("/DEV_AI/api/chat/updates?sessionToken=413494f3-0000-0000-0000-000000000001&since=2026-10-05T08:00:00Z", "/DEV_AI/api/chat/updates")]
    [InlineData("/DEV_AI/AI/Message/ClearSession?sessionToken=413494f3-0000-0000-0000-000000000001", "/DEV_AI/AI/Message/ClearSession")]
    [InlineData("/api/zoho/desk/thread/Zx9-secret?x=1", "/api/zoho/desk/thread/***")]
    public void A_query_string_is_never_logged(string pathAndQuery, string expected)
    {
        Assert.Equal(expected, RequestLog.SafePath(pathAndQuery));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?sessionToken=413494f3-0000-0000-0000-000000000001")]
    public void No_path_is_logged_as_the_root(string? path)
    {
        Assert.Equal("/", RequestLog.SafePath(path));
    }

    [Fact]
    public void The_loggers_that_write_full_urls_are_held_at_warn()
    {
        // log4net.config as it is published: copied next to the test assembly from TripEx.Api.
        var config = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "log4net.config"));
        string? Level(string logger) => config.Root!.Elements("logger")
            .FirstOrDefault(l => (string?)l.Attribute("name") == logger)?
            .Element("level")?.Attribute("value")?.Value;

        Assert.Equal("WARN", Level("Microsoft.AspNetCore.Hosting.Diagnostics"));
        Assert.Equal("WARN", Level("System.Net.Http.HttpClient"));
        // The narrower request-outcome loggers stay: none of them writes a URL.
        Assert.Equal("INFO", Level("Microsoft.AspNetCore.Authentication"));
        Assert.Equal("INFO", Level("Microsoft.AspNetCore.Authorization"));
        Assert.Equal("INFO", Level("Microsoft.AspNetCore.Mvc.Infrastructure"));
    }
}
