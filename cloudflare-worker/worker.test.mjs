// Unit tests for the CF-WORKER 3-sibling-route contribute Worker (v3).
// Covers: NFKD+leet profanity fold, path routing, preload bare-asset reject,
// and KV-counter rate limit 5/60s. Run via `node --test worker.test.mjs`.
import test from 'node:test';
import assert from 'node:assert/strict';
import { isProfane, filterPayloadCommon, routeFor, rateLimit } from './worker.js';

test('isProfane catches bare slur (baseline)', () => {
  assert.equal(isProfane('nigger'), true);
});

test('isProfane catches 5 leet-substitution inputs across the slur list', () => {
  // 4->a, 0->o, 1->i/l, 3->e, 5->s, 7->t, 8->b, $->s, @->a
  assert.equal(isProfane('n1gg3r'), true, '1->i, 3->e');
  assert.equal(isProfane('f@gg0t'), true, '@->a, 0->o');
  assert.equal(isProfane('r3t4rd'), true, '3->e, 4->a');
  assert.equal(isProfane('$pic'),   true, '$->s');
  assert.equal(isProfane('ch1nk'),  true, '1->i');
});

test('isProfane strips NFKD combining marks (zalgo bypass attempt)', () => {
  // "nigger" with combining diacritics interleaved (NFKD-decomposable)
  assert.equal(isProfane('ńìĝg̃ēr'), true);
});

test('isProfane keeps common PoE words clean (no false positives)', () => {
  for (const s of ['Ezomyte', 'The Twilight Strand', 'Kitava', 'Doedre', 'Atlas']) {
    assert.equal(isProfane(s), false, s);
  }
});

test('isProfane no longer applies 8/12-char gibberish gates (they were dropped)', () => {
  // A random 15-char label with high uniqueness the v2 isGibberish flagged.
  // isProfane must return false — length-based gates are gone.
  assert.equal(isProfane('xkq4bzvpmlrjynt'), false);
});

test('routeFor maps sibling routes + /submit legacy alias; other paths -> null', () => {
  assert.equal(routeFor(new URL('https://w.dev/submit-atlas')).kind,   'atlas');
  assert.equal(routeFor(new URL('https://w.dev/submit-buffs')).kind,   'buffs');
  assert.equal(routeFor(new URL('https://w.dev/submit-preload')).kind, 'preload');
  // Legacy v0.20.x alias: /submit routes to atlas so rollback clients stay working.
  assert.equal(routeFor(new URL('https://w.dev/submit')).kind,         'atlas');
  assert.equal(routeFor(new URL('https://w.dev/')),        null);
  assert.equal(routeFor(new URL('https://w.dev/nope')),    null);
});

test('filterPayloadCommon(atlas, ...) keeps v0.20-shape names+objectives', () => {
  const r = filterPayloadCommon('atlas', {
    names: { 'Metadata/Test/A': 'Alpha Name', 'Metadata/Test/B': 'Beta Name' },
    objectives: [
      { label: 'Kill Boss', category: 'boss', priority: 1, enabled: true },
      { label: 'nigger',    category: 'x' }, // profanity — must drop
    ],
  });
  assert.equal(r.names.length, 2);
  assert.equal(r.objectives.length, 1);
  assert.equal(r.objectives[0].label, 'Kill Boss');
});

test('filterPayloadCommon(buffs, ...) keeps valid tier + drops junk', () => {
  const r = filterPayloadCommon('buffs', {
    buffs: [
      { path: 'Metadata/Buffs/Charm/Speed', tier: 2 },
      { path: 'Metadata/Buffs/Aura/Grace',  tier: 'high' },
      { path: '',                           tier: 1 }, // empty path — drop
      { path: 'nigger',                     tier: 1 }, // profanity — drop
    ],
  });
  assert.equal(r.buffs.length, 2);
  assert.equal(r.buffs[0].tier, 2);
  assert.equal(r.buffs[1].tier, 'high');
});

test('/submit-preload rejects bare .dds / .ao paths', () => {
  const r = filterPayloadCommon('preload', {
    preloads: [
      { path: 'foo.dds',                       freq: 3 },  // bare — reject
      { path: 'thing.ao',                      freq: 1 },  // bare — reject
      { path: 'Metadata/Monsters/Goatman.ao',  freq: 9 },  // qualified — keep
    ],
  });
  assert.equal(r.preloads.length, 1);
  assert.equal(r.preloads[0].path, 'Metadata/Monsters/Goatman.ao');
});

