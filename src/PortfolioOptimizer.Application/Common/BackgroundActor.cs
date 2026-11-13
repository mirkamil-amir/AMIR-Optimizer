namespace PortfolioOptimizer.Application.Common;

/// <summary>
/// Identity of a background job that acts for one user (Matrix Pilot acting for the owner of its portfolio). Set once per
/// job scope from the stored owner id – never from a request or model output – so every owner-scoped service applies the
/// same checks as for a request of that user. A web request can never set it (the HTTP identity always wins and
/// <see cref="ActAs"/> refuses inside a request).
/// </summary>
public sealed class BackgroundActor
{
    public Guid? UserId { get; private set; }
    public string? Label { get; private set; }

    // requestUser: the request's authenticated user, if any – it must be null (background scopes only).
    public void ActAs(Guid userId, string label, Guid? requestUser)
    {
        if (requestUser is not null) throw new InvalidOperationException("A background identity cannot be set inside a web request.");
        if (UserId is { } existing && existing != userId) throw new InvalidOperationException("This job scope already acts for another user.");
        UserId = userId;
        Label = label;
    }
}
