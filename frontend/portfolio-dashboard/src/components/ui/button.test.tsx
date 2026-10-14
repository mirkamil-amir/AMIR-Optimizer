import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { Button } from './button'

describe('Button', () => {
  it('renders a native button by default', () => {
    render(<Button>Save</Button>)
    expect(screen.getByRole('button', { name: 'Save' }).tagName).toBe('BUTTON')
  })

  it('keeps link semantics when rendered as a link', () => {
    render(<Button variant="outline" render={<a href="/optimize" className="extra" />}>Optimize</Button>)
    const link = screen.getByRole('link', { name: 'Optimize' })
    expect(link).toHaveAttribute('href', '/optimize')
    expect(link).toHaveAttribute('data-slot', 'button')
    expect(link.className).toContain('extra')
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })
})
