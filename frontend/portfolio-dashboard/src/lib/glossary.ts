/** Glossary of finance and ML terms used in the app, written for students new to quantitative finance. */
export interface GlossaryEntry {
  id: string
  term: string
  category: 'Returns & risk' | 'Portfolio construction' | 'Machine learning' | 'Evaluation & bias' | 'Market data'
  definition: string
  example?: string
  related?: string[]
}

export const glossary: GlossaryEntry[] = [
  // Returns & risk
  { id: 'return', term: 'Return', category: 'Returns & risk', definition: 'The percentage change in value over a period: price today divided by price before, minus 1. The app uses daily returns from adjusted closing prices.', example: 'A stock rising from 100 to 103 has a 3% return.' },
  { id: 'adjusted-close', term: 'Adjusted close', category: 'Market data', definition: 'A closing price corrected for stock splits and dividends, so that returns computed from it reflect what an investor actually earned (total return). Trades and valuation use the raw (unadjusted) close.' },
  { id: 'annualization', term: 'Annualization (× 252)', category: 'Returns & risk', definition: 'Daily statistics are converted to yearly ones assuming 252 trading days: mean return × 252, volatility × √252.' },
  { id: 'volatility', term: 'Volatility', category: 'Returns & risk', definition: 'The standard deviation of returns – how much returns typically fluctuate around their average. The most common measure of risk.', example: '20% annual volatility means yearly returns typically land within about ±20 percentage points of the average (roughly two years in three).', related: ['variance'] },
  { id: 'variance', term: 'Variance', category: 'Returns & risk', definition: 'Volatility squared. Optimizers work with variance because it is a smooth quadratic function of the weights (wᵀΣw).' },
  { id: 'covariance', term: 'Covariance matrix (Σ)', category: 'Returns & risk', definition: 'A table describing how every pair of stocks moves together. Its diagonal contains each stock\'s variance. The portfolio variance is wᵀΣw.', related: ['correlation', 'shrinkage'] },
  { id: 'correlation', term: 'Correlation', category: 'Returns & risk', definition: 'A number between −1 and +1 showing how closely two stocks move together. Below +1, combining them reduces risk (diversification).', example: 'Two banks typically have a high correlation (0.7+); a bank and a utility a lower one.' },
  { id: 'diversification', term: 'Diversification', category: 'Returns & risk', definition: 'Spreading capital over assets that do not move in lockstep, so their individual swings partly cancel. It reduces risk without necessarily reducing expected return.' },
  { id: 'expected-return', term: 'Expected return (μ)', category: 'Returns & risk', definition: 'The average return an asset is assumed to earn. In this app it is estimated as the historical mean return – a very noisy estimate, not a forecast.' },
  { id: 'risk-free-rate', term: 'Risk-free rate', category: 'Returns & risk', definition: 'The return of a (nearly) riskless investment such as short-term government bills. Sharpe ratios measure return above it.' },
  { id: 'sharpe-ratio', term: 'Sharpe ratio', category: 'Returns & risk', definition: 'Return above the risk-free rate divided by volatility – reward per unit of risk. Higher is better; it depends on the period and on noisy estimates.', example: 'Return 10%, risk-free 3%, volatility 14% → Sharpe = (10 − 3) / 14 = 0.5.' },
  { id: 'sortino-ratio', term: 'Sortino ratio', category: 'Returns & risk', definition: 'Like the Sharpe ratio but only counts downside volatility (returns below the risk-free rate) as risk.' },
  { id: 'max-drawdown', term: 'Maximum drawdown', category: 'Returns & risk', definition: 'The largest fall from a previous peak to a later low over the period – the worst loss an investor who bought at the top would have experienced.' },
  { id: 'var', term: 'Value at Risk (VaR) / Expected shortfall', category: 'Returns & risk', definition: 'VaR 95%: the daily loss exceeded on only 5% of days. Expected shortfall (CVaR): the average loss on those worst 5% of days.' },
  { id: 'beta', term: 'Beta', category: 'Returns & risk', definition: 'How strongly a stock or portfolio moves with the benchmark. Beta 1.2 means it tends to move 1.2% when the benchmark moves 1%.' },
  { id: 'tracking-error', term: 'Tracking error', category: 'Returns & risk', definition: 'Volatility of the difference between a portfolio\'s and the benchmark\'s returns – how far the portfolio strays from the benchmark.' },
  { id: 'risk-contribution', term: 'Risk contribution', category: 'Returns & risk', definition: 'The share of the portfolio\'s variance caused by one position, accounting for its correlations. A stock with 10% of the capital can cause 25% of the risk.' },

  // Portfolio construction
  { id: 'weight', term: 'Portfolio weight', category: 'Portfolio construction', definition: 'The fraction of total portfolio value invested in one position. Weights plus cash add up to 100%.' },
  { id: 'efficient-frontier', term: 'Efficient frontier', category: 'Portfolio construction', definition: 'The set of portfolios with the lowest risk for each level of expected return. Portfolios below it are “inefficient”: another mix offers more return for the same risk.', related: ['minimum-variance', 'tangency'] },
  { id: 'minimum-variance', term: 'Minimum-variance portfolio', category: 'Portfolio construction', definition: 'The least volatile combination of the available assets – the left-most point of the efficient frontier.' },
  { id: 'tangency', term: 'Tangency (maximum-Sharpe) portfolio', category: 'Portfolio construction', definition: 'The frontier portfolio with the highest Sharpe ratio; graphically where a line from the risk-free rate touches the frontier.' },
  { id: 'long-only', term: 'Long-only / short selling / leverage', category: 'Portfolio construction', definition: 'Long-only: all weights are ≥ 0. Short selling (negative weights, betting on falling prices) and leverage (investing more than 100%) are not supported in this app.' },
  { id: 'constraint', term: 'Constraint', category: 'Portfolio construction', definition: 'A rule the optimizer must respect, e.g. maximum 20% per stock or 40% per sector. A “binding” constraint is one that changed the result.' },
  { id: 'infeasible', term: 'Infeasible', category: 'Portfolio construction', definition: 'No portfolio can satisfy all constraints at once (e.g. five stocks with a 15% cap cannot be fully invested). The app explains which constraint is the problem.' },
  { id: 'quadratic-program', term: 'Quadratic program (QP)', category: 'Portfolio construction', definition: 'An optimization problem with a quadratic objective (like variance) and linear constraints. Convex QPs have one global optimum that solvers find reliably.' },
  { id: 'shrinkage', term: 'Shrinkage (Ledoit–Wolf)', category: 'Portfolio construction', definition: 'Pulling the noisy sample covariance matrix toward a simple structured target. It stabilizes estimates when there are many stocks and few observations.' },
  { id: 'rebalancing', term: 'Rebalancing', category: 'Portfolio construction', definition: 'Trading back to target weights after prices have moved them, e.g. monthly.' },
  { id: 'turnover', term: 'Turnover', category: 'Portfolio construction', definition: 'How much of the portfolio is traded: half the sum of absolute weight changes per rebalance. High turnover means high trading costs.' },
  { id: 'transaction-costs', term: 'Transaction costs (bps)', category: 'Portfolio construction', definition: 'Fees and spreads paid on each trade, expressed in basis points (1 bp = 0.01%) of the traded value.' },
  { id: 'benchmark', term: 'Benchmark', category: 'Portfolio construction', definition: 'A reference to judge a strategy against, e.g. the S&P 500 ETF (SPY) or the DAX index. The app picks a benchmark in the same currency as your stocks.' },

  // Machine learning
  { id: 'feature', term: 'Feature', category: 'Machine learning', definition: 'An input the model sees for a stock on a date, computed only from information available that day (e.g. 12-month momentum, 63-day volatility).' },
  { id: 'target', term: 'Target (label)', category: 'Machine learning', definition: 'What the model tries to predict, measured after the decision date, e.g. the return over the next 21 trading days.' },
  { id: 'horizon', term: 'Prediction horizon', category: 'Machine learning', definition: 'How far ahead the target looks: 5, 21 (≈ 1 month) or 63 (≈ 3 months) trading days.' },
  { id: 'training-validation-test', term: 'Training / validation / test period', category: 'Machine learning', definition: 'Training data fits the model, validation data chooses its settings, and the untouched test data – used once – estimates how it performs on new data. In this app they follow each other in time.' },
  { id: 'overfitting', term: 'Overfitting', category: 'Machine learning', definition: 'A model learning noise specific to its training data. Signs: excellent training metrics, poor test metrics.' },
  { id: 'regularization', term: 'Regularization', category: 'Machine learning', definition: 'Penalizing model complexity (e.g. large coefficients in ridge regression) to reduce overfitting.' },
  { id: 'hyperparameter', term: 'Hyperparameter', category: 'Machine learning', definition: 'A setting chosen before training (penalty strength, number of trees). Chosen on the validation period only.' },
  { id: 'information-coefficient', term: 'Information coefficient (IC)', category: 'Machine learning', definition: 'The rank correlation between predictions and realized outcomes across stocks on a date, averaged over dates. 0 = no skill; even 0.05 is considered meaningful in practice if it is stable.' },
  { id: 't-statistic', term: 't-statistic', category: 'Machine learning', definition: 'Average divided by its standard error. Roughly, |t| above 2 means the result is unlikely to be pure chance; below 2 it may well be luck.' },
  { id: 'r-squared', term: 'Out-of-sample R²', category: 'Machine learning', definition: 'How much better predictions are than simply predicting the training average. Negative means worse than that naive forecast – common for stock returns.' },
  { id: 'auc', term: 'ROC-AUC', category: 'Machine learning', definition: 'The probability that a randomly chosen outperformer gets a higher score than a randomly chosen underperformer. 0.5 = coin flip, 1.0 = perfect.' },
  { id: 'calibration', term: 'Calibration', category: 'Machine learning', definition: 'Whether predicted probabilities match observed frequencies: of all predictions of 60%, about 60% should come true.' },
  { id: 'permutation-importance', term: 'Permutation importance', category: 'Machine learning', definition: 'How much worse the model gets when one feature\'s values are shuffled. Shows reliance on a feature, not that the feature causes returns.' },

  // Evaluation & bias
  { id: 'in-sample', term: 'In-sample vs out-of-sample', category: 'Evaluation & bias', definition: 'In-sample results use the same data that chose the weights or trained the model and are too optimistic. Out-of-sample results use data the method never saw.' },
  { id: 'backtest', term: 'Backtest', category: 'Evaluation & bias', definition: 'A simulation of how a strategy would have performed historically. Always hypothetical.' },
  { id: 'walk-forward', term: 'Walk-forward', category: 'Evaluation & bias', definition: 'A backtest in which each decision uses only data available at that time, moving forward step by step.' },
  { id: 'look-ahead-bias', term: 'Look-ahead bias', category: 'Evaluation & bias', definition: 'Accidentally using information that was not available at decision time, which makes results look better than achievable.' },
  { id: 'data-leakage', term: 'Data leakage', category: 'Evaluation & bias', definition: 'Information from the test period leaking into training (e.g. overlapping outcome windows, scaling with all data). Prevented here by chronological splits, purging and training-only preprocessing.' },
  { id: 'purging', term: 'Purging and embargo', category: 'Evaluation & bias', definition: 'Removing training samples whose outcome window overlaps the next period, plus an optional gap, so that no future information leaks backwards.' },
  { id: 'survivorship-bias', term: 'Survivorship bias', category: 'Evaluation & bias', definition: 'Testing only on companies that exist today, ignoring those that failed or were removed from the index. It makes historical results look better. All backtests here use today\'s index members.' },
  { id: 'multiple-testing', term: 'Multiple testing', category: 'Evaluation & bias', definition: 'Trying many strategies or settings and reporting the best: the winner is partly luck. Be sceptical of the best of many experiments.' },
  { id: 'equal-weight', term: 'Equal weight (1/N)', category: 'Evaluation & bias', definition: 'Investing the same amount in every stock. A simple baseline that sophisticated strategies often fail to beat out of sample.' },

  // Market data
  { id: 'delayed-quote', term: 'Delayed quote', category: 'Market data', definition: 'A recent trade price reported by the data provider with a delay (often 15+ minutes). The app never shows real-time prices.' },
  { id: 'last-close', term: 'Last close', category: 'Market data', definition: 'The final price of the most recent completed trading session.' },
  { id: 'stale', term: 'Stale price', category: 'Market data', definition: 'A price older than a few trading days, e.g. because the stock stopped trading or the provider has no recent data.' },
  { id: 'cached', term: 'Cached price', category: 'Market data', definition: 'A stored price retrieved earlier; the app reuses prices for a few minutes instead of asking the provider again and shows when it was retrieved.' },
  { id: 'index-constituents', term: 'Index constituents', category: 'Market data', definition: 'The companies that make up an index such as the DAX 40. Memberships change over time; the DAX 30 list in the app is a historical composition (until September 2021).' },
  { id: 'currency', term: 'Trading currency', category: 'Market data', definition: 'The currency a stock is quoted in (USD, EUR, GBP, JPY). London prices are quoted in pence by providers and converted to pounds. Analyses need stocks in one currency because no FX conversion is applied.' },
]

export const glossaryCategories = ['Returns & risk', 'Portfolio construction', 'Machine learning', 'Evaluation & bias', 'Market data'] as const
