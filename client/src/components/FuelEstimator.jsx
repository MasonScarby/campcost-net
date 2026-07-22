import { useState } from 'react'
import { backendFetch } from '../lib/api'

// US states + DC, alphabetical by name; value is the 2-letter code the API expects.
const US_STATES = [
  ['AL', 'Alabama'], ['AK', 'Alaska'], ['AZ', 'Arizona'], ['AR', 'Arkansas'],
  ['CA', 'California'], ['CO', 'Colorado'], ['CT', 'Connecticut'], ['DE', 'Delaware'],
  ['DC', 'District of Columbia'], ['FL', 'Florida'], ['GA', 'Georgia'], ['HI', 'Hawaii'],
  ['ID', 'Idaho'], ['IL', 'Illinois'], ['IN', 'Indiana'], ['IA', 'Iowa'],
  ['KS', 'Kansas'], ['KY', 'Kentucky'], ['LA', 'Louisiana'], ['ME', 'Maine'],
  ['MD', 'Maryland'], ['MA', 'Massachusetts'], ['MI', 'Michigan'], ['MN', 'Minnesota'],
  ['MS', 'Mississippi'], ['MO', 'Missouri'], ['MT', 'Montana'], ['NE', 'Nebraska'],
  ['NV', 'Nevada'], ['NH', 'New Hampshire'], ['NJ', 'New Jersey'], ['NM', 'New Mexico'],
  ['NY', 'New York'], ['NC', 'North Carolina'], ['ND', 'North Dakota'], ['OH', 'Ohio'],
  ['OK', 'Oklahoma'], ['OR', 'Oregon'], ['PA', 'Pennsylvania'], ['RI', 'Rhode Island'],
  ['SC', 'South Carolina'], ['SD', 'South Dakota'], ['TN', 'Tennessee'], ['TX', 'Texas'],
  ['UT', 'Utah'], ['VT', 'Vermont'], ['VA', 'Virginia'], ['WA', 'Washington'],
  ['WV', 'West Virginia'], ['WI', 'Wisconsin'], ['WY', 'Wyoming'],
]

// Pre-trip fuel cost estimator. Calls POST /api/trips/{id}/fuel-estimate; the
// server owns the math and the seeded state price table. Legs are optional —
// left empty, the server uses the trip's total_miles at the national average.
export default function FuelEstimator({ tripId, totalMiles, onApplied }) {
  const [open, setOpen] = useState(false)
  const [mpg, setMpg] = useState('')
  const [fuelType, setFuelType] = useState('gas')
  const [legs, setLegs] = useState([{ miles: totalMiles ? String(totalMiles) : '', state: '' }])
  const [estimate, setEstimate] = useState(null)
  const [busy, setBusy] = useState(false)
  const [applied, setApplied] = useState(false)
  const [error, setError] = useState('')

  const input = { border: '1px solid #e8d5b0', borderRadius: 10, padding: '10px 12px', fontSize: 14, outline: 'none' }
  const btn = { background: '#f5e6c8', color: '#8b5e3c', border: 'none', borderRadius: 8, padding: '6px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer' }

  const request = async (apply) => {
    setBusy(true)
    setError('')
    try {
      const body = {
        mpg: Number(mpg),
        fuel_type: fuelType,
        apply,
        legs: legs
          .filter(l => Number(l.miles) > 0)
          .map(l => ({ miles: Number(l.miles), state: l.state.trim() || null })),
      }
      const res = await backendFetch(`/api/trips/${tripId}/fuel-estimate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      })
      const data = await res.json()
      if (!res.ok) throw new Error(data.error || 'Estimate failed')
      setEstimate(data.estimate)
      if (data.applied) {
        setApplied(true)
        onApplied?.()
      }
    } catch (e) {
      setError(e.message)
    } finally {
      setBusy(false)
    }
  }

  const setLeg = (i, patch) =>
    setLegs(ls => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)))

  return (
    <div style={{ background: '#fff', borderRadius: 16, padding: 20, boxShadow: '0 1px 3px rgba(74,55,40,0.08)', marginBottom: 20 }}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <div>
          <h2 style={{ fontSize: 17, fontWeight: 600, color: '#4a3728', margin: 0 }}>Fuel estimate</h2>
          {estimate && (
            <p style={{ fontSize: 12, color: '#8b5e3c', margin: '2px 0 0' }}>
              ~{estimate.totalGallons} gal · ${estimate.totalCost.toFixed(2)} (prices as of {estimate.pricesAsOf})
            </p>
          )}
        </div>
        <button onClick={() => setOpen(v => !v)} style={btn}>{open ? 'Close' : 'Estimate'}</button>
      </div>

      {open && (
        <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div style={{ display: 'flex', gap: 8 }}>
            <input
              type="number" min="1" placeholder="MPG (e.g. 12)"
              value={mpg} onChange={e => setMpg(e.target.value)}
              style={{ ...input, flex: 1 }}
            />
            <select value={fuelType} onChange={e => setFuelType(e.target.value)} style={{ ...input, flex: 1 }}>
              <option value="gas">Gas</option>
              <option value="diesel">Diesel</option>
            </select>
          </div>

          {legs.map((leg, i) => (
            <div key={i} style={{ display: 'flex', gap: 8 }}>
              <input
                type="number" min="1" placeholder="Miles"
                value={leg.miles} onChange={e => setLeg(i, { miles: e.target.value })}
                style={{ ...input, flex: 2 }}
              />
              <select
                value={leg.state} onChange={e => setLeg(i, { state: e.target.value })}
                style={{ ...input, flex: 1 }}
              >
                <option value="">State (opt.)</option>
                {US_STATES.map(([code, name]) => (
                  <option key={code} value={code}>{name}</option>
                ))}
              </select>
              {legs.length > 1 && (
                <button onClick={() => setLegs(ls => ls.filter((_, j) => j !== i))} style={{ ...btn, background: 'none' }}>✕</button>
              )}
            </div>
          ))}

          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <button onClick={() => setLegs(ls => [...ls, { miles: '', state: '' }])} style={{ ...btn, background: 'none' }}>
              + Leg
            </button>
            <div style={{ flex: 1 }} />
            <button disabled={busy || !Number(mpg)} onClick={() => request(false)} style={{ ...btn, opacity: busy || !Number(mpg) ? 0.5 : 1 }}>
              Estimate
            </button>
            {estimate && !applied && (
              <button disabled={busy} onClick={() => request(true)} style={{ ...btn, background: '#8b5e3c', color: '#fff' }}>
                Set as fuel budget
              </button>
            )}
          </div>

          {applied && <p style={{ fontSize: 12, color: '#4a7c59', margin: 0 }}>Saved as this trip's fuel budget.</p>}
          {error && <p style={{ fontSize: 12, color: '#b5493a', margin: 0 }}>{error}</p>}

          {estimate?.legs?.length > 1 && (
            <div style={{ fontSize: 12, color: '#8b5e3c' }}>
              {estimate.legs.map((l, i) => (
                <div key={i}>
                  {l.miles} mi{l.state ? ` (${l.state})` : ''} · {l.gallons} gal × ${l.pricePerGallon}/gal = ${l.cost.toFixed(2)}
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
