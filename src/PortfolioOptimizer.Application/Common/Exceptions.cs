namespace PortfolioOptimizer.Application.Common;

public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' was not found.");

/// <summary>Request is syntactically valid but semantically invalid (maps to HTTP 400 with field errors).</summary>
public sealed class ValidationException : Exception
{
    public ValidationException(string message) : base(message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(string field, string message) : base(message)
    {
        Errors = new Dictionary<string, string[]> { [field] = [message] };
    }

    public ValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IDictionary<string, string[]> Errors { get; }
}

/// <summary>The request conflicts with the current state (maps to HTTP 409).</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>Sign-in failed (maps to HTTP 401). The message never reveals whether the account exists.</summary>
public sealed class AuthenticationFailedException(string message) : Exception(message);

/// <summary>The caller is authenticated but not allowed to perform the operation (maps to HTTP 403).</summary>
public sealed class ForbiddenException(string message) : Exception(message);

/// <summary>
/// For last-resort handlers (background jobs, best-effort side tasks, solver fallbacks) that record a failure and carry
/// on: <c>catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))</c>. Fatal runtime errors are not caught – after
/// them the process state cannot be trusted, so they must end the process instead of being logged as an ordinary failure.
/// </summary>
public static class ExceptionFilters
{
    public static bool IsRecoverable(Exception ex) =>
        ex is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException);
}
