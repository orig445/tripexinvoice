namespace TripEx.Api.Models;

/// <summary>
/// What a client says an attachment is FOR, sent as ChatRequest.AttachmentIntent.
///
/// The chat endpoint used to have no such field and no need for one: an attachment arrived as
/// Type="image" and meant exactly one thing — scan this receipt into expense fields. So every
/// picture was OCR'd, including the screenshot someone attached to ask "why does this screen
/// show no trips?", and the answer to that question was a list of merchant/VAT/total fields
/// extracted from a page that has none. That is the behaviour this field exists to end.
/// </summary>
public static class AttachmentIntents
{
    /// <summary>Scan it as an invoice/receipt — the old Type="image" behaviour, unchanged.</summary>
    public const string Scan = "scan";

    /// <summary>Read it as context for the question in the same turn, never as a receipt.</summary>
    public const string Ask = "ask";

    /// <summary>
    /// Let the server decide per attachment (the default when a client says nothing). Only an
    /// invoice/receipt/tax document is scanned; anything else is context for the question.
    /// </summary>
    public const string Auto = "auto";

    /// <summary>Lower-cased and trimmed, with anything unrecognised treated as <see cref="Auto"/>.</summary>
    public static string Normalize(string? intent)
    {
        var value = intent?.Trim().ToLowerInvariant();
        return value switch
        {
            Scan => Scan,
            Ask => Ask,
            _ => Auto,
        };
    }
}

/// <summary>Where a turn's attachments go: the invoice scanner, or the conversation.</summary>
public enum AttachmentRoute
{
    /// <summary>OCR the attachment and reply with the extracted invoice fields.</summary>
    Scan,

    /// <summary>Hand the attachment to the conversational model as context for the user's words.</summary>
    Ask,
}

/// <summary>
/// The decision itself, as a pure function so it is testable without a model call, a database or
/// an HTTP client — the same reason OracleAiService.ResolveChatTarget is one.
/// </summary>
public static class AttachmentRouting
{
    /// <summary>
    /// Is a model call needed to decide? Only for <see cref="AttachmentIntents.Auto"/>, and only
    /// while auto-routing is switched on — an explicit intent is already the answer, and with the
    /// feature off the answer is the old one. Keeps the classifier off the two paths that cannot
    /// use its verdict, so nobody is billed for a reply that is discarded.
    /// </summary>
    public static bool ShouldClassify(string? requestedIntent, bool autoRouteEnabled)
        => autoRouteEnabled && AttachmentIntents.Normalize(requestedIntent) == AttachmentIntents.Auto;

    /// <summary>
    /// Which way this turn's attachments go.
    /// </summary>
    /// <param name="requestedIntent">ChatRequest.AttachmentIntent, in whatever shape it arrived.</param>
    /// <param name="autoRouteEnabled">Milo:ImageAutoRoute — off restores the pre-change behaviour
    /// (everything is scanned) with a config edit and a restart, no deploy.</param>
    /// <param name="hasQuestionText">Whether the user sent words of their own with the attachment.</param>
    /// <param name="classifierAnswer">What the classifier said ("scan"/"ask"), or null when it was
    /// not asked or could not answer.</param>
    public static AttachmentRoute Decide(
        string? requestedIntent,
        bool autoRouteEnabled,
        bool hasQuestionText,
        string? classifierAnswer)
    {
        var intent = AttachmentIntents.Normalize(requestedIntent);

        // An explicit intent is the client telling us what the user pressed — the camera button
        // is "scan this receipt" and nothing else, so it must not depend on a model's opinion.
        if (intent == AttachmentIntents.Scan) return AttachmentRoute.Scan;
        if (intent == AttachmentIntents.Ask) return AttachmentRoute.Ask;

        if (!autoRouteEnabled) return AttachmentRoute.Scan;

        var verdict = classifierAnswer?.Trim().ToLowerInvariant();
        if (verdict == AttachmentIntents.Scan) return AttachmentRoute.Scan;
        if (verdict == AttachmentIntents.Ask) return AttachmentRoute.Ask;

        // No verdict: the classifier was skipped, timed out, or answered something unparseable.
        // Fall back on the one signal that needs no model at all — whether the user actually
        // asked something. With a question, answering it is the useful failure; without one, an
        // attachment on its own has always meant "scan this", so a failure changes nothing.
        return hasQuestionText ? AttachmentRoute.Ask : AttachmentRoute.Scan;
    }
}
