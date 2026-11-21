using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Pilot;

/// <summary>One fallback model: provider, model id and whether it can be called (key present, entry complete).</summary>
public sealed record PilotFallbackModelDto(string Provider, string Model, bool Available);

/// <summary>
/// Matrix Pilot's ordered model list (primary, then fallbacks) as stored on the pilot and compared with the configuration.
/// The owner confirms the whole list; a different configured list stops cycles until it is confirmed again.
/// </summary>
public static class PilotModelChain
{
    /// <summary>The stored form of the fallbacks: <c>Provider:model;Provider:model</c> (empty = none).</summary>
    public static string Key(IEnumerable<ILanguageModel> fallbacks) => string.Join(";", fallbacks.Select(m => $"{m.Provider}:{m.Model}"));

    public static IReadOnlyList<(string Provider, string Model)> Parse(string? key) =>
        string.IsNullOrWhiteSpace(key) ? [] :
        key.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e =>
        {
            var colon = e.IndexOf(':');
            return colon < 0 ? (e, "") : (e[..colon], e[(colon + 1)..]);
        }).ToList();

    /// <summary>Whether the pilot was confirmed for exactly the configured models, in this order.</summary>
    public static bool Matches(MatrixPilot pilot, IPilotLanguageModel model) =>
        pilot.Provider == model.Provider && pilot.Model == model.Model && pilot.FallbackModels == Key(model.Fallbacks);

    /// <summary>"Gemini gemini-3.5-flash, then Groq llama-…" – for messages, events and mandate changes.</summary>
    public static string Describe(string provider, string model, string? fallbacks) =>
        string.Join(", then ", new[] { $"{provider} {model}" }.Concat(Parse(fallbacks).Select(f => $"{f.Provider} {f.Model}")));

    public static string Describe(IPilotLanguageModel model) => Describe(model.Provider, model.Model, Key(model.Fallbacks));

    public static string Describe(MatrixPilot pilot) => Describe(pilot.Provider, pilot.Model, pilot.FallbackModels);

    /// <summary>Whether any model of the list can be called at all.</summary>
    public static bool AnyAvailable(IPilotLanguageModel model) => model.IsAvailable || model.Fallbacks.Any(f => f.IsAvailable);

    public static IReadOnlyList<PilotFallbackModelDto> Dtos(IPilotLanguageModel model) =>
        model.Fallbacks.Select(f => new PilotFallbackModelDto(f.Provider, f.Model, f.IsAvailable)).ToList();

    /// <summary>A stored list (availability unknown for a list that is no longer configured, so it is judged against the configuration).</summary>
    public static IReadOnlyList<PilotFallbackModelDto> Dtos(string? key, IPilotLanguageModel configured) =>
        Parse(key).Select(f => new PilotFallbackModelDto(f.Provider, f.Model,
            configured.Fallbacks.Any(c => c.Provider == f.Provider && c.Model == f.Model && c.IsAvailable))).ToList();
}
