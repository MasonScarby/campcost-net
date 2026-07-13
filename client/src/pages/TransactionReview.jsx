import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import { supabase } from '../lib/supabase'
import { useAuth } from '../context/AuthContext'
import { backendFetch } from '../lib/api'

const CATEGORY_LABELS = {
  fuel: 'Fuel', campground: 'Campground', food_groceries: 'Food & Groceries',
  propane_utilities: 'Propane & Utilities', dump_station: 'Dump Station',
  activities: 'Activities', gear: 'Gear', repairs: 'Repairs', misc: 'Misc',
}

export default function TransactionReview() {
  const { id } = useParams()
  const { user } = useAuth()
  const [unreviewed, setUnreviewed] = useState([])
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [current, setCurrent] = useState(0)
  const [editCategory, setEditCategory] = useState(null)

  useEffect(() => {
    // Review queue is the Plaid `transactions` (source of truth) that were auto-assigned
    // to this trip but not yet user-confirmed (reviewed=false). Browser reads them directly
    // (read-only RLS); confirm/remove writes go through the assign API. No client-side date
    // filtering is needed — the backend already scoped these to the trip by trip_id.
    const fetchUnreviewed = async () => {
      // Explicit columns, NOT select('*'): migration 004 grants column-level SELECT only,
      // and PostgREST expands * to ungranted columns → permission denied → empty queue.
      // deleted_at filter mirrors the backend's soft-delete query filter.
      const { data: txns } = await supabase
        .from('transactions')
        .select('id, trip_id, amount, transaction_date, merchant_name, name, category, pending, reviewed, deleted_at')
        .eq('trip_id', id).eq('reviewed', false).is('deleted_at', null)
        .order('transaction_date', { ascending: false })

      setUnreviewed(txns ?? [])
      if (txns?.[0]) setEditCategory(txns[0].category)
      setLoading(false)
    }
    fetchUnreviewed()

    // Real-time: prepend newly auto-assigned transactions as they arrive
    const channel = supabase
      .channel(`transactions:trip:${id}`)
      .on(
        'postgres_changes',
        { event: 'INSERT', schema: 'public', table: 'transactions', filter: `trip_id=eq.${id}` },
        (payload) => {
          if (payload.new.reviewed === false) {
            setUnreviewed(prev => [payload.new, ...prev])
          }
        }
      )
      .subscribe()

    return () => { supabase.removeChannel(channel) }
  }, [id, user.id])

  const advance = () => {
    const next = current + 1
    setCurrent(next)
    setEditCategory(unreviewed[next]?.category ?? 'misc')
  }

  // Confirm = keep on this trip and mark reviewed (assign to the same trip_id).
  // Sends the (possibly user-corrected) category — the API validates it against the
  // expense_category enum and applies it with the same reviewed=true write.
  const confirmExpense = async () => {
    setSaving(true)
    try {
      const res = await backendFetch(`/api/transactions/${unreviewed[current].id}/assign`, {
        method: 'PATCH',
        body: JSON.stringify({ trip_id: id, category: editCategory }),
      })
      if (!res.ok) { console.error('confirm failed:', res.status); return }
      advance()
    } finally {
      setSaving(false)
    }
  }

  // "Not a trip expense" = unassign the transaction (trip_id null). The immutable Plaid
  // mirror is never edited or deleted; it just leaves this trip. Dedup still blocks re-import.
  const skipExpense = async () => {
    setSaving(true)
    try {
      const res = await backendFetch(`/api/transactions/${unreviewed[current].id}/assign`, {
        method: 'PATCH',
        body: JSON.stringify({ trip_id: null }),
      })
      if (!res.ok) { console.error('remove failed:', res.status); return }
      advance()
    } finally {
      setSaving(false)
    }
  }

  if (loading) return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '60vh' }}>
      <div style={{ width: 32, height: 32, border: '4px solid #2d5a27', borderTopColor: 'transparent', borderRadius: '50%', animation: 'spin 0.8s linear infinite' }} />
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )

  const done = current >= unreviewed.length

  return (
    <div style={{ maxWidth: 540, margin: '0 auto', padding: '32px 16px' }}>

      {/* Header */}
      <div style={{ marginBottom: 24 }}>
        <Link to={`/trips/${id}`} style={{ fontSize: 14, color: '#8b5e3c', textDecoration: 'none', display: 'block', marginBottom: 10 }}>
          ← Back to trip
        </Link>
        <h1 style={{ fontSize: 26, fontWeight: 700, color: '#4a3728', margin: 0 }}>Review Transactions</h1>
        {!done && (
          <p style={{ fontSize: 15, color: '#8b5e3c', marginTop: 6 }}>
            {unreviewed.length - current} remaining — is this a trip expense?
          </p>
        )}
      </div>

      {done ? (
        <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 40, textAlign: 'center' }}>
          <div style={{ fontSize: 48, marginBottom: 12 }}>✓</div>
          <h2 style={{ fontSize: 20, fontWeight: 600, color: '#4a3728', marginBottom: 8 }}>All caught up</h2>
          <p style={{ fontSize: 15, color: '#8b5e3c', marginBottom: 24 }}>No more transactions to review.</p>
          <Link to={`/trips/${id}`} style={{
            background: '#2d5a27', color: 'white', padding: '12px 24px',
            borderRadius: 12, fontSize: 15, fontWeight: 600, textDecoration: 'none', display: 'inline-block'
          }}>
            Back to trip
          </Link>
        </div>
      ) : (
        <>
          {/* Progress bar */}
          <div style={{ height: 6, background: '#e8d5b0', borderRadius: 99, marginBottom: 24, overflow: 'hidden' }}>
            <div style={{ height: '100%', width: `${(current / unreviewed.length) * 100}%`, background: '#2d5a27', borderRadius: 99, transition: 'width 0.3s' }} />
          </div>

          {/* Transaction card */}
          <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 24, marginBottom: 16 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 20 }}>
              <div>
                <p style={{ fontSize: 20, fontWeight: 700, color: '#4a3728', margin: 0 }}>
                  {unreviewed[current].merchant_name || unreviewed[current].name || 'Unknown merchant'}
                </p>
                <p style={{ fontSize: 14, color: '#8b5e3c', marginTop: 4 }}>{unreviewed[current].transaction_date}</p>
              </div>
              <span style={{ fontSize: 28, fontWeight: 700, color: '#4a3728' }}>
                ${Number(unreviewed[current].amount).toFixed(2)}
              </span>
            </div>

            {/* Category is auto-derived from the Plaid feed but user-correctable — it's OUR
                derived field, not immutable Plaid data. The write still goes through the
                API (assign PATCH), never directly to the transactions table. */}
            <div>
              <label style={{ fontSize: 13, fontWeight: 600, color: '#8b5e3c', display: 'block', marginBottom: 8 }}>Category</label>
              <select
                value={editCategory || 'misc'}
                onChange={e => setEditCategory(e.target.value)}
                style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 12, padding: '12px 14px', fontSize: 15, background: 'white', color: '#4a3728', outline: 'none' }}
              >
                {Object.entries(CATEGORY_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </div>
          </div>

          {/* Actions */}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <button onClick={skipExpense} disabled={saving} style={{
              background: 'white', border: '1px solid #e8d5b0', color: '#8b5e3c',
              borderRadius: 12, padding: '14px', fontSize: 15, fontWeight: 600, cursor: 'pointer', opacity: saving ? 0.6 : 1
            }}>
              Not a trip expense
            </button>
            <button onClick={confirmExpense} disabled={saving} style={{
              background: '#2d5a27', color: 'white', border: 'none',
              borderRadius: 12, padding: '14px', fontSize: 15, fontWeight: 600, cursor: 'pointer', opacity: saving ? 0.6 : 1
            }}>
              {saving ? 'Saving...' : 'Add to trip ✓'}
            </button>
          </div>
        </>
      )}
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )
}
