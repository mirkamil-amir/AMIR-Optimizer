using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Pilot;

/// <summary>
/// Permission class of a pilot tool. The model may use READ, ANALYZE and SIMULATE tools only. Proposing is the decision
/// itself (validated by code); executing is done by the deterministic pipeline after validation – the model never holds a
/// tool that writes data.
/// </summary>
public enum PilotToolCategory { Read, Analyze, Simulate }

public sealed class PilotToolException(string code, string message) : Exception(message)
{
    /// <summary>invalid_arguments, not_permitted, budget_exceeded, timeout, failed.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Everything a tool may know about the cycle. Owner, portfolio and decision time come from the stored pilot – never from
/// model output – and no tool accepts a portfolio or user id as an argument.
/// </summary>
public sealed class PilotToolContext
{
    public required Guid OwnerId { get; init; }
    public required Guid PortfolioId { get; init; }
    public required Guid PilotId { get; init; }
    public required MatrixPilotCycle Cycle { get; init; }
    public required Portfolio Portfolio { get; init; }
    public required PilotMandate Mandate { get; init; }
    public required PilotObservation Observation { get; init; }
    public required IReadOnlyDictionary<string, PilotAssetInfo> Universe { get; init; }
    public required string Currency { get; init; }
    public required string PriceSource { get; init; }
    public required string Benchmark { get; init; }
    public required double RiskFreeRate { get; init; }
    public DateTime AsOfUtc => Cycle.AsOfUtc;
    public DateOnly AsOfDate => DateOnly.FromDateTime(Cycle.AsOfUtc);
    /// <summary>Per-cycle cache of computed asset metrics (shared by screening and metric tools).</summary>
    public Dictionary<string, PilotAssetMetrics> MetricsCache { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A tool the model may request. Implementations call real platform services and contain no prompts.</summary>
public abstract class PilotTool
{
    public abstract string Name { get; }
    public abstract string Purpose { get; }
    public abstract PilotToolCategory Category { get; }
    /// <summary>Argument description given to the model (the tool validates the arguments itself).</summary>
    public abstract string Arguments { get; }
    public virtual TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public abstract Task<object> RunAsync(PilotToolContext context, JsonElement args, CancellationToken ct);
    public abstract string Summarize(object result);
}

/// <summary>
/// Runs model-requested tools with the controls that never depend on the model: an allow-list by name and category,
/// argument size limits, a per-cycle budget, a timeout, a size cap on what goes back to the model, and a journal entry for
/// every request – including denied ones.
/// </summary>
public sealed class PilotToolRunner(IEnumerable<PilotTool> tools, PilotJournal journal, ILogger<PilotToolRunner> logger)
{
    public const int MaxToolCallsPerCycle = 30;
    public const int MaxArgumentChars = 4000;
    public const int MaxResultChars = 9000;
    public static readonly IReadOnlySet<PilotToolCategory> AllowedCategories =
        new HashSet<PilotToolCategory> { PilotToolCategory.Read, PilotToolCategory.Analyze, PilotToolCategory.Simulate };

    private readonly Dictionary<string, PilotTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public IReadOnlyCollection<PilotTool> Available => _tools.Values.Where(t => AllowedCategories.Contains(t.Category)).OrderBy(t => t.Name).ToList();

    /// <summary>Runs one request and returns the text given back to the model (a result or an error description).</summary>
    public async Task<PilotToolOutcome> RunAsync(PilotToolContext context, PilotToolRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var name = PilotText.Clip(request.Tool, 60);
        var argsText = request.ArgsJson ?? "{}";
        try
        {
            if (!_tools.TryGetValue(request.Tool ?? "", out var tool) || !AllowedCategories.Contains(tool.Category))
                throw new PilotToolException("not_permitted",
                    $"“{name}” is not a tool Matrix Pilot may use. It cannot change its mandate, permissions, validation, the ledger or the audit trail.");
            if (context.Cycle.ToolCalls >= MaxToolCallsPerCycle)
                throw new PilotToolException("budget_exceeded", $"The cycle has used its {MaxToolCallsPerCycle} tool calls; decide with the evidence gathered.");
            if (argsText.Length > MaxArgumentChars) throw new PilotToolException("invalid_arguments", $"Arguments longer than {MaxArgumentChars} characters.");
            JsonElement args;
            try { args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsText) ? "{}" : argsText).RootElement.Clone(); }
            catch (JsonException) { throw new PilotToolException("invalid_arguments", "Arguments are not valid JSON."); }
            if (args.ValueKind != JsonValueKind.Object) throw new PilotToolException("invalid_arguments", "Arguments must be a JSON object.");

            context.Cycle.ToolCalls++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(tool.Timeout);
            object result;
            try { result = await tool.RunAsync(context, args, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PilotToolException("timeout", $"{tool.Name} did not finish within {tool.Timeout.TotalSeconds:0} s.");
            }
            var json = PilotJson.Serialize(result);
            var forModel = json.Length > MaxResultChars ? json[..MaxResultChars] + "…(truncated)" : json;
            var step = journal.Add(context.Cycle, PilotStepKind.ToolCall, tool.Name, "ok", sw.Elapsed.TotalMilliseconds, tool.Summarize(result),
                argsText, forModel, Hash(json));
            context.Cycle.ToolMs += sw.Elapsed.TotalMilliseconds;
            return new PilotToolOutcome(step.Sequence, tool.Name, true, forModel);
        }
        catch (Exception ex) when (ex is PilotToolException or ValidationException or NotFoundException or ConflictException or Fx.FxRateUnavailableException)
        {
            var code = ex is PilotToolException p ? p.Code : "failed";
            var status = code is "not_permitted" or "budget_exceeded" ? "denied" : "error";
            var step = journal.Add(context.Cycle, PilotStepKind.ToolCall, name, status, sw.Elapsed.TotalMilliseconds, ex.Message, PilotText.Clip(argsText, MaxArgumentChars), null, null);
            context.Cycle.ToolMs += sw.Elapsed.TotalMilliseconds;
            logger.LogInformation("Matrix Pilot tool {Tool} {Status} in cycle {CycleId}: {Code}", name, status, context.Cycle.Id, code);
            return new PilotToolOutcome(step.Sequence, name, false, PilotJson.Serialize(new { error = code, message = ex.Message }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ExceptionFilters.IsRecoverable(ex))
        {
            // A defect inside one tool: logged with its stack trace, journaled as a failed call and reported to the model as
            // unavailable – the cycle continues on the other evidence. (The platform's own steps still stop the cycle.)
            logger.LogError(ex, "Matrix Pilot tool {Tool} failed unexpectedly in cycle {CycleId}", name, context.Cycle.Id);
            const string message = "The tool failed with an internal error and is unavailable in this cycle.";
            var step = journal.Add(context.Cycle, PilotStepKind.ToolCall, name, "error", sw.Elapsed.TotalMilliseconds,
                $"{message} ({ex.GetType().Name})", PilotText.Clip(argsText, MaxArgumentChars), null, null);
            context.Cycle.ToolMs += sw.Elapsed.TotalMilliseconds;
            return new PilotToolOutcome(step.Sequence, name, false, PilotJson.Serialize(new { error = "internal_error", message }));
        }
    }

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
}

public sealed record PilotToolOutcome(int Step, string Tool, bool Ok, string Result);

/// <summary>Appends journal entries to a cycle (kept in order; saved with the cycle).</summary>
public sealed class PilotJournal(TimeProvider clock)
{
    public const int MaxStoredJson = 12_000;

    public MatrixPilotStep Add(MatrixPilotCycle cycle, PilotStepKind kind, string name, string status, double durationMs, string summary,
        string? input = null, string? output = null, string? outputHash = null)
    {
        var step = new MatrixPilotStep
        {
            CycleId = cycle.Id,
            Sequence = cycle.Steps.Count + 1,
            AtUtc = clock.GetUtcNow().UtcDateTime,
            Kind = kind,
            Name = PilotText.Clip(name, 80),
            Status = status,
            DurationMs = Math.Round(durationMs, 1),
            Summary = PilotText.Clip(summary, 1000),
            InputJson = input is null ? null : Cap(input),
            OutputJson = output is null ? null : Cap(output),
            OutputHash = outputHash,
        };
        cycle.Steps.Add(step);
        return step;
    }

    private static string Cap(string s) => s.Length <= MaxStoredJson ? s : s[..MaxStoredJson] + "…(truncated)";
}

/// <summary>Strict readers for tool arguments (anything unexpected is an invalid_arguments error, never a guess).</summary>
public static class PilotArgs
{
    public static List<string> Symbols(JsonElement args, string name, int min, int max, bool required = true)
    {
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
        {
            if (required && min > 0) throw new PilotToolException("invalid_arguments", $"“{name}” is required (a list of {min}–{max} symbols).");
            return [];
        }
        if (e.ValueKind != JsonValueKind.Array) throw new PilotToolException("invalid_arguments", $"“{name}” must be a list of symbols.");
        var list = e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? Asset.NormalizeSymbol(x.GetString()!) : "")
            .Where(s => s.Length is > 0 and <= 20).Distinct().ToList();
        if (list.Count < min || list.Count > max) throw new PilotToolException("invalid_arguments", $"“{name}” needs {min}–{max} distinct symbols (got {list.Count}).");
        return list;
    }

    public static List<string> Strings(JsonElement args, string name, int max)
    {
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return [];
        if (e.ValueKind != JsonValueKind.Array) throw new PilotToolException("invalid_arguments", $"“{name}” must be a list of strings.");
        var list = e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(s => s.Length is > 0 and <= 60).Distinct().ToList();
        if (list.Count > max) throw new PilotToolException("invalid_arguments", $"“{name}” has more than {max} entries.");
        return list;
    }

    public static int Int(JsonElement args, string name, int @default, int min, int max)
    {
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return @default;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var d) || d != Math.Floor(d) || d < min || d > max)
            throw new PilotToolException("invalid_arguments", $"“{name}” must be a whole number between {min} and {max}.");
        return (int)d;
    }

