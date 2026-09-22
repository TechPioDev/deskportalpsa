using System.Net;
using System.Text;
using System.Text.Json;
using Desk.PsaCore.Contracts;

namespace Desk.Tests.Unit.Certification;

/// <summary>
/// In-memory HttpMessageHandler that emulates the Autotask REST v1.0 dialect with real state
/// (companies, contacts, resources, tickets, notes, picklists). Lets the real AutotaskConnector be
/// certified end-to-end — request construction, JSON parsing, error mapping — without a live sandbox.
/// Can be rigged to fail every call with a specific HTTP status for the error-mapping tests.
/// </summary>
public sealed class FakeAutotaskServer(TimeProvider clock) : HttpMessageHandler
{
    /// <summary>Requests seen per entity path, so a test can assert how MANY calls were made and
    /// not merely that the answer looked right. Memoization is invisible in a result.</summary>
    public Dictionary<string, int> RequestCounts { get; } = [];

    /// <summary>Seed a time entry as the provider would return one, referencing a resource and a
    /// billing code so that reading it exercises technician and work-type name resolution.</summary>
    public void SeedTimeEntry(long ticketId, long resourceId = 20, long billingCodeId = 30)
        => TimeEntries.Add(new Dictionary<string, object?>
        {
            ["id"] = ++_seq,
            ["ticketID"] = ticketId,
            ["resourceID"] = resourceId,
            ["billingCodeID"] = billingCodeId,
            ["hoursWorked"] = 0.5m,
            ["dateWorked"] = clock.GetUtcNow().ToString("o"),
        });

    public HttpStatusCode? ForceStatus { get; set; }

    private long _seq = 100;
    private readonly List<Dictionary<string, object?>> _companies =
        [new() { ["id"] = 1L, ["companyName"] = "Acme Corp", ["isActive"] = true }];
    private readonly List<Dictionary<string, object?>> _contacts =
        [new() { ["id"] = 10L, ["companyID"] = 1L, ["emailAddress"] = "user@acme.test", ["firstName"] = "Acme", ["lastName"] = "User", ["isActive"] = 1L }];
    private readonly List<Dictionary<string, object?>> _resources =
        [new() { ["id"] = 20L, ["email"] = "tech@msp.test", ["firstName"] = "Tech", ["lastName"] = "One", ["isActive"] = true }];
    private readonly List<Dictionary<string, object?>> _contracts =
        [new() { ["id"] = 30L, ["companyID"] = 1L, ["contractName"] = "Managed Services", ["contractType"] = 7L, ["status"] = 1L, ["startDate"] = "2026-01-01T00:00:00Z", ["endDate"] = "2026-12-31T00:00:00Z" }];
    private readonly List<Dictionary<string, object?>> _holidays =
        [new() { ["id"] = 60L, ["holidayName"] = "Christmas Day", ["holidayDate"] = "2026-12-25T00:00:00Z" }];
    /// <summary>Which roles each resource actually holds — the pairing Autotask enforces.</summary>
    public List<Dictionary<string, object?>> ResourceRoles { get; } =
        [new() { ["id"] = 900L, ["resourceID"] = 20L, ["roleID"] = 55L, ["isActive"] = true }];

    /// <summary>Time entries the fake accepted, so a test can assert what was actually sent.</summary>
    public List<Dictionary<string, object?>> TimeEntries { get; } = [];

    private readonly List<Dictionary<string, object?>> _tickets = [];
    private readonly List<Dictionary<string, object?>> _notes = [];

    /// <summary>
    /// The title Autotask stored for the last note. Autotask prints this ABOVE the description, so
    /// it is part of what a reader sees — but the connector's read path only returns the body, so a
    /// test cannot reach it any other way.
    /// </summary>
    public string? LastNoteTitle => _notes.LastOrDefault()?.GetValueOrDefault("title")?.ToString();
    private readonly List<Dictionary<string, object?>> _attachments = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (ForceStatus is { } forced)
            return Resp(forced, "{\"errors\":[\"forced\"]}");

        // Credentials must be present on every call (auth surface).
        if (!request.Headers.Contains("ApiIntegrationCode") || !request.Headers.Contains("Secret"))
            return Resp(HttpStatusCode.Unauthorized, "{}");

        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        RequestCounts[path] = RequestCounts.GetValueOrDefault(path) + 1;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

