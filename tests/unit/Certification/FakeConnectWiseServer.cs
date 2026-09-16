using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Desk.Tests.Unit.Certification;

/// <summary>
/// In-memory HttpMessageHandler emulating the ConnectWise Manage REST 3.0 dialect: bare-array list
/// responses, nested {id,name} references, conditions-based filtering, and JSON-Patch updates. Lets
/// the real ConnectWiseConnector be certified end-to-end without a live instance.
/// </summary>
public sealed class FakeConnectWiseServer(TimeProvider clock) : HttpMessageHandler
{
    public HttpStatusCode? ForceStatus { get; set; }

    /// <summary>Body returned with <see cref="ForceStatus"/>. Real instances do not always answer
    /// in the documented error shape, so tests must be able to reproduce what they actually send.</summary>
    public string? ForceBody { get; set; }

    private long _seq = 5000;

    /// <summary>Seed a note as the provider would return it — raw fields, so tests can reproduce
    /// payload quirks the create route never emits (contact authors, empty member stubs).</summary>
    public void SeedNote(long ticketId, Dictionary<string, object?> note)
    {
        note["ticketID"] = ticketId;
        note.TryAdd("id", ++_seq);
        _notes.Add(note);
    }

    /// <summary>Seed a ticket as the provider would return one — references EXPANDED to {id,name},
    /// which is how ConnectWise answers a read. Without this the fake only ever holds tickets the
    /// connector itself created, so it cannot model reading what was already there: the entire sync
    /// direction.</summary>
    public void SeedTicket(Dictionary<string, object?> ticket)
    {
        ticket.TryAdd("id", ++_seq);
        ticket.TryAdd("lastUpdated", clock.GetUtcNow().ToString("o"));
        _tickets.Add(ticket);
    }

    // Documents keyed by id, holding what the real API stores: the record it hangs off, plus bytes.
    private readonly Dictionary<long, (long RecordId, string FileName, byte[] Content)> _documents = [];
    private readonly List<Dictionary<string, object?>> _tickets = [];
    private readonly List<Dictionary<string, object?>> _notes = [];

    /// <summary>Notes as the server stored them, for assertions on what the connector actually sent.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Notes => _notes;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (ForceStatus is { } forced) return Resp(forced, ForceBody ?? "{\"code\":\"forced\"}");

        // ConnectWise requires Basic auth + a clientId header on every call.
        if (request.Headers.Authorization?.Scheme != "Basic" || !request.Headers.Contains("clientId"))
            return Resp(HttpStatusCode.Unauthorized, "{}");

        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var conditions = QueryValue(request.RequestUri.Query, "conditions");
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

        // Directory + field endpoints
        if (path.EndsWith("company/companies")) return Arr("[{\"id\":1,\"name\":\"Acme Corp\",\"deletedFlag\":false}]");
        if (path.EndsWith("company/contacts")) return Arr("[{\"id\":10,\"firstName\":\"Acme\",\"lastName\":\"User\",\"email\":\"user@acme.test\",\"inactiveFlag\":false}]");
        if (path.EndsWith("system/members")) return Arr("[{\"id\":20,\"firstName\":\"Tech\",\"lastName\":\"One\",\"primaryEmail\":\"tech@msp.test\",\"inactiveFlag\":false}]");
        if (path.EndsWith("schedule/holidayLists"))
            return Arr("[{\"id\":5,\"name\":\"Standard Holidays\"}]");
        if (path.Contains("schedule/holidayLists/") && path.EndsWith("/holidays"))
            return Arr("[{\"id\":50,\"name\":\"Christmas Day\",\"date\":\"2026-12-25T00:00:00Z\"},{\"id\":51,\"name\":\"Christmas Day\",\"date\":\"2026-12-25T00:00:00Z\"}]");
        if (path.EndsWith("finance/agreements"))
            return Arr("[{\"id\":40,\"name\":\"Managed Services\",\"type\":{\"id\":2,\"name\":\"Managed\"},\"agreementStatus\":\"Active\",\"startDate\":\"2026-01-01T00:00:00Z\",\"noEndingDateFlag\":true}]");
        if (path.EndsWith("service/boards")) return Arr("[{\"id\":1,\"name\":\"Service Desk\"},{\"id\":2,\"name\":\"Dispatch\"}]");
        if (path.EndsWith("service/priorities")) return Arr("[{\"id\":3,\"name\":\"High\"}]");
        // Board-scoped, and deliberately DIFFERENT per board: the second board carries a status the
        // first does not, so reading only the first board is visible rather than plausible.
        if (path.Contains("service/boards/2/") && path.EndsWith("/statuses"))
            return Arr("[{\"id\":9,\"name\":\"Scheduled\"},{\"id\":1,\"name\":\"New\"}]");
        if (path.Contains("service/boards/") && path.EndsWith("/statuses"))
            return Arr("[{\"id\":1,\"name\":\"New\"},{\"id\":5,\"name\":\"Closed\"}]");
        if (path.Contains("service/boards/") && path.EndsWith("/types"))
            return Arr("[{\"id\":7,\"name\":\"Incident\"}]");