// In-memory KV shim for rate-limit tests (no expiry — one fresh instance per test).
function fakeKv() {
  const store = new Map();
  return {
    async get(k) { return store.get(k) ?? null; },
    async put(k, v /* opts ignored */) { store.set(k, v); },
  };
}

test('rateLimit allows 5, blocks 6+', async () => {
  const env = { RATE_KV: fakeKv() };
  for (let i = 1; i <= 5; i++)
    assert.equal(await rateLimit(env, '1.2.3.4'), true, `req ${i}`);
  assert.equal(await rateLimit(env, '1.2.3.4'), false, 'req 6 blocked');
  assert.equal(await rateLimit(env, '1.2.3.4'), false, 'req 7 blocked');
  // different IP unaffected
  assert.equal(await rateLimit(env, '9.9.9.9'), true);
});

test('rateLimit fail-open when no KV binding (dev/tests without env)', async () => {
  assert.equal(await rateLimit({}, '1.2.3.4'), true);
  assert.equal(await rateLimit(null, '1.2.3.4'), true);
});

// ── Task 7 PROBE-CONTRIBUTE — /submit-trace as the FIFTH sibling route ──
// v0.21 /submit legacy alias MUST remain intact; trace is added AFTER preload, not replacing.
import { gzipSync } from 'node:zlib';

test('routeFor maps /submit-trace to kind:trace (fifth sibling route)', () => {
  assert.equal(routeFor(new URL('https://w.dev/submit-trace')).kind, 'trace');
  // Regression: v0.21 legacy alias untouched — /submit still routes to atlas.
  assert.equal(routeFor(new URL('https://w.dev/submit')).kind,         'atlas');
});

test('filterPayloadCommon(trace, ...) accepts a well-formed envelope', () => {
  const jsonl = Buffer.from(
    '{"event_type":"zone_entered","area_name":"Clearfell"}\n' +
    '{"event_type":"boss_encountered","boss_display_name":"Beira"}\n', 'utf8');
  const b64 = gzipSync(jsonl).toString('base64');
  const r = filterPayloadCommon('trace', {
    install_uuid:   '11111111-2222-4333-8444-555555555555',
    boot_id:        'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:    2,
    jsonl_gzip_b64: b64,
  });
  assert.equal(r.error, undefined);
  assert.equal(r.trace.event_count, 2);
  assert.equal(r.trace.install_uuid, '11111111-2222-4333-8444-555555555555');
});

test('filterPayloadCommon(trace, ...) rejects malformed install_uuid + missing fields', () => {
  const good_b64 = gzipSync(Buffer.from('{"event_type":"zone_entered"}\n')).toString('base64');
  assert.ok(filterPayloadCommon('trace', {}).error, 'empty pack');
  assert.ok(filterPayloadCommon('trace', {
    install_uuid: 'not-a-uuid', boot_id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count: 1, jsonl_gzip_b64: good_b64,
  }).error, 'bad install_uuid');
  assert.ok(filterPayloadCommon('trace', {
    install_uuid: '11111111-2222-4333-8444-555555555555',
    boot_id:      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:  0,   // must be > 0
    jsonl_gzip_b64: good_b64,
  }).error, 'zero event_count');
  assert.ok(filterPayloadCommon('trace', {
    install_uuid: '11111111-2222-4333-8444-555555555555',
    boot_id:      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:  1,
    jsonl_gzip_b64: 'not@base64!!',
  }).error, 'bad base64');
});

test('filterPayloadCommon(trace, ...) does NOT run profanity fold on gzipped bytes', () => {
  // Gzipped bytes are random-looking; the NFKD leet fold is a no-op here. Anything the client
  // COULD have typed as free-text was already sha256-hashed to 16 chars by the writer, so a
  // gzipped body that happens to contain slur-like byte patterns still passes filter — the
  // filter's job at this layer is envelope shape, not text scrubbing.
  const b64 = gzipSync(Buffer.from('irrelevant')).toString('base64');
  const r = filterPayloadCommon('trace', {
    install_uuid: '11111111-2222-4333-8444-555555555555',
    boot_id:      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    event_count:  1,
    jsonl_gzip_b64: b64,
  });
  assert.equal(r.error, undefined);
});
