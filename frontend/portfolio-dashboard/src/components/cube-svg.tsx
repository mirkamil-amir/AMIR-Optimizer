import { forwardRef, type SVGAttributes } from 'react'
import { CUBE_BALLS, cubeBalls, type CubePose } from '@/lib/cube'

/**
 * The Matrix mark as an SVG (lib/cube.ts, D-62): its 27 balls drawn for `pose`, in `color` (one colour; the far side of
 * the open ball is fainter). `lib/cube-animator.ts` redraws later frames into the same circles without React.
 */
export const CubeSvg = forwardRef<SVGSVGElement, { pose: CubePose; color: string } & SVGAttributes<SVGSVGElement>>(
  function CubeSvg({ pose, color, children, ...rest }, ref) {
    const balls = cubeBalls(pose)
    return (
      <svg ref={ref} viewBox="0 0 100 100" {...rest}>
        {children}
        {balls.slice(0, CUBE_BALLS).map((b, i) => (
          <circle key={i} data-ball="" cx={b.x} cy={b.y} r={b.r} fillOpacity={b.o} style={{ fill: color }} />
        ))}
      </svg>
    )
  },
)
