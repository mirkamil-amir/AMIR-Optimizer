import { createContext, useContext, useEffect, useMemo, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, api, onUnauthorized } from './api'
import type { CurrentUser } from './types'

const V1 = '/api/v1'
const ME = ['auth', 'me'] as const

interface AuthContextValue {
  user: CurrentUser | null
  isLoading: boolean
  isApproved: boolean
  isAdministrator: boolean
  login: (email: string, password: string) => Promise<CurrentUser>
  logout: () => Promise<void>
  register: (body: { email: string; password: string; displayName: string }) => Promise<{ message: string }>
  refresh: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

/**
 * Session state from GET /auth/me (cookie session). Authorization is enforced by the server on every request; the
 * client only uses the profile to choose what to show.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient()
  const me = useQuery({
    queryKey: ME,
    queryFn: async () => {
      try {
        return await api.get<CurrentUser>(`${V1}/auth/me`)
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) return null
        throw e
      }
    },
    staleTime: 60_000,
    retry: false,
  })

  // Any 401 from a data endpoint means the session ended (expired, signed out elsewhere, server restart).
  useEffect(() => onUnauthorized(() => qc.setQueryData(ME, null)), [qc])

  const loginMutation = useMutation({
    mutationFn: (body: { email: string; password: string }) => api.post<CurrentUser>(`${V1}/auth/login`, body),
    onSuccess: (user) => {
      qc.clear() // never show cached data of a previous session
      qc.setQueryData(ME, user)
    },
  })

  const value = useMemo<AuthContextValue>(() => {
    const user = me.data ?? null
    return {
      user,
      isLoading: me.isLoading,
      isApproved: user?.status === 'Approved',
      isAdministrator: user?.status === 'Approved' && user.isAdministrator,
      login: (email, password) => loginMutation.mutateAsync({ email, password }),
      // A full page load after signing out discards everything held in memory (query cache, the stock selection, the
      // sign-in redirect target), so nothing of this session carries over to the next person using the browser. The
      // cache is deliberately left alone until then: clearing it would let the route guard redirect first.
      logout: async () => {
        try {
          await api.post<void>(`${V1}/auth/logout`)
        } finally {
          // Same-URL navigations count as reloads and keep history.state (the router's redirect target): clear it first.
          window.history.replaceState(null, '', '/login')
          window.location.replace('/login')
        }
      },
      register: (body) => api.post<{ message: string }>(`${V1}/auth/register`, body),
      refresh: () => void qc.invalidateQueries({ queryKey: ME }),
    }
  }, [me.data, me.isLoading, loginMutation, qc])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider')
  return ctx
}
