import { useEffect, useMemo, useState } from 'react'
import './App.css'
import {
  EMPTY_VEHICLE,
  normalizeVehicle,
  type VehicleInfo,
  type VehiclePayload,
} from './types'

function getClientKey(): string {
  const params = new URLSearchParams(window.location.search)
  return params.get('clientKey')?.trim() || 'EN21'
}

function getSseBaseUrl(): string {
  const fromEnv = import.meta.env.VITE_SSE_BASE_URL as string | undefined
  if (fromEnv && fromEnv.trim() !== '') {
    return fromEnv.replace(/\/$/, '')
  }
  // Dev convenience: use Vite proxy when base URL not set
  return ''
}

type ConnStatus = 'connecting' | 'open' | 'reconnecting' | 'offline'

type StatusTone = 'neutral' | 'success' | 'pending' | 'danger'

const SSE_BACKOFF_MS = [1000, 2000, 5000, 10000, 30000] as const
const SSE_WATCHDOG_MS = 90_000
const SSE_WATCHDOG_TICK_MS = 5_000

function statusTone(value: string, kind: 'approval' | 'payment'): StatusTone {
  const v = value.trim().toLowerCase()
  if (!v || v === '-') return 'neutral'

  const successWords =
    kind === 'approval'
      ? ['已批', '通過', '核准', '批准', 'approved', 'pass', 'ok', '成功']
      : ['已付', '已繳', 'paid', 'complete', '成功', '結清']
  const dangerWords =
    kind === 'approval'
      ? ['拒', '否', '不通過', 'reject', 'fail', '取消']
      : ['未付', '欠', 'unpaid', 'reject', 'fail', '逾期']
  const pendingWords = ['待', '處理', '審核中', 'pending', 'wait', '處理中', '未批']

  if (successWords.some((w) => v.includes(w.toLowerCase()))) return 'success'
  if (dangerWords.some((w) => v.includes(w.toLowerCase()))) return 'danger'
  if (pendingWords.some((w) => v.includes(w.toLowerCase()))) return 'pending'
  return 'neutral'
}

