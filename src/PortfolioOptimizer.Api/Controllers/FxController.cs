using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Fx;

namespace PortfolioOptimizer.Api.Controllers;

/// <summary>Exchange rates used to convert prices between currencies (reporting conversions; nothing is exchanged).</summary>
[ApiController]
[Route("api/v1/fx")]
public sealed class FxController(FxService fx) : ControllerBase
{
    /// <summary>The FX source for the active price source, its quote convention and the supported currencies.</summary>
    [HttpGet("currencies")]
    public async Task<FxInfoDto> Currencies(CancellationToken ct)
    {
        var simulated = await fx.ActiveSourceIsSimulatedAsync(ct);
        var provider = fx.ProviderFor(simulated);
        return new FxInfoDto(provider.Name, provider.Description, provider.IsSimulated, fx.SupportedCurrencies(simulated).Order().ToList(),
            "rate = units of the target currency per one unit of the source currency; converted amount = amount × rate. Cross rates via EUR.",
            FxTable.DefaultMaxCarryForwardDays);
    }

    /// <summary>The latest usable rate from <paramref name="from"/> to <paramref name="to"/> (422 with the reason when none is available).</summary>
    [HttpGet("rate")]
    public async Task<FxRateDto> Rate([FromQuery, Required, RegularExpression("^[A-Z]{3}$")] string from,
        [FromQuery, Required, RegularExpression("^[A-Z]{3}$")] string to, CancellationToken ct)
    {
        var simulated = await fx.ActiveSourceIsSimulatedAsync(ct);
        var q = await fx.LatestAsync(from, to, simulated, ct);
        return new FxRateDto(q.From, q.To, q.Rate, q.ObservationDate, q.CarriedForwardDays, q.Source, q.IsSimulated,
            q.IsIdentity ? "Same currency." :
            $"Reference rate of {q.ObservationDate:yyyy-MM-dd} ({q.Source}){(q.CarriedForwardDays > 0 ? $", carried forward {q.CarriedForwardDays} day(s)" : "")}. " +
            "For analysis and reporting – not a rate at which money can be exchanged.");
    }
}
