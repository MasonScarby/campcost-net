import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import { supabase } from '../lib/supabase'
import { useAuth } from '../context/AuthContext'

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
  const [current, setCurrent] = useState(0)
  const [editCategory, setEditCategory] = useState(null)

  useEffect(() => {
    const fetchUnreviewed = async () => {
      const [{ data: trip }, { data: expenses }] = await Promise.all([
        supabase.from('trips').select('start_date, end_date').eq('id', id).single(),
        supabase.from('expenses').select('*').eq('trip_id', id).eq('reviewed', false)
          .order('expense_date', { ascending: false }),
      ])

      const BUFFER = 2
      const start = trip?.start_date ? new Date(trip.start_date) : null
      const end = trip?.end_date ? new Date(trip.end_date) : null

      const inRange = []
      const outOfRange = []

      for (const e of (expenses ?? [])) {
        const d = new Date(e.expense_date)
        let outside = false
        if (start) {
          const cutoff = new Date(start); cutoff.setDate(cutoff.getDate() - BUFFER)
          if (d < cutoff) { outside = true }
        }
        if (end) {
          const cutoff = new Date(end); cutoff.setDate(cutoff.getDate() + BUFFER)
          if (d > cutoff) { outside = true }
        }
        outside ? outOfRange.push(e) : inRange.push(e)
      }

      setUnreviewed(inRange)
      if (inRange[0]) setEditCategory(inRange[0].category)
      setLoading(false)
    }
    fetchUnreviewed()

    // Real-time: prepend new Plaid-synced expenses as they arrive
    const channel = supabase
      .channel(`expenses:trip:${id}`)
      .on(
        'postgres_changes',
        { event: 'INSERT', schema: 'public', table: 'expenses', filter: `trip_id=eq.${id}` },
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

  const confirmExpense = async () => {
    const { error } = await supabase
      .from('expenses')
      .update({ reviewed: true, category: editCategory })
      .eq('id', unreviewed[current].id)
    if (error) { console.error('confirm error:', error.message); return }
    advance()
  }

  const skipExpense = async () => {
    // Mark reviewed=true + adjusted_amount=0 instead of deleting.
    // Keeping the row preserves plaid_transaction_id so the sync dedup
    // index prevents it from being re-imported on the next sync.
    // TripDetail uses effectiveAmount() which returns adjusted_amount when set,
    // so $0 skipped items add nothing to totals. They're also hidden from the list.
    const { error } = await supabase
      .from('expenses')
      .update({ reviewed: true, adjusted_amount: 0, adjustment_note: 'skipped' })
      .eq('id', unreviewed[current].id)
    if (error) { console.error('skip error:', error.message); return }
    advance()
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
                  {unreviewed[current].merchant_name || unreviewed[current].note || 'Unknown merchant'}
                </p>
                <p style={{ fontSize: 14, color: '#8b5e3c', marginTop: 4 }}>{unreviewed[current].expense_date}</p>
              </div>
              <span style={{ fontSize: 28, fontWeight: 700, color: '#4a3728' }}>
                ${Number(unreviewed[current].amount).toFixed(2)}
              </span>
            </div>

            <div>
              <label style={{ fontSize: 13, fontWeight: 600, color: '#8b5e3c', display: 'block', marginBottom: 8 }}>Category</label>
              <select value={editCategory} onChange={e => setEditCategory(e.target.value)}
                style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 12, padding: '12px 14px', fontSize: 15, outline: 'none', background: 'white', boxSizing: 'border-box' }}>
                {Object.entries(CATEGORY_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </div>
          </div>

          {/* Actions */}
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
            <button onClick={skipExpense} style={{
              background: 'white', border: '1px solid #e8d5b0', color: '#8b5e3c',
              borderRadius: 12, padding: '14px', fontSize: 15, fontWeight: 600, cursor: 'pointer'
            }}>
              Not a trip expense
            </button>
            <button onClick={confirmExpense} style={{
              background: '#2d5a27', color: 'white', border: 'none',
              borderRadius: 12, padding: '14px', fontSize: 15, fontWeight: 600, cursor: 'pointer'
            }}>
              Add to trip ✓
            </button>
          </div>
        </>
      )}
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )
}
