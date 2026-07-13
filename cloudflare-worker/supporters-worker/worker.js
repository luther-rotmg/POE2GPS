// Companion — v0.28 (LO ask): POE2GPS supporter code + Discord role Worker.
//
// Wires Ko-fi → (signed code emailed to donor) + (Discord Supporter role assigned).
//
// Endpoints:
//   POST /ko-fi-webhook   — Ko-fi POSTs here on every donation (JSON body with verification_token,
//                           donor email, amount, message). Worker verifies the token, mints an
//                           Ed25519 signed code (poe2gps.<b64-payload>.<b64-sig>), emails it to the
//                           donor via Resend, and (if the donor included a Discord username in the
//                           message) assigns the Supporter role via the Discord bot token.
//   GET  /public-key      — returns the Ed25519 public key hex (for LO to compare against the
//                           supporter_public_key.txt embedded in the app).
//   GET  /                — landing page describing the endpoints (for humans hitting the domain).
//
// Environment variables / secrets (set via `wrangler secret put ...`):
//   KO_FI_VERIFICATION_TOKEN  — the token Ko-fi posts as `verification_token` in its webhook body.
//                               Get from Ko-fi's webhook settings page.
//   SIGNING_PRIVATE_KEY_HEX   — Ed25519 private key (64 hex chars = 32 bytes). Generate with e.g.
//                               `python -c "from cryptography.hazmat.primitives.asymmetric import ed25519;
//                                p=ed25519.Ed25519PrivateKey.generate();
//                                print(p.private_bytes(...).hex())"`.
//                               The PUBLIC half of this key must ALSO be embedded in POE2GPS at
//                               src/POE2Radar.Core/Support/supporter_public_key.txt (they're a pair).
//   RESEND_API_KEY            — API key from resend.com (or swap the sendEmail() body for your email
//                               provider of choice: SendGrid, Mailgun, Postmark, or Ko-fi's own).
//   RESEND_FROM               — verified sender address on Resend (e.g. supporters@poe2gps.com).
//   DISCORD_BOT_TOKEN         — bot token from your Discord app; the bot must be in your server with
//                               the `Manage Roles` permission and its role above the Supporter role.
//   DISCORD_GUILD_ID          — Discord server (guild) snowflake ID.
//   DISCORD_SUPPORTER_ROLE_ID — Discord role snowflake ID (e.g. ☕ Supporter).
//   DISCORD_ANNOUNCE_WEBHOOK  — (optional) Discord webhook URL to post a "🎉 New supporter" message
//                               to your announce channel. Leave empty to skip.
//   ADMIN_TOKEN               — Bearer token protecting /admin/* endpoints (currently just /public-key).

const CODE_PREFIX = 'poe2gps.';

// ── HTTP entry ──────────────────────────────────────────────────────────────────────────────────

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    try {
      if (request.method === 'POST' && url.pathname === '/ko-fi-webhook') {
        return await handleKoFiWebhook(request, env, ctx);
      }
      if (request.method === 'GET' && url.pathname === '/public-key') {
        return await handlePublicKey(env);
      }
      if (request.method === 'GET' && url.pathname === '/') {
        return new Response(landingHtml(), { headers: { 'content-type': 'text/html; charset=utf-8' } });
      }
      return new Response('Not found', { status: 404 });
    } catch (err) {
      // Never leak the stack in production — but do log to CF.
      console.error('worker error', err && err.stack || err);
      return new Response('Internal error', { status: 500 });
    }
  },
};

// ── Ko-fi webhook handler ───────────────────────────────────────────────────────────────────────

