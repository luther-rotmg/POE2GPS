// POE2GPS community-pack collector. Receives a non-identifying {names, objectives} pack and files it as
// a GitHub issue in luther-rotmg/POE2GPS. The GitHub token is a Worker SECRET (never in the client).
const REPO = 'luther-rotmg/POE2GPS';
const MAX_BYTES = 262144; // 256 KB
const CORS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
  'Access-Control-Allow-Headers': 'Content-Type',
};

export default {
  async fetch(request, env) {
    if (request.method === 'OPTIONS') return new Response(null, { headers: CORS });
    if (request.method !== 'POST') return json(405, { error: 'method not allowed' });

    const body = await request.text();
    if (body.length > MAX_BYTES) return json(413, { error: 'payload too large' });

    let pack;
    try { pack = JSON.parse(body); } catch { return json(400, { error: 'invalid json' }); }
    if (!pack || typeof pack !== 'object' || typeof pack.names !== 'object' || !Array.isArray(pack.objectives))
      return json(400, { error: 'expected {names:object, objectives:array}' });

    // Defense-in-depth: reject anything that smells identifying.
    const forbidden = ['charname', 'character', 'account', 'accountname', 'address', 'ip'];
    const keys = JSON.stringify(pack).toLowerCase();
    if (forbidden.some(k => keys.includes('"' + k + '"'))) return json(400, { error: 'identifying field present' });

    const nameCount = Object.keys(pack.names).length;
    const objCount = pack.objectives.length;
    const issue = {
      title: `Community pack: ${nameCount} names, ${objCount} objectives`,
      body: '```json\n' + JSON.stringify(pack, null, 2).slice(0, MAX_BYTES) + '\n```',
      labels: ['community-pack'],
    };

    const gh = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${env.GITHUB_TOKEN}`,
        'Accept': 'application/vnd.github+json',
        'User-Agent': 'poe2gps-contribute-worker',
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(issue),
    });
    if (!gh.ok) return json(502, { error: 'github rejected', status: gh.status });
    return json(200, { ok: true });
  },
};

function json(status, obj) {
  return new Response(JSON.stringify(obj), { status, headers: { 'Content-Type': 'application/json', ...CORS } });
}
