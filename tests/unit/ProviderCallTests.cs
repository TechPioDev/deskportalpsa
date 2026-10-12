using System.Net;
using System.Net.Http.Headers;
using Desk.Connectors.Autotask;
using Desk.Connectors.ConnectWise;
using Desk.Infrastructure.Connectors;
using Desk.PsaCore.Contracts;
using Desk.PsaCore.Models;
using Desk.Tests.Unit.Certification;
using FluentAssertions;
using Xunit;

namespace Desk.Tests.Unit;

/// <summary>
/// The layer under a connector's HttpClient: how one connection's calls are paced, bounded and
/// repeated. Neither connector did any of it. A rate limit was classified and then nothing waited;
/// a PSA that did not answer held the caller for 100 seconds; and every list but tickets was its
/// first page.
/// </summary>
public class ProviderCallTests
{
    private static readonly Guid Conn = Guid.NewGuid();

    /// <summary>Answers each request with the next scripted step; counts them.</summary>
    private sealed class Script(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = steps[Math.Min(Calls, steps.Length - 1)];
            Calls++;
            return step(request, ct);
        }
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer(HttpStatusCode status, Action<HttpResponseMessage>? with = null)
        => (_, _) =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            with?.Invoke(response);
            return Task.FromResult(response);
        };

    private static readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Refused =
        (_, _) => throw new HttpRequestException("connection refused");

    private static readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> NeverAnswers =
        async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(); };

    private sealed record Rig(HttpClient Client, Script Inner, List<TimeSpan> Waits, TestClock Clock);

    private static Rig Build(Script inner, ProviderCallPolicy? policy = null)
    {
        var clock = new TestClock();
        var waits = new List<TimeSpan>();
        Task Wait(TimeSpan d, CancellationToken _) { waits.Add(d); clock.Advance(d); return Task.CompletedTask; }
        var handler = new ProviderCallHandler(
            policy ?? new ProviderCallPolicy(Conn, "test", RequestsPerMinute: 0),
            new ConnectionThrottle(clock, Wait), clock, delay: Wait, jitter: () => 0.5)   // jitter 0.5 = exactly the nominal delay
        {
            InnerHandler = inner,
        };
        return new Rig(new HttpClient(handler) { BaseAddress = new Uri("https://psa.test/"), Timeout = Timeout.InfiniteTimeSpan }, inner, waits, clock);
    }

    // ---- what is repeated, and what is not -----------------------------------------------------

    [Fact]
    public async Task A_read_that_meets_a_passing_fault_is_tried_again()
    {
        var rig = Build(new Script(Answer(HttpStatusCode.ServiceUnavailable), Refused, Answer(HttpStatusCode.OK)));

        var response = await rig.Client.GetAsync("tickets");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rig.Inner.Calls.Should().Be(3);
        rig.Waits.Should().Equal(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]   // how Autotask answers a write it rejects
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_write_is_never_repeated_on_a_server_error(HttpStatusCode status)
    {
        // It may have been carried out before the answer was lost. Sent again, it is a second
        // ticket, a second note, a second hour on the customer's invoice.
        var rig = Build(new Script(Answer(status), Answer(HttpStatusCode.OK)));

        var response = await rig.Client.PostAsync("tickets", new StringContent("{}"));

        response.StatusCode.Should().Be(status);
        rig.Inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_write_whose_connection_failed_is_not_repeated_either()
    {
        var rig = Build(new Script(Refused, Answer(HttpStatusCode.OK)));

        var act = () => rig.Client.PostAsync("tickets", new StringContent("{}"));

        await act.Should().ThrowAsync<HttpRequestException>();
        rig.Inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_POST_the_connector_marks_as_a_read_is_repeated_like_one()
    {
        // Autotask asks its questions with POST.
        var rig = Build(new Script(Answer(HttpStatusCode.ServiceUnavailable), Answer(HttpStatusCode.OK)));
        using var query = new HttpRequestMessage(HttpMethod.Post, "V1.0/Tickets/query") { Content = new StringContent("{}") }.AsRead();

        var response = await rig.Client.SendAsync(query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        rig.Inner.Calls.Should().Be(2);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_answer_that_would_be_the_same_next_time_is_not_asked_for_again(HttpStatusCode status)
    {
        var rig = Build(new Script(Answer(status), Answer(HttpStatusCode.OK)));

        (await rig.Client.GetAsync("tickets/1")).StatusCode.Should().Be(status);

        rig.Inner.Calls.Should().Be(1);
        rig.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task It_gives_up_after_three_attempts_and_hands_back_what_it_last_got()
    {
        var rig = Build(new Script(Answer(HttpStatusCode.ServiceUnavailable)));

        (await rig.Client.GetAsync("tickets")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        rig.Inner.Calls.Should().Be(3);
    }

    // ---- being told to wait --------------------------------------------------------------------

    [Fact]
    public async Task A_rate_limit_is_waited_out_for_as_long_as_the_PSA_asked_even_on_a_write()
    {
        // A 429 was not processed, so repeating it is safe whatever it was.
        var rig = Build(new Script(
            Answer(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))),
            Answer(HttpStatusCode.Created)));

        var response = await rig.Client.PostAsync("time/entries", new StringContent("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        rig.Waits.Should().Equal(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_Retry_After_given_as_a_time_is_understood()
    {
        // The header has two forms. Only the number of seconds used to be read; a date fell back
        // to a guessed ten seconds, sooner than the PSA had asked.
        var rig = Build(new Script(Answer(HttpStatusCode.OK)));
        var at = rig.Clock.GetUtcNow().AddSeconds(12);
        var limited = Build(new Script(
            Answer(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(at)),
            Answer(HttpStatusCode.OK)));

        (await limited.Client.GetAsync("tickets")).StatusCode.Should().Be(HttpStatusCode.OK);

        limited.Waits.Should().Equal(TimeSpan.FromSeconds(12));
        ProviderRequest.Wait(new RetryConditionHeaderValue(at.AddMinutes(-5)), rig.Clock.GetUtcNow()).Should().Be(TimeSpan.Zero, "a time already past means now");
        ProviderRequest.Wait(null, rig.Clock.GetUtcNow()).Should().BeNull();
    }

    [Fact]
    public async Task A_long_wait_is_not_sat_through_here_the_answer_goes_back_with_its_Retry_After()
    {
        // A person may be waiting on this call. The sync, which can afford to, comes back to the
        // record when the time has passed.
        var rig = Build(new Script(
            Answer(HttpStatusCode.TooManyRequests, r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5))),
            Answer(HttpStatusCode.OK)));

        var response = await rig.Client.GetAsync("tickets");

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromMinutes(5));
        (rig.Inner.Calls, rig.Waits.Count).Should().Be((1, 0));
    }

    // ---- being kept waiting --------------------------------------------------------------------

    [Fact]
    public async Task A_PSA_that_does_not_answer_is_given_up_on_and_the_caller_is_told_it_timed_out()
    {
        var rig = Build(new Script(NeverAnswers), new ProviderCallPolicy(Conn, "test", 0) { Timeout = TimeSpan.FromMilliseconds(40) });

        var act = () => rig.Client.GetAsync("tickets");

        // As a cancellation the caller did not ask for - which is how both connectors recognise a timeout.
        (await act.Should().ThrowAsync<TaskCanceledException>()).WithMessage("*did not answer*");
        rig.Inner.Calls.Should().Be(3, "a read is tried again");
    }

    [Fact]
    public async Task A_write_that_times_out_is_sent_once()
    {
        var rig = Build(new Script(NeverAnswers), new ProviderCallPolicy(Conn, "test", 0) { Timeout = TimeSpan.FromMilliseconds(40) });

        var act = () => rig.Client.PostAsync("tickets", new StringContent("{}"));

        await act.Should().ThrowAsync<TaskCanceledException>();
        rig.Inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_caller_who_gives_up_is_not_made_to_wait_for_a_retry()
    {
        var rig = Build(new Script(NeverAnswers));
        using var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        var act = () => rig.Client.GetAsync("tickets", gaveUp.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Inner.Calls.Should().Be(1);
    }

    // ---- one budget per connection -------------------------------------------------------------

    [Fact]
    public async Task A_connection_keeps_to_its_pace_once_its_burst_is_spent()
    {
        var clock = new TestClock();
        var waits = new List<TimeSpan>();
        var throttle = new ConnectionThrottle(clock, (d, _) => { waits.Add(d); clock.Advance(d); return Task.CompletedTask; });

        // 60 a minute: two minutes' worth is free, which is every ordinary sync.
        for (var i = 0; i < 120; i++) await throttle.WaitAsync(Conn, 60, default);
        waits.Should().BeEmpty();

        // After that, one a second.
        for (var i = 0; i < 5; i++) await throttle.WaitAsync(Conn, 60, default);
        waits.Should().HaveCount(5).And.OnlyContain(w => Math.Abs(w.TotalSeconds - 1) < 0.001);

        // Left alone for a minute, it has a minute's allowance again.
        waits.Clear();
        clock.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 60; i++) await throttle.WaitAsync(Conn, 60, default);
        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task One_connection_out_of_allowance_does_not_slow_another()
    {
        var clock = new TestClock();
        var waits = new List<TimeSpan>();
        var throttle = new ConnectionThrottle(clock, (d, _) => { waits.Add(d); clock.Advance(d); return Task.CompletedTask; });
        var busy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        for (var i = 0; i < 125; i++) await throttle.WaitAsync(busy, 60, default);
        waits.Should().NotBeEmpty();

        waits.Clear();
        await throttle.WaitAsync(quiet, 60, default);

        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_attempt_is_paced_including_the_repeated_ones()
    {
        var clock = new TestClock();
        var paced = 0;
        Task Wait(TimeSpan d, CancellationToken _) { clock.Advance(d); return Task.CompletedTask; }
        var throttle = new ConnectionThrottle(clock, (d, ct) => { paced++; return Wait(d, ct); });
        var inner = new Script(Answer(HttpStatusCode.ServiceUnavailable), Answer(HttpStatusCode.OK));
        var client = new HttpClient(new ProviderCallHandler(new ProviderCallPolicy(Conn, "test", 1), throttle, clock, delay: Wait, jitter: () => 0.5) { InnerHandler = inner })
            { BaseAddress = new Uri("https://psa.test/") };

        // One a minute, a burst of two: the first two attempts are free, a third would wait.
        await client.GetAsync("a");
        paced.Should().Be(0);
        await client.GetAsync("b");
        paced.Should().Be(1);
    }

    // ---- lists, every page ---------------------------------------------------------------------

    private static AutotaskConnector Autotask(HttpMessageHandler server, TimeProvider clock, int pageSize = 500, int maxPages = 100)
        => new(new HttpClient(server) { BaseAddress = new Uri("https://webservices.local/atservicesrest/") },
            new AutotaskConnectorConfig
            {
                BaseUrl = "https://webservices.local/atservicesrest/",
                Credentials = new AutotaskCredentials("code", "user", "secret"),
                ListPageSize = pageSize, MaxListPages = maxPages,
            }, clock);

    [Fact]
    public async Task Autotask_reads_every_page_of_a_tickets_notes()
    {
        // It read the first page and returned it as the thread. The sync then removed, as "deleted
        // in the PSA", every imported note that was merely on page two.
        var clock = new TestClock();
        var c = Autotask(new FakeAutotaskServer(clock), clock, pageSize: 2);
        var ticket = (await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "t", ExternalCompanyId = "1", IdempotencyKey = "k" })).ExternalId!;
        for (var i = 1; i <= 5; i++)
            await c.AddPublicNoteAsync(ticket, new UnifiedTicketNoteCreateRequest("note " + i, IsPublic: true, "n" + i));

        var notes = await c.GetNotesAsync(ticket);

        notes.Select(n => n.Body).Should().BeEquivalentTo(["note 1", "note 2", "note 3", "note 4", "note 5"]);
    }

    [Fact]
    public async Task A_list_too_long_to_finish_is_an_error_not_a_shorter_list()
    {
        // Handed three of five notes as though they were all of them, the sync deletes the other two.
        var clock = new TestClock();
        var c = Autotask(new FakeAutotaskServer(clock), clock, pageSize: 2, maxPages: 2);
        var ticket = (await c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "t", ExternalCompanyId = "1", IdempotencyKey = "k" })).ExternalId!;
        for (var i = 1; i <= 5; i++)
            await c.AddPublicNoteAsync(ticket, new UnifiedTicketNoteCreateRequest("note " + i, IsPublic: true, "n" + i));

        var act = () => c.GetNotesAsync(ticket);

        (await act.Should().ThrowAsync<ConnectorException>()).Which.Should().Match<ConnectorException>(
            e => e.Kind == ConnectorFailureKind.ProviderError && e.Message.Contains("not read in full"));
    }

    [Fact]
    public async Task ConnectWise_reads_every_page_of_a_tickets_notes()
    {
        // One request for the first 1,000. The 1,001st note did not exist as far as the portal knew.
        var clock = new TestClock();
        var server = new FakeConnectWiseServer(clock);
        var c = new ConnectWiseConnector(new HttpClient(server) { BaseAddress = new Uri("https://cw.local/v4_6_release/apis/3.0/") },
            new ConnectWiseConnectorConfig
            {
                BaseUrl = "https://cw.local/v4_6_release/apis/3.0/",
                Credentials = new ConnectWiseCredentials("acme", "pub", "priv", "client-guid"),
            }, clock);
        for (var i = 1; i <= 1001; i++)
            server.SeedNote(77, new Dictionary<string, object?> { ["text"] = "note " + i, ["internalAnalysisFlag"] = false, ["detailDescriptionFlag"] = true });

        var notes = await c.GetNotesAsync("77");

        notes.Should().HaveCount(1001);
    }

    // ---- a connector on the real layer ---------------------------------------------------------

    /// <summary>Fails the first request that matches, then lets everything through to the fake PSA.</summary>
    private sealed class FailsOnce(HttpMessageHandler server, Func<HttpRequestMessage, bool> when, HttpStatusCode with) : DelegatingHandler(server)
    {
        public int Seen;
        private bool _done;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!when(request)) return base.SendAsync(request, ct);
            Seen++;
            if (_done) return base.SendAsync(request, ct);
            _done = true;
            return Task.FromResult(new HttpResponseMessage(with) { Content = new StringContent("{\"errors\":[\"try later\"]}") });
        }
    }

    private static (AutotaskConnector Connector, FailsOnce Flaky) AutotaskOnTheLayer(Func<HttpRequestMessage, bool> when, HttpStatusCode with)
    {
        var clock = new TestClock();
        Task Wait(TimeSpan d, CancellationToken _) { clock.Advance(d); return Task.CompletedTask; }
        var flaky = new FailsOnce(new FakeAutotaskServer(clock), when, with);
        var layer = new ProviderCallHandler(new ProviderCallPolicy(Conn, "autotask", AutotaskConnector.RequestsPerMinute),
            new ConnectionThrottle(clock, Wait), clock, delay: Wait, jitter: () => 0.5) { InnerHandler = flaky };
        return (Autotask(layer, clock), flaky);
    }

    [Fact]
    public async Task An_Autotask_query_that_hits_a_passing_fault_succeeds_on_the_second_try()
    {
        var (c, flaky) = AutotaskOnTheLayer(r => r.RequestUri!.AbsolutePath.EndsWith("Tickets/query"), HttpStatusCode.ServiceUnavailable);

        var page = await c.GetTicketsAsync(new TicketFilter());

        page.Should().NotBeNull();
        flaky.Seen.Should().Be(2, "the query is a POST, and the connector says it is a read");
    }

    [Fact]
    public async Task An_Autotask_create_that_fails_is_sent_once_and_reported()
    {
        var (c, flaky) = AutotaskOnTheLayer(
            r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("V1.0/Tickets"), HttpStatusCode.InternalServerError);

        var act = () => c.CreateTicketAsync(new UnifiedTicketCreateRequest { Title = "t", ExternalCompanyId = "1", IdempotencyKey = "k" });

        (await act.Should().ThrowAsync<ConnectorException>()).Which.Kind.Should().Be(ConnectorFailureKind.ProviderError);
        flaky.Seen.Should().Be(1, "repeating a create that may have been carried out makes a second ticket");
    }
}
