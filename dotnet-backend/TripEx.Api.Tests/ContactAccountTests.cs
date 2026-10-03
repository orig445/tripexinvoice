using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// The customer's company in the ticket's Contact Info (Roi, 2026-09-29). The company is read
/// from the TAS address the customer works in, and the contact is put under a Desk account of
/// that name. Everything here is offline: the naming rule, the decision to link, and reading the
/// two Desk responses the linking depends on.
/// </summary>
public class ContactAccountTests
{
    // ── The company, from the TAS address ────────────────────────────────────────────────────

    [Theory]
    [InlineData("Avt_Test", "Avt")]   // Roi's own example: taseu.combtas.com/Avt_Test/... is Avt
    [InlineData("Avt", "Avt")]        // a production instance with no suffix
    [InlineData("QA_3_70", "QA")]
    [InlineData(" Avt_Test ", "Avt")]
    [InlineData("El-Al_Prod", "El-Al")]
    public void The_company_is_the_instance_name_up_to_its_first_underscore(string instance, string expected)
    {
        Assert.Equal(expected, ChatService.CompanyFromHostInstance(instance));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("_Test")]            // nothing before the underscore
    [InlineData("Avt Test")]         // a space is not something an instance name has
    [InlineData("<script>")]
    [InlineData("Avt.Test")]
    public void Nothing_odd_becomes_an_account_name(string? instance)
    {
        Assert.Null(ChatService.CompanyFromHostInstance(instance));
    }

    // ── Whether to link at all ───────────────────────────────────────────────────────────────

    private const string Fallback = "fallback@example.test";

    [Fact]
    public void A_customer_with_their_own_email_and_no_account_yet_is_linked()
    {
        Assert.True(ZohoTicketSyncWorker.ShouldLinkContactAccount("Avt", "racheli@avt.co.il", "1174385000000001", null, Fallback));
    }

    [Theory]
    [InlineData(null, "racheli@avt.co.il", "c1", null)]      // no company to name
    [InlineData("Avt", null, "c1", null)]                     // the fallback contact: shared by every chat without an email
    [InlineData("Avt", "", "c1", null)]
    [InlineData("Avt", "racheli@avt.co.il", null, null)]      // Desk did not say which contact
    [InlineData("Avt", "racheli@avt.co.il", "c1", "a9")]      // already under a company: an agent's choice stands
    public void Anything_else_is_left_alone(string? company, string? email, string? contactId, string? accountId)
    {
        Assert.False(ZohoTicketSyncWorker.ShouldLinkContactAccount(company, email, contactId, accountId, Fallback));
    }

    [Theory]
    [InlineData("fallback@example.test")]
    [InlineData(" FALLBACK@example.test ")]
    public void A_customer_writing_from_the_fallback_mailbox_never_puts_the_shared_contact_under_a_company(string email)
    {
        // Desk files them under the shared fallback contact; linking it would put every email-less
        // ticket under this one company.
        Assert.False(ZohoTicketSyncWorker.ShouldLinkContactAccount("QA", email, "c1", null, Fallback));
    }

