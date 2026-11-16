// Negative-only PRD probes. Requires playwright; no session/invite/valid attestation.
// Prints only statuses and recognized errors, never cookies/challenges.
import { chromium } from 'playwright';
const browser = await chromium.launch({ headless: true });
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('https://ccxp26.atilaalcantara.com/admin/');
  const rows = await page.evaluate(async () => {
    if (location.origin !== 'https://ccxp26.atilaalcantara.com') throw new Error('Unexpected origin');
    const cases = [
      ['invite_without_session', 'POST', '/api/admin/passkeys/invite', {}, 401],
      ['options_get', 'GET', '/api/admin/passkeys/register/options', null, 405],
      ['verify_get', 'GET', '/api/admin/passkeys/register/verify', null, 405],
      ['options_empty', 'POST', '/api/admin/passkeys/register/options', {}, 401],
      ['options_invalid_invite', 'POST', '/api/admin/passkeys/register/options', { token: 'invalid' }, 401],
      ['verify_empty', 'POST', '/api/admin/passkeys/register/verify', {}, 400],
      ['verify_no_challenge', 'POST', '/api/admin/passkeys/register/verify', {
        id: 'AA', rawId: 'AA', type: 'public-key',
        response: { clientDataJSON: 'e30', attestationObject: 'AA', transports: [] },
        clientExtensionResults: {}
      }, 400]
    ];
    const results = [];
    for (const [name, method, path, body, expected] of cases) {
      const response = await fetch(path, { method, credentials: 'omit',
        headers: { 'Content-Type': 'application/json' },
        ...(body ? { body: JSON.stringify(body) } : {}) });
      const parsed = await response.json().catch(() => ({}));
      const challengeReturned = !!parsed.challenge;
      const expiredInviteError = parsed.error === 'Convite expirado. Solicite outro.';
      results.push({ name, status: response.status, expected, challengeReturned, expiredInviteError,
        pass: response.status === expected && !challengeReturned &&
          (!name.startsWith('verify_') || method === 'GET' || expiredInviteError) });
    }
    return results;
  });
  console.log(JSON.stringify(rows, null, 2));
  if (rows.some(row => !row.pass)) process.exitCode = 1;
} finally {
  await browser.close();
}
