import { useState, useCallback, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { usePlaidLink } from 'react-plaid-link'
import { useAuth } from '../context/AuthContext'
import { supabase } from '../lib/supabase'
import { backendFetch } from '../lib/api'
import { isBankReady } from '../lib/mfa'
import MfaSetup from '../components/MfaSetup'

// Group connections by institution, most recent first within each group
function groupByInstitution(connections) {
  const map = {}
  for (const c of connections) {
    const key = c.institution_name || 'Bank'
    if (!map[key]) map[key] = []
    map[key].push(c)
  }
  for (const key of Object.keys(map)) {
    map[key].sort((a, b) => new Date(b.last_sync_at) - new Date(a.last_sync_at))
  }
  return map
}

export default function ConnectBank() {
  const { user } = useAuth()
  const navigate = useNavigate()
  const [linkToken, setLinkToken] = useState(null)
  const [status, setStatus] = useState('idle') // idle | opening | exchanging | connected | error
  const [errorMsg, setErrorMsg] = useState('')
  const [connections, setConnections] = useState([])
  const [loadingConns, setLoadingConns] = useState(true)
  const [expanded, setExpanded] = useState({}) // { institutionName: true/false }
  const [syncStatus, setSyncStatus] = useState('idle') // idle | syncing | done | error
  const [syncResult, setSyncResult] = useState(null)
  const [confirmDisconnect, setConfirmDisconnect] = useState(null) // connection id
  const [disconnecting, setDisconnecting] = useState(false)
  const [syncWarning, setSyncWarning] = useState(null)
  // How many days of history to pull on first connect. Must stay in sync with the server
  // whitelist (PlaidController.AllowedImportDays); the API re-validates and ignores anything else.
  const [importDays, setImportDays] = useState(30)
  // Clear-all-transactions escape hatch (dupes/stale data). Re-syncable, so a simple confirm —
  // no type-to-confirm (that's reserved for irreversible actions like account/trip deletion).
  const [clearState, setClearState] = useState('idle') // idle | confirm | working | done | error
  const [clearResult, setClearResult] = useState(null)
  // 2FA gate: connecting a bank requires a verified TOTP factor + aal2 session.
  // The server enforces this too (403 on token exchange) — this is just the UX.
  const [mfaReady, setMfaReady] = useState(null) // null = checking | false | true

  useEffect(() => {
    isBankReady().then(setMfaReady).catch(() => setMfaReady(false))
  }, [])

  const fetchConnections = () => {
    supabase
      .from('plaid_connections')
      .select('id, institution_name, last_sync_at')
      .eq('user_id', user.id)
      .then(({ data }) => {
        setConnections(data ?? [])
        setLoadingConns(false)
      })
  }

  const disconnectConnection = async (connectionId) => {
    setDisconnecting(true)
    await supabase.from('plaid_connections').delete().eq('id', connectionId)
    setConnections(prev => prev.filter(c => c.id !== connectionId))
    setConfirmDisconnect(null)
    setDisconnecting(false)
  }

  useEffect(() => {
    fetchConnections()
  }, [user.id, status])

  const handleSync = async () => {
    setSyncStatus('syncing')
    setSyncResult(null)
    setSyncWarning(null)
    try {
      const res = await backendFetch('/api/plaid/sync', { method: 'POST' })
      const data = await res.json()
      if (!res.ok) throw new Error(data.error || `Server error ${res.status}`)
      setSyncResult(data.synced)
      setSyncWarning(data.warning ?? null)
      setSyncStatus('done')
      fetchConnections()
      setTimeout(() => setSyncStatus('idle'), 4000)
    } catch (err) {
      setSyncStatus('error')
      setSyncResult(err.message === 'Failed to fetch' ? 'Backend is not running.' : err.message)
      setTimeout(() => setSyncStatus('idle'), 6000)
    }
  }

  const clearTransactions = async () => {
    setClearState('working')
    setClearResult(null)
    try {
      const res = await backendFetch('/api/account/clear-transactions', { method: 'POST' })
      const data = await res.json()
      if (!res.ok) throw new Error(data.error || `Server error ${res.status}`)
      setClearResult(data.cleared)
      setClearState('done')
      fetchConnections()
      setTimeout(() => setClearState('idle'), 5000)
    } catch (err) {
      setClearState('error')
      setClearResult(err.message === 'Failed to fetch' ? 'Backend is not running.' : err.message)
      setTimeout(() => setClearState('idle'), 6000)
    }
  }

  const getLinkToken = async () => {
    if (mfaReady !== true) return // gated — MfaSetup must complete first
    setStatus('opening')
    setErrorMsg('')
    try {
      const res = await backendFetch('/api/plaid/link-token', {
        method: 'POST',
        body: JSON.stringify({ days: importDays }),
      })
      const data = await res.json()
      if (data.error) throw new Error(data.error)
      setLinkToken(data.link_token)
    } catch (err) {
      setErrorMsg(err.message)
      setStatus('error')
    }
  }

  const onSuccess = useCallback(async (public_token, metadata) => {
    setStatus('exchanging')
    try {
      const res = await backendFetch('/api/plaid/exchange-token', {
        method: 'POST',
        body: JSON.stringify({
          public_token,
          institution_name: metadata?.institution?.name,
        }),
      })
      const data = await res.json()
      if (data.error) throw new Error(data.error)
      setStatus('connected')
    } catch (err) {
      setErrorMsg(err.message)
      setStatus('error')
    }
  }, [user.id])

  const { open, ready } = usePlaidLink({
    token: linkToken,
    onSuccess,
    onExit: () => { setLinkToken(null); setStatus('idle') },
  })

  useEffect(() => {
    if (linkToken && ready) open()
  }, [linkToken, ready, open])

  const grouped = groupByInstitution(connections)
  const institutions = Object.keys(grouped)
  const hasConnections = institutions.length > 0

  // 2FA gate — bank connection is blocked until the session is aal2 with a verified TOTP factor
  if (mfaReady !== true) {
    return (
      <div style={{ maxWidth: 480, margin: '0 auto', padding: '32px 16px' }}>
        <button
          onClick={() => navigate(-1)}
          style={{ background: 'none', border: 'none', color: '#8b5e3c', fontSize: 14, cursor: 'pointer', marginBottom: 24, padding: 0 }}
        >
          ← Back
        </button>
        <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 32 }}>
          <div style={{ textAlign: 'center', marginBottom: 24 }}>
            <div style={{
              width: 56, height: 56, background: '#f5e6c8', borderRadius: 14,
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              fontSize: 28, margin: '0 auto 12px'
            }}>
              🔐
            </div>
            <h1 style={{ fontSize: 20, fontWeight: 700, color: '#4a3728', margin: '0 0 6px' }}>
              Two-factor authentication required
            </h1>
            <p style={{ fontSize: 14, color: '#8b5e3c', margin: 0 }}>
              Because bank data is sensitive, you must secure your account with 2FA
              before connecting a bank.
            </p>
          </div>
          {mfaReady === null
            ? <p style={{ fontSize: 13, color: '#8b5e3c', textAlign: 'center' }}>Checking 2FA status...</p>
            : <MfaSetup onReady={() => setMfaReady(true)} />}
        </div>
      </div>
    )
  }

  return (
    <div style={{ maxWidth: 480, margin: '0 auto', padding: '32px 16px' }}>
      <button
        onClick={() => navigate(-1)}
        style={{ background: 'none', border: 'none', color: '#8b5e3c', fontSize: 14, cursor: 'pointer', marginBottom: 24, padding: 0 }}
      >
        ← Back
      </button>

      <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 32 }}>
        <div style={{ textAlign: 'center', marginBottom: 28 }}>
          <div style={{
            width: 56, height: 56, background: '#f5e6c8', borderRadius: 14,
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            fontSize: 28, margin: '0 auto 12px'
          }}>
            🏦
          </div>
          <h1 style={{ fontSize: 20, fontWeight: 700, color: '#4a3728', margin: '0 0 6px' }}>Connect your bank</h1>
          <p style={{ fontSize: 14, color: '#8b5e3c', margin: 0 }}>
            Auto-import transactions so you never have to manually log expenses on the road.
          </p>
        </div>

        <ul style={{ listStyle: 'none', padding: 0, margin: '0 0 28px', display: 'flex', flexDirection: 'column', gap: 10 }}>
          {[
            'Transactions appear same day (1-4 hrs after purchase)',
            'Bank-level encryption via Plaid',
            'You choose which transactions are trip expenses',
            'Works with 12,000+ banks and credit cards',
          ].map(item => (
            <li key={item} style={{ display: 'flex', gap: 10, fontSize: 14, color: '#4a3728', alignItems: 'flex-start' }}>
              <span style={{ color: '#2d5a27', fontWeight: 700, marginTop: 1 }}>✓</span>
              {item}
            </li>
          ))}
        </ul>

        {/* Connected accounts */}
        {!loadingConns && hasConnections && (
          <div style={{ marginBottom: 24 }}>
            <p style={{ fontSize: 13, fontWeight: 600, color: '#8b5e3c', marginBottom: 10 }}>Connected accounts</p>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {institutions.map(name => {
                const group = grouped[name]
                const primary = group[0]
                const extras = group.slice(1)
                const isExpanded = expanded[name]

                return (
                  <div key={name}>
                    {/* Primary (most recent) connection */}
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', background: '#f9f5ee', borderRadius: 12, padding: '12px 14px' }}>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                        <span style={{ fontSize: 20 }}>🏦</span>
                        <div>
                          <p style={{ fontSize: 13, fontWeight: 600, color: '#4a3728', margin: 0 }}>{primary.institution_name}</p>
                          <p style={{ fontSize: 11, color: '#8b5e3c', margin: 0 }}>
                            Last synced: {primary.last_sync_at ? new Date(primary.last_sync_at).toLocaleDateString() : 'Never'}
                          </p>
                        </div>
                      </div>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                        {extras.length > 0 && (
                          <button
                            onClick={() => setExpanded(prev => ({ ...prev, [name]: !prev[name] }))}
                            style={{ background: 'none', border: 'none', fontSize: 11, color: '#8b5e3c', cursor: 'pointer', padding: 0 }}
                          >
                            {isExpanded ? 'Show less' : `+${extras.length} more`}
                          </button>
                        )}
                        {confirmDisconnect === primary.id ? (
                          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', justifyContent: 'flex-end', maxWidth: 240 }}>
                            <span style={{ fontSize: 11, color: '#8a5a00' }}>Reconnecting the same bank can cause duplicates. Remove?</span>
                            <button
                              onClick={() => disconnectConnection(primary.id)}
                              disabled={disconnecting}
                              style={{ fontSize: 11, fontWeight: 600, color: 'white', background: '#b91c1c', border: 'none', borderRadius: 6, padding: '3px 8px', cursor: 'pointer' }}
                            >
                              {disconnecting ? '...' : 'Yes'}
                            </button>
                            <button
                              onClick={() => setConfirmDisconnect(null)}
                              style={{ fontSize: 11, color: '#8b5e3c', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}
                            >
                              Cancel
                            </button>
                          </div>
                        ) : (
                          <>
                            <span style={{ fontSize: 11, background: '#dcfce7', color: '#15803d', padding: '3px 10px', borderRadius: 99, fontWeight: 600 }}>
                              Connected
                            </span>
                            <button
                              onClick={() => setConfirmDisconnect(primary.id)}
                              style={{ fontSize: 11, color: '#8b5e3c', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}
                            >
                              Disconnect
                            </button>
                          </>
                        )}
                      </div>
                    </div>

                    {/* Older connections for same institution */}
                    {isExpanded && extras.map((c, i) => (
                      <div key={i} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', background: '#f3ede3', borderRadius: 12, padding: '10px 14px', marginTop: 4, marginLeft: 16 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                          <span style={{ fontSize: 16 }}>🏦</span>
                          <div>
                            <p style={{ fontSize: 12, fontWeight: 600, color: '#4a3728', margin: 0 }}>{c.institution_name}</p>
                            <p style={{ fontSize: 11, color: '#8b5e3c', margin: 0 }}>
                              Last synced: {c.last_sync_at ? new Date(c.last_sync_at).toLocaleDateString() : 'Never'}
                            </p>
                          </div>
                        </div>
                        {confirmDisconnect === c.id ? (
                          <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap', justifyContent: 'flex-end', maxWidth: 240 }}>
                            <span style={{ fontSize: 11, color: '#8a5a00' }}>Reconnecting the same bank can cause duplicates. Remove?</span>
                            <button
                              onClick={() => disconnectConnection(c.id)}
                              disabled={disconnecting}
                              style={{ fontSize: 11, fontWeight: 600, color: 'white', background: '#b91c1c', border: 'none', borderRadius: 6, padding: '3px 8px', cursor: 'pointer' }}
                            >
                              {disconnecting ? '...' : 'Yes'}
                            </button>
                            <button
                              onClick={() => setConfirmDisconnect(null)}
                              style={{ fontSize: 11, color: '#8b5e3c', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}
                            >
                              Cancel
                            </button>
                          </div>
                        ) : (
                          <button
                            onClick={() => setConfirmDisconnect(c.id)}
                            style={{ fontSize: 11, color: '#8b5e3c', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}
                          >
                            Disconnect
                          </button>
                        )}
                      </div>
                    ))}
                  </div>
                )
              })}
            </div>

            {/* Sync row */}
            <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginTop: 14 }}>
              <button
                onClick={handleSync}
                disabled={syncStatus === 'syncing'}
                style={{
                  display: 'flex', alignItems: 'center', gap: 6,
                  background: 'white', border: '1px solid #e8d5b0', borderRadius: 10,
                  padding: '8px 14px', fontSize: 13, fontWeight: 600, color: '#4a3728',
                  cursor: syncStatus === 'syncing' ? 'not-allowed' : 'pointer',
                  opacity: syncStatus === 'syncing' ? 0.6 : 1,
                }}
              >
                <span style={{ fontSize: 14 }}>↻</span>
                {syncStatus === 'syncing' ? 'Syncing...' : 'Sync now'}
              </button>
              <p style={{ fontSize: 12, color: '#8b5e3c', margin: 0 }}>
                {syncStatus === 'done' && syncResult !== null
                  ? `${syncResult} new transaction${syncResult !== 1 ? 's' : ''} imported`
                  : syncStatus === 'error'
                  ? syncResult
                  : '★ Pro: auto-syncs within hours of purchase'}
              </p>
            </div>

            {syncWarning && (
              <p style={{
                fontSize: 12, color: '#8a5a00', background: '#fff7e0',
                border: '1px solid #f0dfae', borderRadius: 8, padding: '10px 14px', marginTop: 10
              }}>
                ⚠ {syncWarning}
              </p>
            )}

            {/* Dupe heads-up + clear-all escape hatch */}
            <div style={{ marginTop: 14 }}>
              <p style={{ fontSize: 11, color: '#8b5e3c', margin: '0 0 8px' }}>
                Heads up: disconnecting and reconnecting a bank can create duplicate transactions
                (your bank re-sends its history). If that happens, clear and re-sync.
              </p>
              {clearState === 'confirm' ? (
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                  <span style={{ fontSize: 12, color: '#4a3728' }}>
                    Remove all imported transactions? They can be re-synced from your bank.
                  </span>
                  <button
                    onClick={clearTransactions}
                    style={{ fontSize: 12, fontWeight: 600, color: 'white', background: '#b91c1c', border: 'none', borderRadius: 8, padding: '6px 12px', cursor: 'pointer' }}
                  >
                    Clear all
                  </button>
                  <button
                    onClick={() => setClearState('idle')}
                    style={{ fontSize: 12, color: '#8b5e3c', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}
                  >
                    Cancel
                  </button>
                </div>
              ) : (
                <button
                  onClick={() => setClearState('confirm')}
                  disabled={clearState === 'working'}
                  style={{
                    fontSize: 12, fontWeight: 600, color: '#b91c1c', background: 'none',
                    border: '1px solid #e8bcbc', borderRadius: 8, padding: '6px 12px',
                    cursor: clearState === 'working' ? 'not-allowed' : 'pointer',
                    opacity: clearState === 'working' ? 0.6 : 1,
                  }}
                >
                  {clearState === 'working' ? 'Clearing...' : 'Clear all transactions'}
                </button>
              )}
              {clearState === 'done' && (
                <p style={{ fontSize: 12, color: '#2d5a27', margin: '8px 0 0' }}>
                  ✓ Cleared {clearResult} transaction{clearResult !== 1 ? 's' : ''}. Hit “Sync now” to re-import a clean set.
                </p>
              )}
              {clearState === 'error' && (
                <p style={{ fontSize: 12, color: '#d4622a', margin: '8px 0 0' }}>{clearResult}</p>
              )}
            </div>

            <div style={{ height: 1, background: '#e8d5b0', margin: '20px 0' }} />
          </div>
        )}

        {status === 'connected' ? (
          <div style={{ textAlign: 'center' }}>
            <p style={{ color: '#2d5a27', fontWeight: 600, fontSize: 16, marginBottom: 16 }}>Bank connected!</p>
            <p style={{ color: '#8b5e3c', fontSize: 13, marginBottom: 20 }}>
              Transactions from your active trips will appear in your review queue.
            </p>
            <button
              onClick={() => navigate('/dashboard')}
              style={{ background: '#2d5a27', color: 'white', border: 'none', borderRadius: 12, padding: '12px 24px', fontSize: 14, fontWeight: 600, cursor: 'pointer' }}
            >
              Go to dashboard
            </button>
          </div>
        ) : (
          <>
            {errorMsg && (
              <p style={{ color: '#d4622a', fontSize: 13, background: '#fff5f0', padding: '10px 14px', borderRadius: 8, marginBottom: 16 }}>
                {errorMsg}
              </p>
            )}
            {/* Import-history window — bounds the initial backfill so we only pull the
                history the user wants (less noise, less bank data at rest). */}
            <div style={{ marginBottom: 16 }}>
              <p style={{ fontSize: 13, fontWeight: 600, color: '#4a3728', margin: '0 0 8px' }}>
                Import transactions from the last…
              </p>
              <div style={{ display: 'flex', gap: 8 }}>
                {[14, 30, 60, 90].map(d => (
                  <button
                    key={d}
                    type="button"
                    onClick={() => setImportDays(d)}
                    style={{
                      flex: 1, padding: '8px 0', fontSize: 13, fontWeight: 600, cursor: 'pointer',
                      borderRadius: 10,
                      border: importDays === d ? '1px solid #2d5a27' : '1px solid #e8d5b0',
                      background: importDays === d ? '#2d5a27' : 'white',
                      color: importDays === d ? 'white' : '#8b5e3c',
                    }}
                  >
                    {d} days
                  </button>
                ))}
              </div>
              <p style={{ fontSize: 11, color: '#8b5e3c', margin: '8px 0 0' }}>
                Only this much history is pulled from your bank. You can sync more later.
              </p>
            </div>
            <button
              onClick={getLinkToken}
              disabled={status === 'opening' || status === 'exchanging'}
              style={{
                width: '100%', background: '#2d5a27', color: 'white', border: 'none',
                borderRadius: 12, padding: '14px 16px', fontSize: 14, fontWeight: 600,
                cursor: (status === 'opening' || status === 'exchanging') ? 'not-allowed' : 'pointer',
                opacity: (status === 'opening' || status === 'exchanging') ? 0.6 : 1,
              }}
            >
              {status === 'opening' && 'Opening bank connection...'}
              {status === 'exchanging' && 'Saving connection...'}
              {(status === 'idle' || status === 'error') && (hasConnections ? '+ Add another bank' : 'Connect bank account')}
            </button>
            <p style={{ textAlign: 'center', fontSize: 12, color: '#8b5e3c', marginTop: 12 }}>
              Read-only access. We never store your login credentials.
            </p>
          </>
        )}
      </div>
    </div>
  )
}
