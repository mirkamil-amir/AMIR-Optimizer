import { render, screen } from '@testing-library/react'
import type { ReactNode } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { CurrentUser } from '@/lib/types'
import { RequireAdministrator, RequireApprovedUser } from './route-guards'

const auth = vi.hoisted(() => ({ current: { user: null as CurrentUser | null, isLoading: false, isAdministrator: false } }))
vi.mock('@/lib/auth', () => ({ useAuth: () => auth.current }))
vi.mock('@/lib/portfolio-context', () => ({ PortfolioProvider: ({ children }: { children: ReactNode }) => <>{children}</> }))

const user = (status: CurrentUser['status'], isAdministrator = false): CurrentUser => ({
  id: '1', email: 'u@example.com', displayName: 'U', status, roles: isAdministrator ? ['Administrator'] : ['User'], isAdministrator, createdAtUtc: '2026-01-01T00:00:00Z',
})

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/login" element={<p>login page</p>} />
        <Route path="/account" element={<p>account status page</p>} />
        <Route element={<RequireApprovedUser />}>
          <Route path="/" element={<p>dashboard</p>} />
          <Route element={<RequireAdministrator />}>
            <Route path="/admin/users" element={<p>user administration</p>} />
          </Route>
        </Route>
      </Routes>
    </MemoryRouter>,
  )
}

describe('route guards', () => {
  beforeEach(() => {
    auth.current = { user: null, isLoading: false, isAdministrator: false }
  })

  it('sends anonymous visitors to the sign-in page', () => {
    renderAt('/')
    expect(screen.getByText('login page')).toBeInTheDocument()
  })

  it.each(['Pending', 'Rejected', 'Suspended'] as const)('sends %s accounts to their status page', (status) => {
    auth.current = { user: user(status), isLoading: false, isAdministrator: false }
    renderAt('/')
    expect(screen.getByText('account status page')).toBeInTheDocument()
    expect(screen.queryByText('dashboard')).not.toBeInTheDocument()
  })

  it('renders the app for approved users', () => {
    auth.current = { user: user('Approved'), isLoading: false, isAdministrator: false }
    renderAt('/')
    expect(screen.getByText('dashboard')).toBeInTheDocument()
  })

  it('blocks non-administrators from admin pages', () => {
    auth.current = { user: user('Approved'), isLoading: false, isAdministrator: false }
    renderAt('/admin/users')
    expect(screen.getByText('Administrators only')).toBeInTheDocument()
    expect(screen.queryByText('user administration')).not.toBeInTheDocument()
  })

  it('lets administrators open admin pages', () => {
    auth.current = { user: user('Approved', true), isLoading: false, isAdministrator: true }
    renderAt('/admin/users')
    expect(screen.getByText('user administration')).toBeInTheDocument()
  })
})
