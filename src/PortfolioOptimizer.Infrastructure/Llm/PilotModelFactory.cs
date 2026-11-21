using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortfolioOptimizer.Application.Abstractions;

namespace PortfolioOptimizer.Infrastructure.Llm;

/// <summary>
/// Builds Matrix Pilot's models from configuration (docs/matrix-pilot.md › Models):
/// <list type="bullet">
/// <item>primary: <c>MatrixPilot:Provider</c> + <c>MatrixPilot:Model</c>, or the shared model when no provider is set;</item>
/// <item>fallbacks: <c>MatrixPilot:Fallbacks</c>, an ordered list <c>Provider:model; Provider:model</c>.</item>
/// </list>
/// Every provider uses its own key – <c>MatrixPilot:Keys:&lt;Provider&gt;</c> or the environment variable
/// <c>&lt;PROVIDER&gt;_API_KEY</c>; the shared key (<c>Agent:Llm:ApiKey</c>, <c>LLM_API_KEY</c>) is only ever given to the
/// primary, so one provider's key is never sent to another.
/// </summary>
public static class PilotModelFactory
{
    public static readonly string[] Providers = ["Gemini", "Anthropic", .. OpenAiCompatibleLanguageModel.Endpoints.Keys, "Mock"];

    public static IPilotLanguageModel Create(IServiceProvider sp, IConfiguration configuration)
    {
        var shared = sp.GetRequiredService<IOptions<LanguageModelOptions>>().Value;
        var provider = configuration["MatrixPilot:Provider"];
        var primary = string.IsNullOrWhiteSpace(provider)
            ? sp.GetRequiredService<ILanguageModel>()
            : Build(sp, shared, provider.Trim(), configuration["MatrixPilot:Model"] is { Length: > 0 } m ? m : shared.Model,
                Key(configuration, provider.Trim()) ?? (string.IsNullOrWhiteSpace(shared.ApiKey) ? null : shared.ApiKey), primary: true);
        var fallbacks = ParseList(configuration["MatrixPilot:Fallbacks"])
            .Select(e => Build(sp, shared, e.Provider, e.Model, Key(configuration, e.Provider), primary: false))
            .ToList();
        return new PilotLanguageModel(primary, fallbacks);
    }

    /// <summary>Parses <c>Provider:model; Provider:model</c> (also comma or newline separated); the model id may contain colons.</summary>
    public static IReadOnlyList<(string Provider, string Model)> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] :
        value.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry =>
            {
                var colon = entry.IndexOf(':');
                return colon < 0 ? (entry, "") : (entry[..colon].Trim(), entry[(colon + 1)..].Trim());
            })
            .ToList();

    /// <summary>The key of one provider: <c>MatrixPilot:Keys:&lt;Provider&gt;</c>, else <c>&lt;PROVIDER&gt;_API_KEY</c>.</summary>
    public static string? Key(IConfiguration configuration, string provider) =>
        new[] { configuration[$"MatrixPilot:Keys:{provider}"], Environment.GetEnvironmentVariable($"{provider.ToUpperInvariant()}_API_KEY") }
            .FirstOrDefault(k => !string.IsNullOrWhiteSpace(k))?.Trim();

    private static ILanguageModel Build(IServiceProvider sp, LanguageModelOptions shared, string provider, string? model, string? key, bool primary)
    {
        var options = new LanguageModelOptions
        {
            Provider = provider, Model = model, ApiKey = key ?? "",
            Effort = shared.Effort, TimeoutSeconds = shared.TimeoutSeconds, MaxRetries = shared.MaxRetries,
            RetryBaseSeconds = shared.RetryBaseSeconds, MaxRetryWaitSeconds = shared.MaxRetryWaitSeconds,
        };
        switch (provider)
        {
            case "Gemini":
                // Without a key the Gemini client would look for GEMINI_API_KEY and then LLM_API_KEY itself; a fallback must never
                // pick up the shared key (it may belong to another provider), so it stays unusable instead.
                return key is null && !primary
                    ? new UnconfiguredLanguageModel(provider, string.IsNullOrWhiteSpace(model) ? GeminiLanguageModel.DefaultModel : model.Trim(), "No Gemini API key is configured (GEMINI_API_KEY).")
                    : ActivatorUtilities.CreateInstance<GeminiLanguageModel>(sp, Options.Create(options));
            case "Anthropic":
                return ActivatorUtilities.CreateInstance<AnthropicLanguageModel>(sp, Options.Create(options));
            case "Mock":
                return sp.GetRequiredService<MockLanguageModel>();
        }
        if (OpenAiCompatibleLanguageModel.Endpoints.ContainsKey(provider))
        {
            if (string.IsNullOrWhiteSpace(model))
                return new UnconfiguredLanguageModel(provider, "", $"{provider} needs a model id (write {provider}:<model id>).");
            return new OpenAiCompatibleLanguageModel(provider, model, key,
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiCompatibleLanguageModel.HttpClientName), options,
                sp.GetRequiredService<ILogger<OpenAiCompatibleLanguageModel>>());
        }
        return new UnconfiguredLanguageModel(provider, model ?? "", $"Unknown provider {provider} (known: {string.Join(", ", Providers)}).");
    }
}
