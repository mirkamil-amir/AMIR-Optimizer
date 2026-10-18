using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.MarketData;
using PortfolioOptimizer.Application.Portfolios;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Trading;

/// <summary>Trade proposals and simulated (paper) execution. No real orders are ever sent anywhere.</summary>
public sealed class TradingService(
    IApplicationDbContext db,
    PortfolioService portfolios,
    PortfolioValuationService valuations,
    ICurrentUser currentUser,
    IAuditLog audit,
    TimeProvider clock,
    ILogger<TradingService> logger)
{
    public async Task<TradeProposalDto> CreateFromRunAsync(Guid runId, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var run = await db.OptimizationRuns.AsNoTracking().Include(r => r.Weights).SingleOrDefaultAsync(r => r.Id == runId && r.OwnerId == owner, ct)
                  ?? throw new NotFoundException("Optimization run", runId);
        if (run.Status != OptimizationRunStatus.Succeeded)
            throw new ConflictException($"Optimization run {runId} did not succeed ({run.Status}); it has no allocation to trade.");
        var targets = run.Weights.Where(w => w.Weight > 0).ToDictionary(w => w.AssetId, w => w.Weight);
        return await CreateProposalAsync(run.PortfolioId, run.Id, targets, run.SettingsSnapshot, ct);
    }

    public async Task<TradeProposalDto> RebalancePreviewAsync(Guid portfolioId, RebalancePreviewRequest request, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        if (request.TargetWeights is { Count: > 0 } explicitWeights)
        {
            var symbols = explicitWeights.Keys.Select(Asset.NormalizeSymbol).ToList();
            var assets = await db.Assets.AsNoTracking().Where(a => symbols.Contains(a.Symbol)).ToDictionaryAsync(a => a.Symbol, ct);
            var errors = new Dictionary<string, string[]>();
            var unknown = symbols.Except(assets.Keys).ToList();
            if (unknown.Count > 0) errors["targetWeights"] = [$"Unknown asset(s): {string.Join(", ", unknown)}."];
            if (explicitWeights.Values.Any(v => v < 0 || !double.IsFinite(v))) errors["targetWeights"] = ["Weights must be non-negative (no short selling)."];
            if (explicitWeights.Values.Sum() > 1 + 1e-9) errors["targetWeights"] = ["Weights must sum to at most 1 (the remainder is cash)."];
            if (errors.Count > 0) throw new ValidationException(errors);
            var targets = explicitWeights.ToDictionary(kv => assets[Asset.NormalizeSymbol(kv.Key)].Id, kv => kv.Value);
            return await CreateProposalAsync(portfolioId, null, targets, portfolio.Settings, ct);
        }

        var runQuery = db.OptimizationRuns.AsNoTracking().Where(r => r.PortfolioId == portfolioId && r.Status == OptimizationRunStatus.Succeeded);
        var runId = request.OptimizationRunId
                    ?? await runQuery.OrderByDescending(r => r.CreatedAtUtc).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct)
                    ?? throw new ConflictException("The portfolio has no successful optimization run. Run an optimization or supply target weights.");
        var run = await db.OptimizationRuns.AsNoTracking().Include(r => r.Weights).SingleOrDefaultAsync(r => r.Id == runId, ct)
                  ?? throw new NotFoundException("Optimization run", runId);
        if (run.PortfolioId != portfolioId) throw new ValidationException("optimizationRunId", "The run belongs to a different portfolio.");
        if (run.Status != OptimizationRunStatus.Succeeded) throw new ConflictException("The selected optimization run did not succeed.");
        return await CreateProposalAsync(portfolioId, run.Id, run.Weights.Where(w => w.Weight > 0).ToDictionary(w => w.AssetId, w => w.Weight), portfolio.Settings, ct);
    }

    public async Task<TradeProposalDto> GetProposalAsync(Guid proposalId, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        var p = await db.TradeProposals.AsNoTracking().Include(x => x.Lines)
                    .Where(x => x.Id == proposalId && db.Portfolios.Any(pf => pf.Id == x.PortfolioId && pf.OwnerId == owner))
                    .SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("Trade proposal", proposalId);
        return ToDto(p);
    }

    /// <summary>Most recent proposal created from a run, or a new one if none exists.</summary>
    public async Task<TradeProposalDto> GetOrCreateForRunAsync(Guid runId, CancellationToken ct)
    {
        var owner = currentUser.RequireUserId();
        if (!await db.OptimizationRuns.AnyAsync(r => r.Id == runId && r.OwnerId == owner, ct)) throw new NotFoundException("Optimization run", runId);
        var existing = await db.TradeProposals.AsNoTracking().Include(x => x.Lines)
            .Where(p => p.OptimizationRunId == runId).OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return existing is not null ? ToDto(existing) : await CreateFromRunAsync(runId, ct);
    }

    public async Task<PagedResult<TradeProposalDto>> ListProposalsAsync(Guid portfolioId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new ValidationException("Invalid paging parameters (page ≥ 1, 1 ≤ pageSize ≤ 100).");
        await portfolios.EnsureExistsAsync(portfolioId, ct);
        var q = db.TradeProposals.AsNoTracking().Where(p => p.PortfolioId == portfolioId);
        var total = await q.CountAsync(ct);
        var items = await q.Include(p => p.Lines).OrderByDescending(p => p.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<TradeProposalDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    /// <summary>
    /// Executes a pending proposal in the simulated portfolio. Idempotent per <paramref name="idempotencyKey"/>:
    /// repeating the same request returns the original result without trading again.
    /// </summary>
    public async Task<PaperTradeResultDto> ExecuteAsync(Guid portfolioId, ExecutePaperTradesRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (!request.Confirm) throw new ValidationException("confirm", "Simulated execution requires explicit confirmation (confirm = true).");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new ValidationException("Idempotency-Key", "An Idempotency-Key header (1-100 characters) is required.");

        // Ownership first: replaying a stored result must not leak another user's execution.
        var portfolio = await portfolios.LoadAsync(portfolioId, ct, tracking: true);
        portfolio.EnsureActive();
        var previous = await db.PaperTradeBatches.AsNoTracking().Include(b => b.Orders)
            .SingleOrDefaultAsync(b => b.PortfolioId == portfolioId && b.IdempotencyKey == idempotencyKey, ct);
        if (previous is not null)
        {
            if (previous.ProposalId != request.ProposalId)
                throw new ConflictException("This Idempotency-Key was already used for a different proposal.");
            return ToDto(previous, replay: true);
        }

        var proposal = await db.TradeProposals.Include(p => p.Lines).SingleOrDefaultAsync(p => p.Id == request.ProposalId, ct)
                       ?? throw new NotFoundException("Trade proposal", request.ProposalId);
        if (proposal.PortfolioId != portfolioId) throw new ValidationException("proposalId", "The proposal belongs to a different portfolio.");
        if (proposal.Status != ProposalStatus.Pending)
            throw new ConflictException($"The proposal is {proposal.Status.ToString().ToLowerInvariant()} and can no longer be executed. Create a new proposal.");

        if (portfolio.Version != proposal.PortfolioVersion)
            throw new ConflictException("Holdings or cash changed after this proposal was created. Create a new proposal before executing.");

        var assetIds = proposal.Lines.Select(l => l.AssetId).ToList();
        var assets = await db.Assets.Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        // Execution-time prices and exchange rates (the proposal's estimates may be older).
        var valuation = await valuations.ValueAsync(portfolio, null, ct, assetIds);
        var settings = portfolio.Settings;
        var now = clock.GetUtcNow().UtcDateTime;

        var batch = new PaperTradeBatch
        {
            Id = Guid.NewGuid(),
            PortfolioId = portfolioId,
            ProposalId = proposal.Id,
            IdempotencyKey = idempotencyKey,
            ExecutedAtUtc = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var line in proposal.Lines.Where(l => l.Action == ProposalAction.Sell).Concat(proposal.Lines.Where(l => l.Action == ProposalAction.Buy)))
        {
            var side = line.Action == ProposalAction.Sell ? OrderSide.Sell : OrderSide.Buy;
            var order = new SimulatedOrder
            {
                Id = Guid.NewGuid(),
                BatchId = batch.Id,
                PortfolioId = portfolioId,
                ProposalId = proposal.Id,
                AssetId = line.AssetId,
                Symbol = line.Symbol,
                Side = side,
                RequestedQuantity = line.OrderQuantity,
                CreatedAtUtc = now,
            };
            batch.Orders.Add(order);

            var position = valuation.Positions.GetValueOrDefault(line.AssetId);
            order.Currency = assets[line.AssetId].Currency;
            if (position?.Price is not { } lp)
            {
                order.Status = OrderStatus.Rejected;
                order.Message = "No price available at execution time.";
                continue;
            }
            if (position.UnitValue is not { } basePrice)
            {
                order.Status = OrderStatus.Rejected;
                order.Message = $"No exchange rate at execution time: {position.Problem}.";
                continue;
            }
            // Rounded to the stored precision first, so that quantity × price × stored rate reproduces the booked amount exactly.
            var fx = position.Fx is { } q ? new TradeFx(FxPrecision.Round(q.Rate), q.ObservationDate, q.Source) : TradeFx.SameCurrency;
            if (position.Fx is not null) basePrice = lp.Price * fx.Rate;

            var qty = line.OrderQuantity;
            if (side == OrderSide.Sell)
            {
                var held = portfolio.Holdings.SingleOrDefault(h => h.AssetId == line.AssetId)?.Quantity ?? 0;
                qty = Math.Min(qty, held);
            }
            else
            {
                var affordable = AffordableQuantity(portfolio.CashBalance, basePrice, settings);
                qty = Math.Min(qty, affordable);
            }

            if (qty <= 0)
            {
                order.Status = OrderStatus.Rejected;
                order.Message = side == OrderSide.Buy ? "Insufficient cash at execution price after fees." : "No shares held.";
                continue;
            }

            var fee = TransactionCostModel.Estimate(qty * basePrice, settings.TransactionCostBps, settings.MinimumFee);
            var asset = assets[line.AssetId];
            var execution = side == OrderSide.Buy
                ? portfolio.Buy(asset, qty, lp.Price, lp.PriceDate, fx, fee, order.Id, now)
                : portfolio.Sell(asset, qty, lp.Price, lp.PriceDate, fx, fee, order.Id, now);

            order.FilledQuantity = qty;
            order.ExecutionPrice = lp.Price;
            order.FxRate = fx.Rate;
            order.Status = qty < line.OrderQuantity ? OrderStatus.PartiallyFilled : OrderStatus.Filled;
            order.Message = $"Simulated fill at {lp.Price} {asset.Currency} ({lp.Origin.ToLowerInvariant()} of {lp.PriceDate:yyyy-MM-dd})" +
                            (asset.Currency == portfolio.BaseCurrency ? "." : $"; simulated exchange at {fx.Rate:0.######} {portfolio.BaseCurrency}/{asset.Currency} (reference rate of {fx.RateDate:yyyy-MM-dd}) – no real currency was exchanged.");
            if (line.Price is { } est && est != lp.Price)
                batch.Notes.Add($"{line.Symbol}: executed at {lp.Price} instead of the proposal estimate {est} (newer price available).");
            if (qty < line.OrderQuantity)
                batch.Notes.Add($"{line.Symbol}: filled {qty} of {line.OrderQuantity} requested.");
            batch.TotalFees += execution.Fees;
        }

        batch.CashAfter = portfolio.CashBalance;
        proposal.Status = ProposalStatus.Executed;
        db.PaperTradeBatches.Add(batch);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The portfolio was modified concurrently. Reload and create a new proposal.");
        }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
        {
            throw new ConflictException("A request with this Idempotency-Key is already being processed.");
        }

        logger.LogInformation("Paper trades executed for portfolio {PortfolioId}: proposal {ProposalId}, {Orders} orders, fees {Fees}",
            portfolioId, proposal.Id, batch.Orders.Count, batch.TotalFees);
        await audit.RecordAsync(AuditActions.PaperTradesExecuted, "Portfolio", portfolioId.ToString(),
            $"Proposal {proposal.Id}: {batch.Orders.Count(o => o.Status != OrderStatus.Rejected)} filled, {batch.Orders.Count(o => o.Status == OrderStatus.Rejected)} rejected (simulated).", ct: ct);
        return ToDto(batch, replay: false);
    }

    private static decimal AffordableQuantity(decimal cash, decimal price, PortfolioSettings s) =>
        TradeProposalCalculator.MaxAffordable(cash, price, s.AllowFractionalShares, s.TransactionCostBps, s.MinimumFee);

    private async Task<TradeProposalDto> CreateProposalAsync(Guid portfolioId, Guid? runId, Dictionary<int, double> targets, PortfolioSettings settings, CancellationToken ct)
    {
        var portfolio = await portfolios.LoadAsync(portfolioId, ct);
        portfolio.EnsureActive();
        var held = portfolio.Holdings.Where(h => h.Quantity > 0).ToDictionary(h => h.AssetId, h => h.Quantity);
        var ids = targets.Keys.Union(held.Keys).ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        // Prices in each stock's own currency, converted to the portfolio currency at the rate of the price date.
        var valuation = await valuations.ValueAsync(portfolio, null, ct, ids);

        var computation = TradeProposalCalculator.Compute(new ProposalParameters(
            portfolio.CashBalance,
            ids.Select(id =>
            {
                var v = valuation.Positions.GetValueOrDefault(id);
                var rate = v?.Fx is { } q ? FxPrecision.Round(q.Rate) : 1m;
                return new ProposalAsset(id, assets[id].Symbol, assets[id].Name, held.GetValueOrDefault(id), v?.Price, assets[id].Currency, rate,
                    v?.Fx?.ObservationDate, v?.Price is not null && v.UnitValue is null ? v.Problem : null);
            }).ToList(),
            targets,
            settings.AllowFractionalShares,
            settings.TransactionCostBps,
            settings.MinimumFee,
            held.Count > 0 ? settings.RebalanceDriftThreshold : 0));

        var proposal = new TradeProposal
        {
            Id = Guid.NewGuid(),
            PortfolioId = portfolioId,
            OptimizationRunId = runId,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            Status = ProposalStatus.Pending,
            AllowFractionalShares = settings.AllowFractionalShares,
            PortfolioValue = computation.PortfolioValue,
            CashBefore = computation.CashBefore,
            EstimatedCashAfter = computation.CashAfter,
            EstimatedCosts = computation.TotalCosts,
            TargetCashWeight = computation.TargetCashWeight,
            PortfolioVersion = portfolio.Version,
            Notes = [.. computation.Notes],
            Lines = computation.Lines.OrderByDescending(l => l.TargetWeight).ThenBy(l => l.Symbol).ToList(),
        };
        var foreign = valuation.Positions.Values.Where(v => v.Fx is not null).Select(v => v.Currency).Distinct().Order().ToList();
        if (foreign.Count > 0)
            proposal.Notes.Insert(0, $"Prices in {string.Join(", ", foreign)} are converted to {portfolio.BaseCurrency} with the reference rate of each price date. " +
                                     "Executing buys or sells them through a SIMULATED currency exchange of the portfolio cash; no real money is converted.");
        if (valuation.Positions.Values.Any(v => v.Price?.IsSimulated == true))
            proposal.Notes.Insert(0, "Prices are SIMULATED and do not reflect real markets.");

        await db.TradeProposals.Where(p => p.PortfolioId == portfolioId && p.Status == ProposalStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, ProposalStatus.Superseded), ct);
        db.TradeProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        return ToDto(proposal);
    }

    private static TradeProposalDto ToDto(TradeProposal p)
    {
        var buys = p.Lines.Where(l => l.Action == ProposalAction.Buy).Sum(l => l.EstimatedTradeValue);
        var sells = p.Lines.Where(l => l.Action == ProposalAction.Sell).Sum(l => l.EstimatedTradeValue);
        return new TradeProposalDto(p.Id, p.PortfolioId, p.OptimizationRunId, p.CreatedAtUtc, p.Status, p.AllowFractionalShares,
            p.PortfolioValue, p.CashBefore, p.EstimatedCashAfter, p.EstimatedCosts, buys, sells,
            (double)p.TargetCashWeight, p.PortfolioValue > 0 ? (double)(p.EstimatedCashAfter / p.PortfolioValue) : 0,
            p.Notes,
            p.Lines.Select(l => new TradeProposalLineDto(l.Symbol, l.CompanyName, l.Price, l.PriceDate, l.PriceIsStale, l.TargetWeight,
                l.CurrentWeight, l.ResultingWeight, l.CurrentQuantity, l.TargetValue, l.TargetQuantity, l.OrderQuantity, l.Action,
                l.EstimatedTradeValue, l.EstimatedCost, l.Note, l.Currency, l.FxRate, l.FxRateDate)).ToList());
    }

    private static PaperTradeResultDto ToDto(PaperTradeBatch b, bool replay) =>
        new(b.Id, b.ProposalId, b.ExecutedAtUtc, replay, b.TotalFees, b.CashAfter, b.Orders.Select(o => o.ToDto()).ToList(), b.Notes);
}
