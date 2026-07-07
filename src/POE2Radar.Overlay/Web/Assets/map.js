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
    atlasIcons: {},                        // name → HTMLImageElement (already decoded)
  };

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

  // --- T11/T10: terrain, atlas, landmarks ---

  async function fetchJson(url) {
    const r = await fetch(url);
    if (!r.ok) return null;
    return r.json();
  }

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
    const cx = cw / 2, cy = ch / 2;
    const halfSpan = Math.max(cw, ch) * 0.5;
    const ents = pose.snap.entities || [];

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

      c.fillStyle = col.fill;
      c.beginPath();
      c.arc(drawX, drawY, r, 0, Math.PI * 2);
      c.fill();
      if (col.ring) {
        c.strokeStyle = col.ring;
        c.lineWidth = 1;
        c.stroke();
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
    const cx = state.canvas.clientWidth / 2, cy = state.canvas.clientHeight / 2;
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
      if (!dimmed && m.bestName) {
        c.fillStyle = PAL.monolithLabel;
        c.font = '11px Consolas, monospace';
        c.textAlign = 'center'; c.textBaseline = 'top';
        c.fillText(m.bestName, dx, dy + 11);
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
    const cx = state.canvas.clientWidth / 2, cy = state.canvas.clientHeight / 2;
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
    const cx = state.canvas.clientWidth / 2, cy = state.canvas.clientHeight / 2;
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
    const cx = state.canvas.clientWidth / 2, cy = state.canvas.clientHeight / 2;

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

  function draw(pose) {
    const c = state.ctx;
    const cw = state.canvas.clientWidth;
    const ch = state.canvas.clientHeight;

    c.clearRect(0, 0, state.canvas.width, state.canvas.height);
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

    drawLandmarks(pose);
    drawAtlas(pose);
    drawEntities(pose);
    drawMonoliths(pose);
    drawPlayer();

    if (!document.body.classList.contains('obs')) {
      const ec = (pose.snap.entities || []).length;
      state.hud.textContent = `${state.currentArea || '—'} · ${ec} dots · ${state.isoMode ? 'iso' : 'top'} · z${state.zoom} · ${currentFps()} fps`;
    }
  }

  async function onZoneChange(newArea) {
    state.currentArea = newArea;
    state.ring.length = 0;                       // stale samples belong to the old zone
    state.serverOffset = 0;
    state.terrain = null;
    state.fogCanvas = null;
    state.atlas = null;
    state.landmarks = null;

    const [t, atlas, landmarks] = await Promise.all([
      fetchTerrain(newArea),
      fetchJson('/api/atlas').catch(() => null),
      fetchJson('/landmarks').catch(() => null),
    ]);

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
      resume();
    }
  });

  window.addEventListener('resize', resizeCanvas);

  window.addEventListener('load', () => {
    resizeCanvas();
    loadAtlasIcons().catch(() => {}); // best-effort; empty bundle in v0.20.0 RC1
    openStream();
    state.rafId = requestAnimationFrame(frame);
  });

  window.__poe2gpsBuildEdges = buildEdges;
})();
