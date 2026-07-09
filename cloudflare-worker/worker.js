// POE2GPS community-pack collector (v3). Splits the v2 single-route into sibling routes:
//   POST /submit-atlas   — {names, objectives}  (v0.20 backward-compat payload shape)
//   POST /submit-buffs   — {buffs}              (buff metadata paths + tier)
//   POST /submit-preload — {preloads}           (metadata paths; bare .dds/.ao rejected)
//   POST /submit-trace   — {install_uuid, boot_id, event_count, jsonl_gzip_b64}
//                          (Task 7 PROBE-CONTRIBUTE — anonymized campaign traces; per-boot upload)
//   POST /submit         — legacy alias -> /submit-atlas (v0.20.x clients + rollback safety;
//                          same {names, objectives} payload shape as /submit-atlas)
// Shared middleware: NFKD+leet profanity fold, KV rate limit 5/60s per CF-Connecting-IP, and
// a GitHub Issue dispatch labelled `community-pack` + `needs-review` (+ optional sub-label).
// GITHUB_TOKEN is a Worker SECRET; never in the client. RATE_KV is a KV namespace bound in wrangler.toml.

const REPO = 'luther-rotmg/POE2GPS';
const MAX_BYTES = 262144; // 256 KB
const RATE_LIMIT = 5;
const RATE_WINDOW_S = 60;
const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type',
};

// Conservative 7-word slur list. Kept tight to avoid false positives on legit PoE metadata
// (Ezomyte, Doedre, Kitava). Maintainer-editable; every entry must be lower-case letters only
// so the folded-input comparison in isProfane() matches with a simple .includes().
const SLURS = ['nigger', 'faggot', 'retard', 'kike', 'spic', 'chink', 'cunt'];

// Leet-substitution table applied AFTER NFKD normalization and BEFORE letter-collapse.
// Digits + a few punctuation glyphs commonly used to bypass a naive word-list filter.
const LEET = {
  '4': 'a', '@': 'a',
  '0': 'o',
  '1': 'i',
  '3': 'e',
  '5': 's', '$': 's',
  '7': 't',
  '8': 'b',
};

// ── shared middleware: profanity (NFKD + strip combining marks + leet-fold + collapse) ──
//
// Pipeline:
//   1. NFKD-normalize so decomposable glyphs (n̈, ê, ï) split into base + combining mark.
//   2. Strip Unicode category Mn (Nonspacing_Mark) — kills zalgo-style bypass attempts.
//   3. Lowercase, then per-char leet-fold (digits/@/$ -> letters).
//   4. Collapse to [a-z]+ — spaces and remaining punctuation drop out, so "n i g g e r" folds
//      to "nigger". Also produce an alt pass mapping i->l for slurs typed with `l` variants.
//   5. Substring-match either folded or alt against the 7-word list.
//
// The v2 8/12-char gibberish gates are deliberately GONE — they misfired on legitimate PoE
// metadata paths and their contribution to abuse-blocking was marginal.
export function isProfane(s) {
  if (typeof s !== 'string') return false;
  const folded = s
    .normalize('NFKD')
    .replace(/\p{Mn}/gu, '')
    .toLowerCase()
    .split('')
    .map(ch => LEET[ch] ?? ch)
    .join('')
    .replace(/[^a-z]+/g, '');
  const alt = folded.replace(/i/g, 'l');
  return SLURS.some(w => folded.includes(w) || alt.includes(w));
}

// ── shared middleware: KV rate limit (5 requests per 60s per CF-Connecting-IP) ──
//
// Fixed-window counter: key `rl:<ip>` holds the request count, TTL = RATE_WINDOW_S. First
// request writes 1 with a 60s TTL; the counter naturally expires when the window elapses.
// On the 6th call within the window `n >= RATE_LIMIT` returns false without a further put,
// so the TTL runs out cleanly instead of being renewed by every rejected probe.
//
// Fail-open when the KV binding is missing (local dev, unit tests) so the Worker still
// answers legitimate requests without a KV namespace configured.
export async function rateLimit(env, ip) {
  if (!env || !env.RATE_KV || !ip) return true;
  const key = `rl:${ip}`;
  const raw = await env.RATE_KV.get(key);
  const n = raw ? parseInt(raw, 10) : 0;
  if (n >= RATE_LIMIT) return false;
  await env.RATE_KV.put(key, String(n + 1), { expirationTtl: RATE_WINDOW_S });
  return true;
}

