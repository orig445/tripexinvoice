using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// A second TAS user on the same computer must not resume the first one's conversation (Roi,
/// 2026-10-04). Only a real contradiction counts: a field both sides know, with different values.
/// </summary>
public class SessionOwnerTests
{
    private static SessionOwner Of(string? email = null, string? customerId = null, string? instance = null)
        => SessionOwner.From(new WidgetIdentityContext { Email = email, CustomerId = customerId, HostInstance = instance });

    [Fact]
    public void Another_email_is_another_user()
    {
        Assert.True(Of("racheli@avt.co.il", "1").BelongsToSomeoneElseThan(Of("dana@avt.co.il", "1")));
    }

    [Fact]
    public void The_same_user_is_the_same_user_whatever_the_case_and_spaces()
    {
        Assert.False(Of("Racheli@Avt.co.il", "1", "Avt_Test").BelongsToSomeoneElseThan(Of(" racheli@avt.co.il ", "1", "avt_test")));
    }

    [Fact]
    public void Another_customer_id_or_instance_is_another_user_even_without_an_email()
    {
        Assert.True(Of(customerId: "1").BelongsToSomeoneElseThan(Of(customerId: "2")));
        Assert.True(Of(instance: "Avt_Test").BelongsToSomeoneElseThan(Of(instance: "QA_3_70")));
    }

    [Theory]
    [InlineData(null, "1", null, "dana@avt.co.il", "1", null)]      // the email arrived later: no contradiction
    [InlineData("racheli@avt.co.il", null, null, null, "7", null)]   // nothing both sides know
    [InlineData("racheli@avt.co.il", "1", null, null, null, null)]   // a request with no identity at all
    public void An_unknown_field_is_not_a_contradiction(string? e1, string? c1, string? i1, string? e2, string? c2, string? i2)
    {
        Assert.False(Of(e1, c1, i1).BelongsToSomeoneElseThan(Of(e2, c2, i2)));
    }

    [Fact]
    public void The_email_is_kept_only_as_a_hash()
    {
        var owner = Of("racheli@avt.co.il");

        Assert.NotNull(owner.EmailHash);
        Assert.Equal(64, owner.EmailHash!.Length);
        Assert.DoesNotContain("racheli", owner.EmailHash);
        Assert.Equal(SessionOwner.HashEmail("RACHELI@avt.co.il "), owner.EmailHash);
    }

    [Fact]
    public void No_identity_is_an_empty_owner()
    {
        Assert.True(SessionOwner.From(null).IsEmpty);
        Assert.True(Of(" ", "", null).IsEmpty);
        Assert.False(Of(customerId: "1").IsEmpty);
    }

    [Fact]
    public void Long_values_fit_the_column()
    {
        Assert.Equal(64, Of(customerId: new string('9', 200)).CustomerId!.Length);
    }
}
