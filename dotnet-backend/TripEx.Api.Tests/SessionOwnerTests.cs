using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TripEx.Api.Data;
using TripEx.Api.Models;
using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// A second TAS user on the same computer must not resume — or read the agent replies of — the
/// first one's conversation (Roi, 2026-10-04; the rule tightened 2026-10-05 and shared with the
/// widget). The email is the per-user key: TAS's customerId is the company, so it decides nothing
/// once an email is known. A request with no identity never matches a conversation that has an
/// owner, a conversation nobody owns is not adopted once it has messages, and a database that
/// cannot say whose a conversation is means a new one (fail closed).
///
/// The ProcessAsync and ResolveOwnedSessionAsync tests run the real ChatService against a scripted
/// database (EF interceptors answer every command) and an HTTP stub that refuses every call: the
/// turns used are the empty-text welcome turn, which never reaches the model.
/// </summary>
public class SessionOwnerTests
{
    private static SessionOwner Of(string? email = null, string? customerId = null, string? instance = null)
        => SessionOwner.From(new WidgetIdentityContext { Email = email, CustomerId = customerId, HostInstance = instance });

    // ── The rule ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Another_email_is_another_user_even_in_the_same_company()
    {
        Assert.True(Of("racheli@avt.example.test", "1").BelongsToSomeoneElseThan(Of("dana@avt.example.test", "1")));
    }

    [Fact]
    public void The_same_email_is_the_same_user_whatever_the_customer_id_says()
    {
        // customerId is company-level, and a TAS session can report it differently or not at all.
        Assert.False(Of("racheli@avt.example.test", "1").BelongsToSomeoneElseThan(Of("racheli@avt.example.test", "7")));
        Assert.False(Of("racheli@avt.example.test", "1").BelongsToSomeoneElseThan(Of("racheli@avt.example.test")));
        Assert.False(Of("racheli@avt.example.test").BelongsToSomeoneElseThan(Of("racheli@avt.example.test", "1", "Avt_Test")));
    }

    [Fact]
    public void The_same_user_is_the_same_user_whatever_the_case_and_spaces()
    {
        Assert.False(Of("Racheli@Avt.Example.Test", "1", "Avt_Test").BelongsToSomeoneElseThan(Of(" racheli@avt.example.test ", "1", "avt_test")));
    }

    [Fact]
    public void A_recorded_email_and_a_request_without_one_are_not_the_same_user()
    {
        Assert.True(Of("racheli@avt.example.test", "1").BelongsToSomeoneElseThan(Of(customerId: "1")));
        Assert.True(Of("racheli@avt.example.test", instance: "Avt_Test").BelongsToSomeoneElseThan(Of(customerId: "1", instance: "Avt_Test")));
    }

    [Fact]
    public void Another_instance_is_another_user_even_with_the_same_email()
    {
        Assert.True(Of("racheli@avt.example.test", instance: "Avt_Test").BelongsToSomeoneElseThan(Of("racheli@avt.example.test", instance: "QA_3_70")));
    }

    [Theory]
    [InlineData(null, "1", null)]
    [InlineData("racheli@avt.example.test", null, null)]
    [InlineData(null, null, "Avt_Test")]
    [InlineData("racheli@avt.example.test", "1", "Avt_Test")]
    public void A_request_with_no_identity_never_matches_an_owned_conversation(string? email, string? customerId, string? instance)
    {
        Assert.True(Of(email, customerId, instance).BelongsToSomeoneElseThan(SessionOwner.None));
        Assert.True(Of(email, customerId, instance).BelongsToSomeoneElseThan(Of(" ", "", null)));
    }

    [Fact]
    public void Without_a_recorded_email_the_customer_id_and_instance_decide_where_both_sides_know_them()
    {
        Assert.True(Of(customerId: "1").BelongsToSomeoneElseThan(Of(customerId: "2")));
        Assert.True(Of(instance: "Avt_Test").BelongsToSomeoneElseThan(Of(instance: "QA_3_70")));
        Assert.False(Of(customerId: "1").BelongsToSomeoneElseThan(Of("racheli@avt.example.test", "1")));
        // Nothing both sides know: no contradiction, as before.
        Assert.False(Of(customerId: "1").BelongsToSomeoneElseThan(Of(instance: "Avt_Test")));
        Assert.False(Of(customerId: "1").BelongsToSomeoneElseThan(Of("racheli@avt.example.test")));
    }