// ── routing ──
// One switch, three sibling routes + one legacy alias. Any other pathname returns null so the
// top-level fetch() handler answers 404 (unambiguous for stale desktop clients).
//
// The `/submit` alias exists so a user who downgrades v0.21 -> v0.20.1 (for any reason) still
// has a working Contribute button — v0.20.1 POSTs to `/submit` with the exact `{names, objectives}`
// shape that /submit-atlas expects. Alias will be sunset only after v0.20.x auto-updater usage
// drops to zero.
export function routeFor(url) {
  switch (url.pathname) {
    case '/submit-atlas':   return { kind: 'atlas' };
    case '/submit-buffs':   return { kind: 'buffs' };
    case '/submit-preload': return { kind: 'preload' };
    case '/submit-trace':   return { kind: 'trace' };   // Task 7 PROBE-CONTRIBUTE — fifth sibling
    case '/submit':         return { kind: 'atlas' };   // legacy v0.20.x alias
    default:                return null;
  }
}

// ── shared identifying-field reject ──
// Defense-in-depth: reject any payload that mentions a well-known identifying key by name,
// even if the desktop client tried to smuggle one in. Cheap string check over the raw JSON.
const FORBIDDEN = ['charname', 'character', 'account', 'accountname', 'address', 'ip'];
function hasIdentifying(pack) {
  const s = JSON.stringify(pack).toLowerCase();
  return FORBIDDEN.some(k => s.includes('"' + k + '"'));
}

function cleanLabel(raw, max) {
  if (typeof raw !== 'string') return null;
  const s = raw.trim();
  if (s.length < 2 || s.length > max) return null;
  if (!/[a-zA-Z]/.test(s)) return null; // must contain at least one letter
  return s;
}

