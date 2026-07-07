// POE2GPS /map + /obs client (v0.20.0). See docs/superpowers/specs/2026-07-06-v0.20.0-map-60hz-clone-design.md.
// Vanilla ES2020, no framework, no build step.

(() => {
  'use strict';

  // --------- Constants (spec §6) ---------
  const RENDER_DELAY_MS = 66.67;   // 2 * (1000/30)
  const RING_SIZE = 6;             // ~200 ms history at 30 Hz
  const REVEAL_RADIUS_CELLS = 24;
  const COS = 0.780430;
  const SIN = 0.625243;
  const OFFSET_EMA_ALPHA = 0.2;

  // --------- Query-param handling for /obs?gps=1 ---------
  const params = new URLSearchParams(location.search);
  if (params.get('gps') === '1') document.body.classList.add('gps-mode');

  // --------- Persistent state ---------
  const state = {
    ring: [],                              // { t: server ms, snap: payload }
    serverOffset: 0,                       // sample.t - performance.now(), smoothed
    currentArea: null,                     // last-seen areaHash string
    zoom: parseInt(localStorage.getItem('zoom') || '4', 10),
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
  };

  function resizeCanvas() {
    const dpr = window.devicePixelRatio || 1;
    state.canvas.width  = Math.floor(window.innerWidth  * dpr);
    state.canvas.height = Math.floor(window.innerHeight * dpr);
    state.canvas.style.width  = window.innerWidth  + 'px';
    state.canvas.style.height = window.innerHeight + 'px';
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

  function onMessage(evt) {
    let snap;
    try { snap = JSON.parse(evt.data); }
    catch { return; }
    const clientT = performance.now();
    const rawOffset = snap.t - clientT;
    state.serverOffset = state.serverOffset === 0
      ? rawOffset
      : state.serverOffset + OFFSET_EMA_ALPHA * (rawOffset - state.serverOffset);
    state.ring.push({ t: snap.t, snap });
    if (state.ring.length > RING_SIZE) state.ring.shift();
    if (snap.area !== state.currentArea) onZoneChange(snap.area);
  }

  function findBracket(renderTime) {
    // Newest to oldest, return the first pair where a.t <= renderTime < b.t.
    // (Older-newer order; renderTime lags newest by RENDER_DELAY_MS so we usually
    //  land inside the ring.)
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
    state.rafId = requestAnimationFrame(frame);
    updateFps(now);
    if (state.ring.length === 0) return;

    const renderTime = performance.now() + state.serverOffset - RENDER_DELAY_MS;
    const bracket = findBracket(renderTime);
    if (!bracket) {
      // Not enough samples yet, or the ring hasn't advanced. Hold the newest pose.
      const newest = state.ring[state.ring.length - 1];
      draw({ player: newest.snap.player, snap: newest.snap });
      return;
    }
    const [a, b] = bracket;
    draw(lerpPose(a, b, renderTime));
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

  // --- T10: terrain + edges ---

  async function fetchTerrain(area) {
    const resp = await fetch('/api/map');
    if (!resp.ok) return null;
    const data = await resp.json();
    if (!data || data.areaHash !== area) return null;
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
    applyIsoTransform(c, cw / 2, ch / 2, state.zoom);
    c.drawImage(cvs, -playerX, -playerY);
    c.restore();
  }

  function draw(pose) {
    const c = state.ctx;
    const cw = state.canvas.clientWidth;
    const ch = state.canvas.clientHeight;

    // 1. Background veil (skipped on /obs)
    c.clearRect(0, 0, state.canvas.width, state.canvas.height);
    if (!document.body.classList.contains('obs')) {
      c.fillStyle = 'rgba(0,0,0,0.55)';
      c.fillRect(0, 0, cw, ch);
    }

    if (state.terrain) {
      // 2. Terrain interior
      blitCentredOnPlayer(state.terrain.interior, pose.player.x, pose.player.y);
      // 3. Terrain edges
      blitCentredOnPlayer(state.terrain.edges, pose.player.x, pose.player.y);
      // 4. Fog mask (T12 refines the reveal painting; here we blit whatever fogCanvas holds)
      if (state.fogCanvas && !state.gpsMode && !document.body.classList.contains('gps-mode')) {
        blitCentredOnPlayer(state.fogCanvas, pose.player.x, pose.player.y);
      }
    }

    // 10. Player marker (placeholder circle, T12 refines to facing triangle)
    c.fillStyle = '#ffffff';
    c.beginPath();
    c.arc(cw / 2, ch / 2, 4, 0, Math.PI * 2);
    c.fill();

    // 11. HUD text
    if (!document.body.classList.contains('obs')) {
      state.hud.textContent = `${state.currentArea || '—'} · ${(pose.snap.entities || []).length} dots · z${state.zoom} · ${currentFps()} fps`;
    }
  }

  async function onZoneChange(newArea) {
    state.currentArea = newArea;
    state.ring.length = 0;                       // stale samples belong to the old zone
    state.serverOffset = 0;
    state.terrain = null;
    state.fogCanvas = null;
    state.atlas = null;                          // T11 refetches
    state.landmarks = null;                      // T11 refetches
    const t = await fetchTerrain(newArea);
    if (!t) return;
    state.terrain = { areaHash: t.areaHash, w: t.w, h: t.h, interior: t.interior, edges: t.edges };
    state.fogCanvas = t.fog;
  }

  // --------- Lifecycle ---------

  function pause() { cancelAnimationFrame(state.rafId); state.rafId = 0; closeStream(); }
  function resume() { openStream(); state.rafId = requestAnimationFrame(frame); }

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) pause();
    else {
      state.ring.length = 0;    // clear stale samples on wake
      state.serverOffset = 0;   // re-seed clock offset from next sample
      resume();
    }
  });

  window.addEventListener('resize', resizeCanvas);

  window.addEventListener('load', () => {
    resizeCanvas();
    openStream();
    state.rafId = requestAnimationFrame(frame);
  });

  window.__poe2gpsBuildEdges = buildEdges;
})();
