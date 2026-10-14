import { describe, expect, it } from 'vitest'
import { ADMIN_ROLE_LABEL, identityLabel } from './identity'

describe('identityLabel', () => {
  it('labels administrators exactly as "Admin Investor"', () => {
    const id = identityLabel({ displayName: 'Administrator', email: 'admin@example.com', isAdministrator: true })
    expect(id.role).toBe('Admin Investor')
    expect(ADMIN_ROLE_LABEL).toBe('Admin Investor')
    expect(id.name).toBe('Administrator')
  })

  it('uses the display name and a student label for regular users', () => {
    const id = identityLabel({ displayName: 'Mira Schmidt', email: 'mira@uni.example', isAdministrator: false })
    expect(id).toEqual({ name: 'Mira Schmidt', role: 'Student investor', initials: 'MS' })
  })

  it('falls back to the e-mail name when no display name is set', () => {
    expect(identityLabel({ displayName: '  ', email: 'jan.weber@uni.example', isAdministrator: false }).name).toBe('jan.weber')
    expect(identityLabel({ displayName: '', email: '', isAdministrator: false }).name).toBe('Signed-in user')
  })
})