// ── per-route payload filter ──
// Exported so tests can hit filterPayloadCommon('preload', ...) without spinning up a fetch()
// mock. Returns either a filtered pack or `{error: string}` for the top-level handler to 400.
export function filterPayloadCommon(kind, pack) {
  if (kind === 'atlas') {
    if (!pack
        || typeof pack.names !== 'object' || Array.isArray(pack.names)
        || !Array.isArray(pack.objectives))
      return { error: 'expected {names:object, objectives:array}' };
    const seen = new Set();
    const names = [];
    for (const [meta, raw] of Object.entries(pack.names)) {
      const nm = cleanLabel(raw, 60);
      if (!nm || isProfane(nm)) continue;
      const key = String(meta).slice(0, 200);
      if (seen.has(key)) continue;
      seen.add(key);
      names.push([key, nm]);
    }
    const objectives = [];
    for (const o of pack.objectives) {
      if (!o || typeof o !== 'object') continue;
      const label = cleanLabel(o.label, 60);
      const category = cleanLabel(o.category, 40);
      if (!label && !category) continue;
      if ((label && isProfane(label)) || (category && isProfane(category))) continue;
      // Whitelist only known non-identifying matcher fields — never spread arbitrary client keys.
      objectives.push({
        label, category,
        priority: o.priority, enabled: o.enabled,
        metadata: o.metadata, categories: o.categories,
        poi: o.poi, rarity: o.rarity, landmarkPath: o.landmarkPath,
      });
    }
    return { names, objectives };
  }

  if (kind === 'buffs') {
    if (!pack || !Array.isArray(pack.buffs)) return { error: 'expected {buffs:array}' };
    const buffs = [];
    for (const b of pack.buffs) {
      if (!b || typeof b !== 'object') continue;
      const path = cleanLabel(b.path, 200);
      if (!path || isProfane(path)) continue;
      const tier = (typeof b.tier === 'string' || typeof b.tier === 'number') ? b.tier : null;
      buffs.push({ path, tier, metadata: b.metadata });
    }
    return { buffs };
  }

  if (kind === 'preload') {
    if (!pack || !Array.isArray(pack.preloads)) return { error: 'expected {preloads:array}' };
    const preloads = [];
    for (const p of pack.preloads) {
      if (!p || typeof p !== 'object') continue;
      const path = cleanLabel(p.path, 200);
      if (!path || isProfane(path)) continue;
      // Reject bare .dds / .ao (raw asset filenames with no metadata prefix). They carry no
      // research signal — only qualified paths like `Metadata/Monsters/Goatman.ao` are useful.
      if (/^[^\/]+\.(dds|ao)$/i.test(path)) continue;
      const freq = (typeof p.freq === 'number' && Number.isFinite(p.freq)) ? p.freq : null;
      preloads.push({ path, freq });
    }
    return { preloads };
  }

  if (kind === 'trace') {
    // Task 7 PROBE-CONTRIBUTE. Spec §2/§8 posture: only install_uuid + boot_id + event_count +
    // gzipped bytes cross the wire. No profanity fold — the JSONL is gzipped random-looking bytes
    // and any user-typed values inside were already sha256-hashed to 16 chars by AnonymizationHelpers
    // before the writer serialized them.
    if (!pack || typeof pack !== 'object') return { error: 'expected trace envelope' };
    const uuidRe = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    if (typeof pack.install_uuid !== 'string' || !uuidRe.test(pack.install_uuid))
      return { error: 'bad install_uuid' };
    if (typeof pack.boot_id !== 'string' || !uuidRe.test(pack.boot_id))
      return { error: 'bad boot_id' };
    if (!Number.isInteger(pack.event_count) || pack.event_count <= 0)
      return { error: 'bad event_count' };
    if (typeof pack.jsonl_gzip_b64 !== 'string' || pack.jsonl_gzip_b64.length === 0)
      return { error: 'bad jsonl_gzip_b64' };
    if (!/^[A-Za-z0-9+/=]+$/.test(pack.jsonl_gzip_b64))
      return { error: 'bad jsonl_gzip_b64' };
    // Approximate the gzipped bytes without decoding: base64 is 4/3 the byte length.
    const approxBytes = Math.floor(pack.jsonl_gzip_b64.length * 3 / 4);
    if (approxBytes > MAX_BYTES) return { error: 'trace too large' };
    return { trace: {
      install_uuid:   pack.install_uuid,
      boot_id:        pack.boot_id,
      event_count:    pack.event_count,
      jsonl_gzip_b64: pack.jsonl_gzip_b64,
    } };
  }

  return { error: 'unknown route' };
}