    public static double Number(JsonElement args, string name, double? @default, double min, double max)
    {
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            return @default ?? throw new PilotToolException("invalid_arguments", $"“{name}” is required.");
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var d) || !double.IsFinite(d) || d < min || d > max)
            throw new PilotToolException("invalid_arguments", $"“{name}” must be a number between {min} and {max}.");
        return d;
    }

    public static string Choice(JsonElement args, string name, string? @default, params string[] allowed)
    {
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null)
            return @default ?? throw new PilotToolException("invalid_arguments", $"“{name}” is required ({string.Join(", ", allowed)}).");
        var s = e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
        if (!allowed.Contains(s)) throw new PilotToolException("invalid_arguments", $"“{name}” must be one of {string.Join(", ", allowed)}.");
        return s;
    }

    /// <summary>A map of symbol → weight (fractions).</summary>
    public static Dictionary<string, double> Weights(JsonElement args, string name, int max)
    {
        if (!args.TryGetProperty(name, out var e) || e.ValueKind != JsonValueKind.Object)
            throw new PilotToolException("invalid_arguments", $"“{name}” must be an object of symbol → weight (fractions, e.g. 0.08).");
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var p in e.EnumerateObject())
        {
            var s = Asset.NormalizeSymbol(p.Name);
            if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetDouble(out var w) || !double.IsFinite(w) || w < 0 || w > 1)
                throw new PilotToolException("invalid_arguments", $"{s}: weight must be a fraction between 0 and 1.");
            if (!map.TryAdd(s, w)) throw new PilotToolException("invalid_arguments", $"{s} appears twice.");
        }
        if (map.Count is 0 || map.Count > max) throw new PilotToolException("invalid_arguments", $"“{name}” needs 1–{max} symbols.");
        if (map.Values.Sum() > 1 + 1e-6) throw new PilotToolException("invalid_arguments", "Weights sum to more than 100 %.");
        return map;
    }
}