        // Picklist field info
        if (path.EndsWith("Tickets/entityInformation/fields", StringComparison.OrdinalIgnoreCase))
            return Json(FieldInfoJson());
        // Contract type/status labels come from the tenant's own metadata, exactly like tickets.
        if (path.EndsWith("Contracts/entityInformation/fields", StringComparison.OrdinalIgnoreCase))
            return Json("{\"fields\":[" +
                "{\"name\":\"contractType\",\"picklistValues\":[{\"value\":\"7\",\"label\":\"Recurring Service\",\"isActive\":true}]}," +
                "{\"name\":\"status\",\"picklistValues\":[{\"value\":\"1\",\"label\":\"Active\",\"isActive\":true}]}]}");
        if (path.EndsWith("entityInformation/userDefinedFields", StringComparison.OrdinalIgnoreCase))
            return Json("{\"fields\":[{\"name\":\"cf_site\",\"picklistValues\":[]}]}");

        // Query endpoints
        if (path.EndsWith("Companies/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(_companies, body));
        if (path.EndsWith("Contacts/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(_contacts, body));
        if (path.EndsWith("Resources/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(_resources, body));
        if (path.EndsWith("Contracts/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(_contracts, body));
        if (path.EndsWith("ResourceRoles/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(ResourceRoles, body));
        // Autotask accepts ticket time only when the resource ACTUALLY HOLDS the role: an
        // unpaired combination is HTTP 500, not a validation 400. Modelling that here is the
        // difference between catching the live failure and shipping it again.
        if (path.EndsWith("V1.0/TimeEntries", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post)
        {
            var input = Parse(body);
            var res = Convert.ToInt64(input.GetValueOrDefault("resourceID") ?? 0L);
            var role = Convert.ToInt64(input.GetValueOrDefault("roleID") ?? 0L);
            var paired = ResourceRoles.Any(r =>
                Convert.ToInt64(r["resourceID"]) == res && Convert.ToInt64(r["roleID"]) == role);
            if (!paired)
                return Resp(HttpStatusCode.InternalServerError,
                    "{\"errors\":[\"The specified AssignedResourceID and AssignedRoleID combination is not currently defined.\"]}");
            // Autotask mandates summary notes on ticket time; ConnectWise does not. A fake that
            // accepted blank notes let the portal ship an entry Autotask would always refuse.
            if (string.IsNullOrWhiteSpace(input.GetValueOrDefault("summaryNotes")?.ToString()))
                return Resp(HttpStatusCode.InternalServerError,
                    "{\"errors\":[\"TimeEntry.summaryNotes can not be blank.\"]}");
            TimeEntries.Add(input);
            return Json($"{{\"itemId\":{++_seq}}}");
        }
        // The real API serves time entries back; this fake only ever accepted them. Without this a
        // ticket's time could be written and never read, so nothing exercised the per-ticket name
        // resolution that turned out to re-fetch whole reference lists.
        if (path.EndsWith("TimeEntries/query", StringComparison.OrdinalIgnoreCase))
            return Json(QueryJson(TimeEntries, body));
        if (path.EndsWith("Holidays/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(_holidays, body));
        // A continuation of an earlier query. The real API hands back a URL and expects a GET on it;
        // modelling that is what makes an unpaginated connector fail here instead of passing.
        if (request.RequestUri!.Query.Contains("nextPage="))
        {
            // The live API refuses a GET here: the URL continues a POSTed query, and asking for it
            // with GET earns a 405. The fake accepted either, so a connector that used GET passed
            // every test and then failed on the first real second page.
            if (request.Method != HttpMethod.Post)
                return Resp(HttpStatusCode.MethodNotAllowed,
                    "{\"Message\":\"The requested resource does not support http method 'GET'.\"}");
            return Json(NextPageJson(QueryValue(request.RequestUri.Query, "nextPage")!));
        }
        if (path.EndsWith("Tickets/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(_tickets, body));
        if (path.EndsWith("TicketNotes/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(_notes, body));
        if (path.EndsWith("TicketAttachments/query", StringComparison.OrdinalIgnoreCase)) return Json(QueryJson(StripData(_attachments), body));

        // Create / update
        if (path.EndsWith("V1.0/Tickets", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post)
            return CreateTicketChecked(body);
        if (path.EndsWith("V1.0/Tickets", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Patch)
            return UpdateTicket(body);
        // Notes are a CHILD collection: creates go to the parent ticket's /Notes route. Posting to
        // the top-level TicketNotes entity 404s, exactly as the live API does.
        if (path.EndsWith("/Notes", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post)
            return CreateNote(body);
        if (path.EndsWith("V1.0/TicketNotes", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post)
            return Resp(HttpStatusCode.NotFound, "{\"errors\":[\"entity not found\"]}");
        // Attachments are a child collection too: create on the ticket route, and only that route
        // returns the base64 payload — the top-level entity always answers with data = null.
        if (path.EndsWith("/Attachments", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post)
            return Json(CreateAttachment(path, body));
        if (path.Contains("/Attachments/", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Get)
        {
            var id = long.Parse(path[(path.LastIndexOf('/') + 1)..]);
            var hit = _attachments.FirstOrDefault(a => (long)a["id"]! == id);
            return Json(hit is null ? "{\"items\":[]}" : "{\"items\":[" + Serialize(hit) + "]}");
        }
        if (path.EndsWith("V1.0/TicketAttachments", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Post)
            return Resp(HttpStatusCode.NotFound, "{\"errors\":[\"entity not found\"]}");

        // Get by id: .../V1.0/Tickets/{id}
        if (path.Contains("V1.0/Tickets/", StringComparison.OrdinalIgnoreCase) && request.Method == HttpMethod.Get)
        {
            var id = long.Parse(path[(path.LastIndexOf('/') + 1)..]);
            var t = _tickets.FirstOrDefault(x => (long)x["id"]! == id);
            return t is null ? Resp(HttpStatusCode.NotFound, "{}") : Json("{\"item\":" + Serialize(t) + "}");
        }

        return Resp(HttpStatusCode.NotFound, "{}");
    }

    /// <summary>
    /// Autotask's numeric picklist fields. The live API answers a label with
    /// HTTP 500 "Could not convert string to integer", so the fake must too — the connector shipped
    /// sending "In Progress" for months because this fake accepted anything.
    /// </summary>
    private static readonly string[] NumericFields = ["status", "priority", "queueID", "ticketCategory"];

    private HttpResponseMessage? RejectNonNumericPicklists(Dictionary<string, object?> input)
    {
        foreach (var field in NumericFields)
        {
            if (input.GetValueOrDefault(field) is not string s || string.IsNullOrEmpty(s)) continue;
            if (!long.TryParse(s, out _))
                return Resp(HttpStatusCode.InternalServerError,
                    $"{{\"errors\":[\"Could not convert string to integer: {s}. Path '{field}', line 1, position 33.\"]}}");
        }
        return null;
    }

    /// <summary>Points a ticket at a contact, as a ticket raised by that customer carries contactID.</summary>
    public void SetTicketContact(long ticketId, long contactId)
        => _tickets.Single(t => Convert.ToInt64(t["id"]) == ticketId)["contactID"] = contactId;

    private HttpResponseMessage CreateTicketChecked(string body)
    {
        var input = Parse(body);
        return RejectNonNumericPicklists(input) ?? Json(CreateTicket(body));
    }

    private string CreateTicket(string body)
    {
        var input = Parse(body);
        var now = clock.GetUtcNow().ToString("o");
        var ticket = new Dictionary<string, object?>
        {
            ["id"] = ++_seq,
            ["title"] = input.GetValueOrDefault("title"),
            ["description"] = input.GetValueOrDefault("description"),
            ["status"] = input.GetValueOrDefault("status"),
            ["priority"] = input.GetValueOrDefault("priority"),
            ["queueID"] = input.GetValueOrDefault("queueID"),
            ["ticketCategory"] = input.GetValueOrDefault("ticketCategory"),
            ["companyID"] = input.GetValueOrDefault("companyID"),
            ["createDate"] = now,
            ["lastActivityDate"] = now,
        };
        _tickets.Add(ticket);
        return $"{{\"itemId\":{ticket["id"]}}}";
    }

    private HttpResponseMessage UpdateTicket(string body)
    {
        var input = Parse(body);
        if (RejectNonNumericPicklists(input) is { } rejected) return rejected;
        var id = Convert.ToInt64(input["id"]);
        var ticket = _tickets.FirstOrDefault(x => (long)x["id"]! == id);
        if (ticket is null) return Resp(HttpStatusCode.NotFound, "{}");
        foreach (var (k, v) in input)
            if (k != "id") ticket[k] = v;
        ticket["lastActivityDate"] = clock.GetUtcNow().ToString("o");
        return Json($"{{\"itemId\":{id}}}");
    }

    private string CreateAttachment(string path, string body)
    {
        var input = Parse(body);
        // .../V1.0/Tickets/{id}/Attachments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parentId = long.Parse(segments[^2]);
        var data = input.GetValueOrDefault("data")?.ToString() ?? "";
        var file = new Dictionary<string, object?>
        {
            ["id"] = ++_seq,
            ["parentID"] = parentId,
            ["ticketID"] = parentId,
            ["title"] = input.GetValueOrDefault("title"),
            ["fullPath"] = input.GetValueOrDefault("fullPath"),
            ["contentType"] = input.GetValueOrDefault("contentType"),
            ["attachmentType"] = input.GetValueOrDefault("attachmentType"),
            ["publish"] = Convert.ToInt32(input.GetValueOrDefault("publish") ?? 1),
            // Autotask reports the size as a decimal, which is exactly why the DTO cannot use long.
            ["fileSize"] = (double)Convert.FromBase64String(data).Length,
            ["attachDate"] = clock.GetUtcNow().ToString("o"),
            ["attachedByResourceID"] = 20L,
            ["data"] = data,
        };
        _attachments.Add(file);
        return $"{{\"itemId\":{file["id"]}}}";
    }

    /// <summary>The list projection withholds the payload, exactly as the live API does.</summary>
    private static List<Dictionary<string, object?>> StripData(List<Dictionary<string, object?>> rows)
        => rows.Select(r => r.ToDictionary(kv => kv.Key, kv => kv.Key == "data" ? null : kv.Value)).ToList();

    private HttpResponseMessage CreateNote(string body)
    {
        var input = Parse(body);
        // The live API rejects a note without a title; keep that contract so the connector's
        // title derivation stays covered.
        if (string.IsNullOrWhiteSpace(input.GetValueOrDefault("title")?.ToString()))
            return Resp(HttpStatusCode.BadRequest, "{\"errors\":[\"Missing Required Field: title\"]}");

        var note = new Dictionary<string, object?>
        {
            ["id"] = ++_seq,
            ["ticketID"] = Convert.ToInt64(input["ticketID"]),
            ["title"] = input.GetValueOrDefault("title"),
            ["description"] = input.GetValueOrDefault("description"),
            ["publish"] = Convert.ToInt32(input.GetValueOrDefault("publish") ?? 1),
            ["createDateTime"] = clock.GetUtcNow().ToString("o"),
            ["creatorResourceID"] = 20L, // the integration user, as Autotask stamps it
        };
        _notes.Add(note);
        return Json($"{{\"itemId\":{note["id"]}}}");
    }

    private sealed record FilterClause(string Field, string Op, long? Number, string? Text,
        long[]? Numbers = null);

    // Minimal filter application: supports eq/gte/in on the fields the connector queries. Values
    // are extracted eagerly so no JsonElement is read after the JsonDocument is disposed.
    private string QueryJson(List<Dictionary<string, object?>> rows, string body)
    {
        var clauses = new List<FilterClause>();
        if (!string.IsNullOrEmpty(body))
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("Filter", out var filters))
            {
                foreach (var f in filters.EnumerateArray())
                {
                    var val = f.GetProperty("value");
                    clauses.Add(new FilterClause(
                        f.GetProperty("field").GetString()!,
                        f.GetProperty("op").GetString()!,
                        val.ValueKind == JsonValueKind.Number ? val.GetInt64() : null,
                        // Booleans matter: Autotask filters on isActive with a real boolean, and a
                        // fake that understood only numbers and strings silently dropped EVERY row
                        // of such a query — which reads in a test exactly like "no data exists".
                        val.ValueKind switch
                        {
                            JsonValueKind.String => val.GetString(),
                            JsonValueKind.True or JsonValueKind.False => val.GetBoolean().ToString(),
                            _ => null,
                        },
                        // "in" carries an ARRAY of ids. Read eagerly like everything else here, so
                        // nothing touches the JsonElement after the document is disposed.
                        val.ValueKind == JsonValueKind.Array
                            ? val.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                                .Select(e => e.GetInt64()).ToArray()
                            : null));
                }
            }
        }

        var result = rows.Where(r => clauses.All(c => Matches(r, c))).ToList();

        // MaxRecords is a LIMIT, and the rows past it are offered through a next-page URL rather
        // than thrown away. The fake used to return every row and no URL, so a connector that never
        // paginated looked complete here while silently truncating against the real API.
        var max = MaxRecords(body);
        return Page(result, max);
    }

    private static string? QueryValue(string query, string key)
        => System.Web.HttpUtility.ParseQueryString(query).Get(key);

    private static int MaxRecords(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("MaxRecords", out var m) ? m.GetInt32() : 500;
        }
        catch { return 500; }
    }

    // Rows still owed to a caller, keyed by the token embedded in the next-page URL.
    private readonly Dictionary<string, (List<Dictionary<string, object?>> Rows, int PageSize)> _pages = [];

    private string Page(List<Dictionary<string, object?>> result, int pageSize)
    {
        var take = result.Take(pageSize).ToList();
        var rest = result.Skip(take.Count).ToList();
        var items = string.Join(",", take.Select(Serialize));

        string next = "null";
        if (rest.Count > 0)
        {
            var token = $"p{++_seq}";
            _pages[token] = (rest, pageSize);
            next = $"\"https://webservices.local/atservicesrest/V1.0/Tickets/query?nextPage={token}\"";
        }

        return $"{{\"items\":[{items}],\"pageDetails\":{{\"count\":{take.Count},\"nextPageUrl\":{next}}}}}";
    }

    private string NextPageJson(string token)
    {
        if (!_pages.Remove(token, out var pending)) return "{\"items\":[],\"pageDetails\":{\"count\":0}}";
        return Page(pending.Rows, pending.PageSize);
    }

    private static bool Matches(Dictionary<string, object?> row, FilterClause c) => c.Op switch
    {
        "eq" => FieldEquals(row, c),
        "gte" => FieldGte(row, c),
        // The connector sends "in" for company, queue and resource import filters as well as for
        // company-name resolution. Falling through to true meant every one of those matched EVERY
        // row, so a filter test could not fail — the fake was more permissive than Autotask, which
        // is how several defects reached production already.
        "in" => FieldIn(row, c),
        _ => true,
    };

    private static bool FieldIn(Dictionary<string, object?> row, FilterClause c)
    {
        if (c.Numbers is null || c.Numbers.Length == 0) return false;
        if (!row.TryGetValue(c.Field, out var actual) || actual is null) return false;
        return Array.IndexOf(c.Numbers, Convert.ToInt64(actual)) >= 0;
    }

    private static bool FieldEquals(Dictionary<string, object?> row, FilterClause c)
    {
        if (!row.TryGetValue(c.Field, out var actual) || actual is null) return false;
        return c.Number is { } n
            ? Convert.ToInt64(actual) == n
            : string.Equals(actual.ToString(), c.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FieldGte(Dictionary<string, object?> row, FilterClause c)
    {
        if (c.Number is { } n) return Convert.ToInt64(row.GetValueOrDefault(c.Field) ?? 0L) >= n;
        if (row.TryGetValue(c.Field, out var actual) && actual is string s
            && DateTimeOffset.TryParse(s, out var d) && DateTimeOffset.TryParse(c.Text, out var since))
            return d >= since;
        return true;
    }

    private static string FieldInfoJson() =>
        "{\"fields\":[" +
        "{\"name\":\"status\",\"picklistValues\":[{\"value\":\"1\",\"label\":\"New\",\"isActive\":true},{\"value\":\"5\",\"label\":\"Complete\",\"isActive\":true},{\"value\":\"7\",\"label\":\"Resolved\",\"isActive\":true}]}," +
        "{\"name\":\"priority\",\"picklistValues\":[{\"value\":\"1\",\"label\":\"High\",\"isActive\":true}]}," +
        "{\"name\":\"queueID\",\"picklistValues\":[{\"value\":\"8\",\"label\":\"Service Desk\",\"isActive\":true}]}]}";

    private static Dictionary<string, object?> Parse(string body) =>
        string.IsNullOrEmpty(body) ? [] :
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body)!
            .ToDictionary(kv => kv.Key, object? (kv) => kv.Value.ValueKind switch
            {
                // Not every number is an integer: hoursWorked is 0.25, and GetInt64 throws on it.
                JsonValueKind.Number => kv.Value.TryGetInt64(out var i) ? i : kv.Value.GetDecimal(),
                JsonValueKind.String => kv.Value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            });

    private static string Serialize(Dictionary<string, object?> row) => JsonSerializer.Serialize(row);

    private static HttpResponseMessage Json(string json) => Resp(HttpStatusCode.OK, json);

    private static HttpResponseMessage Resp(HttpStatusCode code, string json) => new(code)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