// ── issue builder — sub-label per route ──
function buildIssue(kind, f) {
  const baseLabels = ['community-pack', 'needs-review'];

  if (kind === 'atlas') {
    const cats = [...new Set(f.objectives.map(o => o.category).filter(Boolean))].sort();
    const sample = f.names.slice(0, 10).map(([, v]) => '- `' + v + '`').join('\n');
    const full = JSON.stringify(
      { names: Object.fromEntries(f.names), objectives: f.objectives }, null, 2,
    ).slice(0, MAX_BYTES);
    const body = [
      `**${f.names.length} names, ${f.objectives.length} objectives** (auto-filtered)`,
      cats.length ? 'Categories used: ' + cats.map(c => '`' + c + '`').join(', ') : '',
      f.names.length ? '\nSample names:\n' + sample : '',
      '\n<details><summary>Full pack JSON</summary>\n\n```json\n' + full + '\n```\n</details>',
      '\n*Review, then label `approved` to fold into the next release (or close to reject).*',
    ].filter(Boolean).join('\n');
    return {
      title: `Community pack: ${f.names.length} names, ${f.objectives.length} objectives`,
      body,
      labels: baseLabels,
    };
  }

  if (kind === 'buffs') {
    const full = JSON.stringify(f, null, 2).slice(0, MAX_BYTES);
    const body = `**${f.buffs.length} buff paths** (auto-filtered)\n\n`
      + `<details><summary>Full pack JSON</summary>\n\n\`\`\`json\n${full}\n\`\`\`\n</details>\n\n`
      + '*Review, then label `approved` to fold into BuffCatalog.*';
    return {
      title: `Community pack (buffs): ${f.buffs.length} paths`,
      body,
      labels: [...baseLabels, 'buffs'],
    };
  }

  if (kind === 'preload') {
    const full = JSON.stringify(f, null, 2).slice(0, MAX_BYTES);
    const body = `**${f.preloads.length} preload paths** (auto-filtered; bare .dds/.ao rejected)\n\n`
      + `<details><summary>Full pack JSON</summary>\n\n\`\`\`json\n${full}\n\`\`\`\n</details>\n\n`
      + '*Review, then label `approved` to fold into PreloadCatalog.*';
    return {
      title: `Community pack (preload): ${f.preloads.length} paths`,
      body,
      labels: [...baseLabels, 'preload'],
    };
  }

  if (kind === 'trace') {
    const t = f.trace;
    const body =
      `**Campaign trace: ${t.event_count} events** (auto-filtered)\n\n`
      + `- install_uuid: \`${t.install_uuid}\`\n`
      + `- boot_id: \`${t.boot_id}\`\n`
      + `- gzipped bytes (approx): ${Math.floor(t.jsonl_gzip_b64.length * 3 / 4)}\n\n`
      + `<details><summary>Gzipped JSONL (base64)</summary>\n\n\`\`\`\n${t.jsonl_gzip_b64}\n\`\`\`\n</details>\n\n`
      + '*Review, then label `approved` to fold into `resources/campaign-traces/<install_uuid>/<boot_epoch_ms>.jsonl`.*';
    return {
      title:  `Community pack (trace): ${t.event_count} events`,
      body,
      labels: [...baseLabels, 'trace'],
    };
  }
}

function json(status, obj, extraHeaders) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { 'Content-Type': 'application/json', ...CORS, ...(extraHeaders ?? {}) },
  });
}

export default {
  async fetch(request, env) {
    if (request.method === 'OPTIONS') return new Response(null, { headers: CORS });
    if (request.method !== 'POST')    return json(405, { error: 'method not allowed' });

    const url = new URL(request.url);
    const route = routeFor(url);
    if (!route) return json(404, { error: 'unknown route' });

    const ip = request.headers.get('CF-Connecting-IP') || 'unknown';
    if (!(await rateLimit(env, ip))) {
      return json(429, { error: 'rate limit exceeded' }, { 'Retry-After': String(RATE_WINDOW_S) });
    }

    const body = await request.text();
    if (new TextEncoder().encode(body).length > MAX_BYTES)
      return json(413, { error: 'payload too large' });

    let pack;
    try { pack = JSON.parse(body); } catch { return json(400, { error: 'invalid json' }); }
    if (!pack || typeof pack !== 'object') return json(400, { error: 'invalid pack' });
    if (hasIdentifying(pack)) return json(400, { error: 'identifying field present' });

    const f = filterPayloadCommon(route.kind, pack);
    if (f.error) return json(400, { error: f.error });
    const nonEmpty = (route.kind === 'atlas'   && (f.names.length || f.objectives.length))
                  || (route.kind === 'buffs'   && f.buffs.length)
                  || (route.kind === 'preload' && f.preloads.length)
                  || (route.kind === 'trace'   && f.trace && f.trace.event_count > 0);
    if (!nonEmpty) return json(400, { error: 'nothing valid after filtering' });

    const gh = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${env.GITHUB_TOKEN}`,
        'Accept': 'application/vnd.github+json',
        'User-Agent': 'poe2gps-contribute-worker',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(buildIssue(route.kind, f)),
    });
    if (!gh.ok) return json(502, { error: 'github rejected', status: gh.status });

    const accepted = route.kind === 'atlas'
      ? { names: f.names.length, objectives: f.objectives.length }
      : route.kind === 'buffs' ? { buffs: f.buffs.length }
      : route.kind === 'preload' ? { preloads: f.preloads.length }
      : { trace_events: f.trace.event_count };
    return json(200, { ok: true, accepted });
  },
};
