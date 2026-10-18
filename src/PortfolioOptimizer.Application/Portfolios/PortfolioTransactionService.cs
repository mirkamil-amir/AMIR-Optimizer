using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Application.Fx;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Portfolios;

/// <summary>
/// Records manual transactions (buy, sell, deposit, withdrawal, dividend, fee) in one of the current user's portfolios.
/// Each booking is atomic: the journal record, ledger entries, holding and cash change commit together or not at all.
/// Bookings are append-only and in time order – a transaction cannot be dated before the portfolio's latest booking, so
/// the cash and quantity checks at booking time are the checks at the effective date. Simulated bookkeeping only.
/// </summary>
public sealed class PortfolioTransactionService(IApplicationDbContext db, PortfolioService portfolios, FxService fx, IAuditLog audit, TimeProvider clock)
{
    public const string Methodology =
        "Buys and sells use the price you enter (in the stock's trading currency) and, for another currency, the reference exchange " +
        "rate of the effective date; the commission is added to the cost basis of a buy (weighted-average cost) and deducted " +
        "from the proceeds of a sell. Deposits and withdrawals are external cash flows and never count as gains or losses; " +
        "dividends and standalone fees are part of the return. Corrections are new reversing entries; nothing is edited or deleted.";

    public async Task<PortfolioTransactionDto> RecordAsync(Guid portfolioId, RecordTransactionRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new ValidationException("Idempotency-Key", "An Idempotency-Key header (1-100 characters) is required.");

        // Ownership first, so that a replayed key never reveals another user's booking.
        var portfolio = await portfolios.LoadAsync(portfolioId, ct, tracking: true);
        if (await FindByKeyAsync(portfolioId, idempotencyKey, ct) is { } previous)
        {
            if (previous.Type != request.Type) throw new ConflictException("This Idempotency-Key was already used for a different transaction.");
            return await ToDtoAsync(previous, portfolio.CashBalance, replay: true, ct);
        }
        portfolio.EnsureActive();

        var now = clock.GetUtcNow().UtcDateTime;
        var (date, bookedAt) = await BookingTimeAsync(portfolioId, request.Date, now, ct);
        var tx = new PortfolioTransaction
        {
            Id = Guid.NewGuid(),
            PortfolioId = portfolioId,
            Type = request.Type,
            Currency = portfolio.BaseCurrency,
            EffectiveDate = date,
            BookedAtUtc = bookedAt,
            CreatedAtUtc = now,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            IdempotencyKey = idempotencyKey,
        };
        var label = tx.Note is null ? "" : $" – {tx.Note}";

        switch (request.Type)
        {
            case PortfolioTransactionType.Buy or PortfolioTransactionType.Sell:
            {
                var asset = await AssetAsync(request.Symbol, ct);
                var quantity = Required(request.Quantity, "quantity", "Enter the number of shares.");
                var price = Required(request.Price, "price", $"Enter the price per share in {asset.Currency}.");
                var fee = request.Fee ?? 0;
                if (!portfolio.Settings.AllowFractionalShares && quantity != decimal.Truncate(quantity))
                    throw new ValidationException("quantity", "This portfolio does not allow fractional shares (see its optimizer settings).");
                var rate = await RateAsync(asset.Currency, portfolio.BaseCurrency, date, ct);
                var execution = request.Type == PortfolioTransactionType.Buy
                    ? portfolio.Buy(asset, quantity, price, date, rate, fee, tx.Id, bookedAt)
                    : portfolio.Sell(asset, quantity, price, date, rate, fee, tx.Id, bookedAt);
                tx.AssetId = asset.Id;
                tx.Symbol = asset.Symbol;
                tx.Quantity = quantity;
                tx.Price = price;
                tx.Currency = asset.Currency;
                tx.FxRate = execution.FxRate;
                tx.FxRateDate = execution.FxRateDate;
                tx.FxSource = execution.FxSource;
                tx.Fees = execution.Fees;
                tx.CashAmount = execution.NetCashFlow;
                tx.ExecutionId = execution.Id;
                db.Executions.Add(execution);
                foreach (var entry in portfolio.CashEntries.Where(c => c.ReferenceId == execution.Id)) db.CashLedger.Add(entry);
                break;
            }
            case PortfolioTransactionType.Dividend:
            {
                var asset = await AssetAsync(request.Symbol, ct);
                var amount = Required(request.Amount, "amount", $"Enter the net dividend received in {portfolio.BaseCurrency}.");
                var entry = portfolio.ReceiveDividend(asset, amount, bookedAt, $"Dividend {asset.Symbol} (recorded){label}", tx.Id);
                tx.AssetId = asset.Id;
                tx.Symbol = asset.Symbol;
                Book(tx, entry);
                break;
            }
            case PortfolioTransactionType.Deposit:
                Book(tx, portfolio.Deposit(Required(request.Amount, "amount", "Enter the amount."), bookedAt, $"Deposit (recorded){label}", tx.Id));
                break;
            case PortfolioTransactionType.Withdrawal:
                Book(tx, portfolio.Withdraw(Required(request.Amount, "amount", "Enter the amount."), bookedAt, $"Withdrawal (recorded){label}", tx.Id));
                break;
            case PortfolioTransactionType.Fee:
                Book(tx, portfolio.ChargeFee(Required(request.Amount, "amount", "Enter the amount."), bookedAt, $"Fee (recorded){label}", tx.Id));
                break;
            default:
                throw new ValidationException("type", $"Unsupported transaction type {request.Type}.");
        }

        db.PortfolioTransactions.Add(tx);
        await SaveAsync(ct);
        await audit.RecordAsync(AuditActions.TransactionRecorded, "Portfolio", portfolioId.ToString(),
            $"{tx.Type} {tx.Symbol} {tx.Quantity} {tx.CashAmount:0.##} {portfolio.BaseCurrency}".Replace("  ", " ").Trim(), ct: ct);
        return await ToDtoAsync(tx, portfolio.CashBalance, replay: false, ct);
    }

