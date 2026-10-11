using Desk.Application.Tickets;

namespace Desk.Api.Auth;

/// <summary>
/// The company a client's request names, read from <see cref="Header"/>.
///
/// The header is the browser's to send and is trusted for nothing: it says which company the
/// person wants to look at, and <see cref="Desk.Infrastructure.Tickets.ClientAccessResolver"/>
/// decides, from the grants stored for that person, whether they may. A header naming a company
/// they were never given is refused like any other; one that is not an id at all, or is sent
/// twice, is refused too and never read as "no company named".
///
/// Staff requests ignore it: nothing a staff endpoint answers is scoped by it.
/// </summary>
public sealed class HttpActingCompany(IHttpContextAccessor accessor) : IActingCompany
{
    public const string Header = "X-Desk-Company";

    private string? Raw
    {
        get
        {
            if (accessor.HttpContext is not { } http || !http.Request.Headers.TryGetValue(Header, out var values)) return null;
            // Sent twice it arrives as two values; joined, it is not an id, which is the point.
            var raw = values.ToString().Trim();
            return raw.Length == 0 ? null : raw;
        }
    }

    public Guid? RequestedCompanyId => Guid.TryParse(Raw, out var id) && id != Guid.Empty ? id : null;

    public bool Malformed => Raw is { } raw && (!Guid.TryParse(raw, out var id) || id == Guid.Empty);

    public bool IsWrite
        => accessor.HttpContext is { } http
           && !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method));
}
