import type { CurrentUser } from './types'

/** Exact role label shown for administrators wherever their identity appears. */
export const ADMIN_ROLE_LABEL = 'Admin Investor'
export const USER_ROLE_LABEL = 'Student investor'

export interface IdentityLabel {
  name: string
  role: string
  initials: string
}

/**
 * Header identity from the server-side profile (GET /auth/me): the display name, or the e-mail's local part when no
 * display name is set; the role label follows the server's administrator flag (never client-side guesses).
 */
export function identityLabel(user: Pick<CurrentUser, 'displayName' | 'email' | 'isAdministrator'>): IdentityLabel {
  const name = user.displayName?.trim() || user.email?.split('@')[0]?.trim() || 'Signed-in user'
  const words = name.split(/[\s._-]+/).filter(Boolean)
  const initials = (words.length >= 2 ? words[0][0] + words[words.length - 1][0] : name.slice(0, 2)).toUpperCase()
  return { name, role: user.isAdministrator ? ADMIN_ROLE_LABEL : USER_ROLE_LABEL, initials }
}
