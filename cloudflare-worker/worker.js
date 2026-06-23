// POE2GPS community-pack collector (v2). Receives a non-identifying {names, objectives} pack, AUTO-FILTERS
// junk (profanity / gibberish / over-length / dupes / identifying), and files clean submissions as a
// reviewable GitHub Issue (community-pack + needs-review) with a parsed summary. The GitHub token is a
// Worker SECRET (env.GITHUB_TOKEN) — never in the client.
const REPO = 'luther-rotmg/POE2GPS';
const MAX_BYTES = 262144; // 256 KB
const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type',
};
// Conservative profanity/slur list (word-boundary, case-insensitive). Keep it tight to avoid false
// positives on legit PoE terms; extend as the community surfaces abuse. (Maintainer-editable.)
const PROFANITY = ['nigger', 'faggot', 'retard', 'kike', 'spic', 'chink', 'cunt'];

export default {
  async fetch(request, env) {
    if (request.method === 'OPTIONS') return new Response(null, { headers: CORS });
    if (request.method !== 'POST') return json(405, { error: 'method not allowed' });

    const body = await request.text();
    if (new TextEncoder().encode(body).length > MAX_BYTES) return json(413, { error: 'payload too large' });

    let pack;
    try { pack = JSON.parse(body); } catch { return json(400, { error: 'invalid json' }); }
    if (!pack || typeof pack !== 'object'
        || typeof pack.names !== 'object' || Array.isArray(pack.names)
        || !Array.isArray(pack.objectives))
      return json(400, { error: 'expected {names:object, objectives:array}' });

    // Defense-in-depth: reject anything that smells identifying.
    const forbidden = ['charname', 'character', 'account', 'accountname', 'address', 'ip'];
    if (forbidden.some(k => JSON.stringify(pack).toLowerCase().includes('"' + k + '"')))
      return json(400, { error: 'identifying field present' });

    const f = filterPack(pack);
    if (f.names.length === 0 && f.objectives.length === 0)
      return json(400, { error: 'nothing valid after filtering' });

    const gh = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${env.GITHUB_TOKEN}`,
        'Accept': 'application/vnd.github+json',
        'User-Agent': 'poe2gps-contribute-worker',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(buildIssue(f)),
    });
    if (!gh.ok) return json(502, { error: 'github rejected', status: gh.status });
    return json(200, { ok: true, accepted: { names: f.names.length, objectives: f.objectives.length } });
  },
};

// ── filtering ──
function cleanLabel(raw, max) {
  if (typeof raw !== 'string') return null;
  const s = raw.trim();
  if (s.length < 2 || s.length > max) return null;
  if (!/[a-zA-Z]/.test(s)) return null;            // must contain a letter
  return s;
}
function isProfane(s) {
  const l = ' ' + s.toLowerCase() + ' ';
  return PROFANITY.some(w => new RegExp('\\b' + w + '\\b').test(l));
}
function isGibberish(s) {
  const t = s.replace(/[^a-zA-Z]/g, '');
  if (t.length >= 8 && !/[aeiou]/i.test(t)) return true;                     // long run, no vowels
  const uniq = new Set(t.toLowerCase()).size;
  if (t.length >= 12 && uniq / t.length > 0.85) return true;                 // near-random
  return false;
}
function filterPack(pack) {
  const seen = new Set();
  const names = [];
  for (const [meta, raw] of Object.entries(pack.names)) {
    const nm = cleanLabel(raw, 60);
    if (!nm || isProfane(nm) || isGibberish(nm)) continue;
    const key = String(meta).slice(0, 200);
    if (seen.has(key)) continue;
    seen.add(key);
    names.push([key, nm]);
  }
  const objectives = [];
  for (const o of (pack.objectives || [])) {
    if (!o || typeof o !== 'object') continue;
    const label = cleanLabel(o.label, 60);
    const category = cleanLabel(o.category, 40);
    if (!label && !category) continue;
    if ((label && (isProfane(label) || isGibberish(label))) || (category && isProfane(category))) continue;
    objectives.push({ ...o, label, category });
  }
  return { names, objectives };
}
function buildIssue(f) {
  const cats = [...new Set(f.objectives.map(o => o.category).filter(Boolean))].sort();
  const sample = f.names.slice(0, 10).map(([, v]) => '- `' + v + '`').join('\n');
  const full = JSON.stringify({ names: Object.fromEntries(f.names), objectives: f.objectives }, null, 2).slice(0, MAX_BYTES);
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
    labels: ['community-pack', 'needs-review'],
  };
}

function json(status, obj) {
  return new Response(JSON.stringify(obj), { status, headers: { 'Content-Type': 'application/json', ...CORS } });
}
