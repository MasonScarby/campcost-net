import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'
import { supabase } from '../lib/supabase'
import { getPlan, setPlan, fetchPlan, FREE_TRIP_LIMIT } from '../lib/plan'

import { backendFetch } from '../lib/api'
import MfaSetup from '../components/MfaSetup'

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

export default function Account() {
  const { user, signOut } = useAuth()
  const [connections, setConnections] = useState([])
  const [trips, setTrips] = useState([])
  const [loading, setLoading] = useState(true)
  const [expanded, setExpanded] = useState({})
  const [syncStatus, setSyncStatus] = useState('idle') // idle | syncing | done | error
  const [syncResult, setSyncResult] = useState(null)
  const [plan, setPlanState] = useState(getPlan)
  const [unassigned, setUnassigned] = useState([])
  const [assignError, setAssignError] = useState('')

  // Server is the source of truth for the plan; localStorage is just a display cache
  useEffect(() => {
    fetchPlan().then(setPlanState)
  }, [])

  const fetchUnassigned = async () => {
    try {
      const res = await backendFetch('/api/transactions/unassigned')
      if (res.ok) setUnassigned(await res.json())
    } catch { /* backend offline — leave list empty */ }
  }

  const assignTransaction = async (txId, tripId) => {
    if (!tripId) return
    setAssignError('')
    const res = await backendFetch(`/api/transactions/${txId}/assign`, {
      method: 'PATCH',
      body: JSON.stringify({ trip_id: tripId }),
    })
    if (res.ok) {
      setUnassigned(prev => prev.filter(t => t.id !== txId))
    } else {
      // 402 = free-plan trip already holds 50 assigned transactions
      const data = await res.json().catch(() => ({}))
      setAssignError(data.error || `Could not assign (server error ${res.status})`)
    }
  }

  const [planBusy, setPlanBusy] = useState(false)
  const [planError, setPlanError] = useState('')

  // DEV-ONLY plan toggle. Until Stripe lands (FIXES.md #9) there's no real upgrade path,
  // so hit the dev-only POST /api/account/plan, which upserts the `subscriptions` row
  // (the SERVER source of truth PlanService reads) as a privileged role. The browser
  // never writes `subscriptions` — that endpoint 404s outside Development, so this can't
  // be used to self-upgrade in production. Writing localStorage alone no longer works:
  // every page runs fetchPlan() on mount and would overwrite the cache back to the
  // server's value (that's why the old localStorage-only toggle "stopped switching over").
  const togglePlan = async () => {
    if (planBusy) return
    setPlanBusy(true)
    setPlanError('')
    const next = plan === 'free' ? 'pro' : 'free'
    try {
      const res = await backendFetch('/api/account/plan', {
        method: 'POST',
        body: JSON.stringify({ plan: next }),
      })
      if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        setPlanError(`Couldn't change plan: ${data.error || `server error ${res.status}`}`)
        return
      }
      // Server persisted it — set the plan optimistically (no second round-trip).
      setPlan(next)
      setPlanState(next)
    } catch (err) {
      setPlanError(err.message === 'Failed to fetch' ? 'Backend is not running.' : err.message)
    } finally {
      setPlanBusy(false)
    }
  }

  const fetchConnections = async () => {
    const { data } = await supabase
      .from('plaid_connections')
      .select('institution_name, last_sync_at')
      .eq('user_id', user.id)
    setConnections(data ?? [])
  }

  useEffect(() => {
    const fetchData = async () => {
      const [{ data: conns }, { data: tripsData }] = await Promise.all([
        supabase.from('plaid_connections').select('institution_name, last_sync_at').eq('user_id', user.id),
        supabase.from('trips').select('id, name, status').eq('user_id', user.id),
      ])
      setConnections(conns ?? [])
      setTrips(tripsData ?? [])
      setLoading(false)
    }
    fetchData()
    fetchUnassigned()
  }, [user.id])

  const handleSync = async () => {
    setSyncStatus('syncing')
    setSyncResult(null)
    try {
      const res = await backendFetch('/api/plaid/sync', { method: 'POST' })
      const data = await res.json()
      if (!res.ok) throw new Error(data.error || `Server error ${res.status}`)
      setSyncResult(data.synced)
      setSyncStatus('done')
      fetchConnections()
      fetchUnassigned()
      setTimeout(() => setSyncStatus('idle'), 4000)
    } catch (err) {
      if (err.message === 'Failed to fetch') {
        setSyncResult('Backend is not running. Start it with: cd backend && node server.js')
      } else {
        setSyncResult(err.message)
      }
      setSyncStatus('error')
      setTimeout(() => setSyncStatus('idle'), 6000)
    }
  }

  const activeTrips = trips.filter(t => t.status === 'active' || t.status === 'planning').length
  const grouped = groupByInstitution(connections)
  const institutions = Object.keys(grouped)

  return (
    <div style={{ maxWidth: 560, margin: '0 auto', padding: '32px 16px' }}>
      <h1 style={{ fontSize: 24, fontWeight: 700, color: '#4a3728', marginBottom: 24 }}>Account</h1>

      {/* Profile */}
      <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20, marginBottom: 12 }}>
        <h2 style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', marginBottom: 8 }}>Profile</h2>
        <p style={{ fontSize: 14, color: '#8b5e3c' }}>{user?.email}</p>
      </div>

      {/* Plan */}
      <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20, marginBottom: 12 }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 6 }}>
          <h2 style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', margin: 0 }}>Plan</h2>
          <span style={{
            fontSize: 11, padding: '3px 10px', borderRadius: 99, fontWeight: 600,
            background: plan === 'pro' ? '#dcfce7' : '#f5e6c8',
            color: plan === 'pro' ? '#15803d' : '#8b5e3c',
          }}>
            {plan === 'pro' ? 'Pro' : 'Free'}
          </span>
        </div>
        {planError && (
          <p style={{ fontSize: 12, color: '#d4622a', background: '#fff5f0', borderRadius: 8, padding: '8px 12px', margin: '0 0 10px' }}>
            {planError}
          </p>
        )}
        {plan === 'free' ? (
          <>
            <p style={{ fontSize: 13, color: '#8b5e3c', marginBottom: 14 }}>
              {loading ? '...' : `${activeTrips} / ${FREE_TRIP_LIMIT} trip${FREE_TRIP_LIMIT !== 1 ? 's' : ''}`} · manual sync only
            </p>
            <button onClick={togglePlan} disabled={planBusy} style={{
              background: '#2d5a27', color: 'white', border: 'none',
              borderRadius: 12, padding: '10px 18px', fontSize: 13, fontWeight: 600,
              cursor: planBusy ? 'not-allowed' : 'pointer', opacity: planBusy ? 0.6 : 1
            }}>
              {planBusy ? 'Upgrading…' : 'Upgrade to Pro — $6/mo'}
            </button>
          </>
        ) : (
          <>
            <p style={{ fontSize: 13, color: '#8b5e3c', marginBottom: 14 }}>
              Unlimited trips · auto-sync · PDF export · cost stats
            </p>
            <button onClick={togglePlan} disabled={planBusy} style={{
              background: 'none', border: '1px solid #e8d5b0', color: '#8b5e3c',
              borderRadius: 12, padding: '10px 18px', fontSize: 13, fontWeight: 600,
              cursor: planBusy ? 'not-allowed' : 'pointer', opacity: planBusy ? 0.6 : 1
            }}>
              {planBusy ? 'Switching…' : 'Switch to Free (dev)'}
            </button>
          </>
        )}
      </div>

      {/* Security — 2FA (required before connecting a bank) */}
      <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20, marginBottom: 12 }}>
        <h2 style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', marginBottom: 12 }}>Security</h2>
        <MfaSetup />
      </div>

      {/* Connected banks */}
      <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20, marginBottom: 12 }}>
        <h2 style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', marginBottom: 12 }}>Connected banks</h2>

        {loading ? (
          <p style={{ fontSize: 13, color: '#8b5e3c' }}>Loading...</p>
        ) : institutions.length === 0 ? (
          <>
            <p style={{ fontSize: 13, color: '#8b5e3c', marginBottom: 12 }}>No bank connected yet.</p>
            <Link to="/connect-bank" style={{ fontSize: 13, color: '#2d5a27', fontWeight: 600 }}>
              Connect bank account →
            </Link>
          </>
        ) : (
          <>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {institutions.map(name => {
                const group = grouped[name]
                const primary = group[0]
                const extras = group.slice(1)
                const isExpanded = expanded[name]

                return (
                  <div key={name}>
                    {/* Primary (most recent) connection */}
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
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
                        <span style={{ fontSize: 11, background: '#dcfce7', color: '#15803d', padding: '3px 10px', borderRadius: 99, fontWeight: 600 }}>
                          Connected
                        </span>
                      </div>
                    </div>

                    {/* Older connections for same institution */}
                    {isExpanded && extras.map((c, i) => (
                      <div key={i} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', background: '#f9f5ee', borderRadius: 10, padding: '8px 12px', marginTop: 4, marginLeft: 16 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                          <span style={{ fontSize: 16 }}>🏦</span>
                          <div>
                            <p style={{ fontSize: 12, fontWeight: 600, color: '#4a3728', margin: 0 }}>{c.institution_name}</p>
                            <p style={{ fontSize: 11, color: '#8b5e3c', margin: 0 }}>
                              Last synced: {c.last_sync_at ? new Date(c.last_sync_at).toLocaleDateString() : 'Never'}
                            </p>
                          </div>
                        </div>
                        <span style={{ fontSize: 11, color: '#8b5e3c' }}>Older</span>
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

            <Link to="/connect-bank" style={{ fontSize: 13, color: '#2d5a27', fontWeight: 600, display: 'block', marginTop: 14 }}>
              + Add another bank
            </Link>
          </>
        )}
      </div>

      {/* Unassigned transactions — synced from the bank but not yet claimed by a trip */}
      {unassigned.length > 0 && (
        <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20, marginBottom: 12 }}>
          <h2 style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', marginBottom: 4 }}>Unassigned transactions</h2>
          <p style={{ fontSize: 12, color: '#8b5e3c', marginBottom: 12 }}>
            {unassigned.length} transaction{unassigned.length !== 1 ? 's' : ''} not tied to a trip. Assign them so they count toward a trip's spend.
          </p>
          {assignError && (
            <p style={{ fontSize: 12, color: '#8a5a00', background: '#fff7e0', border: '1px solid #f0dfae', borderRadius: 8, padding: '10px 14px', marginBottom: 12 }}>
              ⚠ {assignError}
            </p>
          )}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {unassigned.map(t => (
              <div key={t.id} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8, background: '#f9f5ee', borderRadius: 10, padding: '8px 12px' }}>
                <div style={{ minWidth: 0 }}>
                  <p style={{ fontSize: 13, fontWeight: 600, color: '#4a3728', margin: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {t.merchant_name || t.name || 'Unknown'} · ${Number(t.amount).toFixed(2)}
                  </p>
                  <p style={{ fontSize: 11, color: '#8b5e3c', margin: 0 }}>{t.transaction_date}</p>
                </div>
                <select
                  defaultValue=""
                  onChange={e => assignTransaction(t.id, e.target.value)}
                  style={{ border: '1px solid #e8d5b0', borderRadius: 8, padding: '6px 8px', fontSize: 12, background: 'white', color: '#4a3728' }}
                >
                  <option value="" disabled>Assign to trip…</option>
                  {trips.map(tr => <option key={tr.id} value={tr.id}>{tr.name || tr.id.slice(0, 8)}</option>)}
                </select>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Sign out */}
      <button
        onClick={signOut}
        style={{ background: 'none', border: '1px solid #fca5a5', borderRadius: 12, padding: '10px 18px', fontSize: 13, color: '#dc2626', cursor: 'pointer', width: '100%', fontWeight: 600 }}
      >
        Sign out
      </button>
    </div>
  )
}
