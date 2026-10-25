import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { MatrixMark } from '@/components/brand'
import { skins, useTheme, type ThemeMode } from '@/lib/theme'
import { cn } from '@/lib/utils'

const modes: { value: ThemeMode; label: string }[] = [
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
  { value: 'system', label: 'System' },
]

/** Header button: cycles light → dark → system. */
export function ThemeQuickToggle() {
  const { mode, setMode } = useTheme()
  const current = modes.find((m) => m.value === mode)!
  const next = modes[(modes.indexOf(current) + 1) % modes.length]
  return (
    <Button variant="ghost" size="sm" aria-label={`Theme: ${current.label}. Switch to ${next.label}`} title={`Theme: ${current.label}`} onClick={() => setMode(next.value)}>
      {current.label}
    </Button>
  )
}

/** Settings card: light/dark/system and the colour skin. Saved in this browser only. */
export function AppearanceCard() {
  const { mode, skin, setMode, setSkin } = useTheme()
  return (
    <Card id="appearance">
      <CardHeader>
        <CardTitle className="text-sm">Appearance</CardTitle>
        <CardDescription className="text-xs">Theme and colour skin for this browser. Gains, losses and chart colours keep their meaning in every skin.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div role="radiogroup" aria-label="Theme mode" className="flex flex-wrap gap-2">
          {modes.map((m) => (
            <Button key={m.value} role="radio" aria-checked={mode === m.value} variant={mode === m.value ? 'default' : 'outline'} size="sm" onClick={() => setMode(m.value)}>
              {m.label}
            </Button>
          ))}
        </div>
        <div role="radiogroup" aria-label="Colour skin" className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-5">
          {skins.map((s) => (
            <button key={s.value} type="button" role="radio" aria-checked={skin === s.value} aria-label={s.label} onClick={() => setSkin(s.value)}
              className={cn('flex items-center gap-2.5 rounded-lg border px-3 py-2 text-left hover:bg-muted/60', skin === s.value && 'border-primary ring-2 ring-ring/40')}>
              <span className="relative size-7 shrink-0 overflow-hidden rounded-md border" style={{ background: s.swatch }} aria-hidden>
                <span className="absolute right-0 bottom-0 left-0 h-1.5" style={{ background: s.accent }} />
              </span>
              <span className="min-w-0 flex-1">
                <span className="block text-sm font-medium">{s.label}</span>
                <span className="block truncate text-xs text-muted-foreground">{s.description}</span>
              </span>
            </button>
          ))}
        </div>
        <div className="flex items-center gap-3 rounded-lg border p-3">
          <MatrixMark className="size-10" />
          <div className="text-sm">
            <Button size="sm">Primary action</Button>
            <span className="ml-3 text-muted-foreground">Preview of the current skin</span>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}
