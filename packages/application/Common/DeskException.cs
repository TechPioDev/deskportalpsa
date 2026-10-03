namespace Desk.Application.Common;

/// <summary>Base for domain/application errors that map to well-defined HTTP problem responses.</summary>
public abstract class DeskException(string message) : Exception(message)
{
    public abstract int StatusCode { get; }
    public abstract string ErrorCode { get; }
}

public sealed class NotFoundException(string what) : DeskException($"{what} was not found.")
{
    public override int StatusCode => 404;
    public override string ErrorCode => "not_found";
}

public sealed class ValidationFailedException(string message) : DeskException(message)
{
    public override int StatusCode => 400;
    public override string ErrorCode => "validation_failed";
}

public sealed class ForbiddenException(string message) : DeskException(message)
{
    public override int StatusCode => 403;
    public override string ErrorCode => "forbidden";
}

/// <summary>
/// The requested time cannot be used as asked: what is in the way is in <see cref="Payload"/>, which
/// the API returns with the problem so a screen can show each conflict and offer an override where
/// one is allowed. 409 rather than 400: the request was well formed; the world disagreed.
/// </summary>
public sealed class ConflictException(string message, object payload) : DeskException(message)
{
    public override int StatusCode => 409;
    public override string ErrorCode => "conflict";
    public object Payload { get; } = payload;
}

/// <summary>Thrown when an operation is attempted without an established tenant scope.</summary>
public sealed class TenantScopeMissingException() : DeskException("No tenant scope is established for this operation.")
{
    public override int StatusCode => 403;
    public override string ErrorCode => "tenant_scope_missing";
}