    [Fact]
    public void A_conversation_with_no_owner_contradicts_nobody()
    {
        // Whether it may be taken over is ChatService's call (see the ProcessAsync tests below).
        Assert.False(SessionOwner.None.BelongsToSomeoneElseThan(Of("racheli@avt.example.test", "1")));
        Assert.False(SessionOwner.None.BelongsToSomeoneElseThan(SessionOwner.None));
    }

    [Fact]
    public void The_email_is_kept_only_as_a_hash()
    {
        var owner = Of("racheli@avt.example.test");

        Assert.NotNull(owner.EmailHash);
        Assert.Equal(64, owner.EmailHash!.Length);
        Assert.DoesNotContain("racheli", owner.EmailHash);
        Assert.Equal(SessionOwner.HashEmail("RACHELI@avt.example.test "), owner.EmailHash);
    }

    [Fact]
    public void The_headers_of_a_poll_read_like_the_fields_of_a_message()
    {
        Assert.Equal(Of("racheli@avt.example.test", "1", "Avt_Test"),
            SessionOwner.FromHeaders(" Racheli@avt.example.test", "1 ", "AVT_TEST"));
        // encodeURIComponent on the widget side must not turn the same user into another one.
        Assert.Equal(Of("racheli@avt.example.test", "1", "Avt_Test"),
            SessionOwner.FromHeaders("racheli%40avt.example.test", "1", "Avt_Test"));
        Assert.True(SessionOwner.FromHeaders(null, "", " ").IsEmpty);
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

    // ── POST /api/chat: resume, or a new conversation ────────────────────────────────────────

    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Racheli = "racheli@avt.example.test";

    [Fact]
    public async Task The_same_email_resumes_and_the_missing_fields_are_filled_in_never_overwritten()
    {
        var h = new Harness { Owner = Of(Racheli, "1") };

        var response = await h.Turn(Racheli, customerId: "7", instance: "Avt_Test");

        Assert.Equal(SessionId.ToString(), response.SessionId);
        var updates = h.Db.OwnerWrites.Where(w => w.Sql.StartsWith("UPDATE")).ToList();
        var fill = Assert.Single(updates);   // the instance only: email and customerId are already recorded
        Assert.Contains("SET [instance] =", fill.Sql);
        Assert.Contains("[instance] IS NULL", fill.Sql);
        Assert.Contains("avt_test", fill.Values);
        Assert.DoesNotContain(h.Db.Writes, w => w.Sql.Contains("INSERT INTO [dbo].[chat_session_owners]"));
    }

    [Fact]
    public async Task An_owner_with_no_email_gains_it_from_the_same_user()
    {
        var h = new Harness { Owner = Of(customerId: "1") };

        var response = await h.Turn(Racheli, customerId: "1");

        Assert.Equal(SessionId.ToString(), response.SessionId);
        var fill = Assert.Single(h.Db.Writes, w => w.Sql.Contains("UPDATE [dbo].[chat_session_owners]"));
        Assert.Contains("SET [email_hash] =", fill.Sql);
        Assert.Contains("[email_hash] IS NULL", fill.Sql);
        Assert.Contains(SessionOwner.HashEmail(Racheli), fill.Values);
    }

    [Fact]
    public async Task A_complete_owner_is_left_alone()
    {
        var h = new Harness { Owner = Of(Racheli, "1", "Avt_Test") };

        await h.Turn(Racheli, customerId: "1", instance: "Avt_Test");

        Assert.Empty(h.Db.OwnerWrites);
    }

    [Fact]
    public async Task Another_user_starts_a_new_conversation_that_is_theirs()
    {
        var h = new Harness { Owner = Of(Racheli, "1") };

        var response = await h.Turn("dana@avt.example.test", customerId: "1");

        Assert.NotEqual(SessionId.ToString(), response.SessionId);
        var claim = Assert.Single(h.Db.Writes, w => w.Sql.Contains("INSERT INTO [dbo].[chat_session_owners]"));
        Assert.Equal(Guid.Parse(response.SessionId!), claim.Values[0]);
        Assert.Contains(SessionOwner.HashEmail("dana@avt.example.test"), claim.Values);
        Assert.DoesNotContain(h.Db.Writes, w => w.Sql.Contains("UPDATE [dbo].[chat_session_owners]"));
    }

    [Fact]
    public async Task A_request_without_an_email_does_not_resume_a_conversation_that_has_one()
    {
        var h = new Harness { Owner = Of(Racheli, "1") };

        var response = await h.Turn(email: null, customerId: "1");

        Assert.NotEqual(SessionId.ToString(), response.SessionId);
    }

    [Fact]
    public async Task A_request_with_no_identity_does_not_resume_an_owned_conversation()
    {
        var h = new Harness { Owner = Of(customerId: "1") };

        var response = await h.Turn(email: null);

        Assert.NotEqual(SessionId.ToString(), response.SessionId);
        Assert.Empty(h.Db.OwnerWrites); // nothing to claim it with
    }

    [Fact]
    public async Task A_conversation_nobody_owns_carries_on_for_a_request_with_no_identity()
    {
        var h = new Harness { Owner = null, HasMessages = true };

        var response = await h.Turn(email: null);

        Assert.Equal(SessionId.ToString(), response.SessionId);
        Assert.Empty(h.Db.OwnerWrites);
    }

    [Fact]
    public async Task An_identity_that_arrives_later_does_not_adopt_a_conversation_that_has_messages()
    {
        var h = new Harness { Owner = null, HasMessages = true };

        var response = await h.Turn(Racheli, customerId: "1");

        Assert.NotEqual(SessionId.ToString(), response.SessionId);
        var claim = Assert.Single(h.Db.Writes, w => w.Sql.Contains("INSERT INTO [dbo].[chat_session_owners]"));
        Assert.Equal(Guid.Parse(response.SessionId!), claim.Values[0]);
    }

    [Fact]
    public async Task An_empty_conversation_nobody_owns_is_adopted()
    {
        // The welcome turn the widget can send before TAS's context arrives: nothing in it to read.
        var h = new Harness { Owner = null, HasMessages = false };

        var response = await h.Turn(Racheli, customerId: "1");

        Assert.Equal(SessionId.ToString(), response.SessionId);
        var claim = Assert.Single(h.Db.Writes, w => w.Sql.Contains("INSERT INTO [dbo].[chat_session_owners]"));
        Assert.Equal(SessionId, claim.Values[0]);
    }

    [Fact]
    public async Task An_owner_that_cannot_be_read_means_a_new_conversation()
    {
        var h = new Harness { OwnerReadFails = true };

        var response = await h.Turn(Racheli, customerId: "1");

        Assert.NotEqual(SessionId.ToString(), response.SessionId);
    }

    [Fact]
    public async Task A_resume_check_that_fails_means_a_new_conversation()
    {
        var h = new Harness { Owner = Of(Racheli), SessionReadFails = true };

        var response = await h.Turn(Racheli);

        Assert.NotEqual(SessionId.ToString(), response.SessionId);
    }

    [Fact]
    public async Task Messages_that_cannot_be_counted_mean_a_new_conversation()
    {
        var h = new Harness { Owner = null, MessagesReadFails = true };

        var response = await h.Turn(Racheli);

        Assert.NotEqual(SessionId.ToString(), response.SessionId);
    }

    // ── GET /api/chat/updates ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Updates_answer_the_owner()
    {
        var h = new Harness { Owner = Of(Racheli, "1") };

        Assert.Equal(SessionId, await h.Updates(SessionOwner.FromHeaders(Racheli, "7", null)));
    }

    [Fact]
    public async Task Updates_for_another_user_come_back_empty()
    {
        var h = new Harness { Owner = Of(Racheli, "1") };

        Assert.Equal(Guid.Empty, await h.Updates(SessionOwner.FromHeaders("dana@avt.example.test", "1", null)));
        Assert.Equal(Guid.Empty, await h.Updates(SessionOwner.FromHeaders(null, "1", null)));
    }

    [Fact]
    public async Task Updates_without_identity_headers_are_answered_as_before_while_the_setting_is_off()
    {
        var h = new Harness { Owner = Of(Racheli, "1") };

        Assert.Equal(SessionId, await h.Updates(SessionOwner.None));
        Assert.Equal(SessionId, await h.Updates(null));
        // Not even looked up: today's behaviour exactly.
        Assert.DoesNotContain(h.Db.Commands, c => c.Contains("FROM [chat_session_owners]"));
    }

    [Fact]
    public async Task Updates_without_identity_headers_are_refused_for_an_owned_conversation_once_the_setting_is_on()
    {
        var owned = new Harness { Owner = Of(Racheli, "1"), UpdatesRequireOwner = "true" };
        var unowned = new Harness { Owner = null, UpdatesRequireOwner = "true" };

        Assert.Equal(Guid.Empty, await owned.Updates(SessionOwner.None));
        // A conversation nobody owns answers whoever may resume it, as a message would.
        Assert.Equal(SessionId, await unowned.Updates(SessionOwner.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("yes")]
    public async Task Anything_but_true_leaves_the_setting_off(string? value)
    {
        var h = new Harness { Owner = Of(Racheli), UpdatesRequireOwner = value };

        Assert.Equal(SessionId, await h.Updates(SessionOwner.None));
    }

    [Fact]
    public async Task Updates_whose_owner_cannot_be_read_come_back_empty()
    {
        var h = new Harness { OwnerReadFails = true };

        Assert.Equal(Guid.Empty, await h.Updates(SessionOwner.FromHeaders(Racheli, null, null)));
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A real ChatService over a scripted database: one conversation, SessionId, of UserId.</summary>
    private sealed class Harness
    {
        public SessionOwner? Owner;
        public bool HasMessages;
        public bool OwnerReadFails;
        public bool SessionReadFails;
        public bool MessagesReadFails;
        public string? UpdatesRequireOwner;

        public OwnerDb Db { get; private set; } = new();
        private ChatService? _service;
        private ChatService Service => _service ??= Build();

        /// <summary>An empty-text turn (the welcome) on SessionId, from the widget, with this identity.</summary>
        public Task<ChatResponse> Turn(string? email, string? customerId = null, string? instance = null)
            => Service.ProcessAsync(new ChatRequest
            {
                Text = "",
                SessionId = SessionId.ToString(),
                CustomerName = "Racheli",
                CustomerEmail = email,
                CustomerId = customerId,
                HostInstance = instance,
            }, UserId, "203.0.113.7");

        public Task<Guid> Updates(SessionOwner? caller) => Service.ResolveOwnedSessionAsync(SessionId.ToString(), UserId, caller);

        private ChatService Build()
        {
            Db = new OwnerDb { Owner = Owner, HasMessages = HasMessages, OwnerReadFails = OwnerReadFails,
                               SessionReadFails = SessionReadFails, MessagesReadFails = MessagesReadFails };
            var context = new TripExDbContext(new DbContextOptionsBuilder<TripExDbContext>()
                .UseSqlServer(new SqlConnection(), o => o.MaxBatchSize(1))
                .AddInterceptors(new NeverOpened(), Db)
                .Options);
            context.Database.AutoTransactionBehavior = AutoTransactionBehavior.Never;

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oracle:ApiKey"] = "test-key",
                ["Oracle:Endpoint"] = "https://oci.example.test/chat",
                ["Oracle:Model"] = "test-model",
                ["Zoho:Enabled"] = "false",
                ["Milo:UpdatesRequireOwner"] = UpdatesRequireOwner,
            }).Build();
            var factory = new NoNetwork();

            return new ChatService(
                context,
                new OracleAiService(factory, configuration, context),
                invoiceService: null!, // only the image flow uses it
                new GeolocationService(factory),
                NullLogger<ChatService>.Instance,
                configuration,
                new ZohoDeskService(factory, configuration, NullLogger<ZohoDeskService>.Instance),
                new ZohoTicketSyncQueue());
        }
    }

    /// <summary>
    /// Answers the reads on the way to the turn from a script and records every write with its
    /// parameter values. Selects are answered with the columns EF asked for, in its order.
    /// </summary>
    private sealed class OwnerDb : DbCommandInterceptor
    {
        public SessionOwner? Owner;
        public bool HasMessages;
        public bool OwnerReadFails;
        public bool SessionReadFails;
        public bool MessagesReadFails;
        public readonly List<string> Commands = new();
        public readonly List<(string Sql, List<object?> Values)> Writes = new();

        /// <summary>The owner claims and fill-ins — not SchemaGuard's CREATE, which runs once per process.</summary>
        public List<(string Sql, List<object?> Values)> OwnerWrites => Writes
            .Where(w => w.Sql.Contains("INSERT INTO [dbo].[chat_session_owners]") || w.Sql.TrimStart().StartsWith("UPDATE [dbo].[chat_session_owners]"))
            .ToList();

        private DbDataReader Answer(DbCommand command)
        {
            var sql = command.CommandText;
            Commands.Add(sql);
            var table = new DataTable();

            if (sql.Contains("INSERT ", StringComparison.Ordinal) || sql.Contains("UPDATE ", StringComparison.Ordinal))
            {
                Record(command);
                table.Columns.Add("rowcount", typeof(int));
                table.Rows.Add(1);
            }
            else if (sql.Contains("FROM [chat_sessions]", StringComparison.Ordinal) && sql.Contains("[user_id]", StringComparison.Ordinal))
            {
                if (SessionReadFails) throw new InvalidOperationException("database unavailable (test)");
                table.Columns.Add("user_id", typeof(Guid));
                table.Rows.Add(UserId);
            }
            else if (sql.Contains("FROM [chat_session_owners]", StringComparison.Ordinal))
            {
                if (OwnerReadFails) throw new InvalidOperationException("database unavailable (test)");
                var columns = Regex.Matches(sql[..sql.IndexOf("FROM", StringComparison.Ordinal)], @"\[\w+\]\.\[(\w+)\]")
                    .Select(m => m.Groups[1].Value).ToList();
                foreach (var c in columns)
                    table.Columns.Add(c, c == "session_id" ? typeof(Guid) : c == "created_at" ? typeof(DateTime) : typeof(string));
                if (Owner != null)
                    table.Rows.Add(columns.Select(c => c switch
                    {
                        "session_id" => (object)SessionId,
                        "created_at" => DateTime.UtcNow,
                        "email_hash" => (object?)Owner.EmailHash ?? DBNull.Value,
                        "customer_id" => (object?)Owner.CustomerId ?? DBNull.Value,
                        "instance" => (object?)Owner.Instance ?? DBNull.Value,
                        _ => DBNull.Value,
                    }).ToArray());
            }
            else if (sql.Contains("[chat_messages]", StringComparison.Ordinal) && sql.Contains("EXISTS", StringComparison.Ordinal))
            {
                if (MessagesReadFails) throw new InvalidOperationException("database unavailable (test)");
                table.Columns.Add("v", typeof(bool));
                table.Rows.Add(HasMessages);
            }
            return table.CreateDataReader();
        }

        private void Record(DbCommand command)
            => Writes.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList()));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => new(InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command)));

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
            => InterceptionResult<DbDataReader>.SuppressWithResult(Answer(command));

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            Record(command);
            return new(InterceptionResult<int>.SuppressWithResult(1));
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return new(InterceptionResult<object>.SuppressWithResult(DBNull.Value));
        }
    }

    /// <summary>The connection is never opened: EF is told it is.</summary>
    private sealed class NeverOpened : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
            => new(InterceptionResult.Suppress());

        public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData,
            InterceptionResult result)
            => new(InterceptionResult.Suppress());
    }

    /// <summary>Nothing in these turns may leave the process.</summary>
    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());

        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new InvalidOperationException($"unexpected HTTP call in a test: {request.Method} {request.RequestUri}");
        }
    }
}
