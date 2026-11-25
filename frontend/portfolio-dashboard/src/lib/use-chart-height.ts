import { useEffect, useState } from 'react'

/** Chart height that follows the viewport (42 %, 240–520 px): fills the space beside the accounting card on large screens, stays usable on small ones. */
export function chartHeightFor(viewportHeight: number): number {
  return Math.round(Math.min(520, Math.max(240, viewportHeight * 0.42)))
}

export function useChartHeight(): number {
  const compute = () => (typeof window === 'undefined' ? 360 : chartHeightFor(window.innerHeight))
  const [height, setHeight] = useState(compute)
  useEffect(() => {
    const onResize = () => setHeight(compute())
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [])
  return height
}