        // Ticket notes
        if (path.Contains("/notes"))
        {
            var ticketId = ExtractTicketId(path);
            if (request.Method == HttpMethod.Post) return CreateNote(ticketId, body);
            return Arr("[" + string.Join(",", _notes.Where(n => (long)n["ticketID"]! == ticketId).Select(Serialize)) + "]");
        }

        // Tickets
        if (path.EndsWith("service/tickets") && request.Method == HttpMethod.Get)
        {
            // page/pageSize, as the real API takes them. The fake used to ignore both and return
            // everything, so a connector that never asked for page 2 looked complete here while
            // truncating against a live instance.
            var rows = FilterTickets(conditions)
                .OrderBy(t => (long)t["id"]!)
                .ToList();
            var size = int.TryParse(QueryValue(request.RequestUri.Query, "pageSize"), out var ps) && ps > 0 ? ps : 25;
            var page = int.TryParse(QueryValue(request.RequestUri.Query, "page"), out var pg) && pg > 0 ? pg : 1;
            var slice = rows.Skip((page - 1) * size).Take(size);
            return Arr("[" + string.Join(",", slice.Select(Serialize)) + "]");
        }
        if (path.EndsWith("service/tickets") && request.Method == HttpMethod.Post)
            return CreateTicket(body);
        if (path.Contains("service/tickets/") && request.Method == HttpMethod.Get)
        {
            var id = ExtractTicketId(path);
            var t = _tickets.FirstOrDefault(x => (long)x["id"]! == id);
            return t is null ? Resp(HttpStatusCode.NotFound, "{}") : Ok(Serialize(t));
        }
        if (path.Contains("service/tickets/") && request.Method == HttpMethod.Patch)
            return PatchTicket(ExtractTicketId(path), body);
        // Documents. The real API takes a MULTIPART upload and rejects a JSON body outright with
        // 415 — the connector used to send JSON, so modelling that here keeps it honest.
        if (path.EndsWith("system/documents") && request.Method == HttpMethod.Post)
        {
            if (request.Content is not MultipartFormDataContent)
                return Resp(HttpStatusCode.UnsupportedMediaType,
                    "{\"code\":\"InvalidObject\",\"message\":\"The request entity's media type 'application/json' is not supported for this resource.\"}");

            var parts = (MultipartFormDataContent)request.Content;
            var fields = new Dictionary<string, string>();
            byte[] content = [];
            var fileName = "";
            foreach (var part in parts)
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
                if (name == "file")
                {
                    fileName = part.Headers.ContentDisposition?.FileName?.Trim('"') ?? "";
                    content = await part.ReadAsByteArrayAsync(ct);
                }
                else fields[name] = await part.ReadAsStringAsync(ct);
            }

