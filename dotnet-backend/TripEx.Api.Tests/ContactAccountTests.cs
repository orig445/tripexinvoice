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

    [Fact]
    public void A_customer_with_their_own_email_and_no_account_yet_is_linked()
    {
        Assert.True(ZohoTicketSyncWorker.ShouldLinkContactAccount("Avt", "racheli@avt.co.il", "1174385000000001", null));
    }

    [Theory]
    [InlineData(null, "racheli@avt.co.il", "c1", null)]      // no company to name
    [InlineData("Avt", null, "c1", null)]                     // the fallback contact: shared by every chat without an email
    [InlineData("Avt", "", "c1", null)]
    [InlineData("Avt", "racheli@avt.co.il", null, null)]      // Desk did not say which contact
    [InlineData("Avt", "racheli@avt.co.il", "c1", "a9")]      // already under a company: an agent's choice stands
    public void Anything_else_is_left_alone(string? company, string? email, string? contactId, string? accountId)
    {
        Assert.False(ZohoTicketSyncWorker.ShouldLinkContactAccount(company, email, contactId, accountId));
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
}