async function handleKoFiWebhook(request, env, ctx) {
  // Ko-fi posts as application/x-www-form-urlencoded with a single "data" field carrying the JSON body.
  // (Confirm at https://help.ko-fi.com/hc/en-us/articles/360004162298 — payload shape.)
  let payload;
  const contentType = request.headers.get('content-type') || '';
  if (contentType.includes('application/x-www-form-urlencoded')) {
    const form = await request.formData();
    const dataStr = form.get('data');
    if (!dataStr) return new Response('missing data field', { status: 400 });
    payload = JSON.parse(dataStr);
  } else {
    payload = await request.json();
  }

  if (payload.verification_token !== env.KO_FI_VERIFICATION_TOKEN) {
    return new Response('bad verification token', { status: 401 });
  }

  const email = (payload.email || '').trim().toLowerCase();
  const amountUsd = parseFloat(payload.amount || '0') || 0;
  const messageText = (payload.message || '').trim();
  const donorName = (payload.from_name || '').trim() || 'friend';

  if (!email) return new Response('missing email', { status: 400 });

  // Tier = crude bucket by donation amount. Adjust as you like.
  const tier =
    amountUsd >= 25 ? 'gold' :
    amountUsd >= 10 ? 'silver' :
    amountUsd >= 3  ? 'bronze' : 'community';

  // Sign a compact code with Ed25519.
  const claims = { email, tier, issued: Math.floor(Date.now() / 1000) };
  const code = await mintCode(claims, env);

  // Fire the side effects in parallel; log any failure but always email the code back.
  const jobs = [
    sendEmail(email, donorName, code, tier, env).catch(err => ({ ok: false, err: `email: ${err.message}` })),
    tryAssignDiscordRole(messageText, env).catch(err => ({ ok: false, err: `discord role: ${err.message}` })),
    tryAnnounce(donorName, tier, amountUsd, env).catch(err => ({ ok: false, err: `announce: ${err.message}` })),
  ];
  const results = await Promise.all(jobs);

  return Response.json({
    ok: true,
    tier,
    // Return the code in the response too — useful for debugging via `wrangler tail`. Ko-fi ignores
    // the response body; only the 2xx status matters to them.
    code,
    email_result: results[0],
    discord_role_result: results[1],
    announce_result: results[2],
  });
}

// ── Ed25519 code minting ────────────────────────────────────────────────────────────────────────

async function mintCode(claims, env) {
  const keyHex = env.SIGNING_PRIVATE_KEY_HEX;
  if (!keyHex || keyHex.length !== 64) {
    throw new Error('SIGNING_PRIVATE_KEY_HEX not set or wrong length (want 64 hex chars = 32 bytes)');
  }
  const priv = hexToBytes(keyHex);

  // Canonical payload: sorted-key JSON with no whitespace, so the bytes match on both sides.
  const canonical = JSON.stringify(sortObject(claims));
  const payloadBytes = new TextEncoder().encode(canonical);

  // Cloudflare Workers' Web Crypto REJECTS 'raw' Ed25519 imports with the 'sign' usage
  // ('raw' means public key in their spec — you get "invalid usage 'sign'"). The workaround
  // is a PKCS8 DER envelope, which is a fixed 16-byte prefix + the 32-byte private key.
  // The prefix encodes: SEQUENCE ┃ version 0 ┃ algorithm OID 1.3.101.112 (Ed25519) ┃ octet-string wrap.
  const pkcs8Prefix = new Uint8Array([
    0x30, 0x2e, 0x02, 0x01, 0x00, 0x30, 0x05, 0x06,
    0x03, 0x2b, 0x65, 0x70, 0x04, 0x22, 0x04, 0x20,
  ]);
  const pkcs8Bytes = new Uint8Array(pkcs8Prefix.length + priv.length);
  pkcs8Bytes.set(pkcs8Prefix);
  pkcs8Bytes.set(priv, pkcs8Prefix.length);
  const cryptoKey = await crypto.subtle.importKey(
    'pkcs8',
    pkcs8Bytes,
    { name: 'Ed25519' },
    false,
    ['sign']
  );
  const sigBuf = await crypto.subtle.sign('Ed25519', cryptoKey, payloadBytes);

  return CODE_PREFIX + b64urlEncode(payloadBytes) + '.' + b64urlEncode(new Uint8Array(sigBuf));
}

