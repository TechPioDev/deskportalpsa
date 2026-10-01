namespace Desk.Application.Common;

/// <summary>
/// The correlation id of the request being served, so a record written while serving it - an audit
/// entry above all - can be tied back to that request's logs. Null outside a request (the worker).
/// </summary>
public interface ICorrelationContext
{
    string? CorrelationId { get; }
}
