using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Page links go to the customer's own TAS (Roi, 2026-10-04). Until now every link Milo sent
/// started with the one baseUrl in Data/page-links.json — deveu/QA_3_70 — so a customer in
/// Avt_Test on taseu was sent into another system. A link opens in the whole TAS window, so the
/// other half of each test here is that nothing can turn it into a link to somewhere else.
/// </summary>
public class PageLinkBaseTests
{
    private const string Fallback = "https://deveu.combtas.com/QA_3_70";
    private static readonly string[] Hosts = { ChatService.DefaultPageLinkHosts };

    private static string Base(string? origin, string? instance, string[]? hosts = null)
        => ChatService.PageLinkBase(origin, instance, Fallback, hosts ?? Hosts);

    [Theory]
    [InlineData("https://taseu.combtas.com", "Avt_Test", "https://taseu.combtas.com/Avt_Test")]   // Roi's example
    [InlineData("https://deveu.combtas.com", "DEV_3_66", "https://deveu.combtas.com/DEV_3_66")]
    [InlineData("https://deveu.combtas.com/", "QA_3_70", "https://deveu.combtas.com/QA_3_70")]
    [InlineData("https://TASEU.combtas.com", " Avt_Test ", "https://taseu.combtas.com/Avt_Test")]
    [InlineData("https://combtas.com", "Avt", "https://combtas.com/Avt")]
    public void The_link_starts_at_the_customers_own_TAS(string origin, string instance, string expected)
    {
        Assert.Equal(expected, Base(origin, instance));
    }

    [Theory]
    [InlineData(null, "Avt_Test")]                                // TAS did not send the instance yet: no site to pair it with
    [InlineData("https://taseu.combtas.com", null)]               // site known, instance not: the default, as before
    [InlineData("https://taseu.combtas.com", "")]
    public void Without_both_halves_the_default_is_used(string? origin, string? instance)
    {
        Assert.Equal(Fallback, Base(origin, instance));
    }

    [Theory]
    [InlineData("http://taseu.combtas.com")]                      // not https
    [InlineData("https://evil.example")]
    [InlineData("https://combtas.com.evil.example")]
    [InlineData("https://evilcombtas.com")]                       // a suffix of the name, not a subdomain
    [InlineData("https://taseu.combtas.com:8443")]
    [InlineData("https://user@taseu.combtas.com")]
    [InlineData("https://taseu.combtas.com/Other_Instance")]      // an origin has no path
    [InlineData("https://taseu.combtas.com/?x=1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void A_site_that_is_not_an_allowed_https_TAS_falls_back(string origin)
    {
        Assert.Equal(Fallback, Base(origin, "Avt_Test"));
    }

    [Theory]
    [InlineData("Avt/Test")]
    [InlineData("..")]
    [InlineData("Avt Test")]
    [InlineData("Avt?x=1")]
    [InlineData("<script>")]
    public void An_odd_instance_falls_back(string instance)
    {
        Assert.Equal(Fallback, Base("https://taseu.combtas.com", instance));
    }

    [Fact]
    public void The_allowed_hosts_come_from_config()
    {
        Assert.Equal("https://tas.example.org/Avt", Base("https://tas.example.org", "Avt", new[] { "example.org" }));
        Assert.Equal(Fallback, Base("https://taseu.combtas.com", "Avt", new[] { "example.org" }));
    }

    [Theory]
    [InlineData(null, Fallback)]
    [InlineData("  ", Fallback)]
    [InlineData("https://taseu.combtas.com/Avt_Test/", "https://taseu.combtas.com/Avt_Test")]
    public void Each_server_can_set_its_own_default(string? configured, string expected)
    {
        Assert.Equal(expected, ChatService.DefaultPageLinkBase(configured, Fallback));
    }

    [Fact]
    public void The_widgets_flat_host_origin_reaches_the_identity_context()
    {
        var request = new ChatRequest { Text = "hi", HostOrigin = "https://taseu.combtas.com", HostInstance = "Avt_Test" };

        request.NormalizeWidgetShape();

        Assert.Equal("https://taseu.combtas.com", request.Widget?.HostOrigin);
        Assert.Equal("Avt_Test", request.Widget?.HostInstance);
    }

    [Fact]
    public void The_usage_import_still_reads_the_instance_with_the_origin_logged_before_it()
    {
        var lines = new[]
        {
            "2026-10-04 06:01:56.767 +00:00 [INFO ] [CHAT-CONTINUITY] source=web session=ed17dd93-0145-4110-aef5-fa941bc1b698 continued=False",
            "2026-10-04 06:01:56.991 +00:00 [INFO ] [WIDGET-CONTEXT] hasToken=False customerId=1 customerName=Racheli company=Main role=(null) pageContext=tasks.aspx locale=en-US origin=https://taseu.combtas.com instance=Avt_Test",
            "2026-10-04 06:01:57.004 +00:00 [INFO ] [OCI] Request body length=121077, model=google.gemini-2.5-pro (default)",
            "2026-10-04 06:02:01.943 +00:00 [INFO ] [OCI] usage prompt=100 completion=5 total=150 cached=-1 finish=stop",
            "2026-10-04 06:02:01.952 +00:00 [INFO ] [CHAT] session=ed17dd93-0145-4110-aef5-fa941bc1b698 user=x source=web intent=general rag=1c latency=4952ms",
        };

        Assert.Equal("Avt", Assert.Single(MiloUsageLogImport.Parse(lines)).Company);
    }
}