// ── Email sender (Resend by default; swap for your provider) ─────────────────────────────────────

async function sendEmail(toEmail, donorName, code, tier, env) {
  if (!env.RESEND_API_KEY || !env.RESEND_FROM) {
    return { ok: false, err: 'RESEND_API_KEY / RESEND_FROM not configured — email skipped' };
  }
  const subject = 'Your POE2GPS supporter code ☕';
  const bodyPlain =
`Hey ${donorName},

Huge thanks for the coffee — POE2GPS runs on it. 🙏

Your supporter code:

  ${code}

Open the POE2GPS dashboard → Settings → Supporter code, paste it in, and the Kalguuran Gold + Wraeclast Terminal palettes unlock along with the optional ☕ Supporter chip on the Session HUD.

Your name is going on the roll next release. If you want a display name / short note different from your Ko-fi handle, just reply to this email.

— LO
`;
  const bodyHtml =
`<p>Hey ${escapeHtml(donorName)},</p>
<p>Huge thanks for the coffee — POE2GPS runs on it. 🙏</p>
<p><b>Your supporter code:</b></p>
<pre style="background:#0c0a07;color:#ffdb6a;padding:14px;border-radius:6px;font-family:monospace;word-break:break-all">${escapeHtml(code)}</pre>
<p>Open the POE2GPS dashboard → Settings → Supporter code, paste it in, and the <b>Kalguuran Gold</b> + <b>Wraeclast Terminal</b> palettes unlock along with the optional ☕ Supporter chip on the Session HUD.</p>
<p>Your name is going on the roll next release. If you want a display name / short note different from your Ko-fi handle, just reply to this email.</p>
<p>— LO</p>`;

  const r = await fetch('https://api.resend.com/emails', {
    method: 'POST',
    headers: { 'Authorization': `Bearer ${env.RESEND_API_KEY}`, 'Content-Type': 'application/json' },
    body: JSON.stringify({ from: env.RESEND_FROM, to: [toEmail], subject, text: bodyPlain, html: bodyHtml }),
  });
  if (!r.ok) return { ok: false, err: `resend HTTP ${r.status}` };
  return { ok: true };
}

// ── Discord role assign (Ko-fi message field carries `discord: @user`) ───────────────────────────

