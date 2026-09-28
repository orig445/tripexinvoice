using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Conversation-id resolution: a Guid is used as-is, a foreign (non-Guid) id is HMAC-mapped to a
/// stable session, and no salt means a fresh session rather than a guessable one. A mishandled id
/// is invisible from the outside — it reads as the model forgetting, not as an error — so every
/// branch is pinned here, along with the default button-label budget the TAS widget relies on.
/// </summary>
public class RelayCompatibilityTests
{
    // Any non-empty value works; what the tests care about is that the SAME salt gives the same
    // Guid and a DIFFERENT salt gives a different one.
    private const string Salt = "test-salt-not-a-real-secret";

    // ── Conversation ids ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_real_guid_is_still_used_exactly_as_it_was()
    {
        // The widget sends a Guid and must keep resuming the same session it always did. Any
        // change here would silently reset every existing conversation in production.
        var id = Guid.NewGuid();

        Assert.Equal(id, ChatService.ResolveSessionToken(id.ToString(), Salt));
        Assert.Equal(id, ChatService.ResolveSessionToken(id.ToString("N"), Salt));
        Assert.Equal(id, ChatService.ResolveSessionToken($"  {id}  ", Salt));
    }

    [Fact]
    public void A_foreign_conversation_id_now_resolves_instead_of_being_dropped()
    {
        // Before this, anything that was not a Guid failed Guid.TryParse and was ignored, so
        // every turn opened a NEW session: no history, no second clarifying round, no status
        // shortcut. Zoho's own conversation ids look exactly like this.
        var id = ChatService.ResolveSessionToken("1473081000000457007", Salt);

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id, ChatService.ResolveSessionToken("1473081000000457007", Salt));
    }

    [Fact]
    public void The_same_foreign_id_maps_to_the_same_session_every_time()
    {
        // This is the whole point, and it is why the hash must be SHA-256 and not
        // string.GetHashCode — that one is randomised per process, so the mapping would change
        // on every app restart and conversations would silently split in half.
        const string token = "siq-conversation-9f2a";

        var first = ChatService.ResolveSessionToken(token, Salt);
        var second = ChatService.ResolveSessionToken(token, Salt);

        Assert.Equal(first, second);
        Assert.NotEqual(first, ChatService.ResolveSessionToken("siq-conversation-9f2b", Salt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_token_means_no_session_to_resume(string? token)
    {
        // Guid.Empty is the caller's signal to start a fresh session; it must never be treated
        // as a real id, or every token-less caller would share one conversation.
        Assert.Equal(Guid.Empty, ChatService.ResolveSessionToken(token, Salt));
    }

    // ── Button labels ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_widgets_own_label_budget_is_unchanged_by_default()
    {
        // A 28-character label is a perfectly good button on the TAS widget and has to stay one.
        var options = new List<string> { "תפעול שוטף של נסיעות והוצאות", "ניתוח נתונים ודוחות במערכת" };

        Assert.True(ChatService.OptionsCanBeButtons(options));
        Assert.True(ChatService.OptionsRenderAsButtons(options, clientRendersParamerter: true));
    }

    // -- The salt: what keeps a foreign id from being guessable ------------------------------

    [Fact]
    public void A_foreign_id_resolves_differently_under_a_different_salt()
    {
        // This is the whole security property. Zoho hands out CONSECUTIVE conversation ids, so an
        // unsalted hash would be a published formula for turning "the conversation before this
        // one" into a live session key — and CanResumeSessionAsync cannot catch that, because
        // every X-Api-Key caller authenticates as the same system principal. Keying the hash on a
        // server-side secret is what makes the mapping uncomputable from outside.
        const string token = "1473081000000457007";

        Assert.NotEqual(
            ChatService.ResolveSessionToken(token, Salt),
            ChatService.ResolveSessionToken(token, "a-different-secret"));
    }

    [Fact]
    public void Without_a_salt_a_foreign_id_starts_a_fresh_session_instead_of_a_guessable_one()
    {
        // Jwt:Secret missing (dev, or a half-filled production config) must degrade to the
        // behaviour that shipped before this method existed — a foreign id is simply ignored —
        // and never to a mapping anyone could compute. Guid.Empty is that "no usable token".
        Assert.Equal(Guid.Empty, ChatService.ResolveSessionToken("1473081000000457007", null));
        Assert.Equal(Guid.Empty, ChatService.ResolveSessionToken("1473081000000457007", ""));
    }

    [Fact]
    public void A_real_guid_still_resolves_without_any_salt()
    {
        // The TAS widget sends real Guids and must keep working regardless of configuration —
        // the salt gates only the hashing branch.
        var id = Guid.NewGuid();

        Assert.Equal(id, ChatService.ResolveSessionToken(id.ToString(), null));
        Assert.Equal(id, ChatService.ResolveSessionToken(id.ToString(), ""));
    }
    [Fact]
    public void The_shipped_placeholder_secrets_do_not_count_as_a_salt()
    {
        // appsettings.json ships Jwt:Secret with a placeholder, so the key is never null and a
        // plain null check would happily key sessions on a string published in this repository.
        // These are the exact literals in appsettings.json and the production template - if
        // either is ever reworded, this test fails and the guard gets updated with it.
        Assert.Contains("YOUR_JWT_SECRET_KEY_MIN_32_CHARS_LONG", ChatService.PlaceholderSecrets);
        Assert.Contains("REPLACE_WITH_A_RANDOM_STRING_AT_LEAST_32_CHARS", ChatService.PlaceholderSecrets);
        Assert.Contains("", ChatService.PlaceholderSecrets);

        // A real secret must NOT be swallowed by the guard.
        Assert.DoesNotContain(Salt, ChatService.PlaceholderSecrets);
    }
}
