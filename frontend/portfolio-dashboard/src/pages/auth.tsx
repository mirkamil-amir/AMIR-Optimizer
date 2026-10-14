import { useState, type ReactNode } from 'react'
import { Link, Navigate, useLocation, useNavigate } from 'react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { CheckCircle2, Clock, Loader2, LogOut, ShieldAlert, Sigma } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { LoadingBlock } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { dateTime } from '@/lib/format'

function AuthLayout({ title, description, children, footer }: { title: string; description: ReactNode; children: ReactNode; footer?: ReactNode }) {
  return (
    <div className="flex min-h-svh items-center justify-center bg-muted/30 px-4 py-10">
      <div className="w-full max-w-sm space-y-6">
        <div className="flex items-center justify-center gap-2">
          <div className="flex size-8 items-center justify-center rounded-md bg-foreground text-background">
            <Sigma className="size-4" aria-hidden />
          </div>
          <span className="text-lg font-semibold">AMIR Optimizer</span>
        </div>
        <Card>
          <CardHeader>
            <CardTitle>{title}</CardTitle>
            <CardDescription>{description}</CardDescription>
          </CardHeader>
          <CardContent>{children}</CardContent>
          {footer && <CardFooter className="justify-center text-sm text-muted-foreground">{footer}</CardFooter>}
        </Card>
        <p className="text-center text-xs text-muted-foreground">Research and simulated trading only. No real orders are placed. Not investment advice.</p>
      </div>
    </div>
  )
}

function FieldRow({ id, label, error, children }: { id: string; label: string; error?: string; children: ReactNode }) {
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id}>{label}</Label>
      {children}
      {error && <p className="text-xs text-loss" role="alert">{error}</p>}
    </div>
  )
}

const loginSchema = z.object({
  email: z.email('Enter a valid e-mail address'),
  password: z.string().min(1, 'Enter your password'),
})

