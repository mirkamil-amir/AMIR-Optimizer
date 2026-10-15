import { expect, test } from '@playwright/test'
import { ADMIN_STATE } from './helpers'

// The agentic research journey on the e2e stack: deterministic rules (no language model), simulated prices.
// Objective → brief (stated vs assumed) → universe with evidence → real optimization + review → interpretation →
// refinement proposed then confirmed → comparison → deliberate save → reopen.
test.use({ storageState: ADMIN_STATE })

test('research assistant: from objective to a saved, reproducible run', async ({ page }) => {
  test.setTimeout(180_000)
  await page.goto('/')
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'AI research' }).click()
  await expect(page.getByRole('heading', { name: 'AI research assistant' })).toBeVisible()

  // 1–3: objective → brief.
  await page.getByLabel('Your research objective').fill('A low risk portfolio of US stocks without energy companies, at most 40% in any single stock.')
  await page.getByRole('button', { name: 'Start research' }).click()
  await expect(page).toHaveURL(/\/research\/[0-9a-f-]{36}$/, { timeout: 60_000 })
  await expect(page.getByText('Deterministic rules (no language model)')).toBeVisible()
  const brief = page.getByText('Research brief').locator('xpath=ancestor::*[@data-slot="card"][1]')
  await expect(brief.getByText('United States (S&P 500)')).toBeVisible()
  await expect(brief.getByText('You said').first()).toBeVisible()
  await expect(brief.getByText('Assumed').first()).toBeVisible()
  await expect(brief.getByText(/financially absorb/)).toBeVisible()          // loss capacity is asked, not invented

  // 4–5: universe research with evidence, sector exclusion honoured, simulated data labelled.
  await expect(page.getByText('How the shortlist was chosen:')).toBeVisible()
  await expect(page.getByText(/SIMULATED/).first()).toBeVisible()
  await expect(page.getByText(/Meets the data requirements; selected/).first()).toBeVisible()
  await expect(page.getByLabel('Screening summary')).toContainText(/\d+ excluded by you/)
  await page.getByRole('button', { name: /Show all \d+ screened stocks with reasons/ }).click()
  await expect(page.getByRole('cell', { name: /Sector Energy excluded by you/ }).first()).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Include XOM' })).toBeDisabled()
  await page.getByRole('button', { name: 'Show eligible stocks only' }).click()

  // 6–9: real optimizer + critical review + interpretation grounded in the numbers.
  await page.getByRole('button', { name: 'Run analysis' }).click()
  const hypotheses = page.getByRole('tablist', { name: 'Hypotheses' })
  await expect(hypotheses.getByRole('tab')).toHaveCount(2, { timeout: 90_000 })
  await expect(hypotheses.getByRole('tab', { name: /Equal weight \(baseline\)/ })).toBeVisible()
  await hypotheses.getByRole('tab', { name: /from your brief/ }).click()
  const panel = page.getByRole('tabpanel')
  await expect(panel.getByText('Generated from the computed values', { exact: true })).toBeVisible()
  await expect(panel.getByRole('heading', { name: 'What the optimizer found' })).toBeVisible()
  await expect(panel.getByRole('heading', { name: 'What could be misleading' })).toBeVisible()
  await expect(panel.getByRole('heading', { name: 'What remains unknown' })).toBeVisible()
  await expect(panel.getByRole('heading', { name: 'Critical review' })).toBeVisible()
  await expect(panel.getByText('In-sample estimate')).toBeVisible()

  // 10: a question is answered from computed values and changes nothing.
  const ask = page.getByLabel('Your question or change')
  await ask.fill('Can you guarantee the highest returns?')
  await page.getByRole('button', { name: 'Interpret' }).click()
  const proposal = page.getByRole('region', { name: 'Proposed change' })
  await expect(proposal.getByText(/No analysis here can guarantee returns/)).toBeVisible()
  await proposal.getByRole('button', { name: 'Dismiss' }).click()
  await expect(proposal).toBeHidden()

  // 11–13: a refinement is shown before it runs, then recalculated with explicit inputs; earlier results stay.
  await ask.fill('cap each stock at 35%')
  await page.getByRole('button', { name: 'Interpret' }).click()
  await expect(proposal.getByText(/nothing has been recalculated yet/)).toBeVisible()
  await expect(proposal.getByText(/per-stock maximum 40% → 35%/)).toBeVisible()
  await expect(hypotheses.getByRole('tab')).toHaveCount(2)
  await proposal.getByRole('button', { name: 'Apply and recalculate' }).click()
  await expect(hypotheses.getByRole('tab')).toHaveCount(3, { timeout: 90_000 })
  await expect(hypotheses.getByRole('tab', { name: /– refined/ })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('tabpanel').getByText(/max 35% per stock/)).toBeVisible()

  // 14: comparison of alternatives on the same window.
  await expect(page.getByText('Compare alternatives')).toBeVisible()
  await expect(page.getByRole('columnheader', { name: 'Weight A' })).toBeVisible()
  await expect(page.getByText(/same window/).first()).toBeVisible()

  // 15–16: saving is deliberate (confirmation dialog) and reproducible (a normal optimization run).
  await page.getByRole('tabpanel').getByRole('button', { name: 'Save as optimization run' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText(/no orders are placed/)).toBeVisible()
  await dialog.getByRole('button', { name: 'Confirm and save' }).click()
  await expect(page.getByText('Saved as an optimization run')).toBeVisible({ timeout: 60_000 })
  const activity = page.getByRole('list', { name: 'Activity log' })
  await expect(activity.getByText('Saved a research run')).toBeVisible()
  await expect(activity.getByText('Ran the portfolio optimizer').first()).toBeVisible()

  // 17: reopen – the session is listed and the saved run opens in the existing optimizer view.
  await page.getByRole('tabpanel').getByRole('link', { name: 'Open saved run' }).click()
  await expect(page).toHaveURL(/\/optimize\/[0-9a-f-]{36}$/)
  await expect(page.getByText('What your result means')).toBeVisible({ timeout: 30_000 })
  await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'AI research' }).click()
  const row = page.getByRole('row').filter({ hasText: 'saved run' }).first()
  await expect(row.getByRole('link', { name: /United States \(S&P 500\) · minimum variance/ })).toBeVisible()
})

test('research assistant asks for the market instead of guessing', async ({ page }) => {
  await page.goto('/research')
  await page.getByLabel('Your research objective').fill('Build me a diversified portfolio with medium risk.')
  await page.getByRole('button', { name: 'Start research' }).click()
  await expect(page.getByText('One answer needed before research can start')).toBeVisible({ timeout: 60_000 })
  await expect(page.getByText(/Which market/)).toBeVisible()
  await page.getByRole('combobox', { name: 'Market' }).click()
  await page.getByRole('option', { name: 'United States (S&P 500)' }).click()
  await page.getByRole('button', { name: 'Save brief and research universe' }).click()
  await expect(page.getByText('How the shortlist was chosen:')).toBeVisible({ timeout: 60_000 })
})
