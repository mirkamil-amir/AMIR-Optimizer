using Microsoft.EntityFrameworkCore;
using PortfolioOptimizer.Application.Abstractions;
using PortfolioOptimizer.Application.Common;
using PortfolioOptimizer.Application.Dtos;
using PortfolioOptimizer.Domain;

namespace PortfolioOptimizer.Application.Portfolios;

/// <summary>
/// Ledger › Transactions (D-66): one list of everything that added to or took from the portfolio, newest first – every
/// buy and sell (each <see cref="TradeExecution"/>, whether booked by hand, from a confirmed proposal or by Matrix Pilot)
/// and every cash flow from outside or income (deposits, withdrawals, dividends, standalone fees). A trade's own
/// settlement and commission are part of its row, never separate rows; orders are the intermediate step of a proposal
/// (their fills are the executions) and a rejected order changed nothing, so neither is listed. Read only.
/// </summary>
public sealed class PortfolioActivityService(IApplicationDbContext db, PortfolioService portfolios)
{
    private static readonly CashEntryType[] External =
        [CashEntryType.Deposit, CashEntryType.Withdrawal, CashEntryType.Dividend, CashEntryType.Fee];

    public async Task<PagedResult<PortfolioActivityDto>> ListAsync(Guid portfolioId, int page, int pageSize, CancellationToken ct)
    {
        if (page < 1) throw new ValidationException("page", "Page must be ≥ 1.");
        if (pageSize is < 1 or > 500) throw new ValidationException("pageSize", "Page size must be between 1 and 500.");
        var portfolio = await portfolios.LoadAsync(portfolioId, ct); // owner-scoped: another user's portfolio → 404

        var executionIds = db.Executions.Where(e => e.PortfolioId == portfolioId).Select(e => e.Id);
        var trades = db.Executions.AsNoTracking().Where(e => e.PortfolioId == portfolioId)
            .Select(e => new Row { Id = e.Id, AtUtc = e.ExecutedAtUtc, IsTrade = true });
        // A trade's commission is a Fee entry that refers to its execution: it belongs to the trade's row.
        var cash = db.CashLedger.AsNoTracking()
            .Where(c => c.PortfolioId == portfolioId && External.Contains(c.Type)
                        && !(c.Type == CashEntryType.Fee && c.ReferenceId != null && executionIds.Contains(c.ReferenceId.Value)))
            .Select(c => new Row { Id = c.Id, AtUtc = c.TimestampUtc, IsTrade = false });

        var total = await trades.CountAsync(ct) + await cash.CountAsync(ct);
        var rows = await trades.Concat(cash).OrderByDescending(r => r.AtUtc).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var tradeIds = rows.Where(r => r.IsTrade).Select(r => r.Id).ToList();
        var cashIds = rows.Where(r => !r.IsTrade).Select(r => r.Id).ToList();
        var executions = await db.Executions.AsNoTracking().Where(e => tradeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var entries = await db.CashLedger.AsNoTracking().Where(c => cashIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        // Hand-booked records behind trades and cash entries (the journal of "Record transaction").
        var journal = await db.PortfolioTransactions.AsNoTracking()
            .Where(t => t.PortfolioId == portfolioId
                        && ((t.ExecutionId != null && tradeIds.Contains(t.ExecutionId.Value)) || (t.CashEntryId != null && cashIds.Contains(t.CashEntryId.Value))))
            .ToListAsync(ct);
        var byExecution = journal.Where(t => t.ExecutionId is not null).ToDictionary(t => t.ExecutionId!.Value);
        var byCash = journal.Where(t => t.CashEntryId is not null).ToDictionary(t => t.CashEntryId!.Value);
        // Proposal behind each executed order, and which proposals Matrix Pilot booked.
        var orderIds = executions.Values.Select(e => e.OrderId).Distinct().ToList();
        var proposalOf = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.ProposalId, ct);
        var proposalIds = proposalOf.Values.Distinct().ToList();
        var pilotProposals = (await db.MatrixPilotCycles.AsNoTracking()
            .Where(c => c.ProposalId != null && proposalIds.Contains(c.ProposalId.Value)).Select(c => c.ProposalId!.Value).ToListAsync(ct)).ToHashSet();

        var items = rows.Select(r =>
        {
            if (r.IsTrade)
            {
                var e = executions[r.Id];
                var hand = byExecution.GetValueOrDefault(e.Id);
                Guid? proposal = hand is null && proposalOf.TryGetValue(e.OrderId, out var p) ? p : null;
                var source = hand is not null ? ActivitySource.BookedByHand
                    : proposal is { } pid && pilotProposals.Contains(pid) ? ActivitySource.MatrixPilot : ActivitySource.TradeProposal;
                return new PortfolioActivityDto(e.Id, e.Side == OrderSide.Buy ? "Buy" : "Sell", e.ExecutedAtUtc, e.PriceDate, e.Symbol,
                    e.Quantity, e.Price, e.Currency, e.FxRate, e.NetCashFlow, e.Fees, e.RealizedPnl, source, proposal,
                    hand?.Id, CanReverse: false, Reversed: false, Reversal: false, hand?.Note);
            }
            var c = entries[r.Id];
            var booked = byCash.GetValueOrDefault(c.Id);
            var origin = booked is not null ? ActivitySource.BookedByHand
                : c.Type == CashEntryType.Deposit && c.Description.StartsWith("Initial investment", StringComparison.Ordinal) ? ActivitySource.StartingCash
                : ActivitySource.System;
            // A reversal is booked as an opposite entry of the same type (a deposit of −500): label it as what it is.
            var kind = booked?.ReversesId is not null ? "Reversal" : c.Type.ToString();
            return new PortfolioActivityDto(c.Id, kind, c.TimestampUtc, booked?.EffectiveDate ?? DateOnly.FromDateTime(c.TimestampUtc),
                booked?.Symbol, null, null, portfolio.BaseCurrency, 1, c.Amount, 0, null, origin, null, booked?.Id,
                CanReverse: booked is { ReversesId: null, ReversedById: null } && portfolio.ArchivedAtUtc is null,
                Reversed: booked?.ReversedById is not null, Reversal: booked?.ReversesId is not null, booked?.Note ?? (booked is null ? c.Description : null));
        }).ToList();
        return new PagedResult<PortfolioActivityDto>(items, page, pageSize, total);
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public DateTime AtUtc { get; init; }
        public bool IsTrade { get; init; }
    }
}
