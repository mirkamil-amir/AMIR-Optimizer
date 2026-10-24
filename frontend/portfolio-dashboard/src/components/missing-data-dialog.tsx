import { useRef, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Download, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { errorMessage } from '@/lib/api'
import { date } from '@/lib/format'
import { MissingDataList } from '@/components/missing-data-list'
import { checkData, downloadMissingData, getSyncJob, keys } from '@/lib/queries'
import { nextStep, shouldAsk } from '@/lib/data-check'
import type { DataCheck, DataCheckRequest, SyncJob } from '@/lib/types'

type Phase = 'review' | 'downloading' | 'incomplete'
interface State { phase: Phase; request: DataCheckRequest; check: DataCheck; proceed: () => unknown; problems: string[] }

const POLL_MS = 2000
const MAX_WAIT_MS = 10 * 60 * 1000

/**
 * Checks the stored prices and exchange rates an analysis needs before it starts. When something is missing and can be
 * downloaded, a dialog asks: download and continue, continue without, or cancel. Nothing is filled in: what the data
 * source does not have stays missing and the analysis explains it.
 */
export function useMissingDataGuard(): { guard: (request: DataCheckRequest, proceed: () => unknown) => Promise<void>; checking: boolean; dialog: ReactNode } {
  const qc = useQueryClient()
  const [state, setState] = useState<State | null>(null)
  const [checking, setChecking] = useState(false)
  const cancelled = useRef(false)

  const guard = async (request: DataCheckRequest, proceed: () => unknown) => {
    setChecking(true)
    let check: DataCheck
    try {
      check = await checkData(request)
    } catch (e) {
      // The check is advice only: the analysis still validates and reports missing data itself.
      toast.warning('Could not check the stored data', { description: errorMessage(e) })
      setChecking(false)
      await proceed()
      return
    }
    setChecking(false)
    if (!shouldAsk(check)) {
      await proceed()
      return
    }
    cancelled.current = false
    setState({ phase: 'review', request, check, proceed, problems: [] })
  }

  const download = async () => {
    if (!state) return
    setState({ ...state, phase: 'downloading' })
    try {
      const started = await downloadMissingData(state.request)
      const begin = Date.now()
      let jobs: SyncJob[] = []
      let check = state.check
      while (Date.now() - begin < MAX_WAIT_MS) {
        if (cancelled.current) return
        await new Promise((resolve) => setTimeout(resolve, POLL_MS))
        jobs = await Promise.all(started.syncJobIds.map(getSyncJob))
        const jobsDone = jobs.every((j) => j.status !== 'Queued' && j.status !== 'Running')
        check = await checkData(state.request)
        setState((s) => (s ? { ...s, check } : s))
        const step = nextStep(check, jobsDone)
        if (step === 'wait') continue
        qc.invalidateQueries({ queryKey: keys.status })
        qc.invalidateQueries({ queryKey: ['stocks'] })
        if (step === 'proceed') {
          setState(null)
          toast.success('Missing data downloaded')
          await state.proceed()
          return
        }
        break
      }
      const problems = jobs.filter((j) => j.status === 'Failed' || j.assetsFailed > 0).map((j) => j.message ?? 'A price download failed.')
      if (Date.now() - begin >= MAX_WAIT_MS) problems.push('Downloads are still running after 10 minutes; they continue in the background.')
      setState((s) => (s ? { ...s, check, phase: 'incomplete', problems } : s))
    } catch (e) {
      setState((s) => (s ? { ...s, phase: 'incomplete', problems: [errorMessage(e)] } : s))
    }
  }

  const close = () => {
    cancelled.current = true
    setState(null)
  }
  const continueWithout = async () => {
    const proceed = state?.proceed
    close()
    await proceed?.()
  }

  const dialog = state && (
    <Dialog open onOpenChange={(open) => { if (!open) close() }}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{state.phase === 'incomplete' ? 'Some data is still missing' : 'Data missing for this analysis'}</DialogTitle>
          <DialogDescription>
            {state.phase === 'downloading'
              ? 'Downloading… the analysis starts as soon as the data is stored. You can close this window; downloads continue.'
              : state.phase === 'incomplete'
                ? 'The data source could not supply everything. Continue without it (affected stocks are left out or use a shorter period, as the results explain) or cancel.'
                : `Needed: ${date(state.check.from)} – ${date(state.check.to)}, in ${state.check.currency}. Download the missing prices and exchange rates now?`}
          </DialogDescription>
        </DialogHeader>
        <MissingDataList check={state.check} />
        {state.problems.length > 0 && (
          <ul className="space-y-1 text-xs text-destructive">{state.problems.map((p, i) => <li key={i}>{p}</li>)}</ul>
        )}
        <DialogFooter>
          <Button variant="ghost" onClick={close}>Cancel</Button>
          {state.phase !== 'downloading' && <Button variant="outline" onClick={continueWithout}>Continue without</Button>}
          {state.phase === 'review' && (
            <Button onClick={download} disabled={!state.check.downloadable && !state.check.downloading}>
              <Download className="size-4" /> Download and continue
            </Button>
          )}
          {state.phase === 'downloading' && <Button disabled><Loader2 className="size-4 animate-spin" /> Downloading…</Button>}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
  return { guard, checking, dialog }
}
