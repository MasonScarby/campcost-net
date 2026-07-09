// Supabase Auth MFA (TOTP) helpers.
// 2FA is optional at signup but REQUIRED before connecting a bank:
//  - client gate: ConnectBank checks getMfaStatus() and blocks Plaid Link
//  - server gate: the API rejects /api/plaid/exchange-token unless the JWT aal claim is aal2
import { supabase } from './supabase'

/**
 * Returns { currentLevel, nextLevel, hasVerifiedTotp, verifiedFactor }.
 * A user is bank-ready only when hasVerifiedTotp && currentLevel === 'aal2'.
 */
export async function getMfaStatus() {
  const [{ data: aal, error: aalError }, { data: factors, error: factorsError }] =
    await Promise.all([
      supabase.auth.mfa.getAuthenticatorAssuranceLevel(),
      supabase.auth.mfa.listFactors(),
    ])
  if (aalError) throw aalError
  if (factorsError) throw factorsError

  const verifiedFactor = (factors?.totp ?? []).find(f => f.status === 'verified') ?? null

  return {
    currentLevel: aal?.currentLevel ?? null,
    nextLevel: aal?.nextLevel ?? null,
    hasVerifiedTotp: Boolean(verifiedFactor),
    verifiedFactor,
  }
}

/** True when the session satisfies the bank-connection requirement. */
export async function isBankReady() {
  const { currentLevel, hasVerifiedTotp } = await getMfaStatus()
  return hasVerifiedTotp && currentLevel === 'aal2'
}

/** Starts TOTP enrollment. Returns { factorId, qrCode, secret }. */
export async function enrollTotp() {
  // Clean up any abandoned unverified factors first — Supabase rejects
  // duplicate friendly names on re-enrollment attempts.
  const { data: factors } = await supabase.auth.mfa.listFactors()
  for (const f of factors?.all ?? []) {
    if (f.factor_type === 'totp' && f.status === 'unverified') {
      await supabase.auth.mfa.unenroll({ factorId: f.id })
    }
  }

  const { data, error } = await supabase.auth.mfa.enroll({
    factorType: 'totp',
    friendlyName: 'Authenticator app',
  })
  if (error) throw error
  return {
    factorId: data.id,
    qrCode: data.totp.qr_code, // SVG data URI, render in <img src=...>
    secret: data.totp.secret,  // manual-entry fallback
  }
}

/** Challenge + verify a factor with a 6-digit code. Session upgrades to aal2 on success. */
export async function verifyTotp(factorId, code) {
  const { data: challenge, error: challengeError } =
    await supabase.auth.mfa.challenge({ factorId })
  if (challengeError) throw challengeError

  const { error: verifyError } = await supabase.auth.mfa.verify({
    factorId,
    challengeId: challenge.id,
    code,
  })
  if (verifyError) throw verifyError
}
