/**
 * Plain-language meaning of every ML feature (feature set `features-2026.10-v1`, `MachineLearning/Features/FeatureCatalog.cs`).
 * The exact formula comes from the server's catalogue; this adds what the number measures, how to read it and why a model
 * might use it. "Why" states a common hypothesis, not a finding of this app – the experiment's own test metrics decide
 * whether a feature helped. Every text must match the formula in the catalogue; a changed formula needs a new entry.
 */
export interface FeatureMeaning {
  /** Short human name. */
  label: string
  /** What the number measures, in everyday words. */
  meaning: string
  /** How to read high and low values. */
  reading: string
  /** Why a model might use it (hypothesis). */
  why: string
}

export const featureMeanings: Record<string, FeatureMeaning> = {
  ret_5: {
    label: 'Return over the last week',
    meaning: 'How much the price changed over the last 5 trading days.',
    reading: '+3 % = the stock rose 3 % in the past week; negative = it fell.',
    why: 'Very recent winners often give back part of their gains and recent losers recover a little (short-term reversal).',
  },
  mom_21: {
    label: 'Return over the last month',
    meaning: 'Price change over the last 21 trading days (about one month).',
    reading: 'Positive = higher than a month ago.',
    why: 'Captures one-month moves, which tend to partly reverse rather than continue.',
  },
  mom_63: {
    label: 'Return over the last 3 months',
    meaning: 'Price change over the last 63 trading days (about three months).',
    reading: 'Positive = higher than three months ago.',
    why: 'Medium-term momentum: stocks that rose over several months have tended to keep rising for a while.',
  },
  mom_126: {
    label: 'Return over the last 6 months',
    meaning: 'Price change over the last 126 trading days (about six months).',
    reading: 'Positive = higher than six months ago.',
    why: 'A longer momentum window – slower, less noisy than the 1- and 3-month returns.',
  },
  mom_12_1: {
    label: '12-month momentum (skipping the last month)',
    meaning: 'Return from 12 months ago to 1 month ago; the most recent month is left out.',
    reading: 'Positive = the stock rose over the past year, not counting the latest month.',
    why: 'The classic momentum signal. The last month is skipped because it tends to reverse and would blur the trend.',
  },
  ma_gap_50: {
    label: 'Distance to the 50-day average',
    meaning: 'How far today\'s price is above or below its average price of the last 50 trading days.',
    reading: '+5 % = 5 % above its recent average (an up-trend); negative = below it.',
    why: 'A simple trend gauge for the last ten weeks.',
  },
  ma_gap_200: {
    label: 'Distance to the 200-day average',
    meaning: 'How far today\'s price is above or below its average price of the last 200 trading days.',
    reading: 'Positive = above its long-term average (long-term up-trend); negative = below it.',
    why: 'The long-term trend; often watched as a sign of a healthy or a broken trend.',
  },
  vol_21: {
    label: 'Volatility over the last month',
    meaning: 'How strongly the price swung day to day over the last 21 trading days, expressed per year.',
    reading: '30 % = large swings; 15 % = calm. Higher = riskier.',
    why: 'Calmer stocks have often delivered better risk-adjusted returns than very volatile ones (low-volatility effect); it is also the natural input for volatility forecasts.',
  },
  vol_63: {
    label: 'Volatility over the last 3 months',
    meaning: 'Day-to-day price swings over the last 63 trading days, expressed per year.',
    reading: 'Higher = larger swings.',
    why: 'A steadier risk measure than the one-month volatility.',
  },
  downside_vol_63: {
    label: 'Downside volatility over the last 3 months',
    meaning: 'Like volatility, but only the falling days count (days with a gain count as zero).',
    reading: 'Higher = larger and more frequent losses.',
    why: 'Investors mind losses more than gains; this separates stocks that swing down from those that swing up.',
  },
  drawdown_126: {
    label: 'Drop from the 6-month high',
    meaning: 'How far the price is below its highest price of the last 126 trading days.',
    reading: '0 % = at its 6-month high; −20 % = 20 % below that high.',
    why: 'Shows whether a stock is near its highs or in a slump.',
  },
  max_ret_21: {
    label: 'Largest daily jump in the last month',
    meaning: 'The single biggest one-day gain in the last 21 trading days.',
    reading: '+12 % = on one day the stock rose 12 %.',
    why: '"Lottery-like" stocks with extreme jumps have often done worse afterwards.',
  },
  beta_126: {
    label: 'Beta to the benchmark',
    meaning: 'How much the stock tends to move when the benchmark moves, over the last 126 trading days.',
    reading: '1 = moves like the market; 1.5 = amplifies market moves; 0.5 = dampens them.',
    why: 'Separates defensive from aggressive stocks.',
  },
  corr_126: {
    label: 'Correlation with the benchmark',
    meaning: 'How closely the stock\'s daily ups and downs follow the benchmark\'s, over 126 trading days.',
    reading: '+1 = moves in lockstep; 0 = unrelated; negative = tends to move the other way.',
    why: 'Tells whether the stock mostly follows the market or its own story.',
  },
  excess_mom_63: {
    label: '3-month return versus the benchmark',
    meaning: 'The stock\'s 3-month return minus the benchmark\'s 3-month return.',
    reading: '+4 % = beat the benchmark by 4 percentage points.',
    why: 'Momentum relative to the market rather than in absolute terms.',
  },
  rel_volume_21_63: {
    label: 'Recent trading volume versus normal',
    meaning: 'Average daily volume of the last 21 days divided by that of the last 63 days.',
    reading: '1 = normal activity; 2 = twice as much trading as usual.',
    why: 'Unusual trading often comes with news or attention.',
  },
  log_dollar_volume_21: {
    label: 'Liquidity (money traded per day)',
    meaning: 'The natural logarithm of the average amount of money traded per day over the last 21 days (price × volume).',
    reading: 'Higher = more liquid; one step of 1.0 means roughly 2.7 times as much money traded.',
    why: 'Liquid stocks are usually larger and cheaper to trade; illiquid ones behave differently.',
  },
  sector_rel_mom_63: {
    label: '3-month return versus sector peers',
    meaning: 'The stock\'s 3-month return minus the median 3-month return of the stocks in the same sector of this universe (at least 3 incl. itself).',
    reading: 'Positive = did better than its sector; negative = worse.',
    why: 'Separates a stock\'s own strength from a move of its whole industry.',
  },
  market_trend_200: {
    label: 'Market trend',
    meaning: 'How far the benchmark is above or below its 200-day average – the same value for every stock on a date.',
    reading: 'Positive = rising market; negative = falling market.',
    why: 'Lets the model treat stocks differently in rising and falling markets.',
  },
  market_vol_63: {
    label: 'Market volatility',
    meaning: 'The benchmark\'s volatility over the last 63 trading days, per year – the same value for every stock on a date.',
    reading: 'Higher = more turbulent market.',
    why: 'Lets the model treat stocks differently in calm and turbulent markets.',
  },
}
