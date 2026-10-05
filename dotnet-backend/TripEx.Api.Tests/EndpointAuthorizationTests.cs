using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TripEx.Api.Auth;
using TripEx.Api.Controllers;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Who may call which chatbot endpoint (Roi, 2026-10-05). The API authenticates every TAS widget
/// session — ApiKeyAuthenticationHandler accepts any well-formed GUID as a key — and anyone who
/// self-registers, so a plain [Authorize] is open to the world. What changes shared chatbot state
/// or reads other users' conversations is admin only; the widget's own endpoints must stay open, or
/// the chat stops working.
///
/// Evaluated with ASP.NET's real authorization service against the endpoints' own attributes, and
/// with principals made the way the API makes them: the API-key one by the real handler, the JWT
/// ones with the role claim AuthService puts in the token.
/// </summary>
public class EndpointAuthorizationTests
{
    public enum Access { Anonymous, AnyCaller, AdminOnly }

    /// <summary>
    /// Every action of every chatbot controller, and who may call it. An endpoint added to one of
    /// these controllers fails Every_endpoint_is_classified until it is listed here — on purpose.
    /// </summary>
    private static readonly Dictionary<string, Access> Expected = new()
    {
        ["Knowledge.Upload"] = Access.AdminOnly,
        ["Knowledge.ListDocuments"] = Access.AdminOnly,
        ["Knowledge.DeleteDocument"] = Access.AdminOnly,
        ["Knowledge.UpdateTags"] = Access.AdminOnly,
        ["Knowledge.Process"] = Access.AdminOnly,
        ["Chat.ListTickets"] = Access.AdminOnly,

        // The widget: a message, and the poll for agent replies (held to the owner rule instead).
        ["Chat.Chat"] = Access.AnyCaller,
        ["Chat.Updates"] = Access.AnyCaller,

        // Open by design, each with its own lock or none needed: the widget's New chat button, the
        // Zoho webhook (secret in the URL), the usage page (its own key), sign-in and health.
        ["WidgetSession.ClearSession"] = Access.Anonymous,
        ["ZohoWebhook.Validate"] = Access.Anonymous,
        ["ZohoWebhook.ThreadAdded"] = Access.Anonymous,
        ["Usage.Page"] = Access.Anonymous,
        ["Usage.Summary"] = Access.Anonymous,
        ["Usage.Export"] = Access.Anonymous,
        ["Usage.ImportLogs"] = Access.Anonymous,
        ["Auth.Register"] = Access.Anonymous,
        ["Auth.Login"] = Access.Anonymous,
        ["Health.Health"] = Access.Anonymous,
    };

    private static readonly Type[] ChatbotControllers =
    {
        typeof(KnowledgeController), typeof(ChatController), typeof(WidgetSessionController),
        typeof(ZohoWebhookController), typeof(UsageController), typeof(AuthController), typeof(HealthController),
    };

    [Fact]
    public void Every_endpoint_is_classified()
    {
        var actual = ChatbotControllers.SelectMany(Actions).Select(a => a.Name).OrderBy(n => n).ToList();
        Assert.Equal(Expected.Keys.OrderBy(n => n).ToList(), actual);
    }

    [Fact]
    public async Task Each_endpoint_admits_exactly_who_it_should()
    {
        var tasSession = await ApiKeyPrincipal(Guid.NewGuid().ToString().ToUpperInvariant());
        var staticKey = await ApiKeyPrincipal(StaticKey);
        var selfRegistered = JwtPrincipal("user");
        var admin = JwtPrincipal("admin");
        var nobody = new ClaimsPrincipal(new ClaimsIdentity());

        var wrong = new List<string>();
        foreach (var (name, controller, method) in ChatbotControllers.SelectMany(Actions))
        {
            var expected = Expected[name];
            async Task Check(string who, ClaimsPrincipal user, bool shouldPass)
            {
                if (await Allowed(controller, method, user) != shouldPass)
                    wrong.Add($"{name}: {who} {(shouldPass ? "refused" : "admitted")}");
            }

            await Check("an admin", admin, true);
            await Check("a TAS session token", tasSession, expected != Access.AdminOnly);
            await Check("the static API key", staticKey, expected != Access.AdminOnly);
            await Check("a self-registered user", selfRegistered, expected != Access.AdminOnly);
            await Check("an unauthenticated caller", nobody, expected == Access.Anonymous);
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public async Task A_tas_session_token_is_authenticated_but_has_no_role()
    {
        var principal = await ApiKeyPrincipal("A86F73DF-923B-4395-9DC6-9A05A5300B54");

        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.False(principal.IsInRole("admin"));
        Assert.Null(principal.FindFirst(ClaimTypes.Role));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private const string StaticKey = "test-static-key";

    private static IEnumerable<(string Name, Type Controller, MethodInfo Method)> Actions(Type controller)
        => controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Select(m => ($"{controller.Name.Replace("Controller", "")}.{m.Name}", controller, m));

    /// <summary>What MVC's authorization filter decides for this action, from its own attributes.</summary>
    private static async Task<bool> Allowed(Type controller, MethodInfo method, ClaimsPrincipal user)
    {
        if (controller.IsDefined(typeof(AllowAnonymousAttribute), true) || method.IsDefined(typeof(AllowAnonymousAttribute), true))
            return true;

        var data = controller.GetCustomAttributes<AuthorizeAttribute>(true)
            .Concat(method.GetCustomAttributes<AuthorizeAttribute>(true))
            .Cast<IAuthorizeData>()
            .ToList();
        if (data.Count == 0) return true;

        var services = new ServiceCollection().AddLogging().AddAuthorization().BuildServiceProvider();
        var policy = await AuthorizationPolicy.CombineAsync(services.GetRequiredService<IAuthorizationPolicyProvider>(), data);
        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, policy!);
        return result.Succeeded;
    }

    /// <summary>A caller as the real ApiKeyAuthenticationHandler authenticates it, from TAS's "Token" header.</summary>
    private static async Task<ClaimsPrincipal> ApiKeyPrincipal(string token)
    {
        var handler = new ApiKeyAuthenticationHandler(
            new FixedOptions(new ApiKeyAuthenticationOptions { ApiKey = StaticKey, ClientName = "TripExClient" }),
            NullLoggerFactory.Instance, UrlEncoder.Default);
        var context = new DefaultHttpContext();
        context.Request.Headers["Token"] = token;
        await handler.InitializeAsync(new AuthenticationScheme("ApiKey", null, typeof(ApiKeyAuthenticationHandler)), context);

        var result = await handler.AuthenticateAsync();
        Assert.True(result.Succeeded);
        return result.Principal!;
    }

    /// <summary>A signed-in user as JwtBearer hands it on: the role AuthService wrote, under ClaimTypes.Role.</summary>
    private static ClaimsPrincipal JwtPrincipal(string role) => new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
        new Claim(ClaimTypes.Role, role),
    }, "Bearer"));

    private sealed class FixedOptions(ApiKeyAuthenticationOptions value) : IOptionsMonitor<ApiKeyAuthenticationOptions>
    {
        public ApiKeyAuthenticationOptions CurrentValue => value;
        public ApiKeyAuthenticationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ApiKeyAuthenticationOptions, string?> listener) => null;
    }
}