    // ── Reading Desk's answers ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_created_ticket_says_which_contact_and_whether_it_has_an_account()
    {
        var (contact, account) = ZohoDeskService.ReadCreatedTicketLinks(
            "{\"id\":\"1174385000016736967\",\"contactId\":\"1174385000000431001\",\"accountId\":null}");

        Assert.Equal("1174385000000431001", contact);
        Assert.Null(account);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void An_unreadable_ticket_response_yields_nothing(string? json)
    {
        Assert.Equal((null, null), ZohoDeskService.ReadCreatedTicketLinks(json));
    }

    [Fact]
    public void Only_an_exact_name_counts_as_the_account()
    {
        // Search matches loosely: "Avt" finds "Avtech" too, and that must not become Avt's account.
        var json = "{\"data\":[{\"id\":\"11\",\"accountName\":\"Avtech\"},{\"id\":\"22\",\"accountName\":\" avt \"}]}";

        Assert.Equal("22", ZohoDeskService.FindAccountId(json, "Avt"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]                                                    // Desk answers 204 with no body when nothing matches
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"11\",\"accountName\":\"Avtech\"}]}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("truncated {")]
    public void No_exact_match_means_none_found(string? json)
    {
        Assert.Null(ZohoDeskService.FindAccountId(json, "Avt"));
    }

    [Fact]
    public void A_numeric_id_is_read_as_well()
    {
        Assert.Equal("22", ZohoDeskService.FindAccountId("{\"data\":[{\"id\":22,\"accountName\":\"Avt\"}]}", "Avt"));
    }

    // ── The widget field reaching the model ──────────────────────────────────────────────────

    [Fact]
    public void The_widgets_host_instance_is_carried_into_the_identity_context()
    {
        var request = new ChatRequest { Text = "hi", CustomerName = "Racheli", HostInstance = "Avt_Test" };

        request.NormalizeWidgetShape();

        Assert.Equal("Avt_Test", request.Widget?.HostInstance);
    }

    [Fact]
    public void The_host_instance_alone_is_enough_to_build_the_context()
    {
        var request = new ChatRequest { Text = "hi", HostInstance = "Avt_Test" };

        request.NormalizeWidgetShape();

        Assert.Equal("Avt_Test", request.Widget?.HostInstance);
        Assert.True(request.IsTasWidgetClient);
    }

    // ── A token without the account scopes ───────────────────────────────────────────────────
    // Every 401 throws the shared access token away, and Zoho mints at most ten per ten minutes.
    // Asking again on every new ticket could lock all of Zoho out, so a refusal pauses linking.
    // Nothing here leaves the process: a stub answers every request.

    private sealed class StubDesk : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public HttpStatusCode Search = HttpStatusCode.OK;
        public HttpStatusCode ContactPatch = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add($"{request.Method} {path}");
            var (status, body) = path switch
            {
                "/oauth/v2/token" => (HttpStatusCode.OK, "{\"access_token\":\"test-token\",\"expires_in\":3600}"),
                "/api/v1/accounts/search" => (Search, Search == HttpStatusCode.OK
                    ? "{\"data\":[{\"id\":\"77\",\"accountName\":\"QA\"}]}"
                    : "{\"errorCode\":\"SCOPE_MISMATCH\"}"),
                _ when path.StartsWith("/api/v1/contacts/") => (ContactPatch, "{}"),
                _ => (HttpStatusCode.OK, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        public int Searches => Calls.Count(c => c.EndsWith("/api/v1/accounts/search"));
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static ZohoDeskService Desk(StubDesk stub)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Zoho:Enabled"] = "true",
            ["Zoho:ClientId"] = "test-client",
            ["Zoho:ClientSecret"] = "test-secret",
            ["Zoho:RefreshToken"] = "test-refresh",
            ["Zoho:OrgId"] = "1",
            ["Zoho:DepartmentId"] = "2",
            ["Zoho:FallbackContactEmail"] = Fallback,
            ["Zoho:AccountsBaseUrl"] = "https://accounts.example.test",
            ["Zoho:ApiBaseUrl"] = "https://desk.example.test",
        }).Build();
        return new ZohoDeskService(new StubFactory(stub), config, NullLogger<ZohoDeskService>.Instance);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_search_pauses_linking_for_an_hour_instead_of_asking_on_every_ticket(HttpStatusCode refusal)
    {
        var stub = new StubDesk { Search = refusal };
        var desk = Desk(stub);
        var now = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        desk.UtcNow = () => now;

        Assert.False(await desk.LinkContactToAccountAsync("t1", "c1", "QA"));
        Assert.False(await desk.LinkContactToAccountAsync("t2", "c2", "QA"));
        Assert.False(await desk.LinkContactToAccountAsync("t3", "c3", "Avt"));
        Assert.Equal(1, stub.Searches);

        now = now + ZohoDeskService.AccountLinkPause + TimeSpan.FromMinutes(1);
        stub.Search = HttpStatusCode.OK;

        Assert.True(await desk.LinkContactToAccountAsync("t4", "c4", "QA"));
        Assert.Equal(2, stub.Searches);
    }

    [Fact]
    public async Task A_refused_contact_update_pauses_linking_too()
    {
        var stub = new StubDesk { ContactPatch = HttpStatusCode.Unauthorized };
        var desk = Desk(stub);

        Assert.False(await desk.LinkContactToAccountAsync("t1", "c1", "QA"));
        var callsAfterFirst = stub.Calls.Count;
        Assert.False(await desk.LinkContactToAccountAsync("t2", "c2", "QA"));

        Assert.Equal(callsAfterFirst, stub.Calls.Count);
    }

    [Fact]
    public async Task Any_other_refusal_does_not_pause_linking()
    {
        // A 422 is about this one request, not about the token, so the next ticket still tries.
        var stub = new StubDesk { Search = HttpStatusCode.UnprocessableEntity };
        var desk = Desk(stub);

        Assert.False(await desk.LinkContactToAccountAsync("t1", "c1", "QA"));
        Assert.False(await desk.LinkContactToAccountAsync("t2", "c2", "QA"));

        Assert.Equal(2, stub.Searches);
    }

    [Fact]
    public async Task A_working_token_links_and_remembers_the_account()
    {
        var stub = new StubDesk();
        var desk = Desk(stub);

        Assert.True(await desk.LinkContactToAccountAsync("t1", "c1", "QA"));
        Assert.True(await desk.LinkContactToAccountAsync("t2", "c2", "QA"));

        Assert.Equal(1, stub.Searches);
        Assert.Contains("PATCH /api/v1/contacts/c2", stub.Calls);
        Assert.Contains("PATCH /api/v1/tickets/t2", stub.Calls);
    }
}
