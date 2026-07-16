// POE2GPS /map + /obs client (v0.20.0). See docs/superpowers/specs/2026-07-06-v0.20.0-map-60hz-clone-design.md.
// Vanilla ES2020, no framework, no build step.

(() => {
  'use strict';

  // --------- Constants (spec §6) ---------
  const RENDER_DELAY_MS = 66.67;   // 2 * (1000/30)
  const RING_SIZE = 6;             // ~200 ms history at 30 Hz
  let REVEAL_RADIUS_CELLS = 60;
  const COS = 0.780430;
  const SIN = 0.625243;
  const OFFSET_EMA_ALPHA = 0.2;

  // --------- v0.35 Stream-Safe Overlay Mode: DelayRingBuffer ---------
  // Strict FIFO with time-based dequeue. Never drops mid-sequence — the ordering guarantee is
  // what makes the full/delta interleave safe: SseChannel emits `full=true` as the first frame
  // after any AreaHash change (see SseChannel.cs:84-89), so a preserved-order dequeue always
  // seeds a new zone with a full snapshot before any deltas for that zone arrive at applyFrameToState.
  class DelayRingBuffer {
    constructor(delayMs) { this._delayMs = Math.max(0, delayMs|0); this._queue = []; }
    setDelayMs(ms) { this._delayMs = Math.max(0, ms|0); }
    getDelayMs()  { return this._delayMs; }
    push(frame, receivedAt) { this._queue.push({ receivedAt, frame }); }
    drainReady(now) {
      const out = [];
      while (this._queue.length && this._queue[0].receivedAt + this._delayMs <= now) {
        out.push(this._queue.shift().frame);
      }
      return out;
    }
    size() { return this._queue.length; }
    clear() { this._queue.length = 0; }
  }

  // --------- Query-param handling for /obs?gps=1 ---------
  const params = new URLSearchParams(location.search);

  // v0.35: bootstrap safe-mode delay buffer from <body class="obs safe-mode" data-safe-delay-sec="30">
  const _safeOn      = document.body.classList.contains('safe-mode');
  const _safeDelayMs = _safeOn ? Math.max(0, parseInt(document.body.dataset.safeDelaySec || '30', 10) * 1000) : 0;
  const _safeBuf     = _safeOn ? new DelayRingBuffer(_safeDelayMs) : null;
  const _safeMaskZone    = _safeOn && document.body.dataset.safeMaskZone === '1';
  const _safeHideoutBlur = _safeOn && document.body.dataset.safeHideoutBlur === '1';
  const _safeEntityNameFog = _safeOn && document.body.dataset.safeEntityNameFog === '1';

  // --------- Persistent state ---------
  const state = {
    ring: [],                              // { t: server ms, snap: payload }
    serverOffset: 0,                       // sample.t - performance.now(), smoothed
    currentArea: null,                     // last-seen areaHash string
    // v0.31 Prospector: mutable float zoom + cursor-anchored pan offset (wheel/keyboard drives both).
    zoom: Math.max(0.5, Math.min(32, parseFloat(localStorage.getItem('mapZoom') || localStorage.getItem('zoom') || '4'))),
    panX: 0,
    panY: 0,
    isoMode: localStorage.getItem('iso_mode') !== '0',  // default true (iso)
    gpsMode: (params.get('gps') === '1') || localStorage.getItem('gps_mode') === '1',
    lastFrameNow: 0,
    fpsWindow: [],
    hud: document.getElementById('hud'),
    canvas: document.getElementById('c'),
    ctx: null,
    es: null,                              // EventSource
    rafId: 0,
    // Filled in by T10.
    terrain: null,                         // { areaHash, w, h, interior: HTMLCanvasElement, edges: HTMLCanvasElement }
    fogCanvas: null,                       // HTMLCanvasElement, painted per reveal
    atlas: null,                           // /api/atlas response
    landmarks: null,                       // /landmarks response
    atlasIcons: {},                        // name → HTMLImageElement (already decoded)
    userIcons: new Map(),                  // v0.36 W2: iconKey -> HTMLImageElement (already decoded from /api/user-icons)
    // T8: persistent entity map, merged from full/delta frames.
    entities: new Map(),                   // id -> { id, x, y, cat, rar, hp, hpMax }
    isHideout:   false,
  };

  // Restore gps-mode class from either URL param (?gps=1) or last-session localStorage.
  // state.gpsMode ORs both paths — see state initializer above.
  if (state.gpsMode) document.body.classList.add('gps-mode');

  async function loadAtlasIcons() {
    const r = await fetch('/assets/atlas-icons.json');
    if (!r.ok) return;
    const bundle = await r.json();
    for (const [name, dataUri] of Object.entries(bundle)) {
      if (!dataUri || name.startsWith('_')) continue;
      await new Promise((res, rej) => {
        const img = new Image();
        img.onload = () => { state.atlasIcons[name] = img; res(); };
        img.onerror = rej;
        img.src = dataUri;
      });
    }
  }

  // v0.36 W2: precedence-string used as the state.userIcons Map key.
  // Manifest entries are indexed under EVERY key they can match, so
  // resolveEntityIcon is a plain Map lookup at frame time — no scanning.
  function iconKey(entry) {
    // Prefer the most specific key; a single manifest entry can be
    // reachable via metadataGlob OR cat.rar OR cat depending on which
    // fields it populates.
    if (entry.metadataGlob) return 'meta:' + entry.metadataGlob;
    if (entry.category && entry.rarity) return entry.category + '.' + entry.rarity;
    if (entry.category) return entry.category;
    return entry.name;
  }

  async function loadUserIcons() {
    const r = await fetch('/api/user-icons');
    if (!r.ok) return;
    const list = await r.json();
    if (!Array.isArray(list)) return;
    for (const entry of list) {
      if (!entry || !entry.dataUri) continue;
      await new Promise((res, rej) => {
        const img = new Image();
        img.onload  = () => { state.userIcons.set(iconKey(entry), img); res(); };
        img.onerror = rej;
        img.src = entry.dataUri;
      });
    }
  }

  // v0.36 W2: precedence-matching lookup. Order matches the C# overlay's
  // DisplayRule resolve order: metadata substring first, then cat.rar, then cat.
  // Returns an already-decoded HTMLImageElement or null (caller must fall back).
  function resolveEntityIcon(e) {
    if (state.userIcons.size === 0) return null;
    if (e.metadata) {
      for (const [k, img] of state.userIcons) {
        if (k.startsWith('meta:') && e.metadata.indexOf(k.slice(5)) >= 0) return img;
      }
    }
    if (e.cat && e.rar) {
      const hit = state.userIcons.get(e.cat + '.' + e.rar);
      if (hit) return hit;
    }
    if (e.cat) {
      const hit = state.userIcons.get(e.cat);
      if (hit) return hit;
    }
    return null;
  }

  function resizeCanvas() {
    // Defensive (v0.20.1): retry on next rAF if window dims aren't ready yet.
    // Tab restoration, portrait/landscape flip, multi-monitor drag mid-load, or
    // tab-suspend rehydration can all fire `load` with 0-size innerWidth/innerHeight.
    // Without this retry the canvas backing store is left 0x0, entities/terrain
    // silently clip against the empty backing, and the tab paints as pure body-bg
    // black with only the HUD overlay visible (see v0.20.0 tester feedback).
    const w = window.innerWidth;
    const h = window.innerHeight;
    if (w <= 0 || h <= 0) {
      requestAnimationFrame(resizeCanvas);
      return;
    }
    const dpr = window.devicePixelRatio || 1;
    state.canvas.width  = Math.floor(w * dpr);
    state.canvas.height = Math.floor(h * dpr);
    state.canvas.style.width  = w + 'px';
    state.canvas.style.height = h + 'px';
    state.ctx = state.canvas.getContext('2d');
    state.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }

  // --------- SSE ---------

  function openStream() {
    if (state.es) return;
    const es = new EventSource('/stream');
    state.es = es;
    es.addEventListener('message', onMessage);
    es.addEventListener('error', () => {
      // EventSource auto-reconnects — nothing to do except surface it briefly.
      state.hud.textContent = 'reconnecting…';
    });
  }

  function closeStream() {
    if (!state.es) return;
    state.es.close();
    state.es = null;
  }

  function applyFrameToState(snap) {
    const clientT = performance.now();
    const rawOffset = snap.t - clientT;
    state.serverOffset = state.serverOffset === 0
      ? rawOffset
      : state.serverOffset + OFFSET_EMA_ALPHA * (rawOffset - state.serverOffset);
    state.ring.push({ t: snap.t, snap });
    if (state.ring.length > RING_SIZE) state.ring.shift();

    // Delta-entity merge — see server T7 wire format.
    if (snap.full === true && snap.entities) {
      state.entities.clear();
      for (const e of snap.entities) state.entities.set(e.id, e);
    } else if (snap.full === false && snap.entitiesDelta) {
      const d = snap.entitiesDelta;
      if (d.add) for (const e of d.add) state.entities.set(e.id, e);
      if (d.upd) for (const e of d.upd) {
        const existing = state.entities.get(e.id);
        if (existing) { existing.x = e.x; existing.y = e.y; }
      }
      if (d.del) for (const id of d.del) state.entities.delete(id);
    }

    if (snap.area !== state.currentArea) onZoneChange(snap.area);

    state.isHideout = snap.isHideout === true;
  }

  function zoneDisplayName(area) {
    if (_safeMaskZone) return '<area>';
    return area; // upstream renderers may prefer a friendly name; keep as area hex for now.
  }

  function maybeBlurHideoutPose(pose) {
    if (_safeHideoutBlur && state.isHideout) return { x: 0, y: 0 };
    return pose;
  }

  function fogName(s) { return _safeEntityNameFog ? '???' : s; }

  function onMessage(evt) {
    let snap;
    try { snap = JSON.parse(evt.data); }
    catch { return; }
    // v0.35 safe-mode: wire frames are queued; the raf pump dequeues + re-clocks + applies.
    if (_safeBuf) { _safeBuf.push(snap, performance.now()); return; }
    applyFrameToState(snap);
  }

  function pumpSafeBuffer() {
    if (!_safeBuf) return;
    const now = performance.now();
    const ready = _safeBuf.drainReady(now);
    if (ready.length === 0) return;
    // Re-clock each dequeued frame so state.ring + findBracket + renderTime stay well-formed.
    // See spec DelayRingBuffer note: without this the interp bracket never resolves after 30 s.
    for (const frame of ready) {
      frame.t = now + state.serverOffset;
      applyFrameToState(frame);
    }
  }

  function findBracket(renderTime) {
    // Newest to oldest, return the first pair where a.t <= renderTime <= b.t.
    // Closed interval matches how ring samples arrive at sub-ms precision.
    const ring = state.ring;
    for (let i = ring.length - 1; i > 0; i--) {
      if (ring[i - 1].t <= renderTime && renderTime <= ring[i].t) {
        return [ring[i - 1], ring[i]];
      }
    }
    return null;
  }

  function lerpPose(a, b, renderTime) {
    const span = b.t - a.t;
    const u = span > 0 ? (renderTime - a.t) / span : 0;
    const pa = a.snap.player;
    const pb = b.snap.player;
    // Defensive (v0.20.1): if either endpoint lacks finite x/y (a not-yet-migrated
    // snap during a wire-format upgrade window, or a repeated-timestamp sample from
    // network flakiness), fall back to the newer pose unchanged instead of producing
    // NaN. Frame-level Number.isFinite guard is the safety net; this keeps the pose
    // usable when only the older sample is degraded.
    if (!Number.isFinite(pa.x) || !Number.isFinite(pa.y) ||
        !Number.isFinite(pb.x) || !Number.isFinite(pb.y)) {
      return { player: pb, snap: b.snap };
    }
    return {
      player: {
        x: pa.x + (pb.x - pa.x) * u,
        y: pa.y + (pb.y - pa.y) * u,
        hp: pb.hp, hpMax: pb.hpMax,
        es: pb.es, esMax: pb.esMax,
        mana: pb.mana, manaMax: pb.manaMax,
      },
      // Entities/monoliths use the newer snap b as-is; T11 refines this.
      snap: b.snap,
    };
  }

  // --------- rAF loop ---------

  function frame(now) {
    pumpSafeBuffer();
    state.rafId = requestAnimationFrame(frame);
    updateFps(now);
    if (state.ring.length === 0) return;

    // Defensive (v0.20.1): skip drawing when the canvas isn't sized yet. resizeCanvas
    // retries itself; this loop just waits. Prevents the "fully-black map with only
    // HUD visible" symptom the v0.20.0 tester saw when innerWidth was 0 at load.
    if (state.canvas.clientWidth <= 0 || state.canvas.clientHeight <= 0) return;

    const renderTime = performance.now() + state.serverOffset - RENDER_DELAY_MS;
    const bracket = findBracket(renderTime);
    let pose;
    if (bracket) pose = maybeBlurHideoutPose(lerpPose(bracket[0], bracket[1], renderTime));
    else pose = { player: state.ring[state.ring.length - 1].snap.player, snap: state.ring[state.ring.length - 1].snap };

    // Defensive (v0.20.1): if pose.player.x/y is non-finite, skip the frame instead of
    // painting entities at NaN coordinates (which silently no-ops the entire canvas
    // layer, producing the same fully-black tester symptom). lerpPose already tries
    // to recover; this is the final safety net.
    if (!Number.isFinite(pose.player.x) || !Number.isFinite(pose.player.y)) return;

    computeFogReveal(pose.player.x, pose.player.y);
    draw(pose);
  }

  function updateFps(now) {
    if (!state.lastFrameNow) { state.lastFrameNow = now; return; }
    const dt = now - state.lastFrameNow;
    state.lastFrameNow = now;
    state.fpsWindow.push(dt);
    if (state.fpsWindow.length > 60) state.fpsWindow.shift();
  }

  function currentFps() {
    if (state.fpsWindow.length < 2) return 0;
    const avg = state.fpsWindow.reduce((s, x) => s + x, 0) / state.fpsWindow.length;
    return avg > 0 ? Math.round(1000 / avg) : 0;
  }

  // --- T11/T10: terrain, atlas, landmarks ---

  async function fetchJson(url) {
    const r = await fetch(url);
    if (!r.ok) return null;
    return r.json();
  }

  async function fetchTerrain(area, abortIf) {
    // Defensive (v0.21.1): server returns {"ready":false} when the terrain
    // provider is wired but the current zone's walkable bitmap has not loaded
    // yet — race between the first SSE sample carrying the area code and the
    // world-thread's terrain callback becoming ready. Prior behaviour treated
    // the sentinel as a permanent mismatch (undefined areaHash !== area),
    // leaving state.terrain null forever; polylines + entities still rendered
    // via SSE, but the tester saw the "path visible, no map" symptom.
    // Poll briefly. abortIf() lets the caller short-circuit when the player
    // zones out mid-poll so we don't burn network on a stale area.
    const MAX_RETRIES = 20;    // 20 * 250ms = 5s max wait
    const RETRY_MS = 250;
    for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
      if (abortIf && abortIf()) return null;
      const resp = await fetch('/api/map');
      if (!resp.ok) return null;
      const data = await resp.json();
      if (data && data.ready === false) {
        await new Promise(r => setTimeout(r, RETRY_MS));
        continue;
      }
      // SIG-MAP-FIX (v0.23): /api/map ships areaHash as a JSON number; SSE ships area as a hex string
      // (uint.ToString("x")). Coerce the number to hex before comparing. Locked by
      // ApiMapAreaHashWireFormatTests on the C# side — do not drift.
      if (!data || data.areaHash === undefined || data.areaHash.toString(16) !== area) return null;
      const w = data.width, h = data.height;
      const walkable = base64ToUint8(data.walkable);
      if (walkable.length !== w * h) return null;

      // Interior canvas (raw walkable fill).
      const interior = document.createElement('canvas');
      interior.width = w; interior.height = h;
      paintFill(interior, walkable, w, h, [24, 22, 26, Math.round(0.85 * 255)]);

      // Edges canvas (Moore-neighborhood).
      const edgeMap = buildEdges(walkable, w, h);
      const edges = document.createElement('canvas');
      edges.width = w; edges.height = h;
      paintFill(edges, edgeMap, w, h, [255, 232, 180, Math.round(0.95 * 255)]);

      // Fog canvas — start fully unrevealed (T12 will progressively clear).
      const fog = document.createElement('canvas');
      fog.width = w; fog.height = h;
      const fctx = fog.getContext('2d');
      fctx.fillStyle = 'rgba(0,0,0,0.9)';
      fctx.fillRect(0, 0, w, h);

      return { areaHash: area, w, h, interior, edges, fog };
    }
    return null;
  }

  function base64ToUint8(b64) {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
  }

  // 8-neighbor Moore edge detection. Must byte-match TerrainBitmap.ComputeEdgesForTest.
  // A cell is edge if walkable[i]==1 AND any of its 8 neighbors (OOB counted as 0/wall) is 0.
  function buildEdges(walkable, w, h) {
    const out = new Uint8Array(w * h);
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        if (walkable[y * w + x] === 0) continue;
        let isEdge = false;
        for (let dy = -1; dy <= 1 && !isEdge; dy++) {
          const ny = y + dy;
          if (ny < 0 || ny >= h) { isEdge = true; break; }
          for (let dx = -1; dx <= 1; dx++) {
            if (dx === 0 && dy === 0) continue;
            const nx = x + dx;
            if (nx < 0 || nx >= w) { isEdge = true; break; }
            if (walkable[ny * w + nx] === 0) { isEdge = true; break; }
          }
        }
        out[y * w + x] = isEdge ? 1 : 0;
      }
    }
    return out;
  }

  // Paint one RGBA into every cell that maps to 1 in `mask`.
  function paintFill(cvs, mask, w, h, rgba) {
    const ctx = cvs.getContext('2d');
    const img = ctx.createImageData(w, h);
    const [r, g, b, a] = rgba;
    for (let i = 0; i < mask.length; i++) {
      if (mask[i] === 0) continue;
      const j = i * 4;
      img.data[j] = r; img.data[j + 1] = g; img.data[j + 2] = b; img.data[j + 3] = a;
    }
    ctx.putImageData(img, 0, 0);
  }

  // Clear cells within REVEAL_RADIUS_CELLS around the player from fogCanvas.
  // Called from frame() with the interpolated player pose.
  function computeFogReveal(playerX, playerY) {
    if (!state.fogCanvas) return;
    if (document.body.classList.contains('gps-mode')) return; // full map, no fog

    const fctx = state.fogCanvas.getContext('2d');
    // Paint transparent (destination-out) inside a disc centred on the player grid cell.
    const prev = fctx.globalCompositeOperation;
    fctx.globalCompositeOperation = 'destination-out';
    fctx.beginPath();
    fctx.arc(playerX, playerY, REVEAL_RADIUS_CELLS, 0, Math.PI * 2);
    fctx.fill(); // any fill color works with destination-out; alpha is what clears
    fctx.globalCompositeOperation = prev;
  }

  // Iso transform (matches MapProjection.GridDeltaToMapDelta):
  //   X = scale * (dx - dy) * COS
  //   Y = -scale * (dx + dy) * SIN
  // For setTransform(a, b, c, d, e, f): a=scale*COS, b=-scale*SIN, c=-scale*COS, d=-scale*SIN.
  function applyIsoTransform(ctx, centerX, centerY, mapScale) {
    if (!state.isoMode) {
      // Top-down: uniform scale, no shear.
      ctx.setTransform(mapScale, 0, 0, mapScale, centerX, centerY);
      return;
    }
    const a = mapScale * COS;
    const b = -mapScale * SIN;
    const c = -mapScale * COS;
    const d = -mapScale * SIN;
    ctx.setTransform(a, b, c, d, centerX, centerY);
  }

  // Blit an offscreen canvas centred on the player, with iso transform.
  function blitCentredOnPlayer(cvs, playerX, playerY) {
    const c = state.ctx;
    const cw = state.canvas.clientWidth;
    const ch = state.canvas.clientHeight;
    // Player at (playerX, playerY) → viewport centre. Terrain grid indexes into cvs at (px, py)
    // for that world; iso-transform centres it.
    c.save();
    applyIsoTransform(c, cw / 2 + state.panX, ch / 2 + state.panY, state.zoom);
    c.drawImage(cvs, -playerX, -playerY);
    c.restore();
  }

  // --------- T11: Entity, monolith, landmark, atlas, player draw layers ---------

  const PAL = {
    hostileNormal: '#ff4a4a',
    hostileMagic:  '#6a8bff',
    hostileRare:   '#ffd52e',
    hostileUnique: '#ff7a1a',
    friendly:      '#65b6ff',
    poi:           '#ffd85c',
    landmark:      '#f0e6c8',
    monolithRing:  '#c8a24a',
    monolithLabel: '#f0e6c8',
    pathBlue:      'rgba(190,210,255,0.9)',
  };

  function entityColor(cat, rar) {
    if (cat === 'hostile') {
      if (rar === 'unique') return { fill: PAL.hostileUnique, ring: PAL.hostileUnique };
      if (rar === 'rare')   return { fill: PAL.hostileRare,   ring: PAL.hostileRare };
      if (rar === 'magic')  return { fill: PAL.hostileMagic,  ring: null };
      return { fill: PAL.hostileNormal, ring: null };
    }
    if (cat === 'friendly' || cat === 'npc') return { fill: PAL.friendly, ring: null };
    if (cat === 'poi') return { fill: PAL.poi, ring: null };
    return { fill: PAL.hostileNormal, ring: null };
  }

  // World-grid delta → screen-space delta, iso.
  function worldToScreen(dx, dy, scale) {
    if (!state.isoMode) return [scale * dx, scale * dy];
    return [scale * (dx - dy) * COS, -scale * (dx + dy) * SIN];
  }

  function drawEntities(pose) {
    const c = state.ctx;
    const cw = state.canvas.clientWidth;
    const ch = state.canvas.clientHeight;
    const s = state.zoom;
    const px = pose.player.x, py = pose.player.y;
    const cx = cw / 2 + state.panX, cy = ch / 2 + state.panY;
    const halfSpan = Math.max(cw, ch) * 0.5;
    const ents = Array.from(state.entities.values());

    const r = Math.max(2, 3 * s);

    for (const e of ents) {
      // Client-side dead skip (defense in depth; server already filters).
      if (e.hp !== undefined && e.hp <= 0 && e.hpMax > 0) continue;

      const [sx, sy] = worldToScreen(e.x - px, e.y - py, s);
      const drawX = cx + sx, drawY = cy + sy;
      const col = entityColor(e.cat, e.rar);

      if (drawX < -halfSpan || drawX > cw + halfSpan || drawY < -halfSpan || drawY > ch + halfSpan)
        continue; // truly off-screen, way beyond arrow zone

      const onScreen = drawX >= 0 && drawX <= cw && drawY >= 0 && drawY <= ch;
      if (!onScreen) {
        drawOffScreenArrow(c, drawX, drawY, cx, cy, cw, ch, col.fill);
        continue;
      }

      const icon = resolveEntityIcon(e);
      if (icon && icon.width > 0) {
        const w = 2 * r, h = 2 * r;
        c.drawImage(icon, drawX - w / 2, drawY - h / 2, w, h);
      } else {
        c.fillStyle = col.fill;
        c.beginPath();
        c.arc(drawX, drawY, r, 0, Math.PI * 2);
        c.fill();
        if (col.ring) {
          c.strokeStyle = col.ring;
          c.lineWidth = 1;
          c.stroke();
        }
      }
      if (e.cat === 'poi') {
        // Crosshair overlay.
        c.strokeStyle = 'rgba(0,0,0,0.7)';
        c.lineWidth = 1;
        c.beginPath();
        c.moveTo(drawX - 3, drawY); c.lineTo(drawX + 3, drawY);
        c.moveTo(drawX, drawY - 3); c.lineTo(drawX, drawY + 3);
        c.stroke();
      }
    }
  }

  function drawOffScreenArrow(c, drawX, drawY, cx, cy, cw, ch, color) {
    // Clamp to viewport edge along the line from centre to entity.
    const dx = drawX - cx, dy = drawY - cy;
    const t = Math.min(cw / 2 / Math.abs(dx || 1), ch / 2 / Math.abs(dy || 1));
    const ex = cx + dx * t, ey = cy + dy * t;
    const ang = Math.atan2(dy, dx);
    c.save();
    c.translate(ex, ey);
    c.rotate(ang);
    c.fillStyle = color;
    c.beginPath();
    c.moveTo(0, 0);
    c.lineTo(-8, -4);
    c.lineTo(-8, 4);
    c.closePath();
    c.fill();
    c.restore();
  }

  function drawMonoliths(pose) {
    const c = state.ctx;
    const s = state.zoom;
    const px = pose.player.x, py = pose.player.y;
    const cx = state.canvas.clientWidth / 2 + state.panX, cy = state.canvas.clientHeight / 2 + state.panY;
    for (const m of pose.snap.monoliths || []) {
      const [sx, sy] = worldToScreen(m.x - px, m.y - py, s);
      const dx = cx + sx, dy = cy + sy;
      const dimmed = !!m.collected;
      c.strokeStyle = dimmed ? 'rgba(200,162,74,0.4)' : PAL.monolithRing;
      c.lineWidth = 1.5;
      c.beginPath();
      c.arc(dx, dy, 8, 0, Math.PI * 2);
      c.stroke();
      c.fillStyle = dimmed ? 'rgba(240,230,200,0.4)' : '#ffffff';
      c.font = '10px Consolas, monospace';
      c.textAlign = 'center'; c.textBaseline = 'middle';
      c.fillText(String(m.holes ?? '?'), dx, dy);
      if (!dimmed && fogName(m.bestName)) {
        c.fillStyle = PAL.monolithLabel;
        c.font = '11px Consolas, monospace';
        c.textAlign = 'center'; c.textBaseline = 'top';
        c.fillText(fogName(m.bestName), dx, dy + 11);
        if (m.bestEx > 0) {
          c.fillStyle = PAL.monolithRing;
          c.font = '10px Consolas, monospace';
          c.fillText(`${Math.round(m.bestEx)}ex`, dx, dy + 24);
        }
      }
    }
  }

  function drawLandmarks(pose) {
    if (!state.landmarks) return;
    const c = state.ctx;
    const s = state.zoom;
    const px = pose.player.x, py = pose.player.y;
    const cx = state.canvas.clientWidth / 2 + state.panX, cy = state.canvas.clientHeight / 2 + state.panY;
    for (const lm of (state.landmarks.landmarks || state.landmarks || [])) {
      if (typeof lm.x !== 'number' || typeof lm.y !== 'number') continue;
      const [sx, sy] = worldToScreen(lm.x - px, lm.y - py, s);
      const dx = cx + sx, dy = cy + sy;
      c.fillStyle = PAL.landmark;
      c.beginPath();
      c.moveTo(dx, dy - 5); c.lineTo(dx + 5, dy); c.lineTo(dx, dy + 5); c.lineTo(dx - 5, dy);
      c.closePath();
      c.fill();
      if (lm.name) {
        c.fillStyle = PAL.landmark;
        c.font = '12px Consolas, monospace';
        c.textAlign = 'left'; c.textBaseline = 'middle';
        c.shadowColor = 'rgba(0,0,0,0.7)'; c.shadowBlur = 0;
        c.shadowOffsetX = 1; c.shadowOffsetY = 1;
        c.fillText(lm.name, dx + 8, dy);
        c.shadowColor = 'transparent';
      }
    }
  }

  function drawAtlas(pose) {
    if (!state.atlas) return;
    const c = state.ctx;
    const s = state.zoom;
    const px = pose.player.x, py = pose.player.y;
    const cx = state.canvas.clientWidth / 2 + state.panX, cy = state.canvas.clientHeight / 2 + state.panY;
    for (const node of (state.atlas.nodes || [])) {
      const [sx, sy] = worldToScreen(node.x - px, node.y - py, s);
      const dx = cx + sx, dy = cy + sy;
      const icon = state.atlasIcons[node.icon];
      if (icon && icon.width > 0) {
        c.drawImage(icon, dx - 8, dy - 8, 16, 16);
      } else {
        c.fillStyle = PAL.poi;
        c.beginPath(); c.arc(dx, dy, 3, 0, Math.PI * 2); c.fill();
      }
    }
  }

  function drawPlayer() {
    const c = state.ctx;
    const cx = state.canvas.clientWidth / 2 + state.panX, cy = state.canvas.clientHeight / 2 + state.panY;

    // Sample the ring for heading.
    const heading = velocityHeading();
    c.fillStyle = '#ffffff';
    if (heading == null) {
      c.beginPath(); c.arc(cx, cy, 4, 0, Math.PI * 2); c.fill();
      return;
    }
    // Isosceles triangle 10 px long, pointing along heading.
    const [ax, ay] = worldToScreen(Math.cos(heading), Math.sin(heading), 1);
    const ang = Math.atan2(ay, ax); // convert to screen-space angle
    c.save();
    c.translate(cx, cy);
    c.rotate(ang);
    c.beginPath();
    c.moveTo(10, 0); c.lineTo(-5, -5); c.lineTo(-5, 5);
    c.closePath();
    c.fill();
    c.restore();
  }

  function velocityHeading() {
    const ring = state.ring;
    if (ring.length < 3) return null;
    const p0 = ring[ring.length - 3].snap.player;
    const p2 = ring[ring.length - 1].snap.player;
    const dx = p2.x - p0.x, dy = p2.y - p0.y;
    if (Math.sqrt(dx * dx + dy * dy) < 0.15) return null;
    return Math.atan2(dy, dx);
  }

  function drawPaths(pose) {
    if (!pose.snap.paths || !pose.snap.paths.length) return;
    const c = state.ctx;
    const s = state.zoom;
    const px = pose.player.x, py = pose.player.y;
    const cx = state.canvas.clientWidth / 2 + state.panX, cy = state.canvas.clientHeight / 2 + state.panY;
    c.save();
    c.strokeStyle = PAL.pathBlue;
    c.lineWidth = 2;
    c.lineCap = 'round';
    c.lineJoin = 'round';
    for (const p of pose.snap.paths) {
      if (!p.points || p.points.length < 2) continue;
      c.beginPath();
      const [first, ...rest] = p.points;
      const [sx0, sy0] = worldToScreen(first.x - px, first.y - py, s);
      c.moveTo(cx + sx0, cy + sy0);
      for (const pt of rest) {
        const [sx, sy] = worldToScreen(pt.x - px, pt.y - py, s);
        c.lineTo(cx + sx, cy + sy);
      }
      c.stroke();
    }
    c.restore();
  }

  function draw(pose) {
    const c = state.ctx;
    const cw = state.canvas.clientWidth;
    const ch = state.canvas.clientHeight;

    c.clearRect(0, 0, state.canvas.clientWidth, state.canvas.clientHeight);
    if (!document.body.classList.contains('obs')) {
      c.fillStyle = 'rgba(0,0,0,0.55)';
      c.fillRect(0, 0, cw, ch);
    }

    if (state.terrain) {
      blitCentredOnPlayer(state.terrain.interior, pose.player.x, pose.player.y);
      blitCentredOnPlayer(state.terrain.edges,    pose.player.x, pose.player.y);
      if (state.fogCanvas && !document.body.classList.contains('gps-mode')) {
        blitCentredOnPlayer(state.fogCanvas, pose.player.x, pose.player.y);
      }
    }

    drawPaths(pose);          // <-- layer 5, spec-mandated (between fog and landmarks)
    drawLandmarks(pose);
    drawAtlas(pose);
    drawEntities(pose);
    drawMonoliths(pose);
    drawPlayer();

    if (!document.body.classList.contains('obs')) {
      const ec = state.entities.size;
      state.hud.textContent = `${zoneDisplayName(state.currentArea || '—')} · ${ec} dots · ${state.isoMode ? 'iso' : 'top'} · z${state.zoom.toFixed(1)} · ${currentFps()} fps`;
      state.hud.classList.toggle('zone-label-masked', _safeMaskZone);
    }
  }

  let _zoneChangeToken = 0;
  async function onZoneChange(newArea) {
    const myToken = ++_zoneChangeToken;
    state.currentArea = newArea;
    state.ring.length = 0;                       // stale samples belong to the old zone
    state.serverOffset = 0;
    state.terrain = null;
    state.fogCanvas = null;
    state.atlas = null;
    state.landmarks = null;
    state.entities.clear();                      // flush entities on zone change

    const [t, atlas, landmarks] = await Promise.all([
      // v0.21.1: pass the zone-change token so fetchTerrain can stop retrying
      // the moment a newer zone starts loading — otherwise a 5s poll burns
      // network on a stale area.
      fetchTerrain(newArea, () => myToken !== _zoneChangeToken),
      fetchJson('/api/atlas').catch(() => null),
      fetchJson('/landmarks').catch(() => null),
    ]);

    // If another zone-change ran while we awaited, drop our results.
    if (myToken !== _zoneChangeToken) return;

    if (t) {
      state.terrain = { areaHash: t.areaHash, w: t.w, h: t.h, interior: t.interior, edges: t.edges };
      state.fogCanvas = t.fog;
    }
    state.atlas = atlas;
    state.landmarks = landmarks;
  }

  // --------- Lifecycle ---------

  function pause() { cancelAnimationFrame(state.rafId); state.rafId = 0; closeStream(); }
  function resume() { openStream(); state.rafId = requestAnimationFrame(frame); }

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) pause();
    else {
      state.ring.length = 0;    // clear stale samples on wake
      state.serverOffset = 0;   // re-seed clock offset from next sample
      // Defensive (v0.20.1): tab restoration from suspend or from-portrait-to-landscape
      // rotation can leave the canvas backing store stale — re-fire resizeCanvas so
      // dims are re-validated against current window.innerWidth/innerHeight.
      resizeCanvas();
      resume();
    }
  });

  window.addEventListener('resize', resizeCanvas);

  window.addEventListener('load', () => {
    resizeCanvas();
    loadAtlasIcons().catch(() => {}); // best-effort; empty bundle in v0.20.0 RC1
    loadUserIcons().catch(() => {});    // v0.36 W2: best-effort preload of /api/user-icons
    openStream();
    state.rafId = requestAnimationFrame(frame);
    
    // Fetch settings and update REVEAL_RADIUS_CELLS
    fetch('/api/settings')
      .then(response => response.json())
      .then(s => {
        if (typeof s.webMapRevealRadiusCells === 'number' && s.webMapRevealRadiusCells > 0) {
          REVEAL_RADIUS_CELLS = s.webMapRevealRadiusCells;
        }
      })
      .catch(() => {
        // Use default value if settings fetch fails
        REVEAL_RADIUS_CELLS = 60;
      });
  });

  function toggleGpsMode() {
    const on = !document.body.classList.contains('gps-mode');
    document.body.classList.toggle('gps-mode', on);
    localStorage.setItem('gps_mode', on ? '1' : '0');
  }

  const gpsBtn = document.getElementById('gpsToggle');
  if (gpsBtn) gpsBtn.addEventListener('click', toggleGpsMode);

  // v0.31 Prospector: mousewheel zoom with cursor-anchored pan correction. The pixel under the
  // cursor stays put as zoom changes — panX/panY absorb the delta so the viewport-center-based
  // draw pipeline still lands on the correct terrain point. Persists to localStorage.
  state.canvas.addEventListener('wheel', (e) => {
    e.preventDefault();
    const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
    const newZoom = Math.max(0.5, Math.min(32, state.zoom * factor));
    if (newZoom === state.zoom) return;
    const scaleRatio = newZoom / state.zoom;
    const rect = state.canvas.getBoundingClientRect();
    const cw = state.canvas.clientWidth;
    const ch = state.canvas.clientHeight;
    // Cursor coords relative to canvas top-left.
    const cx = e.clientX - rect.left;
    const cy = e.clientY - rect.top;
    state.panX += (cx - cw / 2 - state.panX) * (1 - scaleRatio);
    state.panY += (cy - ch / 2 - state.panY) * (1 - scaleRatio);
    state.zoom = newZoom;
    try { localStorage.setItem('mapZoom', newZoom.toFixed(2)); } catch (err) {}
  }, { passive: false });

  document.addEventListener('keydown', (e) => {
    if (e.repeat) return;
    const t = e.target;
    if (t && t.matches && t.matches('input, textarea, [contenteditable]')) return;
    if (e.key === 'g' || e.key === 'G') toggleGpsMode();
    // v0.31 Prospector: +/- keyboard zoom. Center-anchored (no pan correction).
    if (e.key === '+' || e.key === '=' || e.key === '-' || e.key === '_') {
      e.preventDefault();
      const factor = (e.key === '-' || e.key === '_') ? 1 / 1.15 : 1.15;
      const newZoom = Math.max(0.5, Math.min(32, state.zoom * factor));
      if (newZoom === state.zoom) return;
      state.zoom = newZoom;
      try { localStorage.setItem('mapZoom', newZoom.toFixed(2)); } catch (err) {}
    }
  });

  // --------- Session Recap PNG (obs-only) ---------

  function setupSessionRecap() {
    if (!document.body.classList.contains('obs')) return;   // /map view: skip entirely
    const btn = document.createElement('button');
    btn.id = 'sessRecapBtn';
    btn.textContent = '\u{1F4F8} Save Session PNG';
    btn.className = 'session-recap-btn';
    btn.title = 'Render a 1920\u00D71080 PNG of the current session and download';
    btn.onclick = generateSessionRecap;
    document.body.appendChild(btn);
  }

  async function generateSessionRecap() {
    // Themed palette hex (from /api/settings paletteColors, R1). Fallbacks are the pre-theming defaults.
    let _pc = null;
    try { const _s = await fetch('/api/settings'); if (_s.ok) _pc = (await _s.json())?.paletteColors ?? null; } catch { /* offline fallback */ }
    const PANEL  = _pc?.panel  ?? '#1a1e28';
    const ACCENT = _pc?.accent ?? '#e6d99c';
    const TEXT   = _pc?.text   ?? '#f0e8d0';
    const BORDER = _pc?.border ?? '#6a7080';

    // Fetch freshest session stats from /state.
    let s = {};
    try {
      const resp = await fetch('/state');
      if (resp.ok) {
        const data = await resp.json();
        s = data.session || {};
      }
    } catch (e) { /* use empty object */ }

    const W = 1920, H = 1080;
    const c = document.createElement('canvas');
    c.width = W; c.height = H;
    const ctx = c.getContext('2d');

    // Backdrop.
    ctx.fillStyle = '#0d0f14';
    ctx.fillRect(0, 0, W, H);

    // Header stripe.
    ctx.fillStyle = PANEL;
    ctx.fillRect(0, 0, W, 130);
    ctx.fillStyle = ACCENT;
    ctx.font = 'bold 56px sans-serif';
    ctx.fillText('POE2GPS \u00B7 Session Recap', 60, 88);

    // Wall of stats (2-column grid).
    const totalKills = (s.killsNormal ?? 0) + (s.killsMagic ?? 0) + (s.killsRare ?? 0) + (s.killsUnique ?? 0);
    const rows = [
      ['Total Kills',     totalKills || '\u2014'],
      ['Rare Kills',      s.killsRare ?? '\u2014'],
      ['Unique Kills',    s.killsUnique ?? '\u2014'],
      ['Deaths',          s.deaths ?? '\u2014'],
      ['Maps / hr',       fmtNum(s.mapsPerHour, 2)],
      ['XP Efficiency',   fmtNum(s.xpEfficiency, 2)],
      ['Zones Entered',   s.zonesEntered ?? '\u2014'],
      ['Session Length',  s.sessionElapsed ?? '\u2014'],
    ];

    ctx.font = '28px sans-serif';
    const col1X = 100, col2X = 640;
    const startY = 250, rowH = 66;
    rows.forEach((r, i) => {
      const half = Math.ceil(rows.length / 2);
      const row = i % half;
      const col = i < half ? 0 : 1;
      const x = col === 0 ? col1X : col1X + 900;
      const y = startY + row * rowH;
      ctx.fillStyle = '#8890a0';
      ctx.fillText(r[0], x, y);
      ctx.fillStyle = '#f0e8d0';
      ctx.fillText(String(r[1]), x + 340, y);
    });

    // Footer wordmark.
    ctx.fillStyle = BORDER;
    ctx.font = '20px sans-serif';
    ctx.fillText('github.com/luther-rotmg/POE2GPS', 60, H - 40);

    // Trigger download.
    c.toBlob(blob => {
      if (!blob) return;
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'poe2gps-session-' + Date.now() + '.png';
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }, 'image/png');
  }

  function fmtNum(n, digits) {
    if (n == null || n === '' || isNaN(n)) return '\u2014';
    return Number(n).toFixed(digits);
  }

  function fmtDurSec(sec) {
    if (sec == null || isNaN(sec)) return '\u2014';
    sec = Math.max(0, Math.floor(sec));
    const h = Math.floor(sec / 3600);
    const m = Math.floor((sec % 3600) / 60);
    const s = sec % 60;
    return (h > 0 ? h + 'h ' : '') + (m + 'm ') + s + 's';
  }

  setupSessionRecap();

  window.__poe2gpsBuildEdges = buildEdges;
})();