    /// <summary>
    /// Reverses a recorded deposit, withdrawal, dividend or fee with an opposite booking dated now. Trades are corrected by
    /// recording the opposite trade, because reversing a fill would rewrite cost basis and realized P&amp;L after the fact.
    /// </summary>
    public async Task<PortfolioTransactionDto> ReverseAsync(Guid portfolioId, Guid transactionId, ReverseTransactionRequest request, string idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
            throw new ValidationException("Idempotency-Key", "An Idempotency-Key header (1-100 characters) is required.");
        var portfolio = await portfolios.LoadAsync(portfolioId, ct, tracking: true);
        var original = await db.PortfolioTransactions.SingleOrDefaultAsync(t => t.Id == transactionId && t.PortfolioId == portfolioId, ct)
                       ?? throw new NotFoundException("Transaction", transactionId);
        if (await FindByKeyAsync(portfolioId, idempotencyKey, ct) is { } previous)
        {
            if (previous.ReversesId != transactionId) throw new ConflictException("This Idempotency-Key was already used for a different transaction.");
            return await ToDtoAsync(previous, portfolio.CashBalance, replay: true, ct);
        }
        portfolio.EnsureActive();
        if (original.ReversesId is not null) throw new ConflictException("A reversal cannot itself be reversed; record a new transaction instead.");
        if (original.ReversedById is not null) throw new ConflictException("This transaction has already been reversed.");
        if (original.IsTrade)
            throw new DomainException("Trades cannot be reversed; record the opposite trade (a sell for a mistaken buy) to correct the position.");
        var entry = await db.CashLedger.AsNoTracking().SingleOrDefaultAsync(c => c.Id == original.CashEntryId, ct)
                    ?? throw new ConflictException("The ledger entry of this transaction is missing; it cannot be reversed automatically.");

        var now = clock.GetUtcNow().UtcDateTime;
        var (date, bookedAt) = await BookingTimeAsync(portfolioId, null, now, ct);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var reversal = new PortfolioTransaction
        {
            Id = Guid.NewGuid(),
            PortfolioId = portfolioId,
            Type = original.Type,
            AssetId = original.AssetId,
            Symbol = original.Symbol,
            Currency = original.Currency,
            EffectiveDate = date,
            BookedAtUtc = bookedAt,
            CreatedAtUtc = now,
            Note = note ?? $"Reversal of the {original.Type.ToString().ToLowerInvariant()} booked {original.EffectiveDate:yyyy-MM-dd}",
            IdempotencyKey = idempotencyKey,
            ReversesId = original.Id,
        };
        Book(reversal, portfolio.ReverseCashEntry(entry, bookedAt, $"Reversal: {entry.Description}", reversal.Id));
        original.ReversedById = reversal.Id;
        db.PortfolioTransactions.Add(reversal);
        await SaveAsync(ct);
        await audit.RecordAsync(AuditActions.TransactionReversed, "Portfolio", portfolioId.ToString(), $"{original.Type} {original.Id}", ct: ct);
        return await ToDtoAsync(reversal, portfolio.CashBalance, replay: false, ct);
    }