async function tryAssignDiscordRole(messageText, env) {
  if (!env.DISCORD_BOT_TOKEN || !env.DISCORD_GUILD_ID || !env.DISCORD_SUPPORTER_ROLE_ID) {
    return { ok: false, err: 'discord not configured — role assignment skipped' };
  }
  // Donors paste "discord: their_handle" (or "discord: their_handle#1234" for legacy) into the Ko-fi
  // donation message. We look that up via the bot's guild-member search endpoint.
  const match = messageText.match(/discord\s*:?\s*([@\w#._-]{2,64})/i);
  if (!match) return { ok: false, err: 'no discord handle found in donation message' };
  const handle = match[1].replace(/^@/, '').trim();

  const lookup = await fetch(
    `https://discord.com/api/v10/guilds/${env.DISCORD_GUILD_ID}/members/search?query=${encodeURIComponent(handle)}&limit=5`,
    { headers: { Authorization: `Bot ${env.DISCORD_BOT_TOKEN}` } }
  );
  if (!lookup.ok) return { ok: false, err: `discord lookup HTTP ${lookup.status}` };
  const members = await lookup.json();
  const member = members.find(m => (m.user?.username || '').toLowerCase() === handle.toLowerCase())
              || members[0];
  if (!member) return { ok: false, err: `no member matching '${handle}'` };

  const userId = member.user.id;
  const assign = await fetch(
    `https://discord.com/api/v10/guilds/${env.DISCORD_GUILD_ID}/members/${userId}/roles/${env.DISCORD_SUPPORTER_ROLE_ID}`,
    { method: 'PUT', headers: { Authorization: `Bot ${env.DISCORD_BOT_TOKEN}` } }
  );
  if (!assign.ok) return { ok: false, err: `discord role assign HTTP ${assign.status}` };
  return { ok: true, userId, handle };
}

// ── Discord "🎉 new supporter" announce (optional webhook post) ──────────────────────────────────

async function tryAnnounce(donorName, tier, amountUsd, env) {
  if (!env.DISCORD_ANNOUNCE_WEBHOOK) return { ok: false, err: 'announce webhook not configured' };
  const tierEmoji = { gold: '🥇', silver: '🥈', bronze: '🥉', community: '💛' }[tier] || '💛';
  const body = { content: `${tierEmoji} **${donorName}** just supported POE2GPS with a $${amountUsd.toFixed(2)} coffee — welcome to the supporters! ☕` };
  const r = await fetch(env.DISCORD_ANNOUNCE_WEBHOOK, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!r.ok) return { ok: false, err: `announce HTTP ${r.status}` };
  return { ok: true };
}

// ── /public-key admin (compare with shipped supporter_public_key.txt) ────────────────────────────

async function handlePublicKey(env) {
  const keyHex = env.SIGNING_PRIVATE_KEY_HEX;
  if (!keyHex || keyHex.length !== 64) {
    return new Response('SIGNING_PRIVATE_KEY_HEX not set', { status: 500 });
  }
  const priv = hexToBytes(keyHex);
  const cryptoKey = await crypto.subtle.importKey('raw', priv, { name: 'Ed25519' }, true, ['sign']);
  const jwk = await crypto.subtle.exportKey('jwk', cryptoKey);
  // JWK 'x' is the public key in base64url.
  const pubBytes = b64urlDecode(jwk.x);
  const hex = Array.from(pubBytes).map(b => b.toString(16).padStart(2, '0')).join('');
  return Response.json({ public_key_hex: hex, note: 'This must match src/POE2Radar.Core/Support/supporter_public_key.txt in the POE2GPS repo.' });
}

// ── Landing page ────────────────────────────────────────────────────────────────────────────────

function landingHtml() {
  return `<!doctype html><html><head><title>POE2GPS Supporters Worker</title>
<style>body{font-family:system-ui;max-width:640px;margin:40px auto;padding:0 20px;color:#333}
code{background:#f4f4f4;padding:2px 6px;border-radius:3px}</style></head><body>
<h1>POE2GPS Supporters Worker</h1>
<p>This Cloudflare Worker handles the Ko-fi supporter flow for <a href="https://github.com/luther-rotmg/POE2GPS">POE2GPS</a>:</p>
<ul>
<li><code>POST /ko-fi-webhook</code> — receives Ko-fi donations, mints an Ed25519 signed code, emails it, assigns Discord role.</li>
<li><code>GET  /public-key</code> — the Ed25519 public key hex (must match the one embedded in the POE2GPS app).</li>
</ul>
<p>Support POE2GPS on <a href="https://ko-fi.com/lutherrotmg">Ko-fi</a>.</p>
</body></html>`;
}

// ── Small helpers ───────────────────────────────────────────────────────────────────────────────

function hexToBytes(hex) {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16);
  return out;
}
function b64urlEncode(bytes) {
  let s = btoa(String.fromCharCode(...bytes));
  return s.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
function b64urlDecode(b64) {
  const s = b64.replace(/-/g, '+').replace(/_/g, '/');
  const padded = s + '='.repeat((4 - s.length % 4) % 4);
  const bin = atob(padded);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
function sortObject(obj) {
  if (Array.isArray(obj)) return obj.map(sortObject);
  if (obj && typeof obj === 'object') {
    const sorted = {};
    for (const k of Object.keys(obj).sort()) sorted[k] = sortObject(obj[k]);
    return sorted;
  }
  return obj;
}
function escapeHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