            var id = ++_seq;
            _documents[id] = (long.Parse(fields.GetValueOrDefault("recordId", "0")),
                              fileName.Length > 0 ? fileName : fields.GetValueOrDefault("title", "file"),
                              content);
            return Ok($"{{\"id\":{id},\"title\":\"{fields.GetValueOrDefault("title", "")}\",\"fileName\":\"{fileName}\",\"size\":{content.Length}}}");
        }

        if (path.Contains("system/documents/") && path.EndsWith("/download") && request.Method == HttpMethod.Get)
        {
            var id = long.Parse(path.Split('/')[^2]);
            if (!_documents.TryGetValue(id, out var doc)) return Resp(HttpStatusCode.NotFound, "{}");
            var file = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(doc.Content) };
            file.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            file.Content.Headers.ContentDisposition =
                new ContentDispositionHeaderValue("attachment") { FileName = doc.FileName };
            return file;
        }

        if (path.EndsWith("system/documents") && request.Method == HttpMethod.Get)
        {
            var recordId = long.Parse(QueryValue(request.RequestUri.Query, "recordId") ?? "0");
            var rows = _documents.Where(d => d.Value.RecordId == recordId)
                .Select(d => $"{{\"id\":{d.Key},\"title\":\"{d.Value.FileName}\",\"fileName\":\"{d.Value.FileName}\",\"size\":{d.Value.Content.Length},\"owner\":\"Tech One\"}}");
            return Arr("[" + string.Join(",", rows) + "]");
        }

        return Resp(HttpStatusCode.NotFound, "{}");
    }

    private HttpResponseMessage CreateTicket(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;

        // The live API validates status against the TICKET'S BOARD on create, not globally: a
        // perfectly real status name from another board earns "Service Status X not found for
        // Service Board N". The connector shipped with that bug because this fake accepted any
        // name — so the fake now enforces what ConnectWise enforces.
        if (r.TryGetProperty("status", out var st))
        {
            var okById = st.TryGetProperty("id", out var sid) && (sid.GetInt64() is 1 or 5);
            var okByName = st.TryGetProperty("name", out var sn) && (sn.GetString() is "New" or "Closed");
            if (!okById && !okByName)
                return Resp(HttpStatusCode.BadRequest,
                    "{\"code\":\"InvalidObject\",\"message\":\"ticket object is invalid\",\"errors\":[{\"code\":\"NotFound\",\"message\":\"Service Status not found for Service Board 1\",\"field\":\"status/name\"}]}");
        }

        var now = clock.GetUtcNow();
        // The live API returns references EXPANDED ({id, name}) even when the create sent only an
        // id; readers depend on the name. Mirror that.
        object? status = null;
        if (r.TryGetProperty("status", out var stored))
        {
            long? sid = stored.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : null;
            var name = stored.TryGetProperty("name", out var nmEl) ? nmEl.GetString() : null;
            sid ??= name == "Closed" ? 5 : 1;
            status = new Dictionary<string, object?> { ["id"] = sid, ["name"] = name ?? (sid == 5 ? "Closed" : "New") };
        }
        var ticket = new Dictionary<string, object?>
        {
            ["id"] = ++_seq,
            ["summary"] = Str(r, "summary"),
            ["initialDescription"] = Str(r, "initialDescription"),
            ["status"] = status,
            ["priority"] = RefOf(r, "priority"),
            ["board"] = RefOf(r, "board"),
            ["company"] = RefOf(r, "company"),
            ["lastUpdated"] = now.ToString("o"),
        };
        _tickets.Add(ticket);
        return Ok(Serialize(ticket));
    }

    private HttpResponseMessage PatchTicket(long id, string body)
    {
        var ticket = _tickets.FirstOrDefault(x => (long)x["id"]! == id);
        if (ticket is null) return Resp(HttpStatusCode.NotFound, "{}");
        using var doc = JsonDocument.Parse(body);
        foreach (var op in doc.RootElement.EnumerateArray())
        {
            var path = op.GetProperty("path").GetString()!.TrimStart('/');
            ticket[path] = JsonToObject(op.GetProperty("value"));
        }
        ticket["lastUpdated"] = clock.GetUtcNow().ToString("o");
        return Ok(Serialize(ticket));
    }

    /// <summary>The members CW's ServiceNote actually has. Anything else, CW refuses outright.</summary>
    private static readonly HashSet<string> ServiceNoteMembers = new(StringComparer.Ordinal)
    {
        "id", "ticketId", "text", "detailDescriptionFlag", "internalAnalysisFlag", "resolutionFlag", "issueFlag",
        "member", "contact", "customerUpdatedFlag", "processNotifications", "dateCreated", "createdBy",
        "internalFlag", "externalFlag", "_info",
    };

    private HttpResponseMessage CreateNote(long ticketId, string body)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        // As strict as the real API: an unknown member fails the whole request. The fake accepting
        // emailContactFlag is how a note body that CW rejects on every call shipped green.
        foreach (var p in r.EnumerateObject())
            if (!ServiceNoteMembers.Contains(p.Name))
                return Resp(HttpStatusCode.BadRequest,
                    $"{{\"code\":\"InvalidObject\",\"message\":\"Could not find member '{p.Name}' on object of type 'ServiceNote'.\"}}");
        var note = new Dictionary<string, object?>
        {
            ["id"] = ++_seq,
            ["ticketID"] = ticketId,
            ["text"] = Str(r, "text"),
            ["internalAnalysisFlag"] = r.TryGetProperty("internalAnalysisFlag", out var f) && f.GetBoolean(),
            ["detailDescriptionFlag"] = true,
            ["customerUpdatedFlag"] = r.TryGetProperty("customerUpdatedFlag", out var c) && c.GetBoolean(),
            ["processNotifications"] = r.TryGetProperty("processNotifications", out var pn) && pn.GetBoolean(),
            ["dateCreated"] = clock.GetUtcNow().ToString("o"),
            // CW stamps the authenticated member on notes it accepts.
            ["member"] = new Dictionary<string, object?> { ["id"] = 20L, ["name"] = "Tech One" },
        };
        _notes.Add(note);
        return Ok(Serialize(note));
    }

    private IEnumerable<Dictionary<string, object?>> FilterTickets(string? conditions)
    {
        if (string.IsNullOrEmpty(conditions)) return _tickets;
        var start = conditions.IndexOf('[');
        var end = conditions.IndexOf(']');
        if (conditions.Contains("lastUpdated>") && start >= 0 && end > start
            && DateTimeOffset.TryParse(conditions[(start + 1)..end], out var cutoff))
        {
            return _tickets.Where(t =>
                DateTimeOffset.TryParse(t["lastUpdated"]?.ToString(), out var lu) && lu > cutoff);
        }
        return _tickets;
    }

    // ---- helpers ----

    private static string? QueryValue(string rawQuery, string key)
    {
        foreach (var pair in rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && Uri.UnescapeDataString(pair[..eq]) == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static long ExtractTicketId(string path)
    {
        var seg = path.Split('/');
        var idx = Array.IndexOf(seg, "tickets");
        return idx >= 0 && idx + 1 < seg.Length && long.TryParse(seg[idx + 1], out var id) ? id : -1;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, object?>? RefOf(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? (Dictionary<string, object?>)JsonToObject(v)!
            : null;

    private static object? JsonToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
        JsonValueKind.Number => e.GetInt64(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static string Serialize(Dictionary<string, object?> row) => JsonSerializer.Serialize(row);

    private static HttpResponseMessage Ok(string json) => Resp(HttpStatusCode.OK, json);
    private static HttpResponseMessage Arr(string json) => Resp(HttpStatusCode.OK, json);

    private static HttpResponseMessage Resp(HttpStatusCode code, string json) => new(code)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