    public async Task<PagedResult<PortfolioTransactionDto>> ListAsync(Guid portfolioId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) throw new ValidationException("page", "Page must be ≥ 1.");
        if (pageSize is < 1 or > 500) throw new ValidationException("pageSize", "Page size must be between 1 and 500.");
        await portfolios.EnsureExistsAsync(portfolioId, ct);
        var q = db.PortfolioTransactions.AsNoTracking().Where(t => t.PortfolioId == portfolioId);
        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(t => t.BookedAtUtc).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var cashIds = items.Select(t => t.CashEntryId).Concat(items.Select(t => (Guid?)t.ExecutionId)).OfType<Guid>().ToList();
        var balances = await db.CashLedger.AsNoTracking().Where(c => c.PortfolioId == portfolioId && (cashIds.Contains(c.Id) || (c.ReferenceId != null && cashIds.Contains(c.ReferenceId.Value))))
            .Select(c => new { c.Id, c.ReferenceId, c.BalanceAfter, c.TimestampUtc }).ToListAsync(ct);
        var executionIds = items.Where(t => t.ExecutionId is not null).Select(t => t.ExecutionId!.Value).ToList();
        var realized = await db.Executions.AsNoTracking().Where(e => executionIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.RealizedPnl, ct);
        var dtos = items.Select(t =>
        {
            // Balance after the booking: the last ledger entry it created (a trade's fee follows its settlement).
            var after = balances.Where(b => b.Id == t.CashEntryId || (t.ExecutionId is not null && b.ReferenceId == t.ExecutionId))
                .OrderBy(b => b.TimestampUtc).Select(b => b.BalanceAfter).LastOrDefault();
            return ToDto(t, after, t.ExecutionId is { } e ? realized.GetValueOrDefault(e) : null, replay: false);
        }).ToList();
        return new PagedResult<PortfolioTransactionDto>(dtos, page, pageSize, total);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Effective date and ledger time. The date defaults to today and must lie between the latest booking's date and today;
    /// on an earlier day the booking is placed at 20:00 UTC (after the close), always after the latest existing booking.
    /// </summary>
    private async Task<(DateOnly Date, DateTime BookedAt)> BookingTimeAsync(Guid portfolioId, DateOnly? requested, DateTime now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now);
        var latestCash = await db.CashLedger.Where(c => c.PortfolioId == portfolioId).MaxAsync(c => (DateTime?)c.TimestampUtc, ct);
        var latestTrade = await db.Executions.Where(e => e.PortfolioId == portfolioId).MaxAsync(e => (DateTime?)e.ExecutedAtUtc, ct);
        var latestPriceDate = await db.Executions.Where(e => e.PortfolioId == portfolioId).MaxAsync(e => (DateOnly?)e.PriceDate, ct);
        var latest = new[] { latestCash, latestTrade }.Max();
        var date = requested ?? today;
        if (date > today) throw new ValidationException("date", "The date cannot be in the future.");
        var earliest = new[] { latest is { } l ? DateOnly.FromDateTime(l) : (DateOnly?)null, latestPriceDate }.Max();
        if (earliest is { } e && date < e)
            throw new ValidationException("date", $"The date cannot be before {e:yyyy-MM-dd}, the portfolio's latest booking: bookings stay in time order so balances remain correct.");
        var bookedAt = date == today ? now : date.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);
        if (latest is { } last && bookedAt <= last) bookedAt = last.AddMilliseconds(1);
        if (bookedAt > now) bookedAt = now;
        return (date, bookedAt);
    }

    private async Task<TradeFx> RateAsync(string assetCurrency, string baseCurrency, DateOnly date, CancellationToken ct)
    {
        if (assetCurrency == baseCurrency) return TradeFx.SameCurrency;
        var simulated = await fx.ActiveSourceIsSimulatedAsync(ct);
        try
        {
            var table = await fx.GetTableAsync([assetCurrency, baseCurrency], date, date, simulated, ct);
            var q = table.Rate(assetCurrency, baseCurrency, date);
            return new TradeFx(FxPrecision.Round(q.Rate), q.ObservationDate, q.Source);
        }
        catch (FxRateUnavailableException ex)
        {
            throw new ValidationException("date", $"No {assetCurrency}→{baseCurrency} reference rate for {date:yyyy-MM-dd}: {ex.Message} The trade cannot be booked without a dated rate.");
        }
    }

    private async Task<Asset> AssetAsync(string? symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ValidationException("symbol", "Choose the security.");
        var s = Asset.NormalizeSymbol(symbol);
        return await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Symbol == s, ct)
               ?? throw new ValidationException("symbol", $"Unknown security {s}.");
    }

    private static decimal Required(decimal? value, string field, string message) =>
        value is > 0 ? value.Value : throw new ValidationException(field, message);

    private void Book(PortfolioTransaction tx, CashLedgerEntry entry)
    {
        tx.CashAmount = entry.Amount;
        tx.CashEntryId = entry.Id;
        db.CashLedger.Add(entry);
    }

    private Task<PortfolioTransaction?> FindByKeyAsync(Guid portfolioId, string key, CancellationToken ct) =>
        db.PortfolioTransactions.AsNoTracking().SingleOrDefaultAsync(t => t.PortfolioId == portfolioId && t.IdempotencyKey == key, ct);

    private async Task SaveAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The portfolio was changed at the same time by another request. Reload and try again.");
        }
        catch (DbUpdateException ex) when (db.IsUniqueConstraintViolation(ex))
        {
            throw new ConflictException("A request with this Idempotency-Key is already being processed.");
        }
    }

    private async Task<PortfolioTransactionDto> ToDtoAsync(PortfolioTransaction t, decimal cashNow, bool replay, CancellationToken ct)
    {
        var realized = t.ExecutionId is { } e ? await db.Executions.AsNoTracking().Where(x => x.Id == e).Select(x => x.RealizedPnl).SingleOrDefaultAsync(ct) : null;
        return ToDto(t, cashNow, realized, replay);
    }

    private static PortfolioTransactionDto ToDto(PortfolioTransaction t, decimal balanceAfter, decimal? realized, bool replay) =>
        new(t.Id, t.PortfolioId, t.Type, t.Symbol, t.Quantity, t.Price, t.Currency, t.FxRate, t.FxRateDate, t.FxSource, t.Fees, t.CashAmount,
            t.EffectiveDate, t.BookedAtUtc, t.Note, t.ExecutionId, t.ReversesId, t.ReversedById, realized, balanceAfter, replay);
}