export default function App() {
  const clientKey = useMemo(() => getClientKey(), [])
  const [vehicle, setVehicle] = useState<VehicleInfo>(EMPTY_VEHICLE)
  const [status, setStatus] = useState<ConnStatus>('connecting')
  const [lastError, setLastError] = useState<string>('')

  useEffect(() => {
    let disposed = false
    let es: EventSource | null = null
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null
    let watchdogTimer: ReturnType<typeof setInterval> | null = null
    let backoffIdx = 0
    let lastEventAt = Date.now()
    let everOpened = false

    const base = getSseBaseUrl()
    const url = `${base}/sse/${encodeURIComponent(clientKey)}`

    const clearReconnectTimer = () => {
      if (reconnectTimer !== null) {
        clearTimeout(reconnectTimer)
        reconnectTimer = null
      }
    }

    const resetBackoff = () => {
      backoffIdx = 0
    }

    const noteEvent = () => {
      lastEventAt = Date.now()
    }

    const applyVehicle = (raw: string) => {
      try {
        const data = JSON.parse(raw) as VehiclePayload
        setVehicle(normalizeVehicle(data))
        setLastError('')
      } catch (err) {
        console.error('Failed to parse vehicle payload', err)
        setLastError('無法解析車輛資料')
      }
    }

    const closeEs = () => {
      if (es) {
        try {
          es.close()
        } catch {
          /* ignore */
        }
        es = null
      }
    }

    const scheduleReconnect = () => {
      if (disposed) return
      clearReconnectTimer()
      closeEs()
      setStatus(everOpened ? 'reconnecting' : 'connecting')
      const delay = SSE_BACKOFF_MS[Math.min(backoffIdx, SSE_BACKOFF_MS.length - 1)]
      backoffIdx = Math.min(backoffIdx + 1, SSE_BACKOFF_MS.length - 1)
      reconnectTimer = setTimeout(() => {
        reconnectTimer = null
        connect()
      }, delay)
    }

    const forceReconnect = () => {
      if (disposed) return
      clearReconnectTimer()
      closeEs()
      noteEvent() // prevent watchdog thrash while reconnecting
      setStatus(everOpened ? 'reconnecting' : 'connecting')
      connect()
    }

    const connect = () => {
      if (disposed) return
      clearReconnectTimer()
      closeEs()
      noteEvent() // give this attempt a full watchdog window

      setStatus(everOpened ? 'reconnecting' : 'connecting')
      const source = new EventSource(url)
      es = source

      source.onopen = () => {
        if (disposed || es !== source) return
        everOpened = true
        resetBackoff()
        noteEvent()
        setStatus('open')
      }

      source.onerror = () => {
        if (disposed || es !== source) return
        // Take full control of reconnect (disable browser auto-retry)
        closeEs()
        scheduleReconnect()
      }

      source.addEventListener('vehicle', (ev) => {
        if (disposed || es !== source) return
        noteEvent()
        resetBackoff()
        applyVehicle((ev as MessageEvent).data)
      })

      source.addEventListener('info', (ev) => {
        if (disposed || es !== source) return
        noteEvent()
        resetBackoff()
        applyVehicle((ev as MessageEvent).data)
      })

      source.addEventListener('clear', () => {
        if (disposed || es !== source) return
        noteEvent()
        resetBackoff()
        setVehicle(EMPTY_VEHICLE)
        setLastError('')
      })

      source.addEventListener('error', (ev) => {
        if (disposed || es !== source) return
        // Named SSE "error" event from server (not connection onerror)
        const msg = (ev as MessageEvent).data
        if (typeof msg === 'string' && msg.trim() !== '') {
          noteEvent()
          resetBackoff()
          setLastError(msg)
        }
      })

      source.addEventListener('ping', () => {
        if (disposed || es !== source) return
        noteEvent()
        resetBackoff()
      })

      // default "message" events (no event name) — try as vehicle payload
      source.onmessage = (ev) => {
        if (disposed || es !== source) return
        if (ev.data) {
          noteEvent()
          resetBackoff()
          applyVehicle(ev.data)
        }
      }
    }

    const onVisibilityChange = () => {
      if (disposed) return
      if (document.visibilityState !== 'visible') return
      if (!es || es.readyState !== EventSource.OPEN) {
        forceReconnect()
      }
    }

    document.addEventListener('visibilitychange', onVisibilityChange)

    watchdogTimer = setInterval(() => {
      if (disposed) return
      if (document.visibilityState !== 'visible') return
      if (Date.now() - lastEventAt >= SSE_WATCHDOG_MS) {
        forceReconnect()
      }
    }, SSE_WATCHDOG_TICK_MS)

    connect()

    return () => {
      disposed = true
      clearReconnectTimer()
      if (watchdogTimer !== null) {
        clearInterval(watchdogTimer)
        watchdogTimer = null
      }
      document.removeEventListener('visibilitychange', onVisibilityChange)
      closeEs()
      setStatus('offline')
    }
  }, [clientKey])

  const display = (v: string) => (v && v.trim() !== '' ? v : '—')

  const infoFields: { label: string; value: string }[] = [
    { label: '入場時間', value: vehicle.entryTime },
    { label: '身份', value: vehicle.identity },
    { label: '車輛種類', value: vehicle.vehicleType },
    { label: '前往貨倉', value: vehicle.warehouseDestination },
    { label: '付款時間', value: vehicle.paymentTime },
    {
      label: '預約',
      value: vehicle.hasBooking ? '有預約' : '無預約',
    },
    ...(vehicle.hasBooking && vehicle.bookingId
      ? [{ label: 'Booking ID', value: vehicle.bookingId }]
      : []),
  ]

  const approvalTone = statusTone(vehicle.approvalStatus, 'approval')
  const paymentTone = statusTone(vehicle.paymentStatus, 'payment')

  const statusLabel: Record<ConnStatus, string> = {
    connecting: '連線中…',
    open: '已連線',
    reconnecting: '重連中…',
    offline: '離線',
  }

  return (
    <div className="page">
      <div className="panel">
        <header className="panel-header">
          <div className="logo">
            <img
              className="logo-img"
              src="/affc-logo.png"
              alt="AFFC 機場空運中心"
            />
          </div>
          <h1 className="device-title">設備 {clientKey}</h1>
          <div className={`conn-badge conn-${status}`} title={lastError || undefined}>
            {statusLabel[status]}
          </div>
        </header>

        <section className="plate-band" aria-label="車牌">
          <span className="plate-label">車牌</span>
          <span className="plate-value">{display(vehicle.lpn)}</span>
        </section>

        {lastError ? <div className="error-banner">{lastError}</div> : null}

        <div className="status-row">
          <div className="status-item">
            <span className="field-label">批核狀態</span>
            <span className={`status-chip tone-${approvalTone}`}>
              {display(vehicle.approvalStatus)}
            </span>
          </div>
          <div className="status-item">
            <span className="field-label">付款狀態</span>
            <span className={`status-chip tone-${paymentTone}`}>
              {display(vehicle.paymentStatus)}
            </span>
          </div>
        </div>

        <div className="fields-grid">
          {infoFields.map((f) => (
            <div className="field-row" key={f.label}>
              <span className="field-label">{f.label}</span>
              <span className="field-value">{display(f.value)}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}