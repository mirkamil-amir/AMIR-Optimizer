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
