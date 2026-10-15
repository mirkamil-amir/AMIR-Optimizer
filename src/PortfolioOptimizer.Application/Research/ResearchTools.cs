using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Research;

/// <summary>Permission class of a tool (deny-by-default: the workflow requests a category explicitly per step).</summary>
public enum ToolCategory
{
    /// <summary>Reads shared or the caller's own data.</summary>
    ReadOnlyResearch,
    /// <summary>Deterministic calculation or simulation; nothing is persisted.</summary>
    Calculation,
    /// <summary>Creates or changes the caller's own records; requires an explicit user confirmation.</summary>
    UserDataChange,
}

public sealed class ToolException(string code, string message) : Exception(message)
{
    /// <summary>invalid_arguments, unauthorized, confirmation_required, budget_exceeded, timeout, failed.</summary>
    public string Code { get; } = code;
}

/// <summary>Per-call context. The owner comes from the authenticated request, never from model output.</summary>
public sealed record ToolContext(Guid UserId, ResearchSession Session, bool UserConfirmed);

/// <summary>A typed, validated research tool. Implementations call application services; they never contain prompts.</summary>
public abstract class ResearchTool<TArgs, TResult>
{
    public abstract string Name { get; }
    public abstract string Purpose { get; }
    public abstract ToolCategory Category { get; }
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(60);

    /// <summary>Throws <see cref="ToolException"/> ("invalid_arguments") for invalid input.</summary>
    public abstract void Validate(TArgs args);

    public abstract Task<TResult> ExecuteAsync(ToolContext context, TArgs args, CancellationToken ct);

    /// <summary>One-line, user-facing description of the result for the activity log (no data dumps).</summary>
    public abstract string Summarize(TArgs args, TResult result);
}

/// <summary>
/// Runs tools with the controls that must not depend on the language model: ownership, confirmation for user-data
/// changes, a per-session budget, a timeout, error mapping and an audit event for every call.
/// </summary>
public sealed class ToolRunner(ICurrentUser currentUser, ILogger<ToolRunner> logger, TimeProvider clock)
{
    public const int MaxToolCallsPerSession = 80;

    public async Task<TResult> RunAsync<TArgs, TResult>(ResearchTool<TArgs, TResult> tool, ToolContext context, TArgs args, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Authorization is re-checked at every tool boundary.
            var userId = currentUser.RequireUserId();
            if (userId != context.UserId || context.Session.OwnerId != userId)
                throw new ToolException("unauthorized", "This research session belongs to another account.");
            if (tool.Category == ToolCategory.UserDataChange && !context.UserConfirmed)
                throw new ToolException("confirmation_required", $"{tool.Name} changes your saved data and needs your explicit confirmation.");
            if (context.Session.ToolCalls >= MaxToolCallsPerSession)
                throw new ToolException("budget_exceeded", $"This session has used its {MaxToolCallsPerSession} tool calls. Start a new research session to continue.");
            tool.Validate(args);

            context.Session.ToolCalls++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(tool.Timeout);
            TResult result;
            try
            {
                result = await tool.ExecuteAsync(context, args, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolException("timeout", $"{tool.Name} did not finish within {tool.Timeout.TotalSeconds:0} s.");
            }
            Record(context, tool.Name, "ok", sw, tool.Summarize(args, result));
            return result;
        }
        catch (ToolException ex)
        {
            Record(context, tool.Name, ex.Code is "unauthorized" or "confirmation_required" ? "denied" : "error", sw, ex.Message);
            logger.LogWarning("Research tool {Tool} failed ({Code}): {Message}", tool.Name, ex.Code, ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is ValidationException or NotFoundException or ConflictException or ForbiddenException)
        {
            Record(context, tool.Name, "error", sw, ex.Message);
            throw;
        }
    }

    private void Record(ToolContext context, string name, string status, Stopwatch sw, string summary) =>
        context.Session.Events.Add(new ResearchEvent
        {
            SessionId = context.Session.Id,
            AtUtc = clock.GetUtcNow().UtcDateTime,
            Kind = "tool",
            Name = name,
            Status = status,
            DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            Summary = summary.Length > 500 ? summary[..500] : summary,
        });
}
