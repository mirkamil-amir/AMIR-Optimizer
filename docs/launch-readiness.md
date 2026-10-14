# Launch readiness, positioning and questions for qualified review

Product promise: **Understand your portfolio. Challenge your assumptions. Test your strategy.** The platform explains
optimization, diversification, risk and machine learning on transparent calculations; it does not pick winning stocks
and does not promise returns.

## Differentiators – hypotheses, not proven advantages

| Hypothesis | What exists to test it |
|---|---|
| Explainable optimization | strategy guide (12 parts per strategy), result interpretations generated from the run's numbers |
| Visible data freshness and quality | quote kind, as-of and retrieval time, cached/stale/SIMULATED labels on every price |
| Robustness before trust | Portfolio Reality Check (concentration, risk shares, stress correlation, sensitivity, costs, in/out-of-sample) |
| Honest ML | chronological purged validation, baselines, t-statistics; real-data experiments so far show no edge |
| Watchlist + news with attribution | provider-tagged news, publisher/time/link, event and price-movement comparison without causal claims |

None of these has been validated with users. No testimonials, user numbers, performance records or certifications
exist and none may be implied. Pricing tiers, if ever shown, must be labelled as proposals; no payment code exists.

Wording rules (enforced in reviews): never "beat the market", "guaranteed", "risk-free", "AI predicts winners",
"best portfolio", "live prices", "trending" without a measurement.

## Assessment (2026-10-15)

| Stage | Assessment | Evidence / blockers |
|---|---|---|
| Local development and demonstration | **Ready**, with simulated or personal-use data | builds from a clean clone (after the fix in this pass), 200+ backend and 50+ frontend tests pass, e2e journeys on a disposable stack, documented setup |
| Controlled pilot with real users | **Not yet** | needs hosting with TLS, backups rehearsed, monitoring, CI; data sources (Yahoo unofficial) are for personal, non-commercial use; privacy notice and terms; legal view on advice regulation; accessibility review |
| Public commercial launch | **No** | licensed prices, constituents and news with redistribution rights; regulatory opinion; DPA/GDPR documentation; security review/pen test; support and incident processes |

## Questions for qualified legal and regulatory review (not answered here)

1. **Investment advice / regulated services (Germany: KWG §1 Abs. 1a Nr. 1a, WpIG; EU: MiFID II; UK: FCA PERG 8).**
   Do personalized outputs – optimized weights for a user's own selection, trade proposals in the paper portfolio, the
   reality-check findings – constitute a "personal recommendation"? Does labelling as educational and the absence of
   real orders suffice, or are changes (e.g. no per-user weights, generic examples only) required? A disclaimer alone is
   not assumed to resolve this.
2. **Financial analysis / investment recommendations (MAR Art. 20, Delegated Reg. 2016/958).** Could news relevance
   labels or event comparisons be read as recommendations?
3. **Market data licensing.** Yahoo Finance endpoints are unofficial and restricted to personal, non-commercial use;
   Alpha Vantage free-tier terms; exchange fees for display of delayed data in a commercial product.
4. **News and images.** Display of third-party headlines, summaries and thumbnails (copyright, ancillary press
   publishers' right §§ 87f ff. UrhG / Art. 15 DSM Directive); currently headlines + links + provider teasers, images off.
5. **Index constituents.** Wikipedia CC BY-SA attribution is recorded; index names are trademarks of the index providers.
6. **Privacy (GDPR/BDSG).** Accounts, audit logs with IP addresses, watchlists; retention periods, legal basis,
   processor agreements, data-subject rights (export/deletion), cookie notice (only strictly necessary cookies today).
7. **Consumer protection / marketing.** Claims on any landing page; no performance advertising.
