import { useState, useEffect } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { supabase } from '../lib/supabase'
import { useAuth } from '../context/AuthContext'
import { getPlan, FREE_TRIP_LIMIT } from '../lib/plan'

const DEFAULT_BUDGETS = {
  fuel: 200,
  campground: 150,
  food_groceries: 200,
  propane_utilities: 30,
  dump_station: 20,
  activities: 100,
  gear: 50,
  repairs: 50,
  misc: 50,
}

const CATEGORY_LABELS = {
  fuel: 'Fuel',
  campground: 'Campground',
  food_groceries: 'Food & Groceries',
  propane_utilities: 'Propane & Utilities',
  dump_station: 'Dump Station',
  activities: 'Activities',
  gear: 'Gear',
  repairs: 'Repairs',
  misc: 'Misc',
}

const input = {
  width: '100%', border: '1px solid #e8d5b0', borderRadius: 12,
  padding: '10px 14px', fontSize: 14, outline: 'none',
  boxSizing: 'border-box', fontFamily: 'inherit',
}

const btn = {
  width: '100%', background: '#2d5a27', color: 'white',
  border: 'none', borderRadius: 12, padding: '12px 16px',
  fontSize: 14, fontWeight: 600, cursor: 'pointer',
}

export default function TripNew() {
  const { user } = useAuth()
  const navigate = useNavigate()
  const [step, setStep] = useState(1)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [tripCount, setTripCount] = useState(null)
  const plan = getPlan()

  useEffect(() => {
    supabase
      .from('trips')
      .select('id', { count: 'exact', head: true })
      .eq('user_id', user.id)
      .then(({ count }) => setTripCount(count ?? 0))
  }, [user.id])

  const [trip, setTrip] = useState({
    name: '', destination: '', start_date: '', end_date: '', total_miles: '',
  })
  const [budgets, setBudgets] = useState({ ...DEFAULT_BUDGETS })

  const isBlocked = plan === 'free' && tripCount !== null && tripCount >= FREE_TRIP_LIMIT
  const totalBudget = Object.values(budgets).reduce((sum, v) => sum + Number(v || 0), 0)
  const updateBudget = (cat, val) => setBudgets(prev => ({ ...prev, [cat]: val }))

  const handleCreate = async () => {
    setLoading(true)
    setError('')

    const { data: tripData, error: tripError } = await supabase
      .from('trips')
      .insert({
        user_id: user.id,
        name: trip.name,
        destination: trip.destination || null,
        start_date: trip.start_date || null,
        end_date: trip.end_date || null,
        total_miles: trip.total_miles ? Number(trip.total_miles) : null,
        total_budget: totalBudget,
        status: 'planning',
      })
      .select()
      .single()

    if (tripError) {
      setError(tripError.message)
      setLoading(false)
      return
    }

    const catRows = Object.entries(budgets).map(([category, planned_amount]) => ({
      trip_id: tripData.id,
      category,
      planned_amount: Number(planned_amount || 0),
    }))

    const { error: budgetError } = await supabase.from('budget_categories').insert(catRows)
    if (budgetError) {
      setError(budgetError.message)
      setLoading(false)
      return
    }

    setLoading(false)
    navigate(`/trips/${tripData.id}`)
  }

  if (tripCount === null) return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '60vh' }}>
      <div style={{ width: 28, height: 28, border: '3px solid #2d5a27', borderTopColor: 'transparent', borderRadius: '50%', animation: 'spin 0.8s linear infinite' }} />
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )

  if (isBlocked) return (
    <div style={{ maxWidth: 480, margin: '0 auto', padding: '32px 16px' }}>
      <button onClick={() => navigate('/dashboard')} style={{ background: 'none', border: 'none', color: '#8b5e3c', fontSize: 14, cursor: 'pointer', marginBottom: 24, padding: 0 }}>
        ← Back
      </button>
      <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 40, textAlign: 'center' }}>
        <div style={{ fontSize: 36, marginBottom: 12 }}>🔒</div>
        <h2 style={{ fontSize: 18, fontWeight: 700, color: '#4a3728', marginBottom: 6 }}>
          {tripCount}/{FREE_TRIP_LIMIT} trips used
        </h2>
        <p style={{ fontSize: 14, color: '#8b5e3c', marginBottom: 24 }}>
          Upgrade to Pro for unlimited trips, auto-sync, PDF export, and more.
        </p>
        <Link to="/account" style={{ display: 'block', background: '#2d5a27', color: 'white', borderRadius: 12, padding: '12px 24px', fontSize: 14, fontWeight: 600, textDecoration: 'none', marginBottom: 10 }}>
          Upgrade to Pro — $6/mo
        </Link>
        <button onClick={() => navigate('/dashboard')} style={{ background: 'none', border: 'none', color: '#8b5e3c', fontSize: 13, cursor: 'pointer' }}>
          Go back
        </button>
      </div>
    </div>
  )

  return (
    <div style={{ maxWidth: 560, margin: '0 auto', padding: '32px 16px' }}>

      {/* Header */}
      <div style={{ marginBottom: 24 }}>
        <button
          onClick={() => step === 1 ? navigate('/dashboard') : setStep(1)}
          style={{ background: 'none', border: 'none', color: '#8b5e3c', fontSize: 14, cursor: 'pointer', marginBottom: 12, padding: 0 }}
        >
          ← {step === 1 ? 'Dashboard' : 'Back'}
        </button>
        <h1 style={{ fontSize: 24, fontWeight: 700, color: '#4a3728', margin: '0 0 12px' }}>Plan a Trip</h1>
        <div style={{ display: 'flex', gap: 8 }}>
          {[1, 2].map(s => (
            <div key={s} style={{ flex: 1, height: 6, borderRadius: 99, background: s <= step ? '#2d5a27' : '#e8d5b0' }} />
          ))}
        </div>
      </div>

      {/* Step 1 — Trip details */}
      {step === 1 && (
        <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 24 }}>
          <h2 style={{ fontSize: 16, fontWeight: 600, color: '#4a3728', margin: '0 0 20px' }}>Trip details</h2>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <div>
              <label style={{ fontSize: 12, fontWeight: 600, color: '#8b5e3c', display: 'block', marginBottom: 6 }}>Trip name *</label>
              <input
                type="text"
                placeholder="e.g. Southwest Loop May 2026"
                value={trip.name}
                onChange={e => setTrip(p => ({ ...p, name: e.target.value }))}
                style={input}
              />
            </div>

            <div>
              <label style={{ fontSize: 12, fontWeight: 600, color: '#8b5e3c', display: 'block', marginBottom: 6 }}>Destination</label>
              <input
                type="text"
                placeholder="e.g. Utah & Colorado"
                value={trip.destination}
                onChange={e => setTrip(p => ({ ...p, destination: e.target.value }))}
                style={input}
              />
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
              <div>
                <label style={{ fontSize: 12, fontWeight: 600, color: '#8b5e3c', display: 'block', marginBottom: 6 }}>Start date</label>
                <input type="date" value={trip.start_date} onChange={e => setTrip(p => ({ ...p, start_date: e.target.value }))} style={input} />
              </div>
              <div>
                <label style={{ fontSize: 12, fontWeight: 600, color: '#8b5e3c', display: 'block', marginBottom: 6 }}>End date</label>
                <input type="date" value={trip.end_date} onChange={e => setTrip(p => ({ ...p, end_date: e.target.value }))} style={input} />
              </div>
            </div>

            <div>
              <label style={{ fontSize: 12, fontWeight: 600, color: '#8b5e3c', display: 'block', marginBottom: 6 }}>Estimated miles</label>
              <input
                type="number"
                placeholder="e.g. 1200"
                value={trip.total_miles}
                onChange={e => setTrip(p => ({ ...p, total_miles: e.target.value }))}
                style={input}
              />
            </div>

            <button
              onClick={() => setStep(2)}
              disabled={!trip.name.trim()}
              style={{ ...btn, opacity: !trip.name.trim() ? 0.4 : 1, marginTop: 4 }}
            >
              Set budget →
            </button>
          </div>
        </div>
      )}

      {/* Step 2 — Budget */}
      {step === 2 && (
        <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 24 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 20 }}>
            <h2 style={{ fontSize: 16, fontWeight: 600, color: '#4a3728', margin: 0 }}>Budget by category</h2>
            <span style={{ fontSize: 14, fontWeight: 700, color: '#2d5a27' }}>${totalBudget.toLocaleString()} total</span>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 12, marginBottom: 24 }}>
            {Object.entries(CATEGORY_LABELS).map(([cat, label]) => (
              <div key={cat} style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <label style={{ fontSize: 14, color: '#4a3728', width: 160, flexShrink: 0 }}>{label}</label>
                <div style={{ position: 'relative', flex: 1 }}>
                  <span style={{ position: 'absolute', left: 12, top: '50%', transform: 'translateY(-50%)', fontSize: 14, color: '#8b5e3c' }}>$</span>
                  <input
                    type="number"
                    value={budgets[cat]}
                    onChange={e => updateBudget(cat, e.target.value)}
                    style={{ ...input, paddingLeft: 28 }}
                  />
                </div>
              </div>
            ))}
          </div>

          {error && (
            <p style={{ color: '#d4622a', fontSize: 13, marginBottom: 12, padding: '10px 14px', background: '#fff5f0', borderRadius: 8 }}>
              Error: {error}
            </p>
          )}

          <button
            onClick={handleCreate}
            disabled={loading}
            style={{ ...btn, opacity: loading ? 0.5 : 1 }}
          >
            {loading ? 'Creating trip...' : 'Create trip'}
          </button>
        </div>
      )}
    </div>
  )
}
