import { expect, test as setup } from '@playwright/test'
import { ADMIN_STATE, admin, requireAdminCredentials, signIn } from './helpers'

// One setup step, so nothing else runs when the safety check fails:
// 1. The specs create accounts and runs. They only run against a disposable test stack (ASPNETCORE_ENVIRONMENT=E2E,
//    started by scripts/e2e.sh), so they can never pollute a development or production database.
// 2. The administrator signs in once and the session is shared, keeping the run under the sign-in rate limit.
setup('verify the test environment and sign in as administrator', async ({ page, request }) => {
  if (process.env.E2E_ALLOW_NON_TEST_TARGET !== '1') {
    const response = await request.get('/api/v1/system/test-environment')
    const body = response.ok() ? await response.json() as { isTestEnvironment?: boolean } : {}
    expect(body.isTestEnvironment, 'Refusing to run: the target is not an E2E test stack. Use scripts/e2e.sh.').toBe(true)
  }
  requireAdminCredentials()
  await signIn(page, admin.email, admin.password)
  await expect(page).toHaveURL(/\/$/)
  await page.context().storageState({ path: ADMIN_STATE })
})
