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

  // Stub — T10 fetches terrain / atlas / landmarks and paints layers.
  function draw(pose) {
    const c = state.ctx;
    c.clearRect(0, 0, state.canvas.width, state.canvas.height);
    const bg = document.body.classList.contains('obs') ? null : 'rgba(0,0,0,0.55)';
    if (bg) { c.fillStyle = bg; c.fillRect(0, 0, state.canvas.width, state.canvas.height); }
    // Player marker placeholder — T12 draws the real triangle/facing.
    c.fillStyle = '#ffffff';
    c.beginPath();
    c.arc(state.canvas.clientWidth / 2, state.canvas.clientHeight / 2, 4, 0, Math.PI * 2);
    c.fill();
    // HUD text
    if (!document.body.classList.contains('obs')) {
      const p = pose.player;
      state.hud.textContent = `${state.currentArea || '—'} · ${(pose.snap.entities || []).length} dots · z${state.zoom} · ${currentFps()} fps`;
    }
  }

  // Stub — T10/T14 refill terrain + fog + fetches on zone change.
  function onZoneChange(newArea) {
    state.currentArea = newArea;
    // T10 fills in: fetch(/api/map, /api/atlas, /landmarks), build interior/edges/fog canvases, flush ring.
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
})();