export function LoginPage() {
  const { user, login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [error, setError] = useState<string | null>(null)
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<z.infer<typeof loginSchema>>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: '', password: '' },
  })
  const from = (location.state as { from?: string } | null)?.from ?? '/'

  if (user) return <Navigate to={user.status === 'Approved' ? from : '/account'} replace />

  const onSubmit = handleSubmit(async (v) => {
    setError(null)
    try {
      const me = await login(v.email, v.password)
      navigate(me.status === 'Approved' ? from : '/account', { replace: true })
    } catch (e) {
      setError(errorMessage(e))
    }
  })

  return (
    <AuthLayout
      title="Sign in"
      description="Use the account an administrator approved for you."
      footer={<span>No account yet? <Link to="/register" className="font-medium text-foreground underline underline-offset-4">Apply for access</Link></span>}
    >
      <form onSubmit={onSubmit} className="space-y-4" noValidate>
        {error && (
          <Alert variant="destructive">
            <ShieldAlert />
            <AlertTitle>Sign-in failed</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <FieldRow id="email" label="E-mail" error={errors.email?.message}>
          <Input id="email" type="email" autoComplete="username" autoFocus {...register('email')} />
        </FieldRow>
        <FieldRow id="password" label="Password" error={errors.password?.message}>
          <Input id="password" type="password" autoComplete="current-password" {...register('password')} />
        </FieldRow>
        <Button type="submit" className="w-full" disabled={isSubmitting}>
          {isSubmitting && <Loader2 className="animate-spin" />}
          Sign in
        </Button>
      </form>
    </AuthLayout>
  )
}

const registerSchema = z.object({
  displayName: z.string().trim().min(1, 'Enter your name').max(100),
  email: z.email('Enter a valid e-mail address'),
  password: z.string()
    .min(10, 'At least 10 characters')
    .regex(/[a-z]/, 'Include a lower-case letter')
    .regex(/[A-Z]/, 'Include an upper-case letter')
    .regex(/[0-9]/, 'Include a digit'),
  confirm: z.string(),
}).refine((v) => v.password === v.confirm, { path: ['confirm'], message: 'Passwords do not match' })

export function RegisterPage() {
  const { user, register: apply } = useAuth()
  const [done, setDone] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<z.infer<typeof registerSchema>>({
    resolver: zodResolver(registerSchema),
    defaultValues: { displayName: '', email: '', password: '', confirm: '' },
  })

  if (user) return <Navigate to="/" replace />

  const onSubmit = handleSubmit(async (v) => {
    setError(null)
    try {
      const result = await apply({ email: v.email, password: v.password, displayName: v.displayName })
      setDone(result.message)
    } catch (e) {
      setError(errorMessage(e))
    }
  })

  if (done) {
    return (
      <AuthLayout title="Application received" description="An administrator reviews new accounts before they can be used.">
        <div className="space-y-4">
          <Alert>
            <CheckCircle2 />
            <AlertTitle>Pending approval</AlertTitle>
            <AlertDescription>{done}</AlertDescription>
          </Alert>
          <Button className="w-full" render={<Link to="/login" />}>Go to sign-in</Button>
        </div>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout
      title="Apply for access"
      description="New accounts are reviewed by an administrator. You cannot choose a role; access is granted on approval."
      footer={<span>Already approved? <Link to="/login" className="font-medium text-foreground underline underline-offset-4">Sign in</Link></span>}
    >
      <form onSubmit={onSubmit} className="space-y-4" noValidate>
        {error && (
          <Alert variant="destructive">
            <ShieldAlert />
            <AlertTitle>Registration failed</AlertTitle>
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <FieldRow id="displayName" label="Name" error={errors.displayName?.message}>
          <Input id="displayName" autoComplete="name" {...register('displayName')} />
        </FieldRow>
        <FieldRow id="email" label="E-mail" error={errors.email?.message}>
          <Input id="email" type="email" autoComplete="email" {...register('email')} />
        </FieldRow>
        <FieldRow id="password" label="Password" error={errors.password?.message}>
          <Input id="password" type="password" autoComplete="new-password" {...register('password')} aria-describedby="password-rules" />
          <p id="password-rules" className="text-xs text-muted-foreground">At least 10 characters with upper- and lower-case letters and a digit.</p>
        </FieldRow>
        <FieldRow id="confirm" label="Confirm password" error={errors.confirm?.message}>
          <Input id="confirm" type="password" autoComplete="new-password" {...register('confirm')} />
        </FieldRow>
        <Button type="submit" className="w-full" disabled={isSubmitting}>
          {isSubmitting && <Loader2 className="animate-spin" />}
          Submit application
        </Button>
      </form>
    </AuthLayout>
  )
}

const statusText: Record<string, { title: string; body: string; icon: ReactNode }> = {
  Pending: { title: 'Waiting for approval', body: 'An administrator has not reviewed your application yet. Check back later.', icon: <Clock /> },
  Rejected: { title: 'Application declined', body: 'An administrator declined this application. Contact the administrator if you think this is a mistake.', icon: <ShieldAlert /> },
  Suspended: { title: 'Account suspended', body: 'This account is currently suspended. Contact the administrator to have it reactivated.', icon: <ShieldAlert /> },
}

/** Shown to signed-in accounts that are not (or no longer) approved. */
export function AccountStatusPage() {
  const { user, isLoading, logout, refresh } = useAuth()
  if (isLoading) return <LoadingBlock height="h-svh" />
  if (!user) return <Navigate to="/login" replace />
  if (user.status === 'Approved') return <Navigate to="/" replace />
  const s = statusText[user.status]
  return (
    <AuthLayout title={s.title} description={`Signed in as ${user.email}`}>
      <div className="space-y-4">
        <Alert variant={user.status === 'Pending' ? 'default' : 'destructive'}>
          {s.icon}
          <AlertTitle>Status: {user.status}</AlertTitle>
          <AlertDescription>{s.body}</AlertDescription>
        </Alert>
        <p className="text-xs text-muted-foreground">Applied {dateTime(user.createdAtUtc)}</p>
        <div className="flex gap-2">
          <Button variant="outline" className="flex-1" onClick={refresh}>Check again</Button>
          <Button variant="outline" className="flex-1" onClick={() => void logout()}>
            <LogOut /> Sign out
          </Button>
        </div>
      </div>
    </AuthLayout>
  )
}
