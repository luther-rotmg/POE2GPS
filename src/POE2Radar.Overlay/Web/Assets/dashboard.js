
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
let state=null, zone=null;
let activeTab='filters';
let atlasData=null, atlasView='region', atlasSel=new Set(), atlasHl=null, atlasNav=null, atlasArrow=null, atlasHlSelOnly=false, atlasGroup='all';

/* ── tabs ── */
$$('.tab').forEach(t=>t.onclick=()=>{
  activeTab=t.dataset.tab;
  $$('.tab').forEach(x=>x.classList.toggle('on',x===t));
  $$('.view').forEach(v=>v.hidden = v.dataset.view!==activeTab);
  if(activeTab==='settings'){ loadSettings(); loadKeybinds(); loadQuickStart(); loadAffixCatalog(); renderBnObserved(); }
  if(activeTab==='filters') loadFilters();
  if(activeTab==='landmarks') loadLandmarks();
  if(activeTab==='atlas'){ if(!atlasData) loadAtlas(); else renderAtlas(); loadDynasty(); loadSettings(); loadAtlasGroups(); }
  if(activeTab==='director') loadDirector();
  if(activeTab==='entatlas') loadEntAtlas();
  if(activeTab==='gear') loadGear();
  if(activeTab==='nav') NavDestinations.renderHintOrList();
});

/* ── polling (left rail vitals/zone/census) ── */
async function getJSON(u){ const r=await fetch(u,{cache:'no-store'}); if(!r.ok) throw 0; return r.json(); }
function setConn(live){ $('#conn').classList.toggle('live',live); $('#connTxt').textContent = live?'live':'offline'; }

async function tick(){
  try{
    state = await getJSON('/state');
    setConn(true);
    try{ zone = await getJSON('/api/zone'); }catch(e){ zone=null; }
    renderState();
    if(state) updateDiscordPreview(state);
  }catch(e){ setConn(false); }
}

/* ── settings tab (writes radar/visual settings via the loopback-gated /api/settings) ── */
async function loadSettings(){
  try{
    const s = await getJSON('/api/settings');
    $$('[data-set]').forEach(el=>{
      const k=el.dataset.set;
      if(el.type==='checkbox') el.checked=!!s[k];
      else if(s[k]!==undefined) el.value=s[k];
    });
    hpBars = s.hpBars || null;
    terrain = s.terrain || null;
    gi = s.groundItems || {};
    ea = s.entityArrows || {};
    obsOvr = s.obsOverlay || {};
    discordPres = s.discordPresence || {};
    autoUpd = s.autoUpdate || { mode: 'silent' };
    renderHpBars(); renderTerrain(); renderGround();
    renderEntityArrows(); renderObsOverlay(); renderDiscordPresence(); renderLanInfo();
    renderAutoUpdate(s);
    const mc=document.getElementById('mapCopyUrl'); if(mc) mc.onclick=()=>{ navigator.clipboard.writeText(location.origin+'/map').catch(()=>{}); const t=mc.textContent; mc.textContent='Copied!'; setTimeout(()=>mc.textContent=t,1200); };
    an = await getJSON('/api/affix-nameplates').catch(()=>null); renderAffixNameplates();
    bn = await getJSON('/api/buff-nameplates').catch(()=>null); renderBuffNameplates();
    renderBnObserved();
    if(window._syncDiagPanel) window._syncDiagPanel();
    if(typeof syncContribVisibility === 'function') syncContribVisibility();
    if(typeof showProbeOnboardingIfNeeded === 'function') showProbeOnboardingIfNeeded(s);
  }catch(e){}
}

/* ── quick-start card (first-run onboarding) ── */
async function loadQuickStart(){
  try{
    const s=await getJSON('/api/settings');
    const card=$('#qsCard');
    if(card) card.hidden=!!s.firstRunSeen;
  }catch(e){}
}
function flashQs(msg){ const m=$('#savedMsgQs'); if(!m) return; m.textContent=msg||'✓ applied'; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1800); }
$('#qsApplyBtn')?.addEventListener('click',async()=>{
  try{
    const r=await fetch('/api/quickstart/apply',{method:'POST'});
    if(r.ok){ await loadSettings(); await loadQuickStart(); flashQs('✓ recommended setup applied'); }
    else flashQs('error');
  }catch(e){ flashQs('error'); }
});
$('#qsDismissBtn')?.addEventListener('click',async()=>{
  try{
    const r=await fetch('/api/quickstart/dismiss',{method:'POST'});
    if(r.ok){ await loadQuickStart(); await loadSettings(); }
    else flashQs('error');
  }catch(e){ flashQs('error'); }
});
$('#qsReopenBtn')?.addEventListener('click',()=>{
  const card=$('#qsCard'); if(card) card.hidden=false;
});

/* ── affix nameplates (own endpoint: POST the whole an object to /api/affix-nameplates) ── */
let an=null, anCatalog=[];
/* ── buff icons (own endpoint: POST the whole bn object to /api/buff-nameplates) ── */
let bn=null;
/* ── ground-item labels (nested object: POST the whole {groundItems}) ── */
let gi = null;
function renderGround(){
  if(!gi) return;
  $$('[data-gi]').forEach(el=>{
    const k=el.dataset.gi;
    if(el.type==='checkbox') el.checked=!!gi[k];
    else if(gi[k]!==undefined && gi[k]!==null) el.value=gi[k];
  });
  const cats=new Set((gi.categories||[]).map(c=>(c||'').toLowerCase()));
  $$('#giCats .chip').forEach(c=>c.classList.toggle('on', cats.has(c.dataset.gicat.toLowerCase())));
}
function saveGround(){ if(gi) saveSetting('groundItems', gi); }
function wireGround(){
  $$('[data-gi]').forEach(el=>{
    const k=el.dataset.gi;
    if(el.type==='checkbox') el.onchange=()=>{ gi=gi||{}; gi[k]=el.checked; saveGround(); };
    else if(el.type==='text') el.onchange=()=>{ gi=gi||{}; gi[k]=el.value.trim(); saveGround(); };
    else el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)){ gi=gi||{}; gi[k]=v; saveGround(); } };
  });
  $$('#giCats .chip').forEach(c=>c.onclick=()=>{
    c.classList.toggle('on');
    gi=gi||{};
    gi.categories=$$('#giCats .chip.on').map(x=>x.dataset.gicat);
    saveGround();
  });
}
async function saveSetting(key,val){
  try{
    await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({[key]:val})});
    const m=$('#savedMsg'); m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100);
  }catch(e){}
}
function wireSettings(){
  $$('[data-set]').forEach(el=>{
    const k=el.dataset.set;
    if(el.type==='checkbox') el.onchange=()=>saveSetting(k,el.checked);
    else if(el.tagName==='SELECT') el.onchange=()=>saveSetting(k,el.value);
    // SIG-VOLUME-FIX (v0.23): type='range' inputs (e.g. the audio-alert volume slider) also
    // need parseFloat coercion so the server-side TryInt guard in /api/settings does not silently
    // drop the value as a JSON string. Without this, the slider looked like it worked but
    // AudioAlertVolume was never updated and the audio-cue rebuild never fired.
    else if(el.type==='number' || el.type==='range'){ el.onchange=()=>{const v=parseFloat(el.value); if(!isNaN(v)) saveSetting(k,v);}; } else { el.onchange=()=>saveSetting(k, el.value); }
  });
}
/* ── icon / HP-bar / mechanics editors (nested objects: POST the whole {styles}/{hpBars}) ── */
let styles=null, hpBars=null, terrain=null;
const ICON_KEYS=[
  ['monsterNormal','Monster · Normal'],['monsterMagic','Monster · Magic'],
  ['monsterRare','Monster · Rare'],['monsterUnique','Monster · Unique'],
  ['player','Player'],['npc','NPC'],['chestRare','Chest · Rare'],
  ['chestUnique','Chest · Unique'],['transition','Transition'],
  ['poi','Point of Interest'],['landmark','Landmark']];
const esc=s=>(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
const pct=o=>Math.round((o==null?1:o)*100);

/* ── SVG icon library (served by /api/icons): drives both the in-page previews and the picker grid. ── */
let ICONS=[]; const ICONMAP={};
async function loadIcons(){
  try{ ICONS=await getJSON('/api/icons')||[]; }catch(e){ ICONS=[]; }
  for(const k in ICONMAP) delete ICONMAP[k];
  ICONS.forEach(d=>ICONMAP[(d.name||'').toLowerCase()]=d);
}
const iconDef=name=>ICONMAP[(name||'').toLowerCase()]||null;
function iconSvg(name,color){
  const d=iconDef(name); if(!d) return '';
  const c=color||'currentColor';
  return `<svg viewBox="${d.viewBox}" preserveAspectRatio="xMidYMid meet">`
    + (d.paths||[]).map(p=>`<path d="${esc(p)}" fill="${c}"/>`).join('') + `</svg>`;
}
function pickerHtml(name,color){
  const d=iconDef(name), nm=d?d.name:(name||'Circle');
  return `<span class="iconpick" data-val="${esc(nm)}"><span class="ipreview" style="color:${color||'var(--ink)'}">`
    + iconSvg(nm,color) + `</span><span class="ipname">${esc(nm)}</span><span class="ipcar">▼</span></span>`;
}
function refreshPicker(pk,name,color){
  const d=iconDef(name), nm=d?d.name:(name||'Circle');
  pk.dataset.val=nm;
  const pv=pk.querySelector('.ipreview'); pv.style.color=color||'var(--ink)'; pv.innerHTML=iconSvg(nm,color);
  pk.querySelector('.ipname').textContent=nm;
}
let _iconPop=null;
function ensureIconPop(){
  if(_iconPop) return _iconPop;
  _iconPop=document.createElement('div'); _iconPop.id='iconPop'; document.body.appendChild(_iconPop);
  document.addEventListener('mousedown',e=>{
    if(_iconPop.classList.contains('open') && !_iconPop.contains(e.target) && !e.target.closest('.iconpick')) _iconPop.classList.remove('open');
  });
  return _iconPop;
}
function openIconPicker(anchor,current,cb){
  const pop=ensureIconPop();
  pop.innerHTML='<div class="ipop-grid">'+ICONS.map(d=>
    `<div class="ipop-cell${d.name.toLowerCase()===(current||'').toLowerCase()?' sel':''}" data-n="${esc(d.name)}" title="${esc(d.name)}">`
    + iconSvg(d.name) + `<span class="cn">${esc(d.name)}</span></div>`).join('')+'</div>';
  pop.querySelectorAll('.ipop-cell').forEach(c=>c.onclick=()=>{ pop.classList.remove('open'); cb(c.dataset.n); });
  pop.classList.add('open');
  const r=anchor.getBoundingClientRect(), pw=pop.offsetWidth, ph=pop.offsetHeight;
  let left=Math.min(r.left, innerWidth-8-pw), top=r.bottom+4;
  if(top+ph>innerHeight-8) top=Math.max(8, r.top-4-ph);
  pop.style.left=Math.max(8,left)+'px'; pop.style.top=top+'px';
}
const saveStyles=()=>{ if(styles) saveSetting('styles',styles); };
const saveHpBars=()=>{ if(hpBars) saveSetting('hpBars',hpBars); };

function renderHpBars(){
  if(!hpBars) return;
  $$('[data-hp]').forEach(el=>{ if(hpBars[el.dataset.hp]!==undefined) el.value=hpBars[el.dataset.hp]; });
  $$('[data-hpcolor]').forEach(el=>{ el.value=hpBars[el.dataset.hpcolor]||'#ffffff'; });
}
function wireHpBars(){
  $$('[data-hp]').forEach(el=>{ el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)&&hpBars){ hpBars[el.dataset.hp]=v; saveHpBars(); } }; });
  $$('[data-hpcolor]').forEach(el=>{ el.onchange=()=>{ if(hpBars){ hpBars[el.dataset.hpcolor]=el.value; saveHpBars(); } }; });
}

/* ── terrain color/transparency (POSTs the whole {terrain} object; rebuilds the terrain bitmap) ── */
const saveTerrain=()=>{ if(terrain) saveSetting('terrain',terrain); };
function renderTerrain(){
  if(!terrain) return;
  $$('[data-tcolor]').forEach(el=>{ el.value=terrain[el.dataset.tcolor]||'#ffffff'; });
  $$('[data-topacity]').forEach(el=>{ el.value=Math.round((terrain[el.dataset.topacity]??1)*100); });
  $$('[data-topv]').forEach(el=>{ el.textContent=Math.round((terrain[el.dataset.topv]??1)*100)+'%'; });
}
function wireTerrain(){
  $$('[data-tcolor]').forEach(el=>{ el.onchange=()=>{ if(terrain){ terrain[el.dataset.tcolor]=el.value; saveTerrain(); } }; });
  $$('[data-topacity]').forEach(el=>{
    const k=el.dataset.topacity, v=$(`[data-topv="${k}"]`);
    el.oninput=()=>{ if(v) v.textContent=el.value+'%'; };
    el.onchange=()=>{ if(terrain){ terrain[k]=(+el.value)/100; saveTerrain(); } };
  });
}

function iconRow(key,label,o){
  return `<div class="stylerow" data-k="${key}">
    <label class="sw"><input type="checkbox" class="i-en"${o.enabled?' checked':''}><span class="track"></span><span class="knob"></span></label>
    <span class="nm">${label}</span>
    ${pickerHtml(o.shape,o.color)}
    <input type="color" class="i-color" value="${o.color||'#ffffff'}">
    <input type="range" class="op i-op" min="0" max="100" value="${pct(o.opacity)}">
    <span class="opv">${pct(o.opacity)}%</span>
    <input type="number" class="numin sz i-size" step="0.1" min="0.5" value="${o.size}">
  </div>`;
}
function renderIcons(){
  if(!styles){ $('#iconStyles').innerHTML=''; return; }
  $('#iconStyles').innerHTML=ICON_KEYS.map(([k,l])=>iconRow(k,l,styles[k]||{})).join('');
  $$('#iconStyles .stylerow').forEach(row=>{
    const o=styles[row.dataset.k]; if(!o) return;
    const pk=row.querySelector('.iconpick');
    row.querySelector('.i-en').onchange=e=>{ o.enabled=e.target.checked; saveStyles(); };
    pk.onclick=()=>openIconPicker(pk,o.shape,n=>{ o.shape=n; refreshPicker(pk,n,o.color); saveStyles(); });
    row.querySelector('.i-color').onchange=e=>{ o.color=e.target.value; refreshPicker(pk,o.shape,o.color); saveStyles(); };
    const op=row.querySelector('.i-op'), opv=row.querySelector('.opv');
    op.oninput=()=>{ opv.textContent=op.value+'%'; };
    op.onchange=()=>{ o.opacity=(+op.value)/100; saveStyles(); };
    row.querySelector('.i-size').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ o.size=v; saveStyles(); } };
  });
}

/* Entity categories a mechanic rule can be gated to (value = Poe2Live.EntityCategory name). Empty
   selection = applies to every category. Labels are friendlier than the raw enum names. */
const MECH_CATS=[['Monster','Monsters'],['Chest','Chests'],['Other','Misc / POI'],
  ['Object','Terrain'],['Npc','NPCs'],['Transition','Transitions']];
function mechRow(m,i){
  const cats=m.categories||[];
  return `<div class="mechrow" data-i="${i}">
    <div class="top">
      <label class="sw"><input type="checkbox" class="m-en"${m.enabled?' checked':''}><span class="track"></span><span class="knob"></span></label>
      <input class="mname" placeholder="Name (e.g. Expedition)" value="${esc(m.name)}">
      <button class="delbtn m-del">Remove</button>
    </div>
    <input class="matchin m-match" placeholder="match terms, comma-separated (e.g. Strongbox, StrongBoxes)" value="${esc((m.match||[]).join(', '))}">
    <div class="mcats"><span class="mcats-lbl">Applies to</span>${MECH_CATS.map(([v,l])=>
      `<label class="catchip${cats.includes(v)?' on':''}"><input type="checkbox" class="m-cat" data-cat="${v}"${cats.includes(v)?' checked':''}>${l}</label>`).join('')}
      <span class="mcats-hint">${cats.length?'':'all types'}</span></div>
    <div class="ctl">
      ${pickerHtml(m.shape,m.color)}
      <input type="color" class="m-color" value="${m.color||'#ffffff'}">
      <input type="range" class="op m-op" min="0" max="100" value="${pct(m.opacity)}">
      <span class="opv">${pct(m.opacity)}%</span>
      <input type="number" class="numin sz m-size" step="0.1" min="0.5" value="${m.size}">
    </div>
  </div>`;
}
function renderMechanics(){
  if(!styles){ $('#mechList').innerHTML=''; return; }
  styles.mechanics=styles.mechanics||[];
  $('#mechList').innerHTML=styles.mechanics.map((m,i)=>mechRow(m,i)).join('');
  $$('#mechList .mechrow').forEach(row=>{
    const m=styles.mechanics[+row.dataset.i]; if(!m) return;
    const pk=row.querySelector('.iconpick');
    row.querySelector('.m-en').onchange=e=>{ m.enabled=e.target.checked; saveStyles(); };
    row.querySelector('.mname').onchange=e=>{ m.name=e.target.value; saveStyles(); };
    row.querySelector('.m-match').onchange=e=>{ m.match=e.target.value.split(',').map(s=>s.trim()).filter(Boolean); saveStyles(); };
    row.querySelectorAll('.m-cat').forEach(cb=>{ cb.onchange=()=>{
      m.categories=[...row.querySelectorAll('.m-cat:checked')].map(c=>c.dataset.cat);
      cb.closest('.catchip').classList.toggle('on',cb.checked);
      const h=row.querySelector('.mcats-hint'); if(h) h.textContent=m.categories.length?'':'all types';
      saveStyles(); }; });
    pk.onclick=()=>openIconPicker(pk,m.shape,n=>{ m.shape=n; refreshPicker(pk,n,m.color); saveStyles(); });
    row.querySelector('.m-color').onchange=e=>{ m.color=e.target.value; refreshPicker(pk,m.shape,m.color); saveStyles(); };
    const op=row.querySelector('.m-op'), opv=row.querySelector('.opv');
    op.oninput=()=>{ opv.textContent=op.value+'%'; };
    op.onchange=()=>{ m.opacity=(+op.value)/100; saveStyles(); };
    row.querySelector('.m-size').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ m.size=v; saveStyles(); } };
    row.querySelector('.m-del').onclick=()=>{ styles.mechanics.splice(+row.dataset.i,1); renderMechanics(); saveStyles(); };
  });
}
/* ── Rules tab: unified Display Rules + Hidden cull patterns ── */
let hidden=[], drules=[];
function flashF(){ const m=$('#savedMsgF'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100); }
async function postHidden(body){ try{ await fetch('/api/hidden',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}); flashF(); }catch(e){} }
async function loadFilters(){
  await loadModVocab();   // populate the mods autocomplete BEFORE rendering rule rows reference it
  await loadLabelVocab(); // populate the match-field autocomplete so 'Breach', 'Ritual', 'Boss'… suggest as-you-type
  await loadDrules();
  try{ const h=await getJSON('/api/hidden'); hidden=h.patterns||[]; }catch(e){ hidden=[]; }
  renderHidden();
}
/* The persistent monster-mod catalog feeds the <datalist> the Mods matcher autocompletes against, so
   you can pick a known aura/buff id instead of recalling it. Refreshed each time the Rules tab loads. */
async function loadModVocab(){
  let mods=[]; try{ const r=await getJSON('/api/mods'); mods=(r&&r.mods)||[]; }catch(_){ mods=[]; }
  let dl=document.getElementById('modVocab');
  if(!dl){ dl=document.createElement('datalist'); dl.id='modVocab'; document.body.appendChild(dl); }
  dl.innerHTML=mods.map(m=>`<option value="${esc(m)}">`).join('');
}
async function loadLabelVocab(){
  let dl=document.getElementById('labelVocab');
  if(!dl){ dl=document.createElement('datalist'); dl.id='labelVocab'; document.body.appendChild(dl); }
  let groups={};
  try{ groups=await getJSON('/api/labels'); }catch(e){}
  const labels=[]; Object.values(groups).forEach(arr=>(arr||[]).forEach(l=>labels.push(l)));
  dl.innerHTML = labels.map(l=>'<option value="'+esc(l)+'"></option>').join('');
}

/* ── Display Rules: the unified ordered ruleset. The page holds the array, edits it, and re-POSTs
   the WHOLE list on any change (add / remove / reorder / toggle / field) — same pattern styles used. ── */
const DR_CATS=['Monster','Chest','Npc','Object','Other','Transition','Player','Tile'];
const DR_SELECTS=[['rarity','Rarity',['Normal','Magic','Rare','Unique']],['reaction','Reaction',['Hostile','Friendly']],
  ['life','Life',['Alive','Dead']],['chest','Chest',['Opened','Unopened']],['poi','POI',['Yes','No']],['encounter','Encounter',['Active','Complete']]];
async function saveDrules(){ try{ await fetch('/api/display-rules',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({rules:drules})}); flashF(); }catch(e){} }
async function loadDrules(){ try{ const r=await getJSON('/api/display-rules'); drules=r.rules||[]; }catch(e){ drules=[]; } renderDrules(); }
function drSel(f,l,o,cur){ return `<label class="drsel">${l}<select class="dr-cond" data-f="${f}"><option value=""${!cur?' selected':''}>any</option>`
  +o.map(x=>`<option${cur===x?' selected':''}>${x}</option>`).join('')+`</select></label>`; }
/* Concise matcher→action summary shown on the collapsed row so the list stays scannable. */
function drSummary(r){
  const p=[];
  p.push((r.categories&&r.categories.length)?r.categories.join('/'):'any type');
  if(r.match&&r.match.length) p.push('“'+r.match.join(', ')+'”');
  if(r.mods&&r.mods.length) p.push('mods: '+r.mods.join(', '));
  ['rarity','reaction','life','chest','poi','encounter'].forEach(f=>{ if(r[f]) p.push(r[f]); });
  return esc(p.join(' · '));
}
function drRow(r,i){
  const open=!!r._open, cats=r.categories||[];
  const badges=(r.hide?'<span class="drbadge hide">hide</span>':'')
    +(r.navigable?'<span class="drbadge">path</span>':'');
  const body=open?`<div class="drbody">
      <div class="top"><input class="mname dr-name" value="${esc(r.name)}" placeholder="rule name"></div>
      <input class="matchin dr-match" list="labelVocab" placeholder="match: metadata terms, comma-separated (blank = any) — try Breach, Expedition, Ritual, Boss…" value="${esc((r.match||[]).join(', '))}">
      <input class="matchin dr-mods" list="modVocab" placeholder="monster mods: aura/buff terms, comma-separated (e.g. Aura, ManaSiphon) — blank = any" value="${esc((r.mods||[]).join(', '))}">
      <div class="mcats"><span class="mcats-lbl">Type</span>${DR_CATS.map(c=>
        `<label class="catchip${cats.includes(c)?' on':''}"><input type="checkbox" class="dr-cat" data-cat="${c}"${cats.includes(c)?' checked':''}>${c}</label>`).join('')}</div>
      <div class="drconds">${DR_SELECTS.map(([f,l,o])=>drSel(f,l,o,r[f])).join('')}</div>
      <div class="ctl">
        <label class="drflag dr-hideflag" title="hide matching entities entirely"><input type="checkbox" class="dr-hide"${r.hide?' checked':''}> Hide</label>
        ${pickerHtml(r.shape,r.color)}
        <input type="color" class="dr-color" value="${r.color||'#ffffff'}">
        <input type="range" class="op dr-op" min="0" max="100" value="${pct(r.opacity)}"><span class="opv">${pct(r.opacity)}%</span>
        <input type="number" class="numin sz dr-size" step="0.1" min="0.5" value="${r.size}">
        <input class="mname dr-label" style="flex:1;min-width:70px" value="${esc(r.label||'')}" placeholder="label (optional)">
        <label class="drflag" title="qualify as an auto-path navigation target"><input type="checkbox" class="dr-nav"${r.navigable?' checked':''}> Auto-path</label>
        <label class="drflag" title="draw an edge arrow when this entity is off-screen"><input type="checkbox" class="dr-arrow"${r.offScreenArrow?' checked':''}> Arrow</label>
      </div>
    </div>`:'';
  return `<div class="mechrow drrow${r.hide?' hideon':''}${open?' open':''}${r.enabled?'':' off'}" data-i="${i}">
    <div class="drhead">
      <label class="sw" title="enabled"><input type="checkbox" class="dr-en"${r.enabled?' checked':''}><span class="track"></span><span class="knob"></span></label>
      <span class="drcaret">${open?'▾':'▸'}</span>
      <span class="drswatch" style="color:${r.color||'#fff'}">${r.hide?'':iconSvg(r.shape,r.color)}</span>
      <span class="drnm">${esc(r.name||'(unnamed)')}</span>
      <span class="drsum">${drSummary(r)}</span>
      <span class="drbadges">${badges}</span>
      <span class="drord"><button class="ordbtn dr-up" title="higher precedence">▲</button><button class="ordbtn dr-dn" title="lower precedence">▼</button></span>
      <button class="delbtn dr-del" title="remove">✕</button>
    </div>
    ${body}
  </div>`;
}
function renderDrules(){
  const host=$('#drList'); if(!host) return;
  host.innerHTML = drules.length ? drules.map(drRow).join('') : '<div class="row"><div class="rl hint-row">No display rules yet. Add one below.</div></div>';
  $$('#drList .drrow').forEach(row=>{
    const i=+row.dataset.i, r=drules[i]; if(!r) return;
    const save=saveDrules;
    // Header (always present): click anywhere except a control toggles expand.
    row.querySelector('.drhead').onclick=e=>{ if(e.target.closest('input,button,select,label,.drord')) return; r._open=!r._open; renderDrules(); };
    row.querySelector('.dr-en').onchange=e=>{ r.enabled=e.target.checked; row.classList.toggle('off',!r.enabled); save(); };
    row.querySelector('.dr-up').onclick=()=>{ if(i>0){ const t=drules[i-1]; drules[i-1]=drules[i]; drules[i]=t; renderDrules(); save(); } };
    row.querySelector('.dr-dn').onclick=()=>{ if(i<drules.length-1){ const t=drules[i+1]; drules[i+1]=drules[i]; drules[i]=t; renderDrules(); save(); } };
    row.querySelector('.dr-del').onclick=()=>{ drules.splice(i,1); renderDrules(); save(); };
    if(!r._open) return; // body controls only exist when expanded
    const pk=row.querySelector('.iconpick');
    row.querySelector('.dr-name').onchange=e=>{ r.name=e.target.value; save(); };
    row.querySelector('.dr-match').onchange=e=>{ r.match=e.target.value.split(',').map(s=>s.trim()).filter(Boolean); save(); };
    row.querySelector('.dr-mods').onchange=e=>{ r.mods=e.target.value.split(',').map(s=>s.trim()).filter(Boolean); save(); };
    row.querySelectorAll('.dr-cat').forEach(cb=>cb.onchange=()=>{ r.categories=[...row.querySelectorAll('.dr-cat:checked')].map(c=>c.dataset.cat); cb.closest('.catchip').classList.toggle('on',cb.checked); save(); });
    row.querySelectorAll('.dr-cond').forEach(sel=>sel.onchange=()=>{ r[sel.dataset.f]=sel.value||null; save(); });
    row.querySelector('.dr-hide').onchange=e=>{ r.hide=e.target.checked; row.classList.toggle('hideon',r.hide); save(); };
    pk.onclick=()=>openIconPicker(pk,r.shape,n=>{ r.shape=n; refreshPicker(pk,n,r.color); save(); });
    row.querySelector('.dr-color').onchange=e=>{ r.color=e.target.value; refreshPicker(pk,r.shape,r.color); save(); };
    const op=row.querySelector('.dr-op'),opv=row.querySelector('.opv'); op.oninput=()=>opv.textContent=op.value+'%'; op.onchange=()=>{ r.opacity=(+op.value)/100; save(); };
    row.querySelector('.dr-size').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ r.size=v; save(); } };
    row.querySelector('.dr-label').onchange=e=>{ r.label=e.target.value; save(); };
    row.querySelector('.dr-nav').onchange=e=>{ r.navigable=e.target.checked; save(); };
    row.querySelector('.dr-arrow').onchange=e=>{ r.offScreenArrow=e.target.checked; save(); };
  });
}
$('#drAdd')?.addEventListener('click',()=>{ drules.push({enabled:true,name:'New rule',categories:[],match:[],shape:'Circle',color:'#ffd926',opacity:1,size:4,_open:true}); renderDrules(); saveDrules(); });

/* ── Add-rule picker: browse the area's live ENTITIES + terrain TILE names + monster MODS, filter,
   click to seed a rule (entity → entity rule by category; tile → Tile rule; mod → Monster rule whose
   Mods matcher targets that affix id). Removes the guesswork of typing metadata/mod ids. ── */
let _pickEl=null, _pickEnts=[], _pickTiles=[], _pickKind='all', _pickQ='';
const lastSeg=s=>((s||'').split('/').pop()||'').replace(/@\d+$/,'').replace(/\.tdt$/i,'');
function ensurePick(){
  if(_pickEl) return _pickEl;
  _pickEl=document.createElement('div'); _pickEl.id='pickPop';
  _pickEl.innerHTML=`<div class="pickbox">
    <div class="pickhead">
      <input id="pickSearch" type="search" placeholder="filter by name / metadata / tile path / mod id…">
      <span class="pickkinds"><button class="chip on" data-k="all">All</button><button class="chip" data-k="entity">Entities</button><button class="chip" data-k="tile">Tiles</button><button class="chip" data-k="mod">Mods</button></span>
      <button class="pickclose" title="close">✕</button>
    </div>
    <div class="picklist" id="pickList"></div>
    <div class="pickfoot">Click a target to add a rule for it (opens expanded to refine). Entities seed an entity rule; tiles seed a Tile rule; mods seed a Monster rule matching that affix.</div>
  </div>`;
  document.body.appendChild(_pickEl);
  _pickEl.querySelector('.pickclose').onclick=()=>_pickEl.classList.remove('open');
  _pickEl.onclick=e=>{ if(e.target===_pickEl) _pickEl.classList.remove('open'); };
  _pickEl.querySelector('#pickSearch').oninput=e=>{ _pickQ=e.target.value.toLowerCase(); renderPick(); };
  _pickEl.querySelectorAll('.pickkinds .chip').forEach(c=>c.onclick=()=>{ _pickKind=c.dataset.k; _pickEl.querySelectorAll('.pickkinds .chip').forEach(x=>x.classList.toggle('on',x===c)); renderPick(); });
  return _pickEl;
}
async function openPicker(){
  const pop=ensurePick(); pop.classList.add('open');
  _pickQ=''; _pickKind='all';
  pop.querySelector('#pickSearch').value=''; pop.querySelectorAll('.pickkinds .chip').forEach((x,j)=>x.classList.toggle('on',j===0));
  $('#pickList').innerHTML='<div class="pickempty">Loading…</div>';
  try{ _pickEnts=await getJSON('/entities?limit=1000')||[]; }catch(_){ _pickEnts=[]; }
  try{ const t=await getJSON('/api/tiles'); _pickTiles=(t&&t.tiles)||[]; }catch(_){ _pickTiles=[]; }
  renderPick(); pop.querySelector('#pickSearch').focus();
}
/* Aggregate the live entities' affix-mod ids into distinct rows: one per mod id, with a carrier
   count and a few example monster names — so you can see which auras/buffs are actually in the zone
   right now and pick one to track. (Each entity lists a mod id at most once, so count = #monsters.) */
function pickMods(){
  const map=new Map(); // modId -> {count, names:Set}
  _pickEnts.forEach(e=>{ (e.mods||[]).forEach(m=>{ if(!m)return; let v=map.get(m); if(!v){v={count:0,names:new Set()}; map.set(m,v);} v.count++; const nm=e.name||lastSeg(e.metadata); if(nm) v.names.add(nm); }); });
  return [...map.entries()].sort((a,b)=>b[1].count-a[1].count).map(([m,v])=>({
    kind:'mod', cat:'Mod', name:m, modId:m, count:v.count,
    sub:[...v.names].slice(0,4).join(', ')||'monster affix',
  }));
}
function pickItems(){
  const q=_pickQ, out=[];
  if(_pickKind==='all'||_pickKind==='entity'){
    const seen=new Set();
    _pickEnts.forEach(e=>{ const k=e.category+'|'+e.metadata; if(seen.has(k))return; seen.add(k);
      if(q && !((e.metadata||'').toLowerCase().includes(q)||(e.name||'').toLowerCase().includes(q)||(e.category||'').toLowerCase().includes(q)))return;
      out.push({kind:'entity',cat:e.category,name:e.name||lastSeg(e.metadata),sub:e.metadata,rarity:e.rarity}); });
  }
  if(_pickKind==='all'||_pickKind==='tile'){
    _pickTiles.forEach(p=>{ if(q && !p.toLowerCase().includes(q))return; out.push({kind:'tile',cat:'Tile',name:lastSeg(p),sub:p}); });
  }
  if(_pickKind==='all'||_pickKind==='mod'){
    pickMods().forEach(it=>{ if(q && !(it.name.toLowerCase().includes(q)||it.sub.toLowerCase().includes(q)))return; out.push(it); });
  }
  return out;
}
function renderPick(){
  const items=pickItems(), list=$('#pickList');
  const empty = _pickEnts.length+_pickTiles.length===0;
  list.innerHTML = items.length ? items.slice(0,600).map((it,i)=>
    `<div class="pickrow" data-i="${i}"><span class="pickbadge ${it.kind}">${it.kind==='tile'?'TILE':it.kind==='mod'?'MOD':esc(it.cat)}</span>`
    +`<span class="picknm">${esc(it.name)}</span><span class="picksub">${esc(it.sub)}</span>`
    +(it.kind==='mod'?`<span class="pickcount">×${it.count}</span>`:'')
    +(it.rarity&&it.rarity!=='NonMonster'?`<span class="pickrar">${esc(it.rarity)}</span>`:'')+`</div>`).join('')
    : (empty
        ? `<div class="pickempty">Nothing to pick — the picker only shows entities/tiles from your <b>current zone</b>. To add e.g. <b>Breach</b>, either <i>enter a Breach zone first</i> (then the picker will list the objects) or close this and click <b>+ Add blank rule</b> — the match field now suggests <b>Breach, Ritual, Expedition, Boss…</b> as you type.</div>`
        : `<div class="pickempty">No matches for the current filter.</div>`);
  $$('#pickList .pickrow').forEach(row=>row.onclick=()=>pickItem(items[+row.dataset.i]));
}
function pickItem(it){
  if(!it) return;
  let r;
  if(it.kind==='tile')
    r={enabled:true,name:it.name,categories:['Tile'],match:[lastSeg(it.sub)],shape:'Diamond',color:'#f259f2',opacity:1,size:5,navigable:true,_open:true};
  else if(it.kind==='mod')
    r={enabled:true,name:it.name,categories:['Monster'],match:[],mods:[it.modId],shape:'Star',color:'#26d9c0',opacity:1,size:6,_open:true};
  else
    r={enabled:true,name:it.name,categories:[it.cat],match:[lastSeg(it.sub)],shape:'Star',color:'#ffd926',opacity:1,size:6,_open:true};
  drules.unshift(r); renderDrules(); saveDrules();
  _pickEl.classList.remove('open');
  const first=$('#drList .drrow'); if(first) first.scrollIntoView({block:'center'});
}
$('#drPick')?.addEventListener('click',openPicker);
function renderHidden(){
  $('#hideList').innerHTML = hidden.length ? hidden.map(p=>
    `<span class="chip on" data-p="${esc(p)}">${esc(p)} <b style="margin-left:5px;cursor:pointer">&#10005;</b></span>`).join('')
    : '<span style="color:var(--ink-faint);font-size:11px;font-style:italic">Nothing hidden.</span>';
  $$('#hideList .chip').forEach(c=>c.querySelector('b').onclick=()=>{ postHidden({remove:c.dataset.p}).then(loadFilters); });
}
$('#hideAdd').onclick=()=>{
  const p=$('#hidePattern').value.trim(); if(!p) return;
  $('#hidePattern').value='';
  postHidden({add:p}).then(loadFilters);
};
$('#hidePattern').onkeydown=e=>{ if(e.key==='Enter') $('#hideAdd').click(); };

/* ── Landmarks tab: view/edit the curated map-label table (baked + user overlay) + import/export ── */
let lmEntries=[], lmAreaOnly=true, lmQ='';
function flashL(){ const m=$('#savedMsgL'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100); }
async function loadLandmarks(){
  try{ const r=await getJSON('/api/landmarks'); lmEntries=r.entries||[]; }catch(e){ lmEntries=[]; }
  const a=$('#lmArea'); if(a && !a.value) a.value=(state&&state.areaCode)||'';
  renderLandmarks();
}
async function postLandmarks(body){
  try{ const r=await fetch('/api/landmarks',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}); const j=await r.json(); if(j&&j.entries) lmEntries=j.entries; flashL(); }catch(e){}
  renderLandmarks();
}
function lmRow(e){
  const badge=e.suppressed?'hidden':e.source;
  const del=e.suppressed?'Restore':(e.source==='user'?'Remove':'Hide');
  return `<div class="lmrow${e.suppressed?' sup':''}" data-area="${esc(e.area)}" data-pat="${esc(e.pattern)}">
    <span class="lmbadge ${badge}">${badge}</span>
    <span class="lmarea">${esc(e.area)}</span>
    <input class="mname lmlabel" value="${esc(e.label||'')}" placeholder="${e.suppressed?'(hidden)':'label'}">
    <span class="lmpath" title="${esc(e.pattern)}">${esc(e.pattern)}</span>
    <button class="delbtn lm-del">${del}</button>
  </div>`;
}
function renderLandmarks(){
  const host=$('#lmList'); if(!host) return;
  const area=(state&&state.areaCode)||'';
  const rows=lmEntries.filter(e=>{
    if(lmAreaOnly && e.area!=='*' && e.area!==area) return false;
    if(lmQ){ if(!((e.area+' '+e.pattern+' '+(e.label||'')).toLowerCase().includes(lmQ))) return false; }
    return true;
  });
  host.innerHTML = rows.length ? rows.map(lmRow).join('')
    : `<div class="row"><div class="rl hint-row">No curated landmarks${lmAreaOnly?' for this area ('+esc(area||'—')+')':''}. Add one below${lmAreaOnly?', or turn off &ldquo;This area only&rdquo;':''}.</div></div>`;
  $$('#lmList .lmrow').forEach(row=>{
    const area=row.dataset.area, pat=row.dataset.pat, e=lmEntries.find(x=>x.area===area&&x.pattern===pat); if(!e) return;
    row.querySelector('.lmlabel').onchange=ev=>postLandmarks({set:{area,pattern:pat,label:ev.target.value}});
    row.querySelector('.lm-del').onclick=()=>{
      if(e.suppressed || e.source==='user') postLandmarks({remove:{area,pattern:pat}}); // restore baked / delete user
      else postLandmarks({set:{area,pattern:pat,label:null}});                          // suppress a baked entry
    };
  });
}
$('#lmSearch')?.addEventListener('input',e=>{ lmQ=e.target.value.toLowerCase(); renderLandmarks(); });
$('#lmAreaOnly')?.addEventListener('click',()=>{ lmAreaOnly=!lmAreaOnly; $('#lmAreaOnly').classList.toggle('on',lmAreaOnly); renderLandmarks(); });
$('#lmAdd')?.addEventListener('click',()=>{
  const area=($('#lmArea').value||'').trim(), pat=($('#lmPat').value||'').trim(), label=($('#lmLabel').value||'').trim();
  if(!area||!pat||!label) return;
  $('#lmPat').value=''; $('#lmLabel').value='';
  postLandmarks({set:{area,pattern:pat,label}});
});
$('#lmExport')?.addEventListener('click',async()=>{
  try{ const txt=await (await fetch('/api/landmarks?export=1',{cache:'no-store'})).text();
    const a=document.createElement('a'); a.href=URL.createObjectURL(new Blob([txt],{type:'application/json'}));
    a.download='CustomLandmarks.json'; a.click(); URL.revokeObjectURL(a.href);
  }catch(e){}
});
$('#lmImport')?.addEventListener('click',()=>{
  const inp=document.createElement('input'); inp.type='file'; inp.accept='.json,application/json';
  inp.onchange=()=>{ const f=inp.files&&inp.files[0]; if(!f) return; const rd=new FileReader();
    rd.onload=()=>{ try{ postLandmarks({import:JSON.parse(rd.result)}); }catch(_){ alert('Invalid JSON file'); } };
    rd.readAsText(f); };
  inp.click();
});

/* ── director tab: Zone Plan (live ranked queue from /state) + EC2 CampaignGuide ── */
function renderDirectorQueue(){
  const dq = document.getElementById('dirQueue');
  if (!dq) return;
  const gb = document.getElementById('gpsBanner');
  if (gb) {
    const g = state && state.campaignGps;
    if (g) { gb.hidden = false; gb.textContent = '🧭 ' + g; }   // 🧭
    else { gb.hidden = true; gb.textContent = ''; }
  }
  // v0.21 EC2 CampaignGuide (additive; null on v0.20 backends or when EnableCampaignGps=false).
  // Hide the step-text row + degradation badge when the payload is absent so the DOM subtree
  // costs nothing to lay out in the off / stale-signal case (zero-cost-when-off gate).
  const guide  = state && state.campaignGuide;
  const stepEl = document.getElementById('guideStep');
  const badgeEl= document.getElementById('guideDegradeBadge');
  if (stepEl && badgeEl){
    if (guide && guide.available && guide.text){
      stepEl.hidden = false;
      stepEl.textContent = '▶ ' + guide.text;
      badgeEl.hidden = !guide.stalled;
    } else {
      stepEl.hidden = true;
      stepEl.textContent = '';
      badgeEl.hidden = true;
    }
  }
  const dir = (state && state.director) || [];
  if (dir.length === 0){
    dq.innerHTML = '<div style="opacity:.5;padding:4px 0">No active objectives in this zone</div>';
    return;
  }
  dq.innerHTML = dir.map((o, i) =>
    `<div class="navrow" style="font-weight:${i===0?'600':'400'};opacity:${i===0?'1':'.75'}">` +
    `${i===0 ? '&#9654; ' : ''}` +
    `<span class="navname">${esc(o.label)}</span>` +
    `<span class="navtag">${esc(o.tier||o.category)}</span>` +
    `<span class="navdist">P${o.priority}</span>` +
    `</div>`
  ).join('');
}

/* ── director tab: catalog builder (seen-POIs → objectives) ── */
let dirSeen=[], dirObjs=[], dirQ='';
async function loadDirector(){
  try{ const s=await getJSON('/api/seen-pois'); dirSeen=s.pois||[]; }catch(e){ dirSeen=[]; }
  try{ const o=await getJSON('/api/objectives'); dirObjs=o.objectives||[]; }catch(e){ dirObjs=[]; }
  renderDirector();
}
async function postObjectives(body){
  try{ const r=await fetch('/api/objectives',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
       const j=await r.json(); if(j&&j.objectives) dirObjs=j.objectives; }catch(e){}
  loadDirector();
}
function renderDirector(){
  const cand=$('#dirCandidates');
  if(cand){
    const rows=dirSeen.filter(p=>!p.covered)
      .filter(p=>!dirQ || ((p.name+' '+(p.metadata||p.landmarkPath||'')+' '+p.category).toLowerCase().includes(dirQ)))
      .sort((a,b)=>b.count-a.count);
    cand.innerHTML = rows.length ? rows.map(candRow).join('')
      : '<div class="row"><div class="rl hint-row">Nothing uncatalogued in view — explore more, or clear the filter.</div></div>';
    rows.forEach(p=>{
      const el=cand.querySelector('[data-sig="'+cssEsc(p.signature)+'"]'); if(!el) return;
      el.querySelector('.dir-add').onclick=()=>{
        const cat=el.querySelector('.dir-cat').value;
        const prio=parseInt(el.querySelector('.dir-prio').value,10)||50;
        const tier=el.querySelector('.dir-tier').value||undefined;
        const match = p.landmarkPath ? {landmarkPath:[p.landmarkPath]} : {metadata:[p.metadata]};
        postObjectives({add:Object.assign({id:p.signature,label:p.name,category:cat,priority:prio,enabled:true,tier:tier},match)});
      };
    });
  }
  const cat=$('#dirCatalog');
  if(cat){
    const objs=dirObjs.slice().sort((a,b)=>(b.priority||0)-(a.priority||0));
    cat.innerHTML = objs.length ? objs.map(o=>
      '<div class="row" data-id="'+esc(o.id)+'"><div class="rl">'+esc(o.label)+'<small>'+esc(o.category)+' · prio '+(o.priority||0)+(o.enabled?'':' · off')+'</small></div>'
      + '<button class="delbtn dir-del">Remove</button></div>').join('')
      : '<div class="row"><div class="rl hint-row">No objectives yet.</div></div>';
    objs.forEach(o=>{ const el=cat.querySelector('[data-id="'+cssEsc(o.id)+'"]'); if(el) el.querySelector('.dir-del').onclick=()=>postObjectives({remove:{id:o.id}}); });
  }
  renderDirectorQueue();
}
function candRow(p){
  const tierOpts = ['SeasonalEvent','SideBoss','Bonus','SideZone','Exit']
    .map(t => `<option value="${t}"${t===(p.guessedTier||'Exit')?'selected':''}>${t}</option>`)
    .join('');
  const tierSel = `<select class='numin dir-tier'>${tierOpts}</select>`;
  return '<div class="row" data-sig="'+esc(p.signature)+'">'
    + '<div class="rl">'+esc(p.name)+'<small>'+esc(p.category)+' · '+esc(p.zone||'?')+' · ×'+p.count+'</small></div>'
    + tierSel
    + `<input class='numin dir-cat' list='labelVocab' placeholder='label…' style='width:130px'`
    + ` value='${esc(p.guessedCategory||"")}' title='${p.guessedConf ? "Classifier: "+esc(p.guessedConf) : ""}'>`
    + '<input class="numin dir-prio" type="number" min="0" max="1000" value="50" style="width:64px">'
    + '<button class="delbtn dir-add">Add</button></div>';
}
function cssEsc(s){ return (s||'').replace(/["\\\]]/g,'\\$&'); }
$('#dirSearch')?.addEventListener('input',e=>{ dirQ=e.target.value.toLowerCase(); renderDirector(); });

/* ── entity atlas tab: name everything + classify the notable ── */
let eaEntries=[], eaQ='';
async function loadEntAtlas(){
  try{ const s=await getJSON('/api/entity-atlas'); eaEntries=s.entries||[]; }catch(e){ eaEntries=[]; }
  renderEntAtlas();
}
async function postAtlasName(metadata, name){
  try{ await fetch('/api/entity-atlas/name',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({metadata,name})}); }catch(e){}
  loadEntAtlas();
}
function eaMatch(a){ return !eaQ || ((a.name+' '+a.metadata+' '+a.category).toLowerCase().includes(eaQ)); }
function renderEntAtlas(){
  const un=$('#eaUnnamed');
  if(un){
    const rows=eaEntries.filter(a=>!a.named).filter(eaMatch).sort((x,y)=>y.count-x.count);
    un.innerHTML = rows.length ? rows.map(eaNameRow).join('')
      : '<div class="row"><div class="rl hint-row">Everything in view has a name — explore more, or clear the filter.</div></div>';
    rows.forEach(a=>{
      const el=un.querySelector('[data-m="'+cssEsc(a.metadata)+'"]'); if(!el) return;
      el.querySelector('.ea-save').onclick=()=>{ const v=el.querySelector('.ea-name').value.trim(); if(v) postAtlasName(a.metadata, v); };
    });
  }
  const nt=$('#eaNotable');
  if(nt){
    const rows=eaEntries.filter(a=>a.notable && !a.covered).filter(eaMatch).sort((x,y)=>y.count-x.count);
    nt.innerHTML = rows.length ? rows.map(eaClassRow).join('')
      : '<div class="row"><div class="rl hint-row">No uncatalogued notable entities in view.</div></div>';
    rows.forEach(a=>{
      const el=nt.querySelector('[data-m="'+cssEsc(a.metadata)+'"]'); if(!el) return;
      el.querySelector('.ea-add').onclick=async()=>{
        const cat=el.querySelector('.ea-cat').value;
        const prio=parseInt(el.querySelector('.ea-prio').value,10)||50;
        const tier=el.querySelector('.ea-tier').value||undefined;
        try{ await fetch('/api/objectives',{method:'POST',headers:{'Content-Type':'application/json'},
             body:JSON.stringify({add:{id:'e:'+a.metadata,label:a.name,category:cat,priority:prio,enabled:true,tier:tier,metadata:[a.metadata]}})}); }catch(e){}
        loadEntAtlas();
      };
    });
  }
}
function eaNameRow(a){
  return '<div class="row" data-m="'+esc(a.metadata)+'">'
    + '<div class="rl">'+esc(a.name)+'<small>'+esc(a.category)+' · '+esc(a.zone||'?')+' · ×'+a.count+'</small></div>'
    + '<input class="numin ea-name" type="text" placeholder="friendly name" style="width:160px">'
    + '<button class="delbtn ea-save">Save</button></div>';
}
function eaClassRow(a){
  const tierOpts = ['SeasonalEvent','SideBoss','Bonus','SideZone','Exit']
    .map(t => `<option value="${t}"${t===(a.guessedTier||'Exit')?'selected':''}>${t}</option>`)
    .join('');
  const tierSel = `<select class='numin ea-tier'>${tierOpts}</select>`;
  return '<div class="row" data-m="'+esc(a.metadata)+'">'
    + '<div class="rl">'+esc(a.name)+'<small>'+esc(a.category)+' · '+esc(a.zone||'?')+' · ×'+a.count+'</small></div>'
    + tierSel
    + `<input class='numin ea-cat' list='labelVocab' placeholder='label…' style='width:130px'`
    + ` value='${esc(a.guessedCategory||"")}' title='${a.guessedConf ? "Classifier: "+esc(a.guessedConf) : ""}'>`
    + '<input class="numin ea-prio" type="number" min="0" max="1000" value="50" style="width:64px">'
    + '<button class="delbtn ea-add">Classify</button></div>';
}
$('#eaSearch')?.addEventListener('input',e=>{ eaQ=e.target.value.toLowerCase(); renderEntAtlas(); });
$('#eaExport')?.addEventListener('click',async()=>{
  try{ const p=await getJSON('/api/entity-atlas/export');
    const blob=new Blob([JSON.stringify(p,null,2)],{type:'application/json'});
    const u=URL.createObjectURL(blob); const a=document.createElement('a');
    a.href=u; a.download='atlas-pack.json'; a.click(); URL.revokeObjectURL(u);
  }catch(e){}
});
function flashEa(){ const m=$('#savedMsgEa'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1600); }
/* ── minimal toast helper (CF-FALLBACK-UX) — bottom-right stack, optional action button, 6s dismiss.
   Zero-cost-when-off: #toastHost is lazily appended on first showToast() call. ── */
function showToast(msg, actionLabel, actionFn){
  let host=document.getElementById('toastHost');
  if(!host){ host=document.createElement('div'); host.id='toastHost';
    host.style.cssText='position:fixed;right:16px;bottom:16px;z-index:9999;display:flex;flex-direction:column;gap:8px;max-width:360px;pointer-events:none;';
    document.body.appendChild(host); }
  const t=document.createElement('div');
  t.style.cssText='background:var(--panel2,#1b1610);border:1px solid var(--line,#3a2f1d);color:var(--ink,#e8dcc2);padding:10px 12px;border-radius:6px;box-shadow:var(--shadow,0 8px 20px rgba(0,0,0,.6));font:12px "IBM Plex Mono",Consolas,monospace;pointer-events:auto;display:flex;align-items:center;gap:10px;';
  const span=document.createElement('span'); span.textContent=msg; span.style.flex='1'; t.appendChild(span);
  if(actionLabel && typeof actionFn==='function'){
    const b=document.createElement('button'); b.className='numin'; b.textContent=actionLabel;
    b.style.cssText='padding:4px 8px;cursor:pointer;';
    b.onclick=async()=>{ b.disabled=true; try{ await actionFn(); }finally{ t.remove(); } };
    t.appendChild(b);
  }
  const x=document.createElement('button'); x.textContent='×'; x.setAttribute('aria-label','dismiss');
  x.style.cssText='background:none;border:0;color:var(--ink-dim,#9c8e72);cursor:pointer;font-size:16px;line-height:1;padding:0 2px;';
  x.onclick=()=>t.remove(); t.appendChild(x);
  host.appendChild(t);
  setTimeout(()=>{ if(t.parentNode) t.remove(); }, 6000);
}
/* Sentinel-split (CF-FALLBACK-UX): distinct reasons for {settingsFetchFailed, contributeUrlEmpty, ok}.
   Old code collapsed the two failure modes into `_eaContribUrl=''` and silently opened a GitHub
   template — a lie of contribution. Now the two states each get their own user-visible toast. */
let _eaContribCache=null; // {ok,url,reason,defaultUrl}
async function eaContribUrl(){
  if(_eaContribCache!==null) return _eaContribCache;
  try{
    const s=await getJSON('/api/settings');
    const url=(s.contributeUrl||'').trim();
    const defaultUrl=(s.defaultContributeUrl||'').trim();
    if(!url){ _eaContribCache={ok:false, url:'', reason:'contributeUrlEmpty', defaultUrl}; }
    else    { _eaContribCache={ok:true,  url,   reason:'ok',                defaultUrl}; }
  }catch(e){
    _eaContribCache={ok:false, url:'', reason:'settingsFetchFailed', defaultUrl:''};
  }
  return _eaContribCache;
}
async function restoreDefaultContribUrl(defaultUrl){
  if(!defaultUrl){ showToast('No default URL available — set one in Settings.'); return; }
  try{
    const r=await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({contributeUrl:defaultUrl})});
    if(r.ok){ _eaContribCache=null; showToast('Contribute URL restored to default. Click Contribute again to send.'); }
    else   { showToast('Could not restore default URL (HTTP '+r.status+').'); }
  }catch(e){ showToast('Could not restore default URL (network error).'); }
}
/* Shared gate: returns true if the Contribute POST should proceed; otherwise surfaces the
   correct toast (settingsFetchFailed vs contributeUrlEmpty) and returns false.
   Extended to the Task-11 buff + preload buttons so all three Contribute paths kill the
   silent window.open fallback consistently. */
async function contribGateOrToast(){
  const c=await eaContribUrl();
  if(c.reason==='settingsFetchFailed'){
    showToast('Could not read settings from the overlay — is it still running?');
    return false;
  }
  if(c.reason==='contributeUrlEmpty'){
    showToast('No Contribute URL is set — nothing will be sent.', 'Restore default URL', ()=>restoreDefaultContribUrl(c.defaultUrl));
    return false;
  }
  return true;
}
/* SIG-CONTRIBUTE-PIGGYBACK (v0.23): fire-and-forget POST to /api/contribute-trace after the primary
   Contribute succeeds, so users who already contribute atlas / buffs / preload also contribute the
   campaign probe trace without needing a separate click. Guarded on the enableCampaignProbe checkbox
   so the network round-trip is skipped entirely when the probe is off. Trace failure MUST NOT affect
   the primary Contribute UX — .catch(()=>{}) swallows any network / 4xx / 5xx response silently. */
function piggybackTraceContribute(){
  const probeOn = document.querySelector('[data-set="enableCampaignProbe"]')?.checked;
  if(!probeOn) return;
  fetch('/api/contribute-trace',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'}).catch(()=>{});
}

$('#eaContribute')?.addEventListener('click',async()=>{
  if(!await contribGateOrToast()) return;
  if(!window._eaOkOnce){ if(!confirm('Share your discovered entity names + objectives publicly? This contains no character data.')) return; window._eaOkOnce=true; }
  try{ const r=await fetch('/api/contribute',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
    if(r.ok){ flashEa(); piggybackTraceContribute(); } else { showToast('Contribute failed (HTTP '+r.status+').'); } }catch(e){ showToast('Contribute failed (network error).'); }
});

/* v0.21 CF-DASH-BUTTONS: buff + preload Contribute buttons + zero-cost-when-off DOM sync */
function flashBn(){ const m=$('#savedMsgBn'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1600); }
function flashPr(){ const m=$('#savedMsgPr'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1600); }

/* Hide the buff/preload Contribute buttons when their card's enable toggle is off.
   Verify gate: with data-bn="enabled" unchecked, #bnContribute style.display === "none".
   Same for data-set="preloadEnabled" / #prContribute. */
function syncContribVisibility(){
  const bnEn = document.querySelector('[data-bn="enabled"]')?.checked;
  const prEn = document.querySelector('[data-set="preloadEnabled"]')?.checked;
  const tpEn = document.querySelector('[data-set="enableCampaignProbe"]')?.checked;
  const bnBtn = $('#bnContribute'); if (bnBtn) bnBtn.style.display = bnEn ? '' : 'none';
  const prBtn = $('#prContribute'); if (prBtn) prBtn.style.display = prEn ? '' : 'none';
  const tpBtn = $('#tpContribute'); if (tpBtn) tpBtn.style.display = tpEn ? '' : 'none';
}
document.addEventListener('change', e=>{
  if (e.target && (e.target.matches?.('[data-bn="enabled"]') || e.target.matches?.('[data-set="preloadEnabled"]') || e.target.matches?.('[data-set="enableCampaignProbe"]'))) syncContribVisibility();
});

$('#bnContribute')?.addEventListener('click', async()=>{
  if(!await contribGateOrToast()) return;
  if(!window._bnOkOnce){ if(!confirm('Share your observed buff ids + tiers publicly? This contains no character data.')) return; window._bnOkOnce=true; }
  try{ const r = await fetch('/api/contribute-buffs',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
    if(r.ok){ flashBn(); piggybackTraceContribute(); } else { showToast('Contribute failed (HTTP '+r.status+').'); } }catch(e){ showToast('Contribute failed (network error).'); }
});

$('#prContribute')?.addEventListener('click', async()=>{
  if(!await contribGateOrToast()) return;
  if(!window._prOkOnce){ if(!confirm('Share your observed preload path frequencies publicly? This contains no character data.')) return; window._prOkOnce=true; }
  try{ const r = await fetch('/api/contribute-preload',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
    if(r.ok){ flashPr(); piggybackTraceContribute(); } else { showToast('Contribute failed (HTTP '+r.status+').'); } }catch(e){ showToast('Contribute failed (network error).'); }
});

/* ── Migrate-to-Rule-Engine buttons (R7): stash a partial RuleRecord to sessionStorage
     and dispatch an event that the Rules Engine tab picks up. ── */
function stashMigrationPrefill(prefill){
  try {
    sessionStorage.setItem('rules-engine-prefill', JSON.stringify(prefill));
  } catch(e) { showToast('Could not save migration prefill.'); return; }
  document.dispatchEvent(new CustomEvent('switch-to-rules-engine-with-prefill', {bubbles:true}));
}

document.getElementById('btnMigrateAffix')?.addEventListener('click', () => {
  const first = (an && an.alwaysShow && an.alwaysShow.length) ? an.alwaysShow[0] : null;
  let label = 'affix';
  if (first && anCatalog.length) {
    const entry = anCatalog.find(a => a.modId === first);
    if (entry) label = entry.name || entry.modId;
  }
  stashMigrationPrefill({
    Name: label + ' — migrated',
    Priority: 100,
    Enabled: true,
    When: { Metadata: '^Metadata/Items' },
    Then: [{ kind: 'label', Text: '{name}' }]
  });
});

document.getElementById('btnMigrateBuff')?.addEventListener('click', () => {
  const tier = (bn && bn.tier) || 'Deadly';
  stashMigrationPrefill({
    Name: (tier || 'buff') + ' — migrated',
    Priority: 100,
    Enabled: true,
    When: { HasBuff: '' },
    Then: [{ kind: 'ring', Color: '#ff8000' }]
  });
});

document.getElementById('btnMigrateFilter')?.addEventListener('click', () => {
  if (!drules || !drules.length) { showToast('No display rules to migrate.'); return; }
  const src = drules.find(r => r._open) || drules[0];
  const when = {};
  if (src.match && src.match.length) when.Metadata = src.match.map(m => '\\b' + m.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\b').join('|');
  if (src.rarity) when.Rarity = (src.rarity || '').toLowerCase();
  const then = [];
  if (src.hide) then.push({ kind: 'hide' });
  if (src.label) then.push({ kind: 'label', Text: src.label });
  if (src.color && !src.hide) then.push({ kind: 'tint', Color: src.color });
  if (!then.length) then.push({ kind: 'tint', Color: '#ffd926' });
  stashMigrationPrefill({
    Name: (src.name || 'rule') + ' — migrated',
    Priority: 100,
    Enabled: src.enabled !== false,
    When: when,
    Then: then
  });
});

/* v0.22 PROBE-UI: Contribute-trace + reset-install-id + one-shot onboarding toast.
   Zero-cost-when-off: #tpContribute hidden via syncContribVisibility when EnableCampaignProbe=false;
   onboarding toast only fires when EnableCampaignProbe && !ProbeOnboardingSeen (one-shot, latches on
   window._probeOnboardingFired so multiple loadSettings() calls in a single Dashboard boot can&rsquo;t
   re-fire the toast even before the Got-it click round-trips). */
function flashTp(){ const m=$('#savedMsgTp'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1600); }

$('#tpContribute')?.addEventListener('click', async()=>{
  if(!await contribGateOrToast()) return;
  if(!window._tpOkOnce){ if(!confirm('Share your most recent boot’s campaign trace publicly? Zone traversals only — no character data, hashed NPC/dialogue text.')) return; window._tpOkOnce=true; }
  try{ const r = await fetch('/api/contribute-trace',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
    if(r.ok){ flashTp(); } else { showToast('Contribute failed (HTTP '+r.status+').'); } }catch(e){ showToast('Contribute failed (network error).'); }
});

$('#tpResetInstall')?.addEventListener('click', async()=>{
  if(!confirm('Regenerate the anonymous trace-session id? Existing local JSONL files keep their old id — only new boots use the new one.')) return;
  try{ const r = await fetch('/api/probe/reset-install-id',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
    if(r.ok){ showToast('Trace session id regenerated.'); } else { showToast('Reset failed (HTTP '+r.status+').'); } }catch(e){ showToast('Reset failed (network error).'); }
});

async function showProbeOnboardingIfNeeded(s){
  if(!s) return;
  if(!(s.enableCampaignProbe===true)) return;
  if(s.probeOnboardingSeen===true) return;
  if(window._probeOnboardingFired) return;
  window._probeOnboardingFired=true;
  showToast(
    'Campaign trace probe is on. Your zone traversals get logged to a local file (nothing uploads). One-click Contribute trace in the Campaign panel shares a session so POE2GPS’s Campaign Director gets smarter with more players’ routes. The shared pool is public. Turn off in ⚙ Settings → Campaign trace probe.',
    'Got it',
    async()=>{
      try{ await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({probeOnboardingSeen:true})}); }catch(e){}
    }
  );
}

/* ── gear tab: god-roll detector (experimental, default off) ── */
let gWeights={byStatId:{},target:100,godRollThreshold:85};
async function loadGear(){
  let g={enabled:false,items:[]};
  try{ g=await getJSON('/api/gear'); }catch(e){}
  try{ gWeights=await getJSON('/api/gear-weights'); }catch(e){}
  const st=$('#gStatus');
  if(st) st.innerHTML='<div class="rl hint-row">'+(g.enabled?('Scoring '+((g.items||[]).length)+' inventory item(s).'):'Off — enable "Gear scorer (experimental)" in Settings, then open your inventory in-game.')+'</div>';
  renderGearItems(g.items||[]);
  renderGearGrid(g.items||[]);
  if($('#gTarget')) $('#gTarget').value=gWeights.target;
  if($('#gThreshold')) $('#gThreshold').value=gWeights.godRollThreshold;
  renderGearWeights();
}
function renderGearItems(items){
  const el=$('#gItems'); if(!el) return;
  el.innerHTML = items.length ? items.map(it=>{
    const aff=(it.affixes||[]).map(a=>{
      const chips=(a.statIds||[]).map(id=>'<button class="chip g-chip" data-id="'+esc(id)+'" data-val="'+a.value+'" title="weight this stat (meta scale)">'+esc(id)+'</button>').join(' ');
      const tierTxt=(a.tier?(' &middot; <span class="'+(a.pctOfMax>=90?'tier-top':'tier')+'">T'+a.tier+'/'+a.tierCount+(a.pctOfMax!=null?(' &middot; '+a.pctOfMax+'%'):'')+'</span>'):'');
      return '<div class="rl hint-row" style="padding-left:12px">'+esc(a.line||'')+' &middot; roll '+a.value
        +(a.weight?(' &middot; w'+a.weight+' &rarr; '+a.points+'pts'):'')+tierTxt+'<div style="margin-top:3px">'+chips+'</div></div>';
    }).join('');
    return '<div class="row" style="flex-wrap:wrap"><div class="rl"><span class="rar-'+esc(it.rarity||'Normal')+'">'+(it.godRoll?'&#9733; ':'')+esc(it.name||'(item)')+'</span><small>'+esc(it.rarity||'')+' &middot; inv '+it.inventoryId+'</small></div>'
      +'<div class="numin" style="min-width:54px;text-align:right;font-weight:600">'+it.score+'</div>'
      +'<div style="flex-basis:100%">'+aff+'</div></div>';
  }).join('') : '<div class="row"><div class="rl hint-row">No scored items yet. (Turn the scorer on in Settings and open your inventory in-game.)</div></div>';
  // one-click: weight the chip's stat id on the meta scale (10), norm from the observed roll if unknown.
  el.querySelectorAll('.g-chip').forEach(b=>b.onclick=()=>{
    const id=b.dataset.id, val=parseFloat(b.dataset.val)||1;
    const body={setWeight:{statId:id,weight:10}};
    if(!(gWeights.normById&&gWeights.normById[id]>0)) body.setWeight.norm=Math.max(val,1);
    postGear(body);
  });
}
function scoreColor(s){ // 0=red -> 100=green
  const t=Math.max(0,Math.min(100,s))/100; const h=Math.round(t*120); // 0=red,120=green
  return 'hsl('+h+',60%,55%)';
}
function renderGearGrid(items){
  const el=$('#gGrid'); if(!el) return;
  const sorted=(items||[]).slice().sort((a,b)=>b.score-a.score);
  el.innerHTML = sorted.length ? sorted.map(it=>{
    const t=esc((it.godRoll?'★ ':'')+(it.name||'(item)')+' — '+it.score+' ('+(it.rarity||'')+')');
    return '<div class="gcell" style="background:'+scoreColor(it.score)+'" title="'+t+'">'+it.score+'</div>';
  }).join('') : '<div class="rl hint-row">No scored items yet.</div>';
}
$('#gGridToggle')?.addEventListener('change',e=>{
  const grid=e.target.checked; $('#gGrid').style.display=grid?'flex':'none'; $('#gItems').style.display=grid?'none':'block';
});
function renderGearWeights(){
  const el=$('#gWeightList'); if(!el) return;
  const ks=Object.keys(gWeights.byStatId||{});
  el.innerHTML = ks.length ? ks.map(k=>'<div class="row"><div class="rl">'+esc(k)+'</div><div class="numin">'+gWeights.byStatId[k]+'</div><button class="delbtn g-del" data-k="'+esc(k)+'">Remove</button></div>').join('')
    : '<div class="row"><div class="rl hint-row">No weights yet — copy a stat id from an affix above and add a weight.</div></div>';
  ks.forEach(k=>{ const b=el.querySelector('.g-del[data-k="'+cssEsc(k)+'"]'); if(b) b.onclick=()=>postGear({setWeight:{statId:k,weight:0}}); });
}
async function postGear(body){
  try{ const r=await fetch('/api/gear-weights',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    const j=await r.json(); if(j&&j.weights) gWeights=j.weights; }catch(e){}
  if($('#gTarget')) $('#gTarget').value=gWeights.target;
  if($('#gThreshold')) $('#gThreshold').value=gWeights.godRollThreshold;
  renderGearWeights();
}
$('#gSetWeight')?.addEventListener('click',()=>{ const id=($('#gStatId').value||'').trim(); const w=parseFloat($('#gWeight').value); if(id&&!isNaN(w)) postGear({setWeight:{statId:id,weight:w}}); });
$('#gLoadStarter')?.addEventListener('click',()=>{ if(confirm('Replace your weights with the ladder-meta starter set?')) postGear({reset:'starter'}).then(loadGear); });
$('#gTarget')?.addEventListener('change',e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)) postGear({target:v}); });
$('#gThreshold')?.addEventListener('change',e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)) postGear({threshold:v}); });
$('#eaImport')?.addEventListener('change',e=>{
  const f=e.target.files&&e.target.files[0]; if(!f) return;
  const rd=new FileReader();
  rd.onload=async()=>{ try{ const pack=JSON.parse(rd.result);
      await fetch('/api/entity-atlas/import',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(pack)});
      loadEntAtlas();
    }catch(err){} e.target.value=''; };
  rd.readAsText(f);
});

/* ── dynasty-support maps reference card ── */
async function loadDynasty(){
  const el=$('#dynastyList'); if(!el) return;
  let rows=[];
  try{ rows=await getJSON('/api/dynasty-maps'); }catch(e){}
  el.innerHTML = (rows&&rows.length) ? rows.map(r=>
    '<div class="row" style="flex-wrap:wrap"><div class="rl">'+esc(r.name||'')+'<small>'+esc(r.boss||'')+'</small></div>'
    +'<div style="flex-basis:100%;font-size:11px;color:var(--ink-faint)">'+(r.gems||[]).map(g=>esc(g)).join(' &middot; ')+'</div></div>'
  ).join('') : '<div class="rl hint-row">No dynasty maps loaded.</div>';
}

/* ── atlas tab (read-only inspection of the map-data we can read) ── */
async function loadAtlas(){
  const st=$('#atlasStatus');
  st.textContent='reading…';
  st.classList.remove('err');
  try{
    const resp=await fetch('/api/atlas',{cache:'no-store'});
    if(resp.ok){ atlasData=await resp.json(); renderAtlas(); return; }
    st.classList.add('err');
    if(resp.status===404) st.textContent='Atlas API disabled — enable Web Map or Web OBS in Settings, then Refresh.';
    else st.textContent='Atlas request failed (HTTP '+resp.status+' '+resp.statusText+').';
  }catch(e){
    st.classList.add('err');
    st.textContent='Atlas request failed: '+(e?.message||'network error')+'.';
  }
}
function renderAtlas(){
  const d=atlasData; if(!d){ return; }
  const st=$('#atlasStatus'); const nd=d.nodes;
  if(!(nd&&nd.total)) st.textContent = 'atlas closed — open it in-game + Refresh';
  else st.textContent = nd.total+' nodes · '+nd.hasContent+' with content · '
        +(d.allKinds?.length||0)+' kind / '+(d.allTags?.length||0)+' content / '+(d.allMaps?.length||0)+' map filters';
  // Seed active rules from the overlay (once): tracked + arrow sets. Then render the filter table.
  if(atlasHl===null){ atlasHl=new Set((d.highlightTags||[]).map(t=>t.toLowerCase())); atlasNav=new Set((d.navTags||[]).map(t=>t.toLowerCase())); atlasArrow=new Set((d.arrowTags||[]).map(t=>t.toLowerCase())); }
  renderAtlasHighlight(d);
}
// Biome index → friendly-ish label (best-effort; index is the ground truth).
const BIOMES=['Grass','Sand','Swamp','Forest','Snow','Stone','Volcanic','Coast','Cave','Vaal','Water','Desert','Special'];
const biomeName=i=>(i>=0&&i<BIOMES.length)?BIOMES[i]:('biome '+i);

// Highlight-rule chips: one per distinct content tag on the atlas. Click to toggle → ONLY matching maps
// are drawn in-game. Active set is pushed to the overlay (persisted there).
// Classify a filter row into a category for the table (and grouping/colour).
function catContent(t){ const s=t.toLowerCase(); if(/not shown|\[dnt\]/.test(s))return'Hidden'; if(/boss/.test(s))return'Boss'; if(/influence/.test(s))return'Influence'; return'Mechanic'; }
function catMap(t){ const s=t.toLowerCase(); if(/citadel/.test(s))return'Citadel'; if(/tower/.test(s))return'Tower'; if(/temple/.test(s))return'Temple'; if(/vaal/.test(s))return'Vaal'; return'Map'; }
// Per-category colour (badge tint).
const CATCOL={Boss:'#e0533a',Mechanic:'#3ca0ff',Influence:'#a06cff',Hidden:'#ff5db1',Citadel:'#e0b341',Tower:'#2fb6a8',Temple:'#d98a2b',Vaal:'#c0395a',Unique:'#c678dd',Merchant:'#5aa9e6',Map:'#8a93a0',Type:'#d98a2b'};
function catBadge(cat){ const c=CATCOL[cat]||'#8a93a0'; return '<span style="display:inline-block;padding:1px 8px;border-radius:10px;font-size:11px;font-weight:600;background:'+c+'26;color:'+c+';border:1px solid '+c+'66">'+esc(cat)+'</span>'; }
// Build the unified filter list (content + map) with {title,count,cat,group}.
function atlasFilterRows(d){
  const rows=[];
  // Kind rows first: tracking one (e.g. "Tower") rings + routes to EVERY map of that archetype.
  (d.allKinds||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Kind',cat:t.tag}));
  // Type rows (#7): maps.json type/tags — unique / lineage / arbiter. One-click route-to-all-of-a-kind.
  (d.allDataTags||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Type',cat:'Type'}));
  (d.allTags||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Content',cat:catContent(t.tag),desc:t.desc}));
  (d.allMaps||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Map',cat:catMap(t.tag)}));
  return rows;
}
// ── Atlas colour-groups editor (#7) ─────────────────────────────────────────────────────────────
let atlasGroupsData=[];
async function loadAtlasGroups(){
  let s; try{ s=await getJSON('/api/settings'); }catch(e){ return; }
  atlasGroupsData = Array.isArray(s.atlasGroups) ? s.atlasGroups.map(g=>({name:g.name||'',color:g.color||'#E0B341',maps:(g.maps||[]).slice()})) : [];
  renderAtlasGroups();
}
function saveAtlasGroups(){ saveSetting('atlasGroups', atlasGroupsData); }
function renderAtlasGroups(){
  const box=$('#atlasGroups'); if(!box) return;
  if(atlasGroupsData.length===0){ box.innerHTML='<span class="hint-row" style="opacity:.6">No groups. Maps in a group draw in its colour when tracked.</span>'; return; }
  box.innerHTML = atlasGroupsData.map((g,i)=>
    '<div style="display:grid;grid-template-columns:130px 44px 1fr 60px;gap:8px;align-items:start;padding:5px 0;border-bottom:1px solid var(--line)">'
    +'<input data-gi="'+i+'" data-gf="name" value="'+esc(g.name)+'" placeholder="group name" style="width:100%">'
    +'<input data-gi="'+i+'" data-gf="color" type="color" value="'+esc(g.color)+'" style="width:40px;height:28px;padding:0;border:none;background:none">'
    +'<textarea data-gi="'+i+'" data-gf="maps" rows="2" placeholder="one map name per line" style="width:100%;resize:vertical">'+esc((g.maps||[]).join('\n'))+'</textarea>'
    +'<button class="chip" data-gdel="'+i+'">Delete</button></div>'
  ).join('');
  box.querySelectorAll('[data-gf]').forEach(el=>{
    const i=+el.dataset.gi, f=el.dataset.gf;
    el.onchange=()=>{ if(f==='maps') atlasGroupsData[i].maps=el.value.split('\n').map(x=>x.trim()).filter(Boolean); else atlasGroupsData[i][f]=el.value; saveAtlasGroups(); };
  });
  box.querySelectorAll('[data-gdel]').forEach(b=>b.onclick=()=>{ atlasGroupsData.splice(+b.dataset.gdel,1); renderAtlasGroups(); saveAtlasGroups(); });
}
document.querySelector('#atlasGroupAdd')?.addEventListener('click',()=>{ atlasGroupsData.push({name:'New group',color:'#E0B341',maps:[]}); renderAtlasGroups(); saveAtlasGroups(); });
let atlasHlSort={key:'count',dir:-1};
function renderAtlasHighlight(d){
  const box=$('#atlasHlTable'); if(!box) return;
  let rows=atlasFilterRows(d);
  if(rows.length===0){ box.innerHTML='<span class="hint-row" style="padding:8px;display:block">No filters yet (open the Atlas + Refresh).</span>'; updateHlCount(); return; }
  if(atlasGroup!=='all') rows=rows.filter(r=>r.group===atlasGroup);
  const flt=($('#atlasHlFilter')?.value||'').trim().toLowerCase();
  if(flt) rows=rows.filter(r=>r.title.toLowerCase().includes(flt)||r.cat.toLowerCase().includes(flt)||r.group.toLowerCase().includes(flt));
  if(atlasHlSelOnly) rows=rows.filter(r=>{const k=r.title.toLowerCase(); return atlasHl.has(k)||atlasNav.has(k)||atlasArrow.has(k);});
  const k=atlasHlSort.key, dir=atlasHlSort.dir;
  rows.sort((a,b)=>{
    const ak=a.title.toLowerCase(), bk=b.title.toLowerCase();
    let v;
    if(k==='count') v=a.count-b.count;
    else if(k==='trk') v=(atlasHl.has(ak)?1:0)-(atlasHl.has(bk)?1:0);
    else if(k==='nav') v=(atlasNav.has(ak)?1:0)-(atlasNav.has(bk)?1:0);
    else if(k==='arw') v=(atlasArrow.has(ak)?1:0)-(atlasArrow.has(bk)?1:0);
    else v=(''+a[k]).localeCompare(''+b[k]);
    return v*dir || a.title.localeCompare(b.title);
  });
  const sa=key=> atlasHlSort.key===key ? (atlasHlSort.dir<0?' ▼':' ▲') : '';
  const cell='display:grid;grid-template-columns:30px 30px 34px 1fr 50px 90px;gap:8px;align-items:center;padding:5px 9px';
  let html='<div style="'+cell+';position:sticky;top:0;background:var(--panel,var(--bg-alt));border-bottom:1px solid var(--line);font-weight:600;font-size:11px;text-transform:uppercase;opacity:.75">'
    +'<span data-sort="trk" title="Highlight: ring the map in-game (click to sort)" style="cursor:pointer">&#9745;'+sa('trk')+'</span>'
    +'<span data-sort="nav" title="Nav-to: draw a route to it (click to sort)" style="cursor:pointer">&#8674;'+sa('nav')+'</span>'
    +'<span data-sort="arw" title="Arrow: edge arrow toward it when off-screen (click to sort)" style="cursor:pointer">&#10148;'+sa('arw')+'</span>'
    +'<span data-sort="title" style="cursor:pointer">Title'+sa('title')+'</span>'
    +'<span data-sort="count" style="cursor:pointer;text-align:right">Count'+sa('count')+'</span>'
    +'<span data-sort="cat" style="cursor:pointer">Category'+sa('cat')+'</span></div>';
  html+=rows.map(r=>{
    const key=r.title.toLowerCase(); const trk=atlasHl.has(key), nav=atlasNav.has(key), arw=atlasArrow.has(key);
    return '<div class="hlrow" data-tag="'+esc(r.title)+'" title="click row = toggle Highlight" style="'+cell+';cursor:pointer;border-bottom:1px solid var(--line)'+((trk||nav||arw)?';background:rgba(60,160,255,.14)':'')+'">'
      +'<span style="font-size:15px">'+(trk?'☑':'☐')+'</span>'
      +'<span class="hlnav" data-tag="'+esc(r.title)+'" title="toggle nav-to (route)" style="font-size:15px;cursor:pointer;color:'+(nav?'#3ddc97':'var(--muted)')+'">&#8674;</span>'
      +'<span class="hlarw" data-tag="'+esc(r.title)+'" title="toggle off-screen arrow" style="font-size:15px;cursor:pointer;color:'+(arw?'#e0b341':'var(--muted)')+'">➤</span>'
      +'<span title="'+esc(r.title)+'">'+esc(r.title)+'</span>'
      +'<span class="amono" style="text-align:right">'+r.count+'</span>'
      +'<span>'+catBadge(r.cat)+'</span></div>';
  }).join('');
  box.innerHTML=html;
  $$('#atlasHlTable [data-sort]').forEach(h=>h.onclick=()=>{ const key=h.dataset.sort; if(atlasHlSort.key===key) atlasHlSort.dir*=-1; else atlasHlSort={key,dir:(key==='count'||key==='trk'||key==='nav'||key==='arw')?-1:1}; renderAtlasHighlight(d); });
  $$('#atlasHlTable .hlnav[data-tag]').forEach(a=>a.onclick=e=>{
    e.stopPropagation(); const key=a.dataset.tag.toLowerCase();
    if(atlasNav.has(key)) atlasNav.delete(key); else atlasNav.add(key);
    renderAtlasHighlight(d); postAtlasHighlight();
  });
  $$('#atlasHlTable .hlarw[data-tag]').forEach(a=>a.onclick=e=>{
    e.stopPropagation(); const key=a.dataset.tag.toLowerCase();
    if(atlasArrow.has(key)) atlasArrow.delete(key); else atlasArrow.add(key);
    renderAtlasHighlight(d); postAtlasHighlight();
  });
  $$('#atlasHlTable .hlrow[data-tag]').forEach(row=>row.onclick=()=>{
    const key=row.dataset.tag.toLowerCase();
    if(atlasHl.has(key)) atlasHl.delete(key); else atlasHl.add(key);
    renderAtlasHighlight(d); postAtlasHighlight();
  });
  updateHlCount();
}
// Active-rule chips: one removable chip per tag that has any toggle on, showing which (✓⇢➤). Click ✕ to drop it.
function updateHlCount(){
  const box=$('#atlasActive'); if(!box) return;
  const keys=new Set([...(atlasHl||[]),...(atlasNav||[]),...(atlasArrow||[])]);
  if(keys.size===0){ box.innerHTML='<span class="hint-row" style="opacity:.6">No active rules &mdash; click a row or a Quick set.</span>'; return; }
  // Recover original-case titles from the data.
  const titleOf={}; (atlasData?atlasFilterRows(atlasData):[]).forEach(r=>titleOf[r.title.toLowerCase()]=r.title);
  const chip=k=>{ const t=titleOf[k]||k; const marks=(atlasHl.has(k)?'<span title="Highlight">&#9745;</span>':'')+(atlasNav.has(k)?'<span style="color:#3ddc97" title="Nav">&#8674;</span>':'')+(atlasArrow.has(k)?'<span style="color:#e0b341" title="Arrow">&#10148;</span>':'');
    return '<span class="achip" data-k="'+esc(k)+'" style="display:inline-flex;align-items:center;gap:5px;padding:3px 7px;margin:0 5px 5px 0;border:1px solid var(--line);border-radius:12px;font-size:12px;background:rgba(60,160,255,.10)">'+marks+'<b>'+esc(t)+'</b><span class="achipx" data-k="'+esc(k)+'" style="cursor:pointer;opacity:.6;font-weight:700">&times;</span></span>'; };
  box.innerHTML=[...keys].sort().map(chip).join('');
  $$('#atlasActive .achipx').forEach(x=>x.onclick=()=>{ const k=x.dataset.k; atlasHl.delete(k); atlasNav.delete(k); atlasArrow.delete(k); renderAtlasHighlight(atlasData); postAtlasHighlight(); });
}
// Push the active rules (original-case) to the overlay.
async function postAtlasHighlight(){
  // Build {tag,color,track,nav,arrow} rules: colour = the row's category colour, so in-game rings match the table.
  const rows=atlasData?atlasFilterRows(atlasData):[];
  const rules=rows.filter(r=>{const k=r.title.toLowerCase(); return atlasHl.has(k)||atlasNav.has(k)||atlasArrow.has(k);})
    .map(r=>{const k=r.title.toLowerCase(); return {tag:r.title, color:(CATCOL[r.cat]||'#3ca0ff'), track:atlasHl.has(k), nav:atlasNav.has(k), arrow:atlasArrow.has(k)};});
  try{ await fetch('/api/atlas-highlight',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({rules})}); }catch(e){}
}
$('#atlasHlClear')?.addEventListener('click',()=>{ atlasHl.clear(); atlasNav.clear(); atlasArrow.clear(); if(atlasData) renderAtlasHighlight(atlasData); postAtlasHighlight(); });
$('#atlasHlFilter')?.addEventListener('input',()=>{ if(atlasData) renderAtlasHighlight(atlasData); });
$('#atlasHlSelOnly')?.addEventListener('click',e=>{ atlasHlSelOnly=!atlasHlSelOnly; e.target.classList.toggle('on',atlasHlSelOnly); if(atlasData) renderAtlasHighlight(atlasData); });
$('#atlasHelp')?.addEventListener('click',()=>{ const b=$('#atlasHelpBox'); if(b) b.hidden=!b.hidden; });
// Group filter chips (All / Kind / Content / Map).
$$('[data-group]').forEach(b=>b.addEventListener('click',()=>{ atlasGroup=b.dataset.group; $$('[data-group]').forEach(x=>x.classList.toggle('on',x===b)); if(atlasData) renderAtlasHighlight(atlasData); }));
// Quick presets: select matching rows and flip the relevant toggles in one click.
const ATLAS_PRESETS={
  citadels:{m:r=>r.cat==='Citadel'||/citadel/i.test(r.title), trk:1,nav:1,arw:1},
  deadly:  {m:r=>/deadly/i.test(r.title),                     trk:1,nav:1,arw:0},
  bosses:  {m:r=>/boss/i.test(r.title),                       trk:1,nav:0,arw:0},
  towers:  {m:r=>r.cat==='Tower'||/tower/i.test(r.title),     trk:1,nav:1,arw:0},
  uniques: {m:r=>r.cat==='Unique'||/unique/i.test(r.title),   trk:1,nav:1,arw:0},
};
$$('#atlasPresets [data-preset]').forEach(b=>b.addEventListener('click',()=>{
  const p=ATLAS_PRESETS[b.dataset.preset]; if(!p||!atlasData) return;
  atlasFilterRows(atlasData).filter(p.m).forEach(r=>{ const k=r.title.toLowerCase(); if(p.trk)atlasHl.add(k); if(p.nav)atlasNav.add(k); if(p.arw)atlasArrow.add(k); });
  renderAtlasHighlight(atlasData); postAtlasHighlight();
}));

// Live-nodes grid: each row is a real atlas node. Click a row to SELECT it → the overlay highlights
// it in-game (projection calibration loop). Selection is the set of element addresses.
function renderAtlasNodes(d, f){
  let list=d.nodeList||[];
  if(f) list=list.filter(n=> (''+n.id).includes(f) || biomeName(n.biome).toLowerCase().includes(f)
      || (n.map||'').toLowerCase().includes(f) || (n.hasContent&&'content'.includes(f))
      || (!n.visited&&'unvisited'.includes(f)) || ('biome '+n.biome).includes(f)
      || (n.tags||[]).some(t=>t.toLowerCase().includes(f)));   // match on map name + content names
  if(list.length===0){ $('#atlasList').innerHTML='<div class="hint-row">No live nodes (open the Atlas in-game, then Refresh).</div>'; return; }
  // Content nodes first (the interesting ones), then by tag count.
  list=list.slice().sort((a,b)=>((b.tags||[]).length)-((a.tags||[]).length));
  const head='<div class="arow ahead nrow"><span>Map</span><span>Content</span><span>Biome</span><span>Pos</span></div>';
  const body=list.slice(0,1200).map(n=>{
    const sel=atlasSel.has(n.el)?' sel':'';
    const hot=((n.map&&atlasHl.has(n.map.toLowerCase()))||(n.tags||[]).some(t=>atlasHl.has(t.toLowerCase())));
    const val=(n.tags&&n.tags.length)?' val':'';
    const content=(n.tags||[]).map(t=>'<span class="ntag tc">'+esc(t)+'</span>').join(' ')||'<span class="hint-row">—</span>';
    return '<div class="arow nrow'+val+sel+(hot?' sel':'')+'" data-el="'+esc(n.el)+'">'
      +'<span title="'+esc(n.map||'')+'">'+esc(n.map||'—')+(n.visited?' <span class="ntag tv">✓</span>':'')+'</span>'
      +'<span>'+content+'</span><span>'+esc(biomeName(n.biome))+'</span>'
      +'<span class="amono">('+n.x+','+n.y+')</span></div>';
  }).join('');
  $('#atlasList').innerHTML=head+body
    +'<div class="hint-row" style="margin-top:10px"><b>Click a node row to highlight it in-game</b> (drives the overlay’s atlas highlight — use it to confirm positions / calibrate). Click again to deselect. Showing '+Math.min(list.length,1200)+' of '+list.length+' nodes.</div>';
  $$('#atlasList .nrow[data-el]').forEach(row=>row.onclick=()=>{
    const el=row.dataset.el;
    if(atlasSel.has(el)) atlasSel.delete(el); else atlasSel.add(el);
    row.classList.toggle('sel',atlasSel.has(el));
    postAtlasSel();
  });
}
async function postAtlasSel(){ try{ await fetch('/api/atlas-select',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({els:[...atlasSel]})}); }catch(e){} }

$('#atlasRefresh')?.addEventListener('click',loadAtlas);
$('#atlasSearch')?.addEventListener('input',()=>{ if(atlasData) renderAtlas(); });
$$('#atlasViewCatalog,#atlasViewRegion,#atlasViewNodes').forEach(b=>b?.addEventListener('click',()=>{
  atlasView=b.dataset.view;
  $$('#atlasViewCatalog,#atlasViewRegion,#atlasViewNodes').forEach(x=>x.classList.toggle('on',x===b));
  renderAtlas();
}));

/* ── session HUD live panel ── */
function renderSessionPanel() {
    const s = state && state.session;
    const el = document.getElementById('session-panel');
    if (!el) return;
    if (!s) { el.style.display = 'none'; return; }
    el.style.display = '';
    document.getElementById('sp-session').textContent = s.sessionElapsed || '—';
    document.getElementById('sp-zone').textContent    = s.zoneElapsed    ?? '—';
    document.getElementById('sp-zones').textContent   = s.zonesEntered != null
        ? `${s.zonesEntered} (${(s.zonesPerHour||0).toFixed(1)}/hr)` : '—';
    document.getElementById('sp-area').textContent    = s.currentZoneName || '—';
    document.getElementById('sp-level').textContent   = s.currentAreaLevel ?? '—';
    document.getElementById('sp-deaths').textContent  = s.deaths != null
        ? `${s.deaths} (${s.deathsThisZone ?? 0} here)` : '—';
}

/* ── Session Recap PNG (dashboard) ── */
async function saveSessionRecapPng() {
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
    ['Maps / hr',       fmtRecapNum(s.mapsPerHour, 2)],
    ['XP Efficiency',   fmtRecapNum(s.xpEfficiency, 2)],
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

function fmtRecapNum(n, digits) {
  if (n == null || n === '' || isNaN(n)) return '\u2014';
  return Number(n).toFixed(digits);
}

function fmtRecapDur(sec) {
  if (sec == null || isNaN(sec)) return '\u2014';
  sec = Math.max(0, Math.floor(sec));
  const h = Math.floor(sec / 3600);
  const m = Math.floor((sec % 3600) / 60);
  const s = sec % 60;
  return (h > 0 ? h + 'h ' : '') + (m + 'm ') + s + 's';
}

/* ── left rail ── */
function renderState(){
  const s=state; if(!s) return;
  // Patch-resilience Status panel: derive the three ticks from healthState (see OffsetHealthMonitor).
  const hsName = s.healthState || 'searching';
  const stTick = ok => ok
    ? '<span style="color:var(--good)">&#10003;</span>'
    : '<span style="color:var(--ink-faint)">&#9675;</span>';
  $('#stAttach').innerHTML = stTick(hsName !== 'waiting');
  $('#stZone').innerHTML   = stTick(hsName === 'ok' || hsName === 'loading');
  $('#stPlayer').innerHTML = stTick(hsName === 'ok');
  const stRow = $('#stMsgRow'), stMsgEl = $('#stMsg');
  if (s.healthMessage) {
    stRow.hidden = false;
    stMsgEl.textContent = '⚠ ' + s.healthMessage;
    stMsgEl.style.color = (hsName === 'broken') ? 'var(--blood)' : 'var(--gold)';
  } else { stRow.hidden = true; stMsgEl.textContent = ''; }
  const rsRow = $('#stRescanRow');
  if (rsRow) rsRow.hidden = (hsName === 'ok');
  // Masthead game-health pill (visible on every tab): green ok / amber connecting / red broken / grey benign.
  const hd = $('#healthDot'), ht = $('#healthTxt'), hc = $('#health');
  if (hd) {
    const map = { ok: ['var(--good)', 'in game'], searching: ['var(--gold)', 'connecting'],
      loading: ['var(--gold)', 'loading'], broken: ['var(--blood)', 'out of date?'],
      notingame: ['var(--ink-faint)', 'menu'], waiting: ['var(--ink-faint)', 'no game'] };
    const [col, lbl] = map[hsName] || map.searching;
    hd.style.background = col; ht.textContent = lbl; hc.title = s.healthMessage || '';
  }
  const hp=Math.max(0,Math.min(100,s.hpPct||0)), mp=Math.max(0,Math.min(100,s.manaPct||0)), es=Math.max(0,Math.min(100,s.esPct||0));
  $('#hpBar').style.width=hp+'%'; $('#mpBar').style.width=mp+'%'; $('#esBar').style.width=es+'%';
  $('#hpNum').textContent=hp.toFixed(0)+'%'; $('#mpNum').textContent=mp.toFixed(0)+'%'; $('#esNum').textContent=es.toFixed(0)+'%';
  const areaName=(s.areaName&&s.areaName!==s.areaCode)?s.areaName:'';
  $('#kAreaName').textContent=areaName||s.areaCode||'—';
  $('#kArea').textContent=s.areaCode||'—';
  const act=s.areaAct||0;
  $('#kAlvl').textContent=(act?'Act '+act+' · ':'')+(s.areaLevel?('lvl '+s.areaLevel):'—');
  $('#kMap').textContent=s.mapVisible?'yes':'no';
  $('#cEnt').textContent=s.entityCount||0;
  $('#cPoi').textContent=s.poiCount||0;
  $('#cMon').textContent=(s.counts&&s.counts.Monster)||0;
  $('#cLm').textContent=s.landmarkCount||0;
  $('#areaChip').innerHTML = (areaName||s.areaCode||'—') + ' <b>·</b> ' + (s.inGame?'in game':'town/menu');

  // Runeshape monoliths (from /state): each monolith's value-tier header (best ex · anchor · N holes)
  // with its priced reward rows. Sorted server-side by value; hidden when the area has none.
  const mc=$('#monoCard'), ml=$('#monoList');
  const monos=(s.monoliths||[]).slice().sort((a,b)=>(b.bestEx||0)-(a.bestEx||0));
  if(monos.length){
    mc.hidden=false;
    ml.innerHTML = monos.map(m=>{
      const tier = (m.bestEx||0)>=30 ? '#66e066' : (m.bestEx||0)>=18 ? '#e6c84d' : '#cfcfcf';
      const hdr = (m.bestEx>0?('<b style="color:'+tier+'">'+Math.round(m.bestEx)+' ex</b> · '):'')
                + esc(m.anchor||'?') + ' · ' + (m.holes||0) + 'h' + (m.collected?' · <span style="opacity:.6">collected</span>':'');
      const rows=(m.rewards||[]).filter(r=>r.ex>0).slice(0,6)
        .map(r=>'<div style="display:flex;justify-content:space-between;gap:8px"><span>'+esc(r.name)+(r.count>1?(' ×'+r.count):'')+'</span><span style="opacity:.85">'+Math.round(r.ex)+' ex</span></div>').join('');
      return '<div style="margin:0 0 9px"><div style="margin-bottom:2px">'+hdr+'</div>'
           + '<div style="font-size:12px;opacity:.9;padding-left:8px">'+(rows||'<span style="opacity:.6">no priced rewards</span>')+'</div></div>';
    }).join('');
  } else { mc.hidden=true; ml.innerHTML=''; }

  // Objective Director (from /state): the active objective then the queued ones, priority order.
  const dc=$('#dirCard'), dl=$('#dirList');
  const dir=(s.director||[]);
  if(dir.length){
    dc.hidden=false;
    dl.innerHTML = dir.map((o,i)=>
      '<div style="display:flex;justify-content:space-between;gap:8px'+(i===0?';font-weight:600':';opacity:.75')+'">'
      + '<span>'+(i===0?'▶ ':'')+esc(o.label)+'</span>'
      + '<span style="opacity:.7">'+esc(o.category)+'</span></div>').join('');
  } else { dc.hidden=true; dl.innerHTML=''; }

  // Zone leveling notes (from /api/zone): title + note text, hidden when there's nothing to show.
  const zn=$('#zoneNotes');
  if(zone && (zone.notes||'').trim()){
    zn.hidden=false;
    zn.innerHTML='<div class="zt">'+esc(zone.title||zone.name||'')+'</div>'+esc(zone.notes);
  } else { zn.hidden=true; }

  renderDirectorQueue();
  renderSessionPanel();
}

// Update banner: show a download link if a newer version exists on GitHub (best-effort).
async function checkVersion(){
  try{
    const v=await getJSON('/api/version');
    if(v && v.updateAvailable){
      const b=$('#updateBanner'); if(!b) return;
      const m=$('#updateMsg'); if(m) m.textContent=' — '+(v.latest||'')+' (you have v'+(v.current||'?')+')';
      b.href=v.url||'#'; b.hidden=false; b.style.display='flex';
    }
    renderAuPending(v);
  }catch(e){}
}

/* ── entity arrows card (writes via /api/settings as whole entityArrows object) ── */
let ea = {};
function renderEntityArrows(){
  if(!ea) return;
  document.querySelectorAll('[data-ea]').forEach(el=>{
    const k=el.dataset.ea;
    if(el.type==='checkbox') el.checked=!!ea[k];
    else if(ea[k]!==undefined && ea[k]!==null) el.value=ea[k];
  });
}
function wireEntityArrows(){
  document.querySelectorAll('[data-ea]').forEach(el=>{
    const k=el.dataset.ea;
    const upd=()=>{
      ea=ea||{};
      ea[k]=el.type==='checkbox'?el.checked:(el.type==='number'?parseFloat(el.value||'0'):el.value);
      saveSetting('entityArrows', ea);
    };
    el.onchange=upd;
  });
}

/* ── OBS overlay card (writes via /api/settings as whole obsOverlay object) ── */
let obsOvr = {};
function saveObsOverlay(){ saveSetting('obsOverlay', obsOvr); }
function renderObsOverlay(){
  if(!obsOvr) return;
  const map={showSessionTimer:'obsShowSessionTimer',showZoneTimer:'obsShowZoneTimer',
    showArea:'obsShowArea',showKills:'obsShowKills',showMapsHr:'obsShowMapsHr',
    showXpEff:'obsShowXpEff',showObjective:'obsShowObjective'};
  for(const[k,id] of Object.entries(map)){ const el=document.getElementById(id); if(el) el.checked=!!obsOvr[k]; }
  const tc=document.getElementById('obsTextColor'); if(tc) tc.value=obsOvr.textColor||'#ffffff';
  const op=document.getElementById('obsPanelOpacity'); if(op) op.value=obsOvr.panelOpacity??40;
  const sc=document.getElementById('obsScale'); if(sc) sc.value=obsOvr.scale??1;
  const co=document.getElementById('obsCorner'); if(co) co.value=obsOvr.corner||'top-left';
}
function wireObsOverlay(){
  const boolMap={showSessionTimer:'obsShowSessionTimer',showZoneTimer:'obsShowZoneTimer',
    showArea:'obsShowArea',showKills:'obsShowKills',showMapsHr:'obsShowMapsHr',
    showXpEff:'obsShowXpEff',showObjective:'obsShowObjective'};
  for(const[k,id] of Object.entries(boolMap)){
    const el=document.getElementById(id);
    if(el) el.onchange=()=>{ obsOvr=obsOvr||{}; obsOvr[k]=el.checked; saveObsOverlay(); };
  }
  const tc=document.getElementById('obsTextColor');
  if(tc) tc.onchange=()=>{ obsOvr=obsOvr||{}; obsOvr.textColor=tc.value; saveObsOverlay(); };
  const op=document.getElementById('obsPanelOpacity');
  if(op) op.onchange=()=>{ const v=parseFloat(op.value); if(!isNaN(v)){ obsOvr=obsOvr||{}; obsOvr.panelOpacity=Math.max(0,Math.min(100,v)); saveObsOverlay(); }};
  const sc=document.getElementById('obsScale');
  if(sc) sc.onchange=()=>{ const v=parseFloat(sc.value); if(!isNaN(v)){ obsOvr=obsOvr||{}; obsOvr.scale=Math.max(0.5,Math.min(3.0,v)); saveObsOverlay(); }};
  const co=document.getElementById('obsCorner');
  if(co) co.onchange=()=>{ obsOvr=obsOvr||{}; obsOvr.corner=co.value; saveObsOverlay(); };
  document.getElementById('obsCopyUrl')?.addEventListener('click',()=>{
    navigator.clipboard.writeText('http://localhost:7777/obs').catch(()=>{});
    const b=document.getElementById('obsCopyUrl'); if(b){ const t=b.textContent; b.textContent='Copied!'; setTimeout(()=>b.textContent=t,1200); }
  });
}

/* ── Discord Rich Presence card (writes via /api/settings as whole discordPresence object) ── */
let discordPres = {};
function saveDiscordPresence(){ saveSetting('discordPresence', discordPres); }
function renderDiscordPresence(){
  if(!discordPres) return;
  const en=document.getElementById('dpEnabled'); if(en) en.checked=!!discordPres.enabled;
  // clientId is intentionally omitted from the GET response (stream-safe) — field loads blank by design
  const dt=document.getElementById('dpDetailsTemplate'); if(dt) dt.value=discordPres.detailsTemplate||'{area}';
  const st=document.getElementById('dpStateTemplate');   if(st) st.value=discordPres.stateTemplate||'Level {level} · {mapshr} maps/hr';
  const ti=document.getElementById('dpShowTimer');       if(ti) ti.checked=discordPres.showTimer!==false;
}
function wireDiscordPresence(){
  const en=document.getElementById('dpEnabled');
  if(en) en.onchange=()=>{ discordPres=discordPres||{}; discordPres.enabled=en.checked; saveDiscordPresence(); };
  const ci=document.getElementById('dpClientId');
  // blank clientId = "keep the stored one" (backend preserves it on empty POSTed value)
  if(ci) ci.onchange=()=>{ discordPres=discordPres||{}; discordPres.clientId=ci.value; saveDiscordPresence(); };
  const dt=document.getElementById('dpDetailsTemplate');
  if(dt) dt.onchange=()=>{ discordPres=discordPres||{}; discordPres.detailsTemplate=dt.value; saveDiscordPresence(); };
  const st=document.getElementById('dpStateTemplate');
  if(st) st.onchange=()=>{ discordPres=discordPres||{}; discordPres.stateTemplate=st.value; saveDiscordPresence(); };
  const ti=document.getElementById('dpShowTimer');
  if(ti) ti.onchange=()=>{ discordPres=discordPres||{}; discordPres.showTimer=ti.checked; saveDiscordPresence(); };
}
function updateDiscordPreview(s){
  const prev=document.getElementById('dpPreview'); if(!prev) return;
  // Groove — v0.24: preview tokens extended to match the server-side dictionary. hp/mana/es are
  // rounded from RadarState floats; boss lights up when the current entity list carries any Unique.
  const uniqueCount=(s.entities||[]).filter(e=>e && (e.rarity===3||e.rarity==='Unique')).length;
  const toks={'area':s.areaName||s.areaCode||'','level':s.charLevel||0,
    'hp':Math.round(s.hpPct??100),'mana':Math.round(s.manaPct??100),'es':Math.round(s.esPct??100),
    'zones':s.session?.zonesEntered??0,
    'mapshr':(s.session?.mapsPerHour??0).toFixed(1),'kills':((s.session?.killsNormal??0)+(s.session?.killsMagic??0)+(s.session?.killsRare??0)+(s.session?.killsUnique??0)),
    'deaths':s.session?.deaths??0,'xpeff':(s.session?.xpEfficiency??0),
    'boss':uniqueCount>0?'in boss arena':''};
  function fmt(t){ return (t||'').replace(/\{(\w+)\}/g,(_,k)=>toks[k]??'{'+k+'}'); }
  const dt=document.getElementById('dpDetailsTemplate');
  const st=document.getElementById('dpStateTemplate');
  prev.textContent=fmt(dt?.value||'{area}')+'\n'+fmt(st?.value||'Level {level} · {mapshr} maps/hr');
}

/* ── Remote Access (LAN) card ── */
async function renderLanInfo(){
  try{
    const li=await getJSON('/api/lan-info');
    const box=document.getElementById('lanUrls'); if(!box) return;
    if(!li.addresses||!li.addresses.length){ box.innerHTML='<code style="font-size:12px;color:var(--ink-faint)">no LAN address detected</code>'; return; }
    const note = li.bindFailed
      ? '<small style="color:var(--danger)">LAN bind failed &mdash; running loopback-only. Restart POE2GPS as administrator.</small>'
      : (li.bound==='lan' ? '' : '<small style="color:var(--ink-faint)">LAN access is off &mdash; toggle it on above, then restart.</small>');
    box.textContent='';
    const dim = li.bound!=='lan';
    li.addresses.forEach(a=>{
      ['map','obs'].forEach(p=>{
        const c=document.createElement('code');
        c.style.cssText='font-size:12px;color:'+(dim?'var(--ink-faint)':'var(--gold-bright)');
        c.textContent='http://'+a+':'+li.port+'/'+p;
        box.appendChild(c);
      });
    });
    if(note){ const n=document.createElement('small'); n.innerHTML=note; box.appendChild(n); }
  }catch(e){}
}

/* ── auto-update card (writes via /api/settings as autoUpdate object) ── */
let autoUpd = { mode: 'silent' };
function renderAutoUpdate(s){
  if(s && s.autoUpdate) autoUpd = s.autoUpdate;
  const sel=document.getElementById('au-mode'); if(sel) sel.value = autoUpd.mode || 'silent';
}
function saveAutoUpdate(){
  const sel=document.getElementById('au-mode'); if(!sel) return;
  autoUpd = { mode: sel.value };
  saveSetting('autoUpdate', autoUpd);
}
function wireAutoUpdate(){
  const sel=document.getElementById('au-mode'); if(sel) sel.onchange = saveAutoUpdate;
}
function renderAuPending(v){
  const el=document.getElementById('au-pending'); if(!el) return;
  el.textContent = (v && v.pendingVersion) ? ('Update '+v.pendingVersion+' downloaded — installs on next launch.') : '';
}

/* ── affix nameplates card (own endpoint /api/affix-nameplates) ── */
function renderAffixNameplates(){
  if(!an) return;
  document.querySelectorAll('[data-an]').forEach(el=>{
    const k=el.dataset.an;
    if(el.type==='checkbox') el.checked=!!an[k];
    else if(an[k]!==undefined) el.value=an[k];
  });
  renderAnOverrides();
}
async function saveAffixNameplates(){
  try{ await fetch('/api/affix-nameplates',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(an)});
    const m=$('#savedMsg'); if(m){m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100);} }catch(e){}
}
function wireAffixNameplates(){
  document.querySelectorAll('[data-an]').forEach(el=>{
    const k=el.dataset.an;
    const upd=()=>{ an = an||{}; an[k] = el.type==='checkbox'?el.checked : (el.type==='number'?parseInt(el.value||'0',10):el.value); saveAffixNameplates(); };
    if(el.type==='checkbox'||el.tagName==='SELECT') el.onchange=upd; else el.onchange=upd;
  });
  const s=document.getElementById('anSearch'); if(s) s.oninput=renderAnOverrides;
}
async function loadAffixCatalog(){ try{ const r=await getJSON('/api/affix-catalog'); anCatalog=r.affixes||[]; renderAnOverrides(); }catch(e){} }
function renderAnOverrides(){
  const box=document.getElementById('anOverrides'); if(!box||!an) return;
  const q=(document.getElementById('anSearch')?.value||'').toLowerCase();
  const always=new Set(an.alwaysShow||[]), hide=new Set(an.hide||[]);
  box.innerHTML = anCatalog.filter(a=>!q||a.name.toLowerCase().includes(q)||a.modId.toLowerCase().includes(q)).slice(0,300).map(a=>{
    const st = always.has(a.modId)?'show':hide.has(a.modId)?'hide':'';
    return `<div class="row" style="gap:6px"><div class="rl">${a.name} <small>${a.tier}${a.curated?'':' · seen'}</small></div>
      <select class="numin" data-anov="${a.modId}"><option value=""${st===''?' selected':''}>—</option><option value="show"${st==='show'?' selected':''}>Always</option><option value="hide"${st==='hide'?' selected':''}>Hide</option></select></div>`;
  }).join('');
  box.querySelectorAll('[data-anov]').forEach(sel=>{ sel.onchange=()=>{
    const id=sel.dataset.anov; an.alwaysShow=(an.alwaysShow||[]).filter(x=>x!==id); an.hide=(an.hide||[]).filter(x=>x!==id);
    if(sel.value==='show') an.alwaysShow.push(id); else if(sel.value==='hide') an.hide.push(id);
    saveAffixNameplates();
  };});
}
/* ── buff icons card (own endpoint /api/buff-nameplates) ── */
function renderBuffNameplates(){
  if(!bn) return;
  document.querySelectorAll('[data-bn]').forEach(el=>{
    const k=el.dataset.bn;
    if(el.type==='checkbox') el.checked=!!bn[k];
    else if(bn[k]!==undefined) el.value=bn[k];
  });
}
async function saveBuffNameplates(){
  try{ await fetch('/api/buff-nameplates',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(bn)});
    const m=$('#savedMsg'); if(m){m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100);} }catch(e){}
}
function wireBuffNameplates(){
  document.querySelectorAll('[data-bn]').forEach(el=>{
    const k=el.dataset.bn;
    el.onchange=()=>{ bn = bn||{}; bn[k] = el.type==='checkbox'?el.checked : (el.type==='number'?parseInt(el.value||'0',10):el.value); saveBuffNameplates(); };
  });
}
async function renderBnObserved(){
  const box=document.getElementById('bnObserved'); if(!box) return;
  try{ const r=await getJSON('/api/buffs'); const list=r.buffs||[];
    box.innerHTML = list.length ? list.slice(0,200).map(b=>`<div class="row"><div class="rl">${b.id} <small>${b.tier}</small></div></div>`).join('')
                                : '<div class="row"><div class="rl"><small>none observed yet</small></div></div>';
  }catch(e){}
}
wireSettings(); wireHpBars(); wireTerrain(); wireGround(); wireAffixNameplates(); wireBuffNameplates(); wireEntityArrows(); wireObsOverlay(); wireDiscordPresence(); wireAutoUpdate();
document.querySelectorAll('[data-audiotest]').forEach(b=>b.onclick=()=>fetch('/api/audio-test',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({cue:b.dataset.audiotest})}));
loadLabelVocab();
loadIcons().then(()=>{ loadSettings(); loadFilters(); loadPresets(); loadKeybinds(); loadQuickStart(); }); // Rules is the default tab
tick(); setInterval(tick, 1000);
checkVersion();

/* ── preload alert diagnostic poller ── */
let _preloadPollId=null;
function startPreloadPoll(){
  if(_preloadPollId) return;
  _preloadPollId=setInterval(async()=>{
    const panel=$('#preloadDiagPanel'); if(!panel||panel.style.display==='none') return;
    try{
      const d=await getJSON('/api/preload');
      const hitsEl=$('#preloadHits');
      if(hitsEl){
        if(!d.enabled){ hitsEl.textContent='(disabled)'; }
        else if(!d.hits||!d.hits.length){ hitsEl.textContent='No hits this zone.'; }
        else{ hitsEl.innerHTML=d.hits.map(h=>'<span style="margin-right:8px;color:'+esc(h.Color||'#ccc')+'">'+esc(h.Label||h.Tier)+'</span>').join(''); }
      }
      const tblEl=$('#preloadFreqTable');
      if(tblEl&&d.diagnostic&&d.diagnostic.length){
        tblEl.innerHTML='<table style="width:100%;border-collapse:collapse"><thead><tr>'
          +'<th style="text-align:left;padding:2px 6px;color:var(--ink-faint)">Path</th>'
          +'<th style="text-align:right;padding:2px 6px;color:var(--ink-faint)">Hits</th>'
          +'<th style="text-align:right;padding:2px 6px;color:var(--ink-faint)">Freq</th>'
          +'</tr></thead><tbody>'
          +d.diagnostic.map(r=>'<tr><td style="padding:1px 6px;word-break:break-all;color:var(--ink)">'+esc(r.path)+'</td>'
            +'<td style="padding:1px 6px;text-align:right;color:var(--ink-faint)">'+r.hits+'</td>'
            +'<td style="padding:1px 6px;text-align:right;color:var(--ink-faint)">'+(r.freq*100).toFixed(1)+'%</td></tr>').join('')
          +'</tbody></table>';
      } else if(tblEl&&(!d.diagnostic||!d.diagnostic.length)){
        tblEl.innerHTML='<div style="color:var(--ink-faint);padding:4px 6px">'+(d.diagnostic?'No frequency data yet.':'Diagnostic mode is off — enable above.')+'</div>';
      }
    }catch(e){}
  },2000);
}
// Show/hide diagnostic panel based on preloadDiagnostic checkbox
(function(){
  const cb=document.querySelector('[data-set="preloadDiagnostic"]');
  const panel=$('#preloadDiagPanel');
  if(cb&&panel){
    function syncDiagPanel(){ panel.style.display=cb.checked?'block':'none'; if(cb.checked) startPreloadPoll(); }
    cb.addEventListener('change',syncDiagPanel);
    // Also sync after loadSettings runs (settings may set checked before we wire it)
    const origLoad=window._origLoadSettings||loadSettings;
    window._origLoadSettings=origLoad;
    window._syncDiagPanel=syncDiagPanel;
  }
})();
/* ── presets card (export copy/download + import paste/file) ── */
function flashPreset(msg){ const m=$('#savedMsgPreset'); if(!m) return; m.textContent=msg||'✓ preset applied'; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1800); }
async function presetExport(){ return await (await fetch('/api/preset/export',{cache:'no-store'})).json(); }
$('#presetCopy')?.addEventListener('click',async()=>{ try{ const p=await presetExport(); await navigator.clipboard.writeText(p.code); flashPreset('✓ share-code copied'); }catch(e){ flashPreset('copy failed'); } });
$('#presetDownload')?.addEventListener('click',async()=>{ try{ const p=await presetExport(); const a=document.createElement('a'); a.href=URL.createObjectURL(new Blob([p.json],{type:'application/json'})); a.download='radar-preset.poe2preset'; a.click(); URL.revokeObjectURL(a.href); }catch(e){} });
async function presetImport(body){ if(!confirm('Applying a preset replaces your display rules and visual styles. A backup is saved first. Continue?')) return; try{ const r=await fetch('/api/preset/import',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}); if(r.ok){ await loadSettings(); flashPreset('✓ preset applied'); } else flashPreset('import rejected'); }catch(e){ flashPreset('import failed'); } }
$('#presetApplyCode')?.addEventListener('click',()=>{ const c=$('#presetCode').value.trim(); if(c) presetImport({code:c}); });
$('#presetFile')?.addEventListener('change',e=>{ const f=e.target.files&&e.target.files[0]; if(!f) return; const rd=new FileReader(); rd.onload=()=>{ try{ presetImport(JSON.parse(rd.result)); }catch(_){ flashPreset('invalid preset file'); } }; rd.readAsText(f); e.target.value=''; });
async function presetApply(name){ if(!confirm('Applying a preset replaces your display rules and visual styles. A backup is saved first. Continue?')) return; try{ const r=await fetch('/api/preset/apply',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})}); if(r.ok){ await loadSettings(); flashPreset('✓ preset applied'); loadPresets(); } else flashPreset('apply rejected'); }catch(e){ flashPreset('apply failed'); } }
async function loadPresets(){ try{ const r=await (await fetch('/api/preset/list',{cache:'no-store'})).json(); const el=$('#presetList'); if(!el) return; el.innerHTML=''; (r.presets||[]).forEach(p=>{ const row=document.createElement('div'); row.className='row'; row.innerHTML='<div class="rl">'+(p.builtIn?'⭐ ':'')+p.name+'</div>'; const apply=document.createElement('button'); apply.className='addbtn'; apply.textContent='Apply'; apply.onclick=()=>presetApply(p.name); row.appendChild(apply); if(!p.builtIn){ const del=document.createElement('button'); del.className='addbtn'; del.textContent='Delete'; del.onclick=async()=>{ if(!confirm('Delete preset "'+p.name+'"?')) return; await fetch('/api/preset/delete',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:p.name})}); loadPresets(); flashPreset('✓ deleted'); }; row.appendChild(del); } el.appendChild(row); }); }catch(e){} }
$('#presetSave')?.addEventListener('click',async()=>{ const name=$('#presetSaveName').value.trim(); if(!name) return; const r=await fetch('/api/preset/save',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})}); if(r.ok){ $('#presetSaveName').value=''; loadPresets(); flashPreset('✓ saved'); } else flashPreset('save rejected'); });

$('#stRescanBtn')?.addEventListener('click', async () => {
  const b = $('#stRescanBtn'), t = b.textContent;
  b.textContent = 're-scanning…'; b.disabled = true;
  try { await fetch('/api/rescan', { method: 'POST' }); } catch (e) {}
  setTimeout(() => { b.textContent = t; b.disabled = false; }, 2000);
});

/* ── Keybinds card ── */
const KB_CODE_TO_VK = (()=>{
  const m={};
  for(let i=1;i<=12;i++) m['F'+i]=0x6F+i;             // F1=0x70..F12=0x7B
  'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('').forEach((c,i)=>m['Key'+c]=0x41+i);
  '0123456789'.split('').forEach((c,i)=>m['Digit'+c]=0x30+i);
  Object.assign(m,{BracketRight:0xDD,BracketLeft:0xDB,Semicolon:0xBA,Slash:0xBF,
    Minus:0xBD,Equal:0xBB,Backquote:0xC0,Quote:0xDE,Backslash:0xDC,
    Comma:0xBC,Period:0xBE,Space:0x20});
  return m;
})();
function flashKb(msg){ const m=$('#savedMsgKb'); if(!m) return; m.textContent=msg||'✓ saved'; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1800); }
let _kbCapturing=null; // action name currently in capture mode, or null
function kbCancelCapture(){
  if(!_kbCapturing) return;
  const row=document.querySelector('[data-kb="'+_kbCapturing+'"]');
  if(row) row.querySelector('.kbrebind').textContent='Rebind';
  _kbCapturing=null;
  document.removeEventListener('keydown',_kbKeydown,true);
}
function _kbKeydown(e){
  e.preventDefault(); e.stopPropagation();
  if(/^(Control|Alt|Shift|Meta)/.test(e.key)) return; // ignore bare modifier presses
  const vk=KB_CODE_TO_VK[e.code];
  const action=_kbCapturing;
  kbCancelCapture();
  if(!vk||!action) return;
  fetch('/api/keybinds',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action,vk})})
    .then(r=>{ if(r.status===409){ flashKb('⚠ already bound'); } else if(r.ok){ loadKeybinds(); flashKb('✓ saved'); } else { flashKb('error'); } })
    .catch(()=>flashKb('error'));
}
async function loadKeybinds(){
  kbCancelCapture();
  try{
    const list=await getJSON('/api/keybinds');
    const el=$('#kbRows'); if(!el) return;
    el.innerHTML='';
    list.forEach(kb=>{
      const row=document.createElement('div'); row.className='row'; row.dataset.kb=kb.action;
      const rl=document.createElement('div'); rl.className='rl'; rl.textContent=kb.action;
      const badge=document.createElement('span'); badge.className='numin'; badge.style.cssText='min-width:110px;text-align:center;display:inline-block;font-family:ui-monospace,Consolas,monospace;font-size:12px'; badge.textContent=kb.label;
      const btn=document.createElement('button'); btn.className='kbrebind addbtn'; btn.textContent='Rebind';
      btn.onclick=()=>{
        if(_kbCapturing===kb.action){ kbCancelCapture(); return; }
        kbCancelCapture();
        _kbCapturing=kb.action;
        btn.textContent='press a key…';
        document.addEventListener('keydown',_kbKeydown,true);
      };
      row.appendChild(rl); row.appendChild(badge); row.appendChild(btn);
      el.appendChild(row);
    });
  }catch(e){}
}
$('#kbReset')?.addEventListener('click',async()=>{
  if(!confirm('Reset all keybinds to defaults?')) return;
  try{
    const r=await fetch('/api/keybinds/reset',{method:'POST'});
    if(r.ok){ await loadKeybinds(); flashKb('✓ reset to defaults'); } else flashKb('reset failed');
  }catch(e){ flashKb('error'); }
});

/* ── Settings: search filter + collapsible cards ── */
(function(){
  // Inject chevron spans into all collapsible card headings (those with data-card).
  function initSettingsCards(){
    const view=document.querySelector('[data-view="settings"]'); if(!view) return;
    view.querySelectorAll('.card[data-card]').forEach(card=>{
      const key='settings-card-'+card.dataset.card;
      // Restore collapsed state from localStorage.
      if(localStorage.getItem(key)==='1') card.classList.add('collapsed');
      // Add chevron to h3 (once; guard idempotency).
      const h3=card.querySelector('h3'); if(!h3) return;
      if(!h3.querySelector('.chevron')){
        const ch=document.createElement('span'); ch.className='chevron'; ch.textContent='▾'; h3.appendChild(ch);
      }
      // Toggle collapse on h3 click.
      h3.onclick=()=>{
        card.classList.toggle('collapsed');
        localStorage.setItem(key, card.classList.contains('collapsed')?'1':'0');
      };
    });
  }

  // Search filter: hide cards whose text doesn't match the query (never hide statusCard or qsCard).
  function applySettingsSearch(q){
    const view=document.querySelector('[data-view="settings"]'); if(!view) return;
    view.querySelectorAll('.card').forEach(card=>{
      if(card.id==='statusCard'||card.id==='qsCard'){ card.hidden=false; return; }

      // Reset any prior no-match hide so this iteration can unhide it if it now matches.
      card.hidden = false;

      // Empty query → everything visible; nothing more to do.
      if (!q) return;

      // Scope search corpus based on collapse state: collapsed cards match on title only,
      // since their body is display:none via `.card.collapsed > :not(h3) { display: none }`.
      const isCollapsed = card.classList.contains('collapsed');
      const corpus = isCollapsed
        ? (card.querySelector('h3')?.textContent ?? '')
        : card.textContent;

      if (corpus.toLowerCase().indexOf(q) === -1) {
        card.hidden = true;
      }
    });
  }

  // Wire up the search box.
  const srch=document.getElementById('settingsSearch');
  if(srch){
    srch.addEventListener('input',()=>applySettingsSearch(srch.value.toLowerCase().trim()));
    // Clear search when leaving the tab so state is clean on re-enter.
  }

  // Hook the settings tab click to init cards and clear search on each activation.
  document.querySelectorAll('.tab[data-tab="settings"]').forEach(tab=>{
    tab.addEventListener('click',()=>{ initSettingsCards(); if(srch){ srch.value=''; applySettingsSearch(''); } });
  });
  // Also run once immediately in case the settings view is pre-shown or for the initial page load.
  initSettingsCards();
})();

/* ── Reach — v0.26 (CHOR-42): boss cheat sheet loader ──────────────────────────────────────────
   Fetches /api/bosses once on first Bosses-tab activation. Renders the shipped catalog as a list
   of cards showing name, tier, damage-type row (color-coded), one-shots to dodge, overcap
   thresholds, flask notes, and phase transitions. No search / filter yet — small catalog. */
let __bossesLoaded = false;
/* v0.30 Instinct: per-character wipe log. Fetches /api/wipe-log alongside the boss cheat sheets so
   the "Your wipes" card shows a table for the current character. Endpoint payload:
   { character, wipes: {bossKey: count}, total, allCharacters: [...] }. Auto-maps bossKey → label
   using the /api/bosses catalog. Silent-empty when the character has no wipes yet. */
async function loadWipeLog(bossEntries){
  const body = document.getElementById('wipeLogBody');
  if (!body) return;
  try {
    const r = await fetch('/api/wipe-log');
    if (!r.ok) { body.innerHTML = '<div class="hint-row" style="opacity:.6">Wipe log unavailable.</div>'; return; }
    const data = await r.json();
    const wipes = data?.wipes || {};
    const keys = Object.keys(wipes);
    const labelFor = {}; (bossEntries||[]).forEach(e => labelFor[e.key] = e.label || e.key);
    if (!data.character) {
      body.innerHTML = '<div class="hint-row" style="opacity:.7">Waiting for character name from game &mdash; log in-game and re-open this tab.</div>';
      return;
    }
    if (!keys.length) {
      body.innerHTML = `<div class="hint-row" style="opacity:.7">No wipes yet for <b>${data.character}</b>. May the RNG smile on you.</div>`;
      return;
    }
    const rows = keys.sort((a,b)=>wipes[b]-wipes[a]).map(k =>
      `<tr><td style="padding:4px 12px 4px 0"><b>${labelFor[k] || k}</b></td>` +
      `<td style="padding:4px 0;font-family:Consolas,monospace">${wipes[k]}×</td></tr>`).join('');
    const others = (data.allCharacters||[]).filter(c => c !== data.character);
    const othersLine = others.length
      ? `<div style="font-size:11px;color:var(--ink-faint);margin-top:8px">Also on record: ${others.map(o=>`<code>${o}</code>`).join(', ')}</div>`
      : '';
    body.innerHTML =
      `<div style="font-size:12px;margin-bottom:6px">Character: <b>${data.character}</b> &middot; ${data.total} total wipes</div>` +
      `<table style="font-size:12px;line-height:1.5;border-collapse:collapse"><tbody>${rows}</tbody></table>` +
      othersLine;
  } catch (err) {
    body.innerHTML = '<div class="hint-row" style="opacity:.6">Wipe log unavailable (network).</div>';
  }
}
async function loadBosses(){
  if (__bossesLoaded) return; __bossesLoaded = true;
  const list = document.getElementById('bossList');
  if (!list) return;
  try {
    const r = await fetch('/api/bosses');
    if (!r.ok) { list.textContent = 'Failed to load cheat sheets ('+r.status+').'; return; }
    const data = await r.json();
    const entries = data?.entries || [];
    if (!entries.length) { list.textContent = 'No cheat-sheet entries shipped in this build.'; return; }
    // Damage-type element → CSS color token.
    const DMG_COL = {phys:'#e8e0d0', fire:'#ff6b3d', cold:'#4dc4ff', lightning:'#ffdd44', chaos:'#c266ff'};
    const html = entries.map(e => {
      const dmg = e.damageTypes || {};
      const dmgRow = ['phys','fire','cold','lightning','chaos']
        .filter(k => (dmg[k]||0) >= 0.05)
        .map(k => `<span style="background:${DMG_COL[k]};color:#000;padding:2px 8px;border-radius:2px;font-size:11px;font-weight:600;margin-right:4px">${k} ${Math.round((dmg[k]||0)*100)}%</span>`)
        .join('');
      const shots = (e.oneShots||[]).map(s => `<li>${s}</li>`).join('');
      const phases = (e.phases||[]).map(p => `<li><b>${p.cue}</b> — ${p.note}</li>`).join('');
      const overRows = Object.entries(e.overcap||{})
        .map(([elem, thresh]) => `<span style="background:${DMG_COL[elem]||'#888'};color:#000;padding:2px 6px;border-radius:2px;font-size:10px;font-weight:600;margin-right:3px">${elem} ${thresh}%</span>`)
        .join('');
      return `
        <div style="border:1px solid var(--line);border-radius:3px;padding:14px 16px;margin-bottom:12px;background:var(--bg-alt)">
          <div style="font-family:'Cinzel','Georgia',serif;font-size:14px;letter-spacing:.2em;text-transform:uppercase;color:var(--gold-bright);margin-bottom:4px">${e.label}</div>
          <div style="font-size:10px;color:var(--ink-faint);letter-spacing:.18em;text-transform:uppercase;margin-bottom:10px">${e.tier} &middot; ${e.category}</div>
          <div style="margin-bottom:12px">${dmgRow}</div>
          <div style="font-size:11px;color:var(--ink-faint);text-transform:uppercase;letter-spacing:.14em;margin-bottom:4px">One-shots to dodge</div>
          <ul style="margin:0 0 10px;padding-left:18px;font-size:12px;line-height:1.6">${shots}</ul>
          ${phases ? `<div style="font-size:11px;color:var(--ink-faint);text-transform:uppercase;letter-spacing:.14em;margin-bottom:4px">Phases</div>
          <ul style="margin:0 0 10px;padding-left:18px;font-size:12px;line-height:1.6">${phases}</ul>` : ''}
          ${overRows ? `<div style="font-size:11px;color:var(--ink-faint);text-transform:uppercase;letter-spacing:.14em;margin-bottom:4px">Over-cap thresholds</div>
          <div style="margin-bottom:10px">${overRows}</div>` : ''}
          ${e.flaskNotes ? `<div style="font-size:12px;color:var(--ink);border-top:1px solid var(--line-soft);padding-top:8px;margin-top:8px"><b>Flask:</b> ${e.flaskNotes}</div>` : ''}
        </div>`;
    }).join('');
    list.innerHTML = html;
    // v0.30 Instinct: hydrate the wipe-log card using the same boss catalog for bossKey → label.
    loadWipeLog(entries);
  } catch (err) {
    list.textContent = 'Failed to load cheat sheets (network error).';
  }
}
document.querySelectorAll('.tab[data-tab="bosses"]').forEach(t => t.addEventListener('click', loadBosses));

/* ── v0.31 Prospector — Item Filters tab: card grid over /api/item-filters. ───────────────────
   Fetches the filter list + match counters + mod-name autocomplete (from ModCatalog via /api/mods)
   on tab open. Save-on-change discipline: every mutation POSTs the whole list. Cards support
   name/color/priority/enabled edits, add/remove requirements, and delete/duplicate. */
let __ifData = { filters: [] };
let __ifMatches = { ground: 0, equipped: 0, inventory: 0, stash: 0, byFilter: {} };
let __panelState = { character: false, inventory: false, stash: false };
let __ifSort = (typeof localStorage !== 'undefined' && localStorage.getItem('ifSort')) || 'name';
let __ifHideZero = (typeof localStorage !== 'undefined' && localStorage.getItem('ifHideZero')) === '1';

function ifEsc(s){ return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }
function ifTotalCount(id) {
  const c = (__ifMatches.byFilter && __ifMatches.byFilter[id]) || {};
  return (c.ground || 0) + (c.equipped || 0) + (c.inventory || 0);
}
async function loadItemFilters(){
  const list = document.getElementById('ifList');
  if (!list) return;
  try {
    const r = await fetch('/api/item-filters'); __ifData = await r.json();
    const m = await fetch('/api/item-filters/matches'); __ifMatches = await m.json();
    try { const p = await fetch('/api/panels'); __panelState = await p.json(); } catch { __panelState = { character: false, inventory: false, stash: false }; }
    renderItemFilters();
  } catch { list.innerHTML = '<div class="hint-row" style="opacity:.6">Failed to load item filters.</div>'; }
}
function renderItemFilters(){
  const host = document.getElementById('ifList'); if (!host) return;
  const filters = __ifData.filters || [];
  if (!filters.length) {
    host.innerHTML = '<div class="hint-row" style="opacity:.7">No filters yet. Click <b>+ New filter</b> or <b>Restore starter presets</b>.</div>';
    return;
  }
  const panelIcon = (open) => open ? '🟢' : '⚫';
  const panelStrip = `<div class="hint-row" style="margin-bottom:8px;opacity:.75;font-size:12px">
    Panels open: ${panelIcon(__panelState.character)} Character · ${panelIcon(__panelState.inventory)} Inventory · ${panelIcon(__panelState.stash)} Stash
  </div>`;
  const summary = `<div class="hint-row" style="margin-bottom:8px;opacity:.75">🎯 total matches — ground: ${__ifMatches.ground || 0}${__ifMatches.equipped ? ' · equipped: ' + __ifMatches.equipped : ''}${__ifMatches.inventory ? ' · inventory: ' + __ifMatches.inventory : ''}</div>`;
  const controls = `<div class="hint-row" style="margin-bottom:8px;display:flex;gap:12px;align-items:center;font-size:12px">
    <label>Sort:
      <select id="ifSortSel" style="margin-left:4px">
        <option value="name"${__ifSort==='name'?' selected':''}>Name</option>
        <option value="priority"${__ifSort==='priority'?' selected':''}>Priority (high → low)</option>
        <option value="matches"${__ifSort==='matches'?' selected':''}>Most matches now</option>
      </select>
    </label>
    <label style="margin-left:8px"><input type="checkbox" id="ifHideZeroChk"${__ifHideZero?' checked':''}> Hide 0-match filters</label>
  </div>`;
  const raw = __ifData.filters || [];
  let filtersToRender = raw.map((f, origIdx) => ({ ...f, __origIdx: origIdx }));
  if (__ifHideZero) filtersToRender = filtersToRender.filter(f => ifTotalCount(f.id) > 0);
  if (__ifSort === 'priority') filtersToRender.sort((a, b) => (b.priority ?? 100) - (a.priority ?? 100));
  else if (__ifSort === 'matches') filtersToRender.sort((a, b) => ifTotalCount(b.id) - ifTotalCount(a.id));
  // 'name' → keep insertion order (already sensible: alphabetical by convention in the preset seed)
  const html = filtersToRender.map((f, i) => `
    <div class="card" data-fi="${f.__origIdx}" style="padding:12px">
      <div style="display:flex;align-items:center;gap:6px;margin-bottom:8px">
        <input type="color" class="if-color" value="${ifEsc(f.color || '#FFFFFF')}" title="Border color">
        <input class="if-name" value="${ifEsc(f.name || 'Untitled')}" style="flex:1;font-weight:bold" placeholder="Filter name">
        <input class="if-priority" type="number" value="${f.priority ?? 100}" min="0" max="1000" style="width:60px" title="Priority (higher wins ties)">
        <label class="sw" title="enabled"><input type="checkbox" class="if-enabled" ${f.enabled ? 'checked' : ''}><span class="track"></span><span class="knob"></span></label>
        <button class="delbtn if-del" title="delete">&times;</button>
      </div>
      <div class="if-reqs" style="display:flex;flex-direction:column;gap:4px;margin-bottom:6px">
        ${(f.requirements || []).map((r, ri) => `
          <div class="if-req" data-ri="${ri}" style="display:flex;gap:4px;align-items:center;font-size:12px">
            <input class="if-req-stat" list="modVocab" value="${ifEsc(r.statId || '')}" style="flex:1" placeholder="stat id (e.g. local_energy_shield_pct)">
            <select class="if-req-op" style="width:70px">
              ${['>=', '<=', '==', 'between'].map(op => `<option value="${op}"${r.op === op ? ' selected' : ''}>${op}</option>`).join('')}
            </select>
            <input class="if-req-val" type="number" value="${r.value ?? 0}" step="0.01" style="width:60px">
            ${r.op === 'between' ? `<input class="if-req-valmax" type="number" value="${r.valueMax ?? 0}" step="0.01" style="width:60px">` : ''}
            <button class="delbtn if-req-del" title="remove requirement">&times;</button>
          </div>
        `).join('')}
      </div>
      <button class="addbtn if-req-add" style="width:auto;margin:0 0 8px;padding:4px 10px;font-size:11px">+ requirement</button>
      <div style="font-size:11px;color:var(--ink-faint)">🎯 ground: ${(__ifMatches.byFilter?.[f.id]?.ground) || 0}${(__ifMatches.byFilter?.[f.id]?.equipped) ? ' · equipped: ' + __ifMatches.byFilter[f.id].equipped : ''}${(__ifMatches.byFilter?.[f.id]?.inventory) ? ' · inventory: ' + __ifMatches.byFilter[f.id].inventory : ''}</div>
    </div>
  `).join('');
  host.innerHTML = controls + panelStrip + summary + html;
  host.querySelectorAll('[data-fi]').forEach(card => {
    const i = +card.dataset.fi;
    const f = __ifData.filters[i]; if (!f) return;
    card.querySelector('.if-color').onchange = e => { f.color = e.target.value; saveItemFilters(); };
    card.querySelector('.if-name').onchange  = e => { f.name = e.target.value; saveItemFilters(); };
    card.querySelector('.if-priority').onchange = e => { f.priority = parseInt(e.target.value, 10) || 0; saveItemFilters(); };
    card.querySelector('.if-enabled').onchange = e => { f.enabled = e.target.checked; saveItemFilters(); };
    card.querySelector('.if-del').onclick = () => {
      if (!confirm('Delete filter "' + f.name + '"?')) return;
      __ifData.filters.splice(i, 1); saveItemFilters(); renderItemFilters();
    };
    card.querySelector('.if-req-add').onclick = () => {
      (f.requirements ||= []).push({ statId: '', op: '>=', value: 0 });
      saveItemFilters(); renderItemFilters();
    };
    card.querySelectorAll('.if-req').forEach(reqEl => {
      const ri = +reqEl.dataset.ri;
      const req = f.requirements[ri]; if (!req) return;
      reqEl.querySelector('.if-req-stat').onchange = e => { req.statId = e.target.value; saveItemFilters(); };
      reqEl.querySelector('.if-req-op').onchange = e => { req.op = e.target.value; saveItemFilters(); renderItemFilters(); };
      reqEl.querySelector('.if-req-val').onchange = e => { req.value = parseFloat(e.target.value) || 0; saveItemFilters(); };
      const vmax = reqEl.querySelector('.if-req-valmax'); if (vmax) vmax.onchange = e => { req.valueMax = parseFloat(e.target.value) || 0; saveItemFilters(); };
      reqEl.querySelector('.if-req-del').onclick = () => { f.requirements.splice(ri, 1); saveItemFilters(); renderItemFilters(); };
    });
  });
  
  const sortSel = document.getElementById('ifSortSel');
  if (sortSel) sortSel.onchange = () => {
    __ifSort = sortSel.value;
    try { localStorage.setItem('ifSort', __ifSort); } catch {}
    renderItemFilters();
  };
  const hzChk = document.getElementById('ifHideZeroChk');
  if (hzChk) hzChk.onchange = () => {
    __ifHideZero = hzChk.checked;
    try { localStorage.setItem('ifHideZero', __ifHideZero ? '1' : '0'); } catch {}
    renderItemFilters();
  };
}
async function saveItemFilters(){
  try {
    await fetch('/api/item-filters', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(__ifData)
    });
    const m = await fetch('/api/item-filters/matches'); __ifMatches = await m.json();
    const el = document.getElementById('savedMsgIf');
    if (el) { el.classList.add('show'); clearTimeout(el._t); el._t = setTimeout(() => el.classList.remove('show'), 1100); }
  } catch {}
}
document.getElementById('ifAdd')?.addEventListener('click', () => {
  (__ifData.filters ||= []).push({
    id: 'user-' + Date.now(),
    name: 'New Filter', enabled: false, color: '#FF8800', priority: 100,
    requirements: [],
  });
  saveItemFilters(); renderItemFilters();
});
document.getElementById('ifRestore')?.addEventListener('click', async () => {
  if (!confirm('Restore starter presets? Existing filters are kept; only missing preset ids are appended.')) return;
  try { await fetch('/api/item-filters/restore-presets', { method: 'POST' }); } catch {}
  loadItemFilters();
});
document.querySelectorAll('.tab[data-tab="itemfilters"]').forEach(t => t.addEventListener('click', loadItemFilters));

/* v0.33 Drop Timeline — recent-drops list rendered on the Drops tab. Reads /api/drops
   (populated by DropTimeline.Snapshot). Reuses ifEsc from the Item Filters block. */
let __drops = { drops: [] };
async function loadDrops(){
  const list = document.getElementById('dropsList');
  if (!list) return;
  try {
    const r = await fetch('/api/drops');
    __drops = await r.json();
    renderDrops();
  } catch { list.innerHTML = '<div class="hint-row" style="opacity:.6">Failed to load drops.</div>'; }
}
function dropRarityColor(r) {
  switch (r) {
    case 'Unique': return '#af6025';
    case 'Rare':   return '#ffff77';
    case 'Magic':  return '#8888ff';
    default:       return '#c8c8c8';
  }
}
function dropFmtAgo(ts) {
  const secs = Math.max(0, Math.floor(Date.now()/1000 - ts));
  if (secs < 60) return secs + 's ago';
  if (secs < 3600) return Math.floor(secs/60) + 'm ago';
  if (secs < 86400) return Math.floor(secs/3600) + 'h ago';
  return Math.floor(secs/86400) + 'd ago';
}
function renderDrops(){
  const host = document.getElementById('dropsList');
  if (!host) return;
  const rows = (__drops.drops || []).slice().reverse();
  if (!rows.length) {
    host.innerHTML = '<div class="hint-row" style="opacity:.7">No drops recorded yet. Enable <code>EnableDropTimeline</code> in settings and play a bit.</div>';
    return;
  }
  host.innerHTML = rows.map(d => `
    <div class="card" style="padding:6px 10px;display:flex;gap:10px;align-items:center;border-left:3px solid ${dropRarityColor(d.rarity)}">
      <div style="min-width:60px;font-weight:bold;color:${dropRarityColor(d.rarity)}">${ifEsc(d.rarity)}</div>
      <div style="flex:1">${ifEsc(d.name)}</div>
      <div style="opacity:.6;min-width:100px">${ifEsc(d.zone || '-')}</div>
      <div style="opacity:.6;min-width:80px;text-align:right">${dropFmtAgo(d.ts)}</div>
    </div>
  `).join('');
}
document.querySelectorAll('.tab[data-tab="drops"]').forEach(t => t.addEventListener('click', loadDrops));

// CODEX-JS-START
(function () {
  const input = document.getElementById('codex-character-input');
  const loadBtn = document.getElementById('codex-load-btn');
  const jumpSel = document.getElementById('codex-jump-date');
  const book = document.getElementById('codex-book');
  const status = document.getElementById('codex-status');
  const chips = document.querySelectorAll('[data-codex-filter]');
  const activeFilters = { level: true, boss: true, death: true, drop: true };
  let currentEvents = [];
  function setStatus(msg) { if (status) status.textContent = msg; }
  function fmtDay(tsSec) {
    const d = new Date(tsSec * 1000);
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return y + '-' + m + '-' + day;
  }
  function iconFor(kind) {
    switch (kind) {
      case 'level': return '[Lv]';
      case 'boss':  return '[Bo]';
      case 'death': return '[Dx]';
      case 'drop':  return '[Dr]';
      default:      return '[??]';
    }
  }
  function summaryFor(e) {
    switch (e.kind) {
      case 'level': return 'Level ' + (e.from || '?') + ' -> ' + (e.to || '?');
      case 'boss':  return 'Killed ' + (e.boss || 'unknown');
      case 'death': return 'Died in ' + (e.zone || 'unknown') + ' (area lvl ' + (e.areaLevel != null ? e.areaLevel : '?') + ')';
      case 'drop':  return (e.rarity || '?') + ' drop: ' + (e.name || 'unknown') + ' in ' + (e.zone || '?');
      default:      return JSON.stringify(e);
    }
  }
  function render() {
    const visible = currentEvents.filter(e => activeFilters[e.kind]);
    const byDay = new Map();
    for (const e of visible) {
      const day = fmtDay(e.ts);
      if (!byDay.has(day)) byDay.set(day, []);
      byDay.get(day).push(e);
    }
    const days = Array.from(byDay.keys()).sort();
    book.innerHTML = '';
    jumpSel.innerHTML = '<option value="">Jump to date...</option>';
    for (const day of days) {
      const spread = document.createElement('div');
      spread.className = 'codex-spread';
      spread.id = 'codex-day-' + day;
      const header = document.createElement('h3');
      header.className = 'codex-day-header';
      header.textContent = day;
      spread.appendChild(header);
      const list = document.createElement('ul');
      list.className = 'codex-day-events';
      for (const e of byDay.get(day)) {
        const li = document.createElement('li');
        li.className = 'codex-event codex-event-' + e.kind;
        const t = new Date(e.ts * 1000);
        const hh = String(t.getHours()).padStart(2, '0');
        const mm = String(t.getMinutes()).padStart(2, '0');
        li.textContent = hh + ':' + mm + ' ' + iconFor(e.kind) + ' ' + summaryFor(e);
        list.appendChild(li);
      }
      spread.appendChild(list);
      book.appendChild(spread);
      const opt = document.createElement('option');
      opt.value = day;
      opt.textContent = day + ' (' + byDay.get(day).length + ')';
      jumpSel.appendChild(opt);
    }
    setStatus(visible.length + ' events across ' + days.length + ' days');
  }
  function load() {
    const name = (input.value || '').trim();
    if (!/^[A-Za-z0-9_-]{1,40}$/.test(name)) {
      setStatus('Invalid character name (letters, digits, _-, up to 40 chars).');
      return;
    }
    setStatus('Loading...');
    fetch('/api/codex?character=' + encodeURIComponent(name))
      .then(r => {
        if (r.status === 404) { currentEvents = []; render(); setStatus('No codex file for that character yet.'); return null; }
        if (!r.ok) { throw new Error('HTTP ' + r.status); }
        return r.json();
      })
      .then(j => { if (!j) return; currentEvents = Array.isArray(j.events) ? j.events : []; render(); })
      .catch(err => setStatus('Load failed: ' + err.message));
  }
  loadBtn.addEventListener('click', load);
  input.addEventListener('keydown', ev => { if (ev.key === 'Enter') load(); });
  chips.forEach(chip => chip.addEventListener('click', () => {
    const k = chip.getAttribute('data-codex-filter');
    activeFilters[k] = !activeFilters[k];
    chip.classList.toggle('active', activeFilters[k]);
    render();
  }));
  jumpSel.addEventListener('change', () => {
    const v = jumpSel.value;
    if (!v) return;
    const el = document.getElementById('codex-day-' + v);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
  });
})();
// CODEX-JS-END

/* ── Support — v0.27 (LO ask, expanded): supporters roll v2 ────────────────────────────────────
   Reads /api/supporters. Renders:
   - large total-count number + label
   - "Latest supporter: @name" line (last entry in the JSON — LO manages authoring order)
   - rotating pitch quote from a small pool (cycles roughly every minute)
   - pill chips with tier color for every supporter (hover a pill to see their role) */
const SUPPORTER_QUOTES = [
  "POE2GPS runs on curiosity and coffee. Every drop is one person's work against a game that changes its offsets every patch. If it saved you time in a map, consider chipping in — it directly buys the hours that ship the next drop.",
  "This tool is free, open source, and read-only by policy. If it stayed out of your way and gave you a working GPS, one coffee funds the next round of patch-drift-chasing.",
  "Every atlas node icon, every waygate marker, every session HUD chip — that's community feedback plus a lot of memory-reading late-night hours. Coffee helps.",
  "POE2GPS ships against a moving target — GGG's offsets change every patch, and so does the workload. A tip on Ko-fi keeps the lights on for the maintainer.",
  "The Waystone risk parser, the boss cheat sheets, the /map layer, the campaign probe — all shipped on the free tier. Chip in if it earned it.",
];
let __supportersLoaded = false;
async function loadSupporters(){
  const list = document.getElementById('supportersList');
  const countEl = document.getElementById('supportersCount');
  const latestEl = document.getElementById('supportersLatest');
  const quoteEl = document.getElementById('supportersQuote');
  // Rotate the pitch quote on every Settings-tab activation (idx changes per minute).
  if (quoteEl && SUPPORTER_QUOTES.length) {
    const idx = Math.floor((Date.now() / 60000) % SUPPORTER_QUOTES.length);
    quoteEl.textContent = SUPPORTER_QUOTES[idx];
  }
  if (__supportersLoaded) return; __supportersLoaded = true;
  if (!list) return;
  try {
    const r = await fetch('/api/supporters');
    if (!r.ok) return;
    const data = await r.json();
    const sups = data?.supporters || [];
    if (countEl) countEl.textContent = String(sups.length);
    if (!sups.length) {
      list.innerHTML = '<span style="color:var(--ink-faint);font-size:11px">Be the first to join the roll &mdash; <a href="https://ko-fi.com/lutherrotmg" target="_blank" rel="noopener" style="color:var(--gold-bright)">chip in on Ko&#8209;fi</a></span>';
      return;
    }
    // Latest = last entry in the JSON (LO manages authoring order for "latest" semantics).
    const latest = sups[sups.length - 1];
    if (latestEl && latest) latestEl.innerHTML = `Latest: <b style="color:var(--gold-bright)">${latest.name}</b>`;

    const TIER_COL = { gold:'#f5c94f', silver:'#c8c8c8', bronze:'#c78d5a', community:'#8090a0' };
    list.innerHTML = sups.map(s => {
      const col = TIER_COL[s.tier] || TIER_COL.community;
      const title = s.note ? ` title="${(s.note+'').replace(/"/g,'&quot;')}"` : '';
      return `<span${title} style="background:${col};color:#000;padding:4px 10px;border-radius:12px;font-size:11px;font-weight:600;letter-spacing:.02em">${s.name}</span>`;
    }).join('');
  } catch (err) { /* silent — the section can be empty */ }
}
document.querySelectorAll('.tab[data-tab="settings"]').forEach(t => t.addEventListener('click', loadSupporters));
// Also try to load immediately in case Settings is the initial view.
loadSupporters();

/* Support — v0.41 (LO ask): cached supporter gate — JS mirror of POE2Radar.Core.Support.SupporterGate.
   Exposes the supporter status to the overlay's render loop and dashboard cosemetics without a
   per-call network roundtrip. Calls refresh() on settings save or page load to keep the cache
   aligned with the C# validator. Defaults to false until the first refresh. */
(() => {
  let _supporterCached = false;
  window.__supporterGate = {
    isSupporter: () => _supporterCached,
    refresh: async () => {
      try {
        const r = await fetch('/api/settings');
        if (!r.ok) return;
        const s = await r.json();
        _supporterCached = !!s.isSupporter;
      } catch (e) { /* silent — network failure keeps prior cache */ }
    },
    _syncFrom: (isSup) => { _supporterCached = !!isSup; }
  };
})();

/* v0.41 B2 Panel inventory: enumerate and query [data-panel-id] elements. */
(() => {
  window.__panelInventory = {
    list: () => Array.from(document.querySelectorAll('[data-panel-id]')).map(el => el.getAttribute('data-panel-id')).filter(Boolean),
    get: (slug) => document.querySelector('[data-panel-id="' + slug + '"]')
  };
})();

/* v0.41 S3 SupporterHint inline card: renders a hint card into mountEl when the user
   is not a supporter. Idempotent — safe to call repeatedly with the same element. */
window.__supporterHint = (() => {
  const HINT_HTML = '<div class="supporter-hint">' +
    '<span class="supporter-hint-icon">&#x2728;</span>' +
    '<div class="supporter-hint-body">' +
    '<div class="supporter-hint-title">Supporter feature</div>' +
    '<div class="supporter-hint-copy">Save your Ko-fi code in Settings to unlock this.</div>' +
    '</div>' +
    '<button type="button" class="supporter-hint-link" onclick="document.querySelector(\'.tab[data-tab=settings]\')?.click()">Go to Settings &rarr;</button>' +
    '</div>';
  return {
    render: (mountEl) => {
      if (!mountEl) return;
      if (window.__supporterGate && window.__supporterGate.isSupporter()) {
        mountEl.innerHTML = '';
        return;
      }
      mountEl.innerHTML = HINT_HTML;
    },
    hide: (mountEl) => {
      if (!mountEl) return;
      mountEl.innerHTML = '';
    }
  };
})();

/* Palette — v0.38 (F2): load user-created palette CSS blocks from /api/palettes and inject
   them into #user-palette-styles as body[data-palette="user-<slug>"] blocks. Invoked at module load
   BEFORE applySupporterCosmetics so a persisted user palette renders on first paint. Silently
   swallows errors so a failing /api/palettes never breaks the page. */
async function loadUserPalettesCss(){
  try {
    const r = await fetch('/api/palettes');
    if (!r.ok) return;
    const d = await r.json();
    const styleEl = document.getElementById('user-palette-styles');
    if (!styleEl) return;
    const VARS = ['--gold','--gold-bright','--gold-deep','--ink','--ink-dim','--ink-faint','--panel','--panel2','--bg','--bg-alt','--line','--line-soft','--good'];
    styleEl.textContent = (d.palettes||[]).map(p => 'body[data-palette="user-'+p.slug+'"]{'+VARS.map(v=>v+':'+p.vars[v]+';').join('')+'}').join('\n');
  } catch (err) { /* non-fatal — retry-safe, never clears an already-populated styleEl */ }
}

/* Support — v0.27 (LO ask): apply the supporter cosmetic palette and reveal chip state.
   Reads /api/settings (whole payload) to grab the isSupporter flag + palette + code state, applies
   the data-palette attribute on <body>, and lights the "VALID" chip next to the code input. Silently
   falls back to Default palette when the code is missing or invalid so users can't render the app
   broken by pasting garbage. */
async function applySupporterCosmetics(){
  try {
    const r = await fetch('/api/settings');
    if (!r.ok) return;
    const s = await r.json();
    try { window.__supporterGate._syncFrom(s.isSupporter); } catch {}
    const chip = document.getElementById('supporterCodeState');
    const codeIn = document.querySelector('[data-set="supporterCode"]');
    const paletteSel = document.querySelector('[data-set="dashboardPalette"]');
    if (codeIn && !document.activeElement?.matches?.('[data-set="supporterCode"]')) codeIn.value = s.supporterCode || '';
    if (paletteSel && !document.activeElement?.matches?.('[data-set="dashboardPalette"]')) paletteSel.value = s.dashboardPalette || '';
    if (chip) {
      if (!s.supporterCode) { chip.textContent = ''; chip.style.color = 'var(--ink-faint)'; }
      else if (s.isSupporter) { chip.textContent = '✓ Valid'; chip.style.color = 'var(--good)'; }
      else { chip.textContent = '✗ Unrecognized'; chip.style.color = '#e88'; }
    }
    // Apply the palette only when the code validates — non-supporters see the default palette
    // even if they somehow POSTed a palette value.
    const _imp = (function(){ try { return JSON.parse(localStorage.getItem('poe2gps.importedPalette') || 'null'); } catch(e) { return null; } })();
    const effectivePalette = s.isSupporter ? ((_imp && _imp.slug) || s.dashboardPalette || '') : '';
    if (_imp && _imp.slug) { try { window.__paletteInjectImportedStyle && window.__paletteInjectImportedStyle(_imp); } catch(e){} }
    document.body.setAttribute('data-palette', effectivePalette);
    try {
      const paletteMount = document.getElementById('dashboardPaletteHint');
      if (paletteMount) window.__supporterHint.render(paletteMount);
      const paletteSel = document.querySelector('[data-set="dashboardPalette"]');
      if (paletteSel) paletteSel.disabled = !window.__supporterGate.isSupporter();
    } catch {}
  } catch (err) { /* silent */ }
}
window.__reloadUserPalettes = loadUserPalettesCss;
loadUserPalettesCss().then(applySupporterCosmetics);

/* Support automation — v0.27.1 (LO ask): maintainer helper.
   Unhides on ?admin=1 URL param. Live-computes SHA-256 (WebCrypto) as LO types.
   Auto-fills paste-ready snippets for supporter_hashes.json + supporters.json + a Ko-fi DM template. */
(function(){
  const helper = document.getElementById('maintainerHelper');
  if (!helper) return;
  const params = new URLSearchParams(location.search);
  if (params.get('admin') !== '1') return;
  helper.style.display = 'block';

  const $ = id => document.getElementById(id);
  const raw = $('mhRawCode'), name = $('mhName'), tier = $('mhTier'), note = $('mhNote');
  const hashOut = $('mhHash'), jsonOut = $('mhJsonEntry'), dmOut = $('mhDmTemplate');

  async function sha256Hex(s){
    const bytes = new TextEncoder().encode(s.trim().toLowerCase());
    const digest = await crypto.subtle.digest('SHA-256', bytes);
    return Array.from(new Uint8Array(digest)).map(b => b.toString(16).padStart(2,'0')).join('');
  }
  function randomCode(){
    // Human-readable random code — 3 blocks of 4 uppercase alphanumeric, no ambiguous glyphs.
    const chars = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
    const block = () => Array.from({length:4}, () => chars[Math.floor(Math.random()*chars.length)]).join('');
    return `POE2GPS-${block()}-${block()}-${block()}`;
  }
  async function recompute(){
    const codeStr = raw.value.trim();
    hashOut.value = codeStr ? (await sha256Hex(codeStr)) : '';
    const nameStr = name.value.trim() || 'DonorName';
    const noteStr = note.value.trim();
    const tierStr = tier.value;
    jsonOut.value = `{ "name": "${nameStr.replace(/"/g,'\\"')}", "tier": "${tierStr}", "note": "${noteStr.replace(/"/g,'\\"')}" }`;
    dmOut.value = `Hey — huge thanks for the coffee! 🙏\n\nYour POE2GPS supporter code:\n${codeStr || '(paste a code above)'}\n\nOpen the dashboard → Settings → Supporter code, paste it in, and you'll unlock the Kalguuran Gold + Wraeclast Terminal palettes plus the optional ☕ Supporter chip on the Session HUD. Your name (${nameStr}) is going on the roll same-release.\n\n— LO`;
  }
  raw.addEventListener('input', recompute);
  name.addEventListener('input', recompute);
  note.addEventListener('input', recompute);
  tier.addEventListener('change', recompute);
  $('mhGenerate').addEventListener('click', async () => { raw.value = randomCode(); await recompute(); });
  async function copyToClip(el, btn){
    try { await navigator.clipboard.writeText(el.value); btn.textContent = '✓ Copied'; setTimeout(()=>btn.textContent = '📋 Copy', 1400); } catch {}
  }
  $('mhCopyHash').addEventListener('click', () => copyToClip(hashOut, $('mhCopyHash')));
  $('mhCopyJson').addEventListener('click', () => copyToClip(jsonOut, $('mhCopyJson')));
  $('mhCopyDm').addEventListener('click',   () => copyToClip(dmOut,   $('mhCopyDm')));
  recompute();
})();
// Re-check on every settings save so the palette applies live when the code turns green.
document.addEventListener('change', e => {
  if (e.target?.matches?.('[data-set="supporterCode"], [data-set="dashboardPalette"], [data-set="showSupporterBadge"]')) {
    setTimeout(applySupporterCosmetics, 200);
  }
});

// v0.35 — palette preview swatches shown below the dashboardPalette <select>. Hardcoded map
// (slug -> [bg, panel, gold, ink]) rather than runtime CSS parsing; kept byte-for-byte in sync
// with the body[data-palette="..."] blocks in dashboard.css and locked by the xUnit test
// DashboardPalettePreviewTests.PalettePreviewsMapCoversEverySlug. Clicking a chip sets
// sel.value and dispatches a bubbling 'change' event so the existing supporter-gate ternary
// in applySupporterCosmetics re-runs — the widget itself never gates.
const PALETTE_PREVIEWS = {
  '': ['#0d1220', '#141c30', '#f5c94f', '#f0e6cf'],
  'kalguuran':           ['#150c05', '#241708', '#f5c94f', '#f0e6cf'],
  'terminal':            ['#030803', '#061006', '#66ff66', '#b0ffb0'],
  'ultimatum-red':       ['#150505', '#240a0a', '#d94a4a', '#f2d6d6'],
  'sanctum-cream':       ['#14100a', '#2a2418', '#d4b26a', '#f5ecd6'],
  'necropolis-amethyst': ['#0d0515', '#1a0a24', '#b56ad9', '#ecd6f5'],
  'delirium-static':     ['#050d14', '#0e1a24', '#7fd8e6', '#dceff5'],
  'legion-bronze':       ['#150e08', '#241a10', '#c78e4a', '#f0ddc0'],
  'ritual-blood':        ['#0a0308', '#180814', '#b83060', '#f0d0d8'],
  'trial-ordeal':        ['#050403', '#14120a', '#f5d84a', '#f5edc4'],
  'blight-bloom':        ['#080a05', '#14180a', '#a8c748', '#e0e8b8'],
};

// Shared in-flight promise for GET /api/palettes — serves both refreshUserPaletteOptions()
// and renderPalettePreview() on page load; invalidated on user-palettes-changed event.
let _userPalettesPromise = null;
function _fetchUserPalettes() {
  if (!_userPalettesPromise) _userPalettesPromise = fetch('/api/palettes').then(r => r.ok ? r.json() : {palettes:[]}).then(d => d.palettes||[]).catch(() => []);
  return _userPalettesPromise;
}

// Populate <select data-set="dashboardPalette"> with user-forge palette options.
async function refreshUserPaletteOptions() {
  const sel = document.querySelector('[data-set="dashboardPalette"]');
  if (!sel) return;
  const palettes = await _fetchUserPalettes();
  // Remove existing user-* options (preserving the 11 built-in options).
  const existing = sel.querySelectorAll('option[value^="user-"]');
  for (const opt of existing) opt.remove();
  // Append after built-in options.
  for (const p of palettes) {
    const opt = document.createElement('option');
    opt.value = 'user-' + p.slug;
    opt.textContent = 'Forge: ' + (p.displayName || p.slug);
    sel.appendChild(opt);
  }
}

async function renderPalettePreview() {
  const container = document.getElementById('palettePreview');
  const sel = document.querySelector('[data-set="dashboardPalette"]');
  if (!container || !sel) return;
  const current = sel.value || '';
  container.innerHTML = '';
  for (const [slug, tints] of Object.entries(PALETTE_PREVIEWS)) {
    const chip = document.createElement('div');
    chip.className = 'chip' + (slug === current ? ' sel' : '');
    chip.title = slug || 'Default';
    chip.setAttribute('role', 'option');
    chip.setAttribute('data-slug', slug);
    for (const c of tints) {
      const sw = document.createElement('span');
      sw.className = 'sw';
      sw.style.background = c;
      chip.appendChild(sw);
    }
    chip.addEventListener('click', () => {
      sel.value = slug;
      sel.dispatchEvent(new Event('change', { bubbles: true }));
      renderPalettePreview();
    });
    container.appendChild(chip);
  }
  // User forge palettes — chips rendered after built-ins, with data-slug="user-<slug>".
  const userPalettes = await _fetchUserPalettes();
  for (const palette of userPalettes) {
    const chip = document.createElement('div');
    const slug = 'user-' + palette.slug;
    chip.className = 'chip' + (slug === current ? ' sel' : '');
    chip.title = 'Forge: ' + (palette.displayName || palette.slug);
    chip.setAttribute('role', 'option');
    chip.setAttribute('data-slug', slug);
    const tints = palette.preview || ['#333', '#555', '#aaa', '#ddd'];
    for (const c of tints) {
      const sw = document.createElement('span');
      sw.className = 'sw';
      sw.style.background = c;
      chip.appendChild(sw);
    }
    chip.addEventListener('click', () => {
      sel.value = slug;
      sel.dispatchEvent(new Event('change', { bubbles: true }));
      renderPalettePreview();
    });
    container.appendChild(chip);
  }
}

// Initial render + refresh on any dashboardPalette change (covers both the native <select>
// and our own chip-click paths — both dispatch the same 'change' event on the <select>).
document.addEventListener('DOMContentLoaded', async () => {
  await refreshUserPaletteOptions();
  renderPalettePreview();
});
document.addEventListener('change', e => {
  if (e.target?.matches?.('[data-set="dashboardPalette"]')) renderPalettePreview();
});
// F3 dispatches user-palettes-changed after Save/Delete — refresh select + chip strip.
document.addEventListener('user-palettes-changed', () => {
  _userPalettesPromise = null;
  refreshUserPaletteOptions();
  renderPalettePreview();
});

// v0.38 Color Forge — preset gallery ---------------------------------------
// The 10 built-in palettes shown as clone-source cards. Kalguuran + terminal
// first (v0.34 OG), then the 8 v0.35 signature palettes in CSS order.
const FORGE_BUILTIN_SLUGS = [
  'kalguuran','terminal',
  'ultimatum-red','sanctum-cream','necropolis-amethyst','delirium-static',
  'legion-bronze','ritual-blood','trial-ordeal','blight-bloom'
];
const FORGE_DISPLAY_NAMES = {
  'kalguuran':'Kalguuran Gold','terminal':'Wraeclast Terminal',
  'ultimatum-red':'Ultimatum Crimson','sanctum-cream':'Sanctum Cream',
  'necropolis-amethyst':'Necropolis Amethyst','delirium-static':'Delirium Static',
  'legion-bronze':'Legion Bronze','ritual-blood':'Ritual Blood',
  'trial-ordeal':'Trial Ordeal','blight-bloom':'Blight Bloom'
};
const FORGE_VAR_NAMES = ['--gold','--gold-bright','--gold-deep','--ink','--ink-dim','--ink-faint','--panel','--panel2','--bg','--bg-alt','--line','--line-soft','--good'];
const FORGE_PREVIEW_VARS = ['--bg','--panel','--gold','--ink'];

// Read all 13 palette vars for `slug` by walking the loaded stylesheets and
// finding the `body[data-palette="<slug>"]` rule. Returns null if not found
// or blocked by CORS. Never hardcodes a hex — CSS is source of truth.
function readPaletteVarsFromCss(slug){
  if(!slug) return null;
  const target = 'body[data-palette="' + slug + '"]';
  for(const sheet of document.styleSheets){
    let rules;
    try{ rules = sheet.cssRules; }catch(_){ continue; } // cross-origin
    if(!rules) continue;
    for(const rule of rules){
      if(rule.type !== 1) continue; // CSSStyleRule
      if(rule.selectorText !== target) continue;
      const out = {};
      for(const v of FORGE_VAR_NAMES){
        const val = rule.style.getPropertyValue(v).trim();
        if(val) out[v] = val;
      }
      return Object.keys(out).length ? out : null;
    }
  }
  return null;
}

function renderForgePresetGallery(){
  const host = document.getElementById('forgePresetGallery');
  if(!host) return;
  host.innerHTML = '';
  for(const slug of FORGE_BUILTIN_SLUGS){
    const vars = readPaletteVarsFromCss(slug);
    if(!vars) continue; // silently skip if CSS missing — test-gated at build
    const card = document.createElement('div');
    card.className = 'forge-preset-card';
    card.setAttribute('role','listitem');
    card.setAttribute('tabindex','0');
    card.dataset.sourceSlug = slug;
    card.title = FORGE_DISPLAY_NAMES[slug] || slug;
    const strip = document.createElement('div');
    strip.className = 'fp-swatches';
    for(const v of FORGE_PREVIEW_VARS){
      const sw = document.createElement('span');
      sw.className = 'fp-sw';
      sw.style.background = vars[v] || '#000';
      strip.appendChild(sw);
    }
    const name = document.createElement('div');
    name.className = 'fp-name';
    name.textContent = FORGE_DISPLAY_NAMES[slug] || slug;
    card.appendChild(strip);
    card.appendChild(name);
    host.appendChild(card);
  }
}

document.addEventListener('DOMContentLoaded', renderForgePresetGallery);

// v0.38 Color Forge — clone-to-editable ------------------------------------
// User-preset store. Interim localStorage key; TODO: swap for server-side
// persistence via /api/forge/presets when the Forge state bead lands.
const FORGE_USER_PRESETS_KEY = 'poe2gps.forge.userPresets';

function forgeLoadUserPresets(){
  try{ const raw = localStorage.getItem(FORGE_USER_PRESETS_KEY);
       return raw ? (JSON.parse(raw) || []) : []; }
  catch(_){ return []; }
}
function forgeSaveUserPresets(list){
  try{ localStorage.setItem(FORGE_USER_PRESETS_KEY, JSON.stringify(list)); }
  catch(_){ /* quota / privacy mode — best-effort only */ }
}

// Returns `<baseSlug>-1`, `<baseSlug>-2`, … first free suffix vs current store.
// Always suffixed (never bare baseSlug) so a cloned preset never collides with
// the built-in slug of the same name.
function forgeUniqueName(baseSlug){
  const existing = new Set(forgeLoadUserPresets().map(p => p.name));
  let i = 1;
  while(existing.has(baseSlug + '-' + i)) i++;
  return baseSlug + '-' + i;
}

function forgeCloneBuiltin(sourceSlug){
  const vars = readPaletteVarsFromCss(sourceSlug);
  if(!vars) return null;
  const name = forgeUniqueName(sourceSlug);
  const preset = { name, sourceSlug, vars };
  const list = forgeLoadUserPresets();
  list.push(preset);
  forgeSaveUserPresets(list);
  const host = document.getElementById('forgePresetGallery');
  if(host){
    host.dispatchEvent(new CustomEvent('forge:preset-cloned', {
      bubbles: true, detail: preset
    }));
  }
  return preset;
}

function wireForgeGalleryClicks(){
  const host = document.getElementById('forgePresetGallery');
  if(!host) return;
  host.addEventListener('click', (e) => {
    const card = e.target.closest('.forge-preset-card');
    if(!card || !host.contains(card)) return;
    const slug = card.dataset.sourceSlug;
    if(slug) forgeCloneBuiltin(slug);
  });
  host.addEventListener('keydown', (e) => {
    if(e.key !== 'Enter' && e.key !== ' ') return;
    const card = e.target.closest('.forge-preset-card');
    if(!card || !host.contains(card)) return;
    e.preventDefault();
    const slug = card.dataset.sourceSlug;
    if(slug) forgeCloneBuiltin(slug);
  });
}
document.addEventListener('DOMContentLoaded', wireForgeGalleryClicks);

/* ── Reach — v0.26 (CHOR-41): waystone mod-risk parser wiring ──────────────────────────────────
   The Parse button POSTs the textarea contents to /api/waystone/parse and renders the tiered
   mod list, combo hits, total score, and skip recommendation. */
async function parseWaystone(){
  const inp = document.getElementById('wsInput');
  const out = document.getElementById('wsResult');
  if (!inp || !out) return;
  const text = inp.value || '';
  if (!text.trim()) { out.innerHTML = '<div style="color:var(--ink-faint);font-size:12px">Paste waystone text above first.</div>'; return; }
  try {
    const r = await fetch('/api/waystone/parse', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify({text}) });
    if (!r.ok) { out.innerHTML = '<div style="color:var(--danger,#e88)">Parse failed (HTTP '+r.status+').</div>'; return; }
    const d = await r.json();
    if (!d.isWaystone) { out.innerHTML = '<div style="color:var(--ink-faint);font-size:12px">Not recognized as a waystone (need <code>Item Class: Waystones</code> header).</div>'; return; }
    const skipBanner = d.shouldSkip
      ? `<div style="background:#c93030;color:#fff;padding:10px 14px;border-radius:3px;margin-bottom:12px;font-family:'Cinzel',Georgia,serif;letter-spacing:.2em;text-transform:uppercase;font-size:12px">⚠ Skip recommended &middot; score ${d.totalScore} / threshold ${d.skipThreshold}</div>`
      : `<div style="background:var(--bg-alt);color:var(--ink);padding:10px 14px;border-radius:3px;margin-bottom:12px;border:1px solid var(--line-soft);font-size:12px">Score ${d.totalScore} &middot; below skip threshold ${d.skipThreshold}</div>`;
    const tierCol = t => t==='Deadly' ? '#e33' : t==='Notable' ? '#e88500' : t==='LethalCombo' ? '#c93030' : '#6a6';
    const modRows = (d.mods||[]).map(m => `
      <div style="display:flex;align-items:center;gap:10px;padding:6px 0;border-bottom:1px dotted var(--line-soft)">
        <span style="background:${tierCol(m.tier)};color:#fff;padding:2px 8px;border-radius:2px;font-size:10px;font-weight:600;letter-spacing:.15em;text-transform:uppercase;min-width:60px;text-align:center">${m.tier}</span>
        <span style="flex:1;font-size:12px">${m.name}</span>
        <span style="color:var(--ink-faint);font-size:11px">+${m.weight}</span>
      </div>`).join('');
    const comboRows = (d.combos||[]).map(c => `
      <div style="display:flex;align-items:center;gap:10px;padding:6px 0;border-bottom:1px dotted var(--line-soft)">
        <span style="background:#c93030;color:#fff;padding:2px 8px;border-radius:2px;font-size:10px;font-weight:600;letter-spacing:.15em;text-transform:uppercase;min-width:60px;text-align:center">Combo</span>
        <span style="flex:1;font-size:12px">${c.label}</span>
        <span style="color:var(--ink-faint);font-size:11px">+${c.bonus}</span>
      </div>`).join('');
    out.innerHTML = `${skipBanner}
      ${(d.rarity||d.tier) ? `<div style="font-size:11px;color:var(--ink-faint);margin-bottom:8px">${d.rarity ? d.rarity + ' &middot; ' : ''}Tier ${d.tier}</div>` : ''}
      ${modRows ? `<div style="font-size:11px;color:var(--ink-faint);text-transform:uppercase;letter-spacing:.14em;margin:8px 0 4px">Mods matched</div>${modRows}` : '<div style="color:var(--ink-faint);font-size:12px">No risky mods matched.</div>'}
      ${comboRows ? `<div style="font-size:11px;color:var(--ink-faint);text-transform:uppercase;letter-spacing:.14em;margin:16px 0 4px">Combos triggered</div>${comboRows}` : ''}`;
  } catch (err) {
    out.innerHTML = '<div style="color:var(--danger,#e88)">Parse failed (network error).</div>';
  }
}
document.getElementById('wsParse')?.addEventListener('click', parseWaystone);

/* ── Groove — v0.24: global save-toast + keyboard shortcuts ────────────────────────────────────
   flashSaved() is available for future callsites (or as a lightweight helper); it does not
   replace the existing per-card savedMsg* spans this drop (kept intact for stability).
   Global keydown listener adds "/" (focus search), "1"–"7" (switch tab), "?" (toggle help),
   and "Esc" (close open modals + cancel keybind capture). Handler ignores keys while typing
   in an input, textarea, or contenteditable region, so search boxes still consume text keys. */
function flashSaved(msg, ms){
  const m = document.getElementById('globalSavedMsg'); if(!m) return;
  m.textContent = msg || '✓ saved';
  m.classList.add('show');
  clearTimeout(m._t); m._t = setTimeout(()=>m.classList.remove('show'), ms || 1400);
}
(function(){
  const SEARCH_BY_TAB = {
    filters:   '#hidePattern',
    landmarks: '#lmSearch',
    atlas:     null,
    settings:  '#settingsSearch',
    director:  '#dirSearch',
    entatlas:  '#eaSearch',
    gear:      null,
  };
  const TABS = ['filters','landmarks','atlas','settings','director','entatlas','gear'];
  function isTyping(t){ return t && (t.tagName==='INPUT' || t.tagName==='TEXTAREA' || t.isContentEditable); }
  function closeAllModals(){
    document.getElementById('pickPop')?.classList.remove('open');
    document.getElementById('iconPop')?.classList.remove('open');
    document.getElementById('helpModal')?.classList.remove('open');
    if (typeof window.kbCancelCapture === 'function') window.kbCancelCapture();
  }
  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') { closeAllModals(); return; }
    if (isTyping(e.target)) return;
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (e.key === '/') {
      e.preventDefault();
      const active = document.querySelector('.tab.active')?.dataset?.tab || 'settings';
      const sel = SEARCH_BY_TAB[active] || '#settingsSearch';
      document.querySelector(sel)?.focus();
      return;
    }
    if (e.key === '?') {
      e.preventDefault();
      document.getElementById('helpModal')?.classList.toggle('open');
      return;
    }
    if (/^[1-7]$/.test(e.key)) {
      const idx = +e.key - 1;
      document.querySelector(`.tab[data-tab="${TABS[idx]}"]`)?.click();
    }
  });
})();

/* ── Session Recap PNG button (dashboard) ── */
document.getElementById('btnSaveSessionPng')?.addEventListener('click', saveSessionRecapPng);

/* ── Color Forge — v0.38 (F3): 13-var slider designer with live sample preview ──────────────────
   13-row HSL/hex slider grid (one row per palette CSS var) with a live sample panel whose
   colors are scoped entirely to #forgePreview as --fp-<varname-without-leading-dashes> — so
   authoring never touches the applied <body> palette until Save. Save/Load/Delete persist
   user palettes via POST/DELETE /api/palettes (F1); on success it calls
   window.__reloadUserPalettes (F2) and dispatches 'user-palettes-changed' so F4 can refresh.
   The toggle wires #forgePanel.hidden and swaps the button label. buildRows() + initFromComputed
   run lazily on first open so the sliders pre-load the currently effective palette. */
(() => {
  const VAR_NAMES = ['--gold','--gold-bright','--gold-deep','--ink','--ink-dim','--ink-faint','--panel','--panel2','--bg','--bg-alt','--line','--line-soft','--good'];

  // Built-in palette hex maps — transcribed byte-for-byte from the body[data-palette="..."]
  // blocks in dashboard.css (lines 206-280). Used to populate sliders on Load-from-<select>.
  const BUILTIN_PALETTES = {
    'kalguuran':           {'--gold':'#f5c94f','--gold-bright':'#ffdb6a','--gold-deep':'#b98a1e','--ink':'#f0e6cf','--ink-dim':'#c9b995','--ink-faint':'#8c7d5b','--panel':'#241708','--panel2':'#1c1207','--bg':'#150c05','--bg-alt':'#2c1e0e','--line':'#4a3319','--line-soft':'#38260f','--good':'#ffd66a'},
    'terminal':            {'--gold':'#66ff66','--gold-bright':'#99ff99','--gold-deep':'#339933','--ink':'#b0ffb0','--ink-dim':'#7fc17f','--ink-faint':'#4d724d','--panel':'#061006','--panel2':'#050c05','--bg':'#030803','--bg-alt':'#0a1a0a','--line':'#206620','--line-soft':'#144614','--good':'#99ff99'},
    'ultimatum-red':       {'--gold':'#d94a4a','--gold-bright':'#ff6a6a','--gold-deep':'#8a1e1e','--ink':'#f2d6d6','--ink-dim':'#c99a9a','--ink-faint':'#8c6060','--panel':'#240a0a','--panel2':'#1c0808','--bg':'#150505','--bg-alt':'#2c0e0e','--line':'#4a1919','--line-soft':'#380f0f','--good':'#ff8080'},
    'sanctum-cream':       {'--gold':'#d4b26a','--gold-bright':'#f0cf85','--gold-deep':'#a1813a','--ink':'#f5ecd6','--ink-dim':'#c9bd9a','--ink-faint':'#8c815c','--panel':'#2a2418','--panel2':'#1e1a10','--bg':'#14100a','--bg-alt':'#322a1c','--line':'#4a3f24','--line-soft':'#38301a','--good':'#e0c78a'},
    'necropolis-amethyst': {'--gold':'#b56ad9','--gold-bright':'#d29aff','--gold-deep':'#6a2a99','--ink':'#ecd6f5','--ink-dim':'#b89ac9','--ink-faint':'#7a5c8c','--panel':'#1a0a24','--panel2':'#14081c','--bg':'#0d0515','--bg-alt':'#22102c','--line':'#3a1e4a','--line-soft':'#2b1438','--good':'#c07fe6'},
    'delirium-static':     {'--gold':'#7fd8e6','--gold-bright':'#a8ecff','--gold-deep':'#3a7a8c','--ink':'#dceff5','--ink-dim':'#9ab8c4','--ink-faint':'#5c7580','--panel':'#0e1a24','--panel2':'#0a141c','--bg':'#050d14','--bg-alt':'#12222c','--line':'#1e3a4a','--line-soft':'#142c38','--good':'#99e6ff'},
    'legion-bronze':       {'--gold':'#c78e4a','--gold-bright':'#e6a866','--gold-deep':'#7a5220','--ink':'#f0ddc0','--ink-dim':'#b8a480','--ink-faint':'#7a6b4c','--panel':'#241a10','--panel2':'#1c140a','--bg':'#150e08','--bg-alt':'#2c2014','--line':'#4a3620','--line-soft':'#382818','--good':'#d9a066'},
    'ritual-blood':        {'--gold':'#b83060','--gold-bright':'#e04880','--gold-deep':'#701830','--ink':'#f0d0d8','--ink-dim':'#b88c98','--ink-faint':'#7a5860','--panel':'#180814','--panel2':'#10050e','--bg':'#0a0308','--bg-alt':'#200c1a','--line':'#3a1428','--line-soft':'#2a0e1c','--good':'#cc5588'},
    'trial-ordeal':        {'--gold':'#f5d84a','--gold-bright':'#ffef88','--gold-deep':'#a88820','--ink':'#f5edc4','--ink-dim':'#c4ba88','--ink-faint':'#786e48','--panel':'#14120a','--panel2':'#0e0c05','--bg':'#050403','--bg-alt':'#1e1a0c','--line':'#38301a','--line-soft':'#262010','--good':'#ffe066'},
    'blight-bloom':        {'--gold':'#a8c748','--gold-bright':'#cce866','--gold-deep':'#607a20','--ink':'#e0e8b8','--ink-dim':'#a8b088','--ink-faint':'#6c7458','--panel':'#14180a','--panel2':'#0e1208','--bg':'#080a05','--bg-alt':'#1c2210','--line':'#384418','--line-soft':'#283010','--good':'#b8dc55'},
  };

  // hex <-> hsl utilities. Standard algorithms (reference: hexToHsl('#f5c94f') ≈ {h:44,s:89,l:63}).
  function hexToHsl(hex){
    const h = hex.replace('#','');
    const r = parseInt(h.slice(0,2),16)/255, g = parseInt(h.slice(2,4),16)/255, b = parseInt(h.slice(4,6),16)/255;
    const max = Math.max(r,g,b), min = Math.min(r,g,b);
    let hue, s, l = (max+min)/2;
    if (max === min) { hue = 0; s = 0; }
    else {
      const d = max - min;
      s = l > 0.5 ? d/(2-max-min) : d/(max+min);
      switch(max){
        case r: hue = (g-b)/d + (g<b?6:0); break;
        case g: hue = (b-r)/d + 2; break;
        default: hue = (r-g)/d + 4; break;
      }
      hue /= 6;
    }
    return { h: Math.round(hue*360), s: Math.round(s*100), l: Math.round(l*100) };
  }
  function hslToHex(h, s, l){
    h = ((h%360)+360)%360; s /= 100; l /= 100;
    const c = (1 - Math.abs(2*l - 1)) * s;
    const x = c * (1 - Math.abs(((h/60)%2) - 1));
    const m = l - c/2;
    let r,g,b;
    if (h < 60) { r=c; g=x; b=0; }
    else if (h < 120) { r=x; g=c; b=0; }
    else if (h < 180) { r=0; g=c; b=x; }
    else if (h < 240) { r=0; g=x; b=c; }
    else if (h < 300) { r=x; g=0; b=c; }
    else { r=c; g=0; b=x; }
    const toHex = v => Math.round((v+m)*255).toString(16).padStart(2,'0');
    return '#'+toHex(r)+toHex(g)+toHex(b);
  }

  const $ = id => document.getElementById(id);
  const rows = new Map(); // varName -> { h, s, l, hex, els:{label,hIn,sIn,lIn,hexIn,swatch} }

  function applyPreview(varName, hex){
    const el = $('forgePreview');
    if (!el) return;
    el.style.setProperty('--fp-' + varName.slice(2), hex);
  }

  function setRow(varName, hex){
    const entry = rows.get(varName); if (!entry) return;
    const hsl = hexToHsl(hex);
    entry.hex = hex;
    entry.h = hsl.h; entry.s = hsl.s; entry.l = hsl.l;
    entry.els.hIn.value = hsl.h;
    entry.els.sIn.value = hsl.s;
    entry.els.lIn.value = hsl.l;
    entry.els.hexIn.value = hex;
    entry.els.hexIn.classList.remove('invalid');
    entry.els.swatch.style.background = hex;
    applyPreview(varName, hex);
  }

  function buildRows(){
    const container = $('forgeSliders');
    if (!container) return;
    container.innerHTML = '';
    rows.clear();
    for (const name of VAR_NAMES){
      const rowEl = document.createElement('div');
      rowEl.className = 'forge-row';
      const label = document.createElement('label');
      label.textContent = name;
      const hIn = document.createElement('input'); hIn.type='range'; hIn.min='0'; hIn.max='360'; hIn.dataset.ch='h';
      const sIn = document.createElement('input'); sIn.type='range'; sIn.min='0'; sIn.max='100'; sIn.dataset.ch='s';
      const lIn = document.createElement('input'); lIn.type='range'; lIn.min='0'; lIn.max='100'; lIn.dataset.ch='l';
      const hexIn = document.createElement('input'); hexIn.type='text'; hexIn.maxLength=7; hexIn.dataset.ch='hex';
      const swatch = document.createElement('div'); swatch.className='forge-swatch';
      rowEl.append(label, hIn, sIn, lIn, hexIn, swatch);
      container.appendChild(rowEl);

      const entry = { hex:'', h:0, s:0, l:0, els:{ label, hIn, sIn, lIn, hexIn, swatch } };
      rows.set(name, entry);

      const onHsl = () => {
        entry.h = +hIn.value; entry.s = +sIn.value; entry.l = +lIn.value;
        const hex = hslToHex(entry.h, entry.s, entry.l);
        entry.hex = hex;
        hexIn.value = hex;
        hexIn.classList.remove('invalid');
        swatch.style.background = hex;
        applyPreview(name, hex);
      };
      hIn.addEventListener('input', onHsl);
      sIn.addEventListener('input', onHsl);
      lIn.addEventListener('input', onHsl);
      hexIn.addEventListener('input', () => {
        const v = hexIn.value.trim();
        if (/^#[0-9a-fA-F]{6}$/.test(v)){
          hexIn.classList.remove('invalid');
          const hsl = hexToHsl(v);
          entry.hex = v; entry.h = hsl.h; entry.s = hsl.s; entry.l = hsl.l;
          hIn.value = hsl.h; sIn.value = hsl.s; lIn.value = hsl.l;
          swatch.style.background = v;
          applyPreview(name, v);
        } else {
          hexIn.classList.add('invalid');
        }
      });
    }
  }

  function initFromComputed(){
    for (const name of VAR_NAMES){
      const v = getComputedStyle(document.body).getPropertyValue(name).trim();
      if (v) setRow(name, v);
    }
  }

  async function populateLoadSelect(){
    const sel = $('forgeLoad'); if (!sel) return;
    sel.innerHTML = '<option value="">Load from&hellip;</option>';
    for (const slug of Object.keys(BUILTIN_PALETTES)){
      const opt = document.createElement('option');
      opt.value = 'builtin:' + slug;
      opt.textContent = slug;
      sel.appendChild(opt);
    }
    try {
      const r = await fetch('/api/palettes');
      if (r.ok){
        const d = await r.json();
        for (const p of (d.palettes||[])){
          const opt = document.createElement('option');
          opt.value = 'user:' + p.slug;
          opt.textContent = p.slug;
          sel.appendChild(opt);
        }
      }
    } catch (err) { /* non-fatal — select simply omits user palettes on a failed fetch */ }
  }

  function loadVarsIntoRows(vars){
    for (const name of VAR_NAMES){
      const v = vars && vars[name];
      if (v) setRow(name, v);
    }
  }

  async function onForgeLoadChange(){
    const sel = $('forgeLoad'); if (!sel) return;
    const val = sel.value; if (!val) return;
    const kind = val.slice(0, val.indexOf(':'));
    const slug = val.slice(val.indexOf(':') + 1);
    if (kind === 'builtin'){
      loadVarsIntoRows(BUILTIN_PALETTES[slug]);
    } else if (kind === 'user'){
      try {
        const r = await fetch('/api/palettes/' + encodeURIComponent(slug));
        if (r.ok){ const d = await r.json(); loadVarsIntoRows(d.vars); }
      } catch (err) { /* non-fatal */ }
    }
    sel.value = '';
  }

  async function onForgeSave(){
    const status = $('forgeStatus'); if (!status) return;
    const nameIn = $('forgeName'); if (!nameIn) return;
    const slug = nameIn.value.trim();
    if (!/^[a-z0-9-]{1,32}$/.test(slug)){ status.textContent = 'invalid slug'; return; }
    const vars = {};
    for (const name of VAR_NAMES){ const entry = rows.get(name); if (entry) vars[name] = entry.hex; }
    try {
      const r = await fetch('/api/palettes', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ slug, displayName: slug, vars }) });
      if (r.ok){
        status.textContent = 'saved';
        window.__reloadUserPalettes?.();
        document.dispatchEvent(new CustomEvent('user-palettes-changed'));
      } else if (r.status === 409){
        status.textContent = 'name already exists';
      } else {
        status.textContent = 'save failed (HTTP ' + r.status + ')';
      }
    } catch (err) { status.textContent = 'save failed (network)'; }
  }

  async function onForgeDelete(){
    const status = $('forgeStatus'); if (!status) return;
    const nameIn = $('forgeName'); if (!nameIn) return;
    const slug = nameIn.value.trim();
    if (!slug){ status.textContent = 'enter a preset name to delete'; return; }
    if (!confirm('Delete palette "' + slug + '"?')) return;
    try {
      const r = await fetch('/api/palettes/' + encodeURIComponent(slug), { method:'DELETE' });
      if (r.ok){
        status.textContent = 'deleted';
        window.__reloadUserPalettes?.();
        document.dispatchEvent(new CustomEvent('user-palettes-changed'));
      } else if (r.status === 404){
        status.textContent = 'not found';
      } else {
        status.textContent = 'delete failed (HTTP ' + r.status + ')';
      }
    } catch (err) { status.textContent = 'delete failed (network)'; }
  }

  let inited = false;
  function initOnce(){
    if (inited) return; inited = true;
    buildRows();
    initFromComputed();
    populateLoadSelect();
  }

  const toggle = $('forgeToggle');
  const panel = $('forgePanel');
  if (toggle && panel){
    toggle.addEventListener('click', () => {
      const open = panel.hasAttribute('hidden');
      if (open){
        initOnce();
        panel.removeAttribute('hidden');
        toggle.textContent = 'Close Color Forge';
      } else {
        panel.setAttribute('hidden', '');
        toggle.textContent = 'Open Color Forge';
      }
    });
  }
  $('forgeSave')?.addEventListener('click', onForgeSave);
  $('forgeDelete')?.addEventListener('click', onForgeDelete);
  $('forgeLoad')?.addEventListener('change', onForgeLoadChange);
})();

// v0.38 Forge — theme code share/import wiring (paletteCodec.js provides encode/decode)
(function(){
  var STORAGE_KEY = 'poe2gps.importedPalette';
  var STYLE_ID = 'importedPaletteStyles';
  var KEYS = (window.__paletteCodec && window.__paletteCodec.KEYS) || [];

  function camelToCssVar(k){ return '--' + k.replace(/[A-Z]/g, function(m){ return '-' + m.toLowerCase(); }); }

  function readLivePalette(){
    var cs = getComputedStyle(document.body);
    var vars = {};
    for (var i = 0; i < KEYS.length; i++){
      var v = cs.getPropertyValue(camelToCssVar(KEYS[i])).trim().toLowerCase();
      // normalize 3-digit shorthand to 6-digit
      if (/^#[0-9a-f]{3}$/.test(v)) v = '#' + v[1]+v[1]+v[2]+v[2]+v[3]+v[3];
      if (!/^#[0-9a-f]{6}$/.test(v)) v = '#000000';
      vars[KEYS[i]] = v;
    }
    var sel = document.querySelector('[data-set="dashboardPalette"]');
    var slug = document.body.getAttribute('data-palette') || '';
    var name = 'Imported Palette';
    if (sel && !/^imported-/.test(slug)) {
      var opt = sel.options[sel.selectedIndex];
      if (opt && opt.text) name = opt.text;
    }
    return { name: name, vars: vars };
  }

  function injectImportedStyle(imp){
    if (!imp || !imp.slug || !imp.vars) return;
    var css = 'body[data-palette="' + imp.slug + '"]{';
    for (var i = 0; i < KEYS.length; i++){
      css += camelToCssVar(KEYS[i]) + ':' + imp.vars[KEYS[i]] + ';';
    }
    css += '}';
    var tag = document.getElementById(STYLE_ID);
    if (!tag){ tag = document.createElement('style'); tag.id = STYLE_ID; document.head.appendChild(tag); }
    tag.textContent = css;
  }
  window.__paletteInjectImportedStyle = injectImportedStyle;

  function slugFromCode(code){
    // reuse the checksum tail from the code itself (6 chars, url-safe) — deterministic per palette
    var parts = String(code).split('-');
    return 'imported-' + (parts[2] || 'xxxxxx').toLowerCase().replace(/[^a-z0-9]/g, 'x');
  }

  function setStatus(el, msg, cls){
    if (!el) return;
    el.textContent = msg || '';
    el.classList.remove('ok','err');
    if (cls) el.classList.add(cls);
  }

  document.addEventListener('DOMContentLoaded', function(){
    var ta   = document.getElementById('paletteShareCode');
    var bC   = document.getElementById('btnPaletteCopy');
    var bI   = document.getElementById('btnPaletteImport');
    var stat = document.getElementById('paletteShareStatus');
    if (!ta || !bC || !bI || !window.__paletteCodec) return;

    bC.addEventListener('click', function(){
      try {
        var live = readLivePalette();
        var code = window.__paletteCodec.encode(live);
        ta.value = code;
        ta.select();
        var copied = false;
        try { copied = document.execCommand && document.execCommand('copy'); } catch(e){}
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(code).then(function(){ setStatus(stat, 'Copied to clipboard', 'ok'); }, function(){ setStatus(stat, copied ? 'Copied' : 'Code ready — press Ctrl+C', 'ok'); });
        } else {
          setStatus(stat, copied ? 'Copied' : 'Code ready — press Ctrl+C', 'ok');
        }
      } catch(e){ setStatus(stat, 'Copy failed: ' + (e && e.message || e), 'err'); }
    });

    bI.addEventListener('click', function(){
      var code = (ta.value || '').trim();
      if (!code){ setStatus(stat, 'Paste a RUNE1-... code first', 'err'); return; }
      var pal = window.__paletteCodec.decode(code);
      if (!pal){ setStatus(stat, 'Invalid or corrupted code', 'err'); return; }
      var imp = { slug: slugFromCode(code), name: pal.name || 'Imported Palette', vars: pal.vars, code: code };
      try { localStorage.setItem(STORAGE_KEY, JSON.stringify(imp)); } catch(e){}
      injectImportedStyle(imp);
      document.body.setAttribute('data-palette', imp.slug);
      setStatus(stat, 'Imported "' + imp.name + '" (live)', 'ok');
    });

    // Re-hydrate on load in case applySupporterCosmetics hasn't run yet
    try {
      var stored = JSON.parse(localStorage.getItem(STORAGE_KEY) || 'null');
      if (stored) injectImportedStyle(stored);
    } catch(e){}
  });
})();

/* ── v0.39 R5 Rules Engine dashboard tab: list + editor + selector/effect builders.
     Consumes R6's /api/rules CRUD endpoints; R1 owns the on-disk RuleRecord shape.
     Fires a 'rules-changed' CustomEvent on save/delete so future R3/R4 can recompile
     without a page reload. PRESERVE ALL OTHER BEHAVIOR — this is an additive IIFE. */
(() => {
  const RULE_CAP = 100;
  const WARN_CAP = 50;
  const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

  // 8 predicates mirror R1's Selector record (all optional, AND-ed).
  const PREDICATE_DEFS = [
    { key: 'Metadata',  label: 'metadata',  type: 'text',   hint: 'regex on entity metadata path' },
    { key: 'Token',     label: 'token',     type: 'text',   hint: 'regex on entity token' },
    { key: 'Rarity',    label: 'rarity',    type: 'select', options: ['unique','rare','magic','normal'] },
    { key: 'ZoneCode',  label: 'zoneCode',  type: 'text',   hint: 'regex on area zone code' },
    { key: 'InHideout', label: 'inHideout', type: 'bool' },
    { key: 'MinLevel',  label: 'minLevel',  type: 'number' },
    { key: 'MaxLevel',  label: 'maxLevel',  type: 'number' },
    { key: 'HasBuff',   label: 'hasBuff',   type: 'text',   hint: 'regex on active buff id' },
  ];

  const EFFECT_KINDS = ['hide','tint','ring','label','sound','pulse'];

  // editor state
  let __editing = null;      // null = closed, {} = new, or the rule being edited
  let __effects = [];        // array of effect state objects { kind, ...fields }
  let __rulesCache = [];     // last loaded list (for priority swaps)

  function esc(s){ return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }

  function setStatus(msg, cls){
    const el = document.getElementById('ruleEditorStatus');
    if (!el) return;
    el.textContent = msg || '';
    el.className = 'palette-share-status' + (cls ? ' ' + cls : '');
  }

  async function loadRules(){
    const list = document.getElementById('rulesList');
    const chip = document.getElementById('ruleCapChip');
    try {
      const r = await fetch('/api/rules', { cache: 'no-store' });
      if (!r.ok) throw 0;
      const data = await r.json();
      __rulesCache = (data && data.rules) ? data.rules : [];
      renderRules(__rulesCache);
      if (chip) {
        const n = __rulesCache.length;
        chip.textContent = n + '/' + RULE_CAP;
        chip.classList.remove('warn','full');
        if (n >= RULE_CAP) chip.classList.add('full');
        else if (n >= WARN_CAP) chip.classList.add('warn');
      }
      const btn = document.getElementById('btnNewRule');
      if (btn) btn.disabled = __rulesCache.length >= RULE_CAP;
    } catch (e) {
      if (list) list.innerHTML = '<div class="rules-error">Failed to load rules (network error).</div>';
      if (chip) { chip.textContent = '?/' + RULE_CAP; chip.classList.remove('warn','full'); }
    }
  }

  function countPredicates(when){
    if (!when) return 0;
    let n = 0;
    for (const def of PREDICATE_DEFS) {
      const v = when[def.key];
      if (v === undefined || v === null || v === '') continue;
      if (def.type === 'bool' && v === false) continue;
      n++;
    }
    return n;
  }

  function summarizeEffects(then){
    if (!then || !then.length) return 'no effects';
    return then.length + ' effect' + (then.length === 1 ? '' : 's') + ': ' + then.map(e => e.kind).join(', ');
  }

  function renderRules(rules){
    const host = document.getElementById('rulesList');
    if (!host) return;
    if (!rules.length) {
      host.innerHTML = '<div class="rules-empty">No rules yet. Click <b>+ New rule</b> to author one, or migrate a rule from the Affix Nameplates / Buff Nameplates / etc tabs.</div>';
      return;
    }
    const sorted = rules.slice().sort((a,b) => (b.Priority||0) - (a.Priority||0));
    host.innerHTML = sorted.map(r => {
      const id = r.Id || '';
      const enabled = r.Enabled !== false;
      const npred = countPredicates(r.When);
      const neff = (r.Then && r.Then.length) || 0;
      return '<div class="rule-card' + (enabled ? '' : ' disabled') + '" data-id="' + esc(id) + '">' +
        '<label class="rc-toggle"><input type="checkbox" class="rc-en" ' + (enabled ? 'checked' : '') + '> on</label>' +
        '<div class="rc-prio">' +
          '<button class="rc-up" title="move up">&#9650;</button>' +
          '<button class="rc-down" title="move down">&#9660;</button>' +
        '</div>' +
        '<span class="rc-name">' + esc(r.Name || '(unnamed)') + '</span>' +
        '<span class="rc-summary">' + npred + ' predicate' + (npred === 1 ? '' : 's') + ' &rarr; ' + summarizeEffects(r.Then) + '</span>' +
        '<div class="rc-actions">' +
          '<button class="rc-edit">edit</button>' +
          '<button class="rc-del">delete</button>' +
        '</div>' +
      '</div>';
    }).join('');

    host.querySelectorAll('.rule-card').forEach(card => {
      const id = card.getAttribute('data-id');
      card.querySelector('.rc-en').addEventListener('change', async (ev) => {
        const checked = ev.target.checked;
        card.classList.toggle('disabled', !checked);
        const rule = __rulesCache.find(x => String(x.Id) === id);
        if (!rule) return;
        const body = JSON.parse(JSON.stringify(rule));
        body.Enabled = checked;
        try {
          const r = await fetch('/api/rules', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(body) });
          if (!r.ok) { ev.target.checked = !checked; card.classList.toggle('disabled', !checked); throw 0; }
          rule.Enabled = checked;
          dispatchRulesChanged();
        } catch (e) {
          const err = document.createElement('div');
          err.className = 'rules-error';
          err.textContent = 'Failed to toggle rule (reverted).';
          host.prepend(err);
          setTimeout(() => err.remove(), 3000);
        }
      });
      card.querySelector('.rc-up').addEventListener('click', () => swapPriority(id, -1));
      card.querySelector('.rc-down').addEventListener('click', () => swapPriority(id, 1));
      card.querySelector('.rc-edit').addEventListener('click', () => {
        const rule = __rulesCache.find(x => String(x.Id) === id);
        if (rule) openEditor(rule);
      });
      card.querySelector('.rc-del').addEventListener('click', () => deleteRule(id));
    });
  }

  async function swapPriority(id, dir){
    const sorted = __rulesCache.slice().sort((a,b) => (b.Priority||0) - (a.Priority||0));
    const idx = sorted.findIndex(r => String(r.Id) === id);
    if (idx < 0) return;
    const j = idx + dir;
    if (j < 0 || j >= sorted.length) return;
    const a = sorted[idx], b = sorted[j];
    const pa = a.Priority || 0, pb = b.Priority || 0;
    const bodyA = JSON.parse(JSON.stringify(a)); bodyA.Priority = pb;
    const bodyB = JSON.parse(JSON.stringify(b)); bodyB.Priority = pa;
    try {
      const r1 = await fetch('/api/rules', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(bodyA) });
      if (!r1.ok) throw 0;
      const r2 = await fetch('/api/rules', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(bodyB) });
      if (!r2.ok) throw 0;
      await loadRules();
      dispatchRulesChanged();
    } catch (e) {
      const host = document.getElementById('rulesList');
      if (host) {
        const err = document.createElement('div');
        err.className = 'rules-error';
        err.textContent = 'Failed to reorder rule.';
        host.prepend(err);
        setTimeout(() => err.remove(), 3000);
      }
    }
  }

  function dispatchRulesChanged(){
    document.dispatchEvent(new CustomEvent('rules-changed'));
  }

  // ── editor ──
  function buildSelectorRows(){
    const host = document.getElementById('selectorRows');
    if (!host) return;
    host.innerHTML = PREDICATE_DEFS.map(def => {
      let input = '';
      if (def.type === 'text') {
        input = '<input type="text" class="sr-input sr-input-text" data-key="' + def.key + '" placeholder="' + esc(def.label) + '">';
      } else if (def.type === 'number') {
        input = '<input type="number" class="sr-input sr-input-num" data-key="' + def.key + '" step="1" style="width:80px">';
      } else if (def.type === 'select') {
        input = '<select class="sr-input sr-input-sel" data-key="' + def.key + '">' +
          def.options.map(o => '<option value="' + o + '">' + o + '</option>').join('') + '</select>';
      } else if (def.type === 'bool') {
        input = '<label class="sr-toggle" style="display:flex;align-items:center;gap:6px;cursor:pointer">' +
          '<input type="checkbox" class="sr-input sr-input-bool" data-key="' + def.key + '"> true</label>';
      }
      const hint = def.hint ? '<span class="sr-hint">' + esc(def.hint) + '</span>' : '';
      return '<div class="selector-row disabled" data-key="' + def.key + '">' +
        '<label class="sr-enable"><input type="checkbox" class="sr-on"> ' + esc(def.label) + '</label>' +
        input + hint + '</div>';
    }).join('');

    host.querySelectorAll('.selector-row').forEach(row => {
      const on = row.querySelector('.sr-on');
      on.addEventListener('change', () => {
        row.classList.toggle('disabled', !on.checked);
        updateSaveGate();
      });
      const inp = row.querySelector('.sr-input');
      if (inp) inp.addEventListener('input', updateSaveGate);
    });
  }

  function readSelector(){
    const when = {};
    document.querySelectorAll('#selectorRows .selector-row').forEach(row => {
      const key = row.getAttribute('data-key');
      const on = row.querySelector('.sr-on').checked;
      if (!on) return;
      const def = PREDICATE_DEFS.find(d => d.key === key);
      const inp = row.querySelector('.sr-input');
      let val = def.type === 'bool' ? inp.checked : inp.value;
      if (def.type === 'number') {
        if (val === '' || val === null) return;
        val = parseInt(val, 10); if (isNaN(val)) return;
      } else if (def.type === 'text' || def.type === 'select') {
        if (val === '') return;
      }
      when[key] = val;
    });
    return when;
  }

  function fillSelector(when){
    when = when || {};
    document.querySelectorAll('#selectorRows .selector-row').forEach(row => {
      const key = row.getAttribute('data-key');
      const def = PREDICATE_DEFS.find(d => d.key === key);
      const v = when[key];
      const on = (v !== undefined && v !== null && v !== '' && !(def.type === 'bool' && v === false));
      row.querySelector('.sr-on').checked = on;
      row.classList.toggle('disabled', !on);
      const inp = row.querySelector('.sr-input');
      if (inp) {
        if (def.type === 'bool') inp.checked = !!v;
        else inp.value = (v === undefined || v === null) ? '' : String(v);
      }
    });
  }

  // ── effects ──
  function renderEffectChips(){
    const host = document.getElementById('effectChips');
    if (!host) return;
    host.innerHTML = __effects.map((e, i) => {
      let config = '';
      if (e.kind === 'tint' || e.kind === 'ring') {
        config = '<input type="color" class="ec-color" value="' + esc(e.Color || '#c8a049') + '">';
      } else if (e.kind === 'label') {
        config = '<input type="text" class="ec-text" placeholder="label text (use {tokens})" value="' + esc(e.Text || '') + '" style="min-width:200px">';
      } else if (e.kind === 'sound') {
        config = '<input type="text" class="ec-text" placeholder="filename (in config/sounds/)" value="' + esc(e.File || '') + '" style="min-width:200px">';
      } else if (e.kind === 'pulse') {
        config = '<select class="ec-sel"><option value="slow"' + (e.Speed === 'slow' ? ' selected' : '') + '>slow</option><option value="fast"' + (e.Speed === 'fast' ? ' selected' : '') + '>fast</option></select>';
      }
      return '<div class="effect-chip" data-i="' + i + '">' +
        '<span class="ec-kind">' + esc(e.kind) + '</span>' +
        '<div class="ec-config">' + config + '</div>' +
        '<div class="ec-reorder"><button class="ec-up" title="move up">&#9650;</button><button class="ec-down" title="move down">&#9660;</button></div>' +
        '<button class="ec-remove" title="remove">&times;</button>' +
      '</div>';
    }).join('');

    host.querySelectorAll('.effect-chip').forEach(chip => {
      const i = parseInt(chip.getAttribute('data-i'), 10);
      const e = __effects[i];
      const colorInp = chip.querySelector('.ec-color');
      if (colorInp) colorInp.addEventListener('input', () => { e.Color = colorInp.value; });
      const textInp = chip.querySelector('.ec-text');
      if (textInp) textInp.addEventListener('input', () => {
        if (e.kind === 'label') e.Text = textInp.value;
        else if (e.kind === 'sound') e.File = textInp.value;
      });
      const sel = chip.querySelector('.ec-sel');
      if (sel) sel.addEventListener('change', () => { e.Speed = sel.value; });
      chip.querySelector('.ec-remove').addEventListener('click', () => {
        __effects.splice(i, 1);
        renderEffectChips();
        updateSaveGate();
      });
      chip.querySelector('.ec-up').addEventListener('click', () => {
        if (i > 0) { const t = __effects[i-1]; __effects[i-1] = __effects[i]; __effects[i] = t; renderEffectChips(); updateSaveGate(); }
      });
      chip.querySelector('.ec-down').addEventListener('click', () => {
        if (i < __effects.length - 1) { const t = __effects[i+1]; __effects[i+1] = __effects[i]; __effects[i] = t; renderEffectChips(); updateSaveGate(); }
      });
    });
  }

  function effectsToJSON(){
    return __effects.map(e => {
      if (e.kind === 'hide') return { kind: 'hide' };
      if (e.kind === 'tint') return { kind: 'tint', Color: e.Color || '#000000' };
      if (e.kind === 'ring') return { kind: 'ring', Color: e.Color || '#000000' };
      if (e.kind === 'label') return { kind: 'label', Text: e.Text || '' };
      if (e.kind === 'sound') return { kind: 'sound', File: e.File || '' };
      if (e.kind === 'pulse') return { kind: 'pulse', Speed: e.Speed || 'slow' };
      return { kind: e.kind };
    });
  }

  function effectsFromJSON(then){
    __effects = (then || []).map(e => {
      if (e.kind === 'tint' || e.kind === 'ring') return { kind: e.kind, Color: e.Color || '#c8a049' };
      if (e.kind === 'label') return { kind: 'label', Text: e.Text || '' };
      if (e.kind === 'sound') return { kind: 'sound', File: e.File || '' };
      if (e.kind === 'pulse') return { kind: 'pulse', Speed: e.Speed || 'slow' };
      return { kind: e.kind || 'hide' };
    });
  }

  function updateSaveGate(){
    const btn = document.getElementById('btnSaveRule');
    if (!btn) return;
    const name = (document.getElementById('ruleNameInput').value || '').trim();
    const ok = name.length > 0 && __effects.length > 0;
    btn.disabled = !ok;
  }

  function openEditor(rule){
    __editing = rule ? rule : {};
    const pane = document.getElementById('rulesEditor');
    if (pane) pane.hidden = false;
    document.getElementById('ruleNameInput').value = rule ? (rule.Name || '') : '';
    document.getElementById('rulePriorityInput').value = rule ? (rule.Priority || 0) : 0;
    document.getElementById('ruleEnabledInput').checked = rule ? (rule.Enabled !== false) : true;
    fillSelector(rule ? rule.When : null);
    effectsFromJSON(rule ? rule.Then : null);
    renderEffectChips();
    setStatus('', '');
    updateSaveGate();
    document.getElementById('ruleNameInput').focus();
  }

  function closeEditor(){
    __editing = null;
    __effects = [];
    const pane = document.getElementById('rulesEditor');
    if (pane) pane.hidden = true;
    setStatus('', '');
  }

  async function saveRule(){
    const name = (document.getElementById('ruleNameInput').value || '').trim();
    if (!name) { setStatus('Name is required', 'err'); return; }
    if (__effects.length === 0) { setStatus('At least one effect is required', 'err'); return; }
    const priority = parseInt(document.getElementById('rulePriorityInput').value, 10) || 0;
    const enabled = document.getElementById('ruleEnabledInput').checked;
    const when = readSelector();
    const then = effectsToJSON();
    const id = (__editing && __editing.Id) ? String(__editing.Id) : EMPTY_GUID;
    const body = { Id: id, Name: name, Priority: priority, Enabled: enabled, When: when, Then: then };
    setStatus('Saving...', '');
    try {
      const r = await fetch('/api/rules', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(body) });
      if (r.status === 409) { const d = await r.json().catch(() => ({})); setStatus('A rule named "' + (d.name || name) + '" already exists', 'err'); return; }
      if (r.status === 400) { const d = await r.json().catch(() => ({})); setStatus(d.error || 'Validation error', 'err'); return; }
      if (!r.ok) { setStatus('Save failed (HTTP ' + r.status + ')', 'err'); return; }
      closeEditor();
      await loadRules();
      dispatchRulesChanged();
    } catch (e) {
      setStatus('Save failed (network error)', 'err');
    }
  }

  async function deleteRule(id){
    if (!confirm('Delete this rule? This cannot be undone.')) return;
    try {
      const r = await fetch('/api/rules/' + encodeURIComponent(id), { method: 'DELETE' });
      if (!r.ok) { const host = document.getElementById('rulesList'); if (host) { const err = document.createElement('div'); err.className = 'rules-error'; err.textContent = 'Delete failed (HTTP ' + r.status + ').'; host.prepend(err); setTimeout(() => err.remove(), 3000); } return; }
      await loadRules();
      dispatchRulesChanged();
    } catch (e) {
      const host = document.getElementById('rulesList');
      if (host) { const err = document.createElement('div'); err.className = 'rules-error'; err.textContent = 'Delete failed (network error).'; host.prepend(err); setTimeout(() => err.remove(), 3000); }
    }
  }

  // ── wire-up (runs once on script load) ──
  function init(){
    buildSelectorRows();

    const btnNew = document.getElementById('btnNewRule');
    if (btnNew) btnNew.addEventListener('click', () => openEditor(null));
    const btnSave = document.getElementById('btnSaveRule');
    if (btnSave) btnSave.addEventListener('click', saveRule);
    const btnCancel = document.getElementById('btnCancelRule');
    if (btnCancel) btnCancel.addEventListener('click', closeEditor);
    const nameInp = document.getElementById('ruleNameInput');
    if (nameInp) nameInp.addEventListener('input', updateSaveGate);
    const addEff = document.getElementById('btnAddEffect');
    if (addEff) addEff.addEventListener('click', () => {
      const sel = document.getElementById('effectKindSelect');
      const kind = sel.value;
      if (!kind || EFFECT_KINDS.indexOf(kind) < 0) { setStatus('Pick an effect kind first', 'err'); return; }
      const seed = { kind };
      if (kind === 'tint' || kind === 'ring') seed.Color = '#c8a049';
      else if (kind === 'label') seed.Text = '';
      else if (kind === 'sound') seed.File = '';
      else if (kind === 'pulse') seed.Speed = 'slow';
      __effects.push(seed);
      renderEffectChips();
      updateSaveGate();
      sel.value = '';
      setStatus('', '');
    });

    // Tab activation: load on first open (lazy). Subsequent opens are no-ops; the
    // list is refreshed on save/delete instead.
    let __loaded = false;
    document.querySelectorAll('.tab[data-tab="rules"]').forEach(t => t.addEventListener('click', () => {
      if (!__loaded) { __loaded = true; loadRules(); }
    }));

    // R7: listen for migration prefill from legacy-tab "Migrate to Rule Engine" buttons.
    document.addEventListener('switch-to-rules-engine-with-prefill', () => {
      const raw = sessionStorage.getItem('rules-engine-prefill');
      if (!raw) return;
      sessionStorage.removeItem('rules-engine-prefill');
      // If editor is open with unsaved changes, confirm first.
      const pane = document.getElementById('rulesEditor');
      if (pane && !pane.hidden && __editing) {
        if (!confirm('Discard your current edit and load the migration prefill?')) return;
      }
      // Activate the Rules tab.
      const rulesTab = document.querySelector('.tab[data-tab="rules"]');
      if (rulesTab) rulesTab.click();
      // Open editor with the parsed prefill.
      try {
        const prefill = JSON.parse(raw);
        openEditor(prefill);
        setStatus('Migration prefill loaded — review and save.', '');
      } catch (e) {
        // If prefill is malformed, open blank + show message.
        openEditor(null);
        setStatus('Migration attempted — please review and fill in the missing fields.', '');
      }
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();

// CARTOGRAPHER-JS-START
// v0.40 P4 Cartographer dashboard tab: position heatmap over 64x64 grid.
// Consumes /api/tracks/{characters,zones} + /api/tracks (P3). Dashboard-only.
// All fetch failures are silently caught (clear canvas + "no data" info chip).
(() => {
  const RAMP = ['rgba(0,0,0,0)', 'rgba(20,40,80,0.5)', 'rgba(30,120,140,0.7)', 'rgba(210,190,80,0.85)', 'rgba(220,120,50,0.95)'];
  const GRID = 64;
  const CELL = 8; // 512 / 64

  const charSelect = document.getElementById('cartoCharSelect');
  const zoneSelect = document.getElementById('cartoZoneSelect');
  const info = document.getElementById('cartoInfo');
  const canvas = document.getElementById('cartoCanvas');
  const ctx = canvas ? canvas.getContext('2d') : null;

  let charCache = null;
  const zoneCache = new Map();   // character -> zones[]
  const tracksCache = new Map();  // "character|zone" -> samples[]

  // P5 route replay state
  let _playbackIndex = 0;
  let _playing = false;
  let _speedMode = '1';
  const SPEED_PRESETS = ['1', '4', '16', 'max'];
  let _rafHandle = null;
  let _heatmapCache = null;       // cached heatmap ImageBitmap or offscreen canvas
  let _heatmapSamples = null;     // samples used for the cached heatmap
  let _currentSamples = null;     // currently loaded samples for route rendering

  function setInfo(msg) { if (info) info.textContent = msg; }

  function clearCanvas() {
    if (!ctx || !canvas) return;
    ctx.clearRect(0, 0, canvas.width, canvas.height);
  }

  function populateCharSelect(chars) {
    if (!charSelect) return;
    charSelect.innerHTML = '<option value="">\u2014 select \u2014</option>';
    for (const c of chars) {
      const opt = document.createElement('option');
      opt.value = c; opt.textContent = c;
      charSelect.appendChild(opt);
    }
    setInfo(chars.length + ' characters');
  }

  function populateZoneSelect(zones) {
    if (!zoneSelect) return;
    zoneSelect.innerHTML = '<option value="">\u2014 select \u2014</option>';
    for (const z of zones) {
      const opt = document.createElement('option');
      opt.value = z; opt.textContent = z;
      zoneSelect.appendChild(opt);
    }
    clearCanvas();
    setInfo(zones.length + ' zones');
  }

  function loadCharacters() {
    if (charCache) { populateCharSelect(charCache); return; }
    try {
      fetch('/api/tracks/characters')
        .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
        .then(j => {
          charCache = Array.isArray(j.characters) ? j.characters : [];
          populateCharSelect(charCache);
        })
        .catch(() => { charCache = []; populateCharSelect(charCache); setInfo('no data'); });
    } catch (e) { charCache = []; populateCharSelect(charCache); setInfo('no data'); }
  }

  function loadZones(character) {
    if (!character) {
      if (zoneSelect) zoneSelect.innerHTML = '<option value="">\u2014 select \u2014</option>';
      clearCanvas(); setInfo(''); return;
    }
    if (zoneCache.has(character)) { populateZoneSelect(zoneCache.get(character)); return; }
    try {
      fetch('/api/tracks/zones?character=' + encodeURIComponent(character))
        .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
        .then(j => {
          const zones = Array.isArray(j.zones) ? j.zones : [];
          zoneCache.set(character, zones);
          populateZoneSelect(zones);
        })
        .catch(() => { zoneCache.set(character, []); populateZoneSelect([]); setInfo('no data'); });
    } catch (e) { zoneCache.set(character, []); populateZoneSelect([]); setInfo('no data'); }
  }

  function loadTracks(character, zone) {
    stopPlayback();
    _playbackIndex = 0;
    _heatmapCache = null;
    _heatmapSamples = null;
    _currentSamples = null;
    const scrub = document.getElementById('cartoScrub');
    if (scrub) { scrub.min = '0'; scrub.max = '0'; scrub.value = '0'; }
    const tsEl = document.getElementById('cartoTimestamp');
    if (tsEl) tsEl.textContent = '0.0s';
    if (!character || !zone) { clearCanvas(); setInfo(''); return; }
    const key = character + '|' + zone;
    if (tracksCache.has(key)) { 
      const samples = tracksCache.get(key);
      _currentSamples = samples;
      renderHeatmap(samples, canvas);
      const scrub = document.getElementById('cartoScrub');
      if (scrub && samples.length > 0) { scrub.min = '0'; scrub.max = String(samples.length - 1); scrub.value = '0'; }
      return;
    }
    try {
      fetch('/api/tracks?character=' + encodeURIComponent(character) + '&zone=' + encodeURIComponent(zone))
        .then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
        .then(j => {
          const samples = Array.isArray(j.samples) ? j.samples : [];
          tracksCache.set(key, samples);
          _currentSamples = samples;
          renderHeatmap(samples, canvas);
          const scrub = document.getElementById('cartoScrub');
          if (scrub && samples.length > 0) { scrub.min = '0'; scrub.max = String(samples.length - 1); scrub.value = '0'; }
        })
        .catch(() => { clearCanvas(); setInfo('no data'); });
    } catch (e) { clearCanvas(); setInfo('no data'); }
  }

  function renderHeatmap(samples, canvas) {
    if (!canvas) return;
    const c2d = canvas.getContext('2d');
    if (!c2d) return;
    c2d.clearRect(0, 0, canvas.width, canvas.height);
    if (!samples || samples.length === 0) { setInfo('0 samples'); return; }
    // compute sample bounds (min/max of x/y across samples)
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const s of samples) {
      const x = s.x, y = s.y;
      if (x < minX) minX = x;
      if (y < minY) minY = y;
      if (x > maxX) maxX = x;
      if (y > maxY) maxY = y;
    }
    const rangeX = (maxX - minX) || 1;
    const rangeY = (maxY - minY) || 1;
    // one pass over samples -> count per cell (Map keyed on "cx,cy")
    const counts = new Map();
    for (const s of samples) {
      const cx = Math.min(GRID - 1, Math.max(0, Math.floor(((s.x - minX) / rangeX) * GRID)));
      const cy = Math.min(GRID - 1, Math.max(0, Math.floor(((s.y - minY) / rangeY) * GRID)));
      const k = cx + ',' + cy;
      counts.set(k, (counts.get(k) || 0) + 1);
    }
    // density = log(1 + count), normalized to [0,1] across the grid
    let maxD = 0;
    const density = new Map();
    for (const [k, c] of counts) {
      const d = Math.log(1 + c);
      density.set(k, d);
      if (d > maxD) maxD = d;
    }
    if (maxD <= 0) maxD = 1;
    // rasterize: empty cells -> transparent; single-sample cell -> lowest non-transparent ramp
    for (const [k, d] of density) {
      const parts = k.split(',');
      const cx = +parts[0], cy = +parts[1];
      const c = counts.get(k);
      let idx;
      if (c === 1) {
        idx = 1; // single-sample -> lowest non-transparent ramp color
      } else {
        const t = d / maxD; // [0,1]
        idx = Math.min(RAMP.length - 1, Math.max(1, Math.ceil(t * (RAMP.length - 1))));
        if (idx < 1) idx = 1;
      }
      c2d.fillStyle = RAMP[idx];
      c2d.fillRect(cx * CELL, cy * CELL, CELL, CELL);
    }
    setInfo(samples.length + ' samples');
    // Cache the heatmap render for route overlay (offscreen canvas)
    const offscreen = document.createElement('canvas');
    offscreen.width = canvas.width;
    offscreen.height = canvas.height;
    const offCtx = offscreen.getContext('2d');
    if (offCtx) {
      offCtx.drawImage(canvas, 0, 0);
      _heatmapCache = offscreen;
    }
    _heatmapSamples = samples;
  }

  // P5 route replay: draws a dotted path from sample[0] to sample[currentIndex] + marker
  function renderRoute(samples, index, canvas) {
    if (!canvas) return;
    const c2d = canvas.getContext('2d');
    if (!c2d) return;
    if (!samples || samples.length === 0 || index < 0 || index >= samples.length) return;

    // Render cached heatmap base (if available)
    if (_heatmapCache) {
      c2d.clearRect(0, 0, canvas.width, canvas.height);
      c2d.drawImage(_heatmapCache, 0, 0);
    }

    // Normalize coordinates to grid space (same as heatmap projection)
    let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
    for (const s of samples) {
      if (s.x < minX) minX = s.x;
      if (s.y < minY) minY = s.y;
      if (s.x > maxX) maxX = s.x;
      if (s.y > maxY) maxY = s.y;
    }
    const rangeX = (maxX - minX) || 1;
    const rangeY = (maxY - minY) || 1;

    function toGrid(s) {
      return {
        gx: Math.min(GRID - 1, Math.max(0, Math.floor(((s.x - minX) / rangeX) * GRID))) * CELL + CELL / 2,
        gy: Math.min(GRID - 1, Math.max(0, Math.floor(((s.y - minY) / rangeY) * GRID))) * CELL + CELL / 2
      };
    }

    c2d.save();
    c2d.globalAlpha = 0.85;

    // Draw dotted path from 0 to index
    c2d.beginPath();
    c2d.strokeStyle = '#f5c94f';
    c2d.lineWidth = 1.5;
    c2d.setLineDash([3, 4]);
    const start = toGrid(samples[0]);
    c2d.moveTo(start.gx, start.gy);
    for (let i = 1; i <= index; i++) {
      const p = toGrid(samples[i]);
      c2d.lineTo(p.gx, p.gy);
    }
    c2d.stroke();

    // Draw marker at currentIndex
    const mp = toGrid(samples[index]);
    c2d.setLineDash([]);
    c2d.fillStyle = '#f5c94f';
    c2d.beginPath();
    c2d.arc(mp.gx, mp.gy, 4, 0, Math.PI * 2);
    c2d.fill();

    c2d.restore();

    // Update timestamp
    const tsEl = document.getElementById('cartoTimestamp');
    if (tsEl) tsEl.textContent = (samples[index].t / 1000).toFixed(1) + 's';
  }

  function stopPlayback() {
    if (_rafHandle) {
      cancelAnimationFrame(_rafHandle);
      _rafHandle = null;
    }
    _playing = false;
    const playBtn = document.getElementById('cartoPlay');
    const pauseBtn = document.getElementById('cartoPause');
    if (playBtn) playBtn.hidden = false;
    if (pauseBtn) pauseBtn.hidden = true;
  }

  function startPlayback() {
    const samples = _currentSamples;
    if (!samples || samples.length === 0) return;
    // If at end, reset to start
    if (_playbackIndex >= samples.length - 1) {
      _playbackIndex = 0;
    }
    stopPlayback();
    _playing = true;
    const playBtn = document.getElementById('cartoPlay');
    const pauseBtn = document.getElementById('cartoPause');
    if (playBtn) playBtn.hidden = true;
    if (pauseBtn) pauseBtn.hidden = false;

    function tick() {
      if (!_playing) return;
      const sp = _currentSamples;
      if (!sp || sp.length === 0) { stopPlayback(); return; }

      let advance;
      if (_speedMode === 'max') {
        advance = 1; // 1 sample per frame
      } else {
        const speed = parseInt(_speedMode, 10) || 1;
        advance = Math.max(1, Math.round(speed / 60));
      }

      _playbackIndex = Math.min(_playbackIndex + advance, sp.length - 1);
      renderRoute(sp, _playbackIndex, canvas);

      // Update scrub slider
      const scrub = document.getElementById('cartoScrub');
      if (scrub) scrub.value = _playbackIndex;

      if (_playbackIndex >= sp.length - 1) {
        stopPlayback();
        return;
      }

      _rafHandle = requestAnimationFrame(tick);
    }

    _rafHandle = requestAnimationFrame(tick);
  }

  // ── wire-up (runs once on script load) ──
  function init() {
    if (charSelect) charSelect.addEventListener('change', () => loadZones(charSelect.value));
    if (zoneSelect) zoneSelect.addEventListener('change', () => loadTracks(charSelect.value, zoneSelect.value));
    let __loaded = false;
    document.querySelectorAll('.tab[data-tab="cartographer"]').forEach(t => t.addEventListener('click', () => {
      if (!__loaded) { __loaded = true; loadCharacters(); }
    }));

    // P5 playback controls
    const playBtn = document.getElementById('cartoPlay');
    if (playBtn) playBtn.addEventListener('click', startPlayback);

    const pauseBtn = document.getElementById('cartoPause');
    if (pauseBtn) pauseBtn.addEventListener('click', stopPlayback);

    const firstBtn = document.getElementById('cartoFirst');
    if (firstBtn) firstBtn.addEventListener('click', () => {
      const samples = _currentSamples;
      if (!samples || samples.length === 0) return;
      stopPlayback();
      _playbackIndex = 0;
      renderRoute(samples, 0, canvas);
      const scrub = document.getElementById('cartoScrub');
      if (scrub) scrub.value = '0';
    });

    const lastBtn = document.getElementById('cartoLast');
    if (lastBtn) lastBtn.addEventListener('click', () => {
      const samples = _currentSamples;
      if (!samples || samples.length === 0) return;
      stopPlayback();
      _playbackIndex = samples.length - 1;
      renderRoute(samples, _playbackIndex, canvas);
      const scrub = document.getElementById('cartoScrub');
      if (scrub) scrub.value = String(_playbackIndex);
    });

    const scrub = document.getElementById('cartoScrub');
    if (scrub) scrub.addEventListener('input', () => {
      const samples = _currentSamples;
      if (!samples || samples.length === 0) return;
      stopPlayback();
      _playbackIndex = parseInt(scrub.value, 10);
      renderRoute(samples, _playbackIndex, canvas);
    });

    const speedSel = document.getElementById('cartoSpeed');
    if (speedSel) speedSel.addEventListener('change', () => {
      _speedMode = speedSel.value;
    });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
// CARTOGRAPHER-JS-END

/* v0.40 P6 Cartographer discoverability hint on Overlay tab: one-time-dismissable card.
   Shows the hint card on page load if localStorage doesn't have the dismissed key.
   Dismiss button sets localStorage and hides the card. */
(() => {
  try {
    if (localStorage.getItem('cartographer-hint-dismissed') === '1') return;
    const el = document.getElementById('cartographerHint');
    if (!el) return;
    el.hidden = false;
    document.getElementById('cartographerHintClose')?.addEventListener('click', () => {
      try { localStorage.setItem('cartographer-hint-dismissed', '1'); } catch {}
      el.hidden = true;
    });
  } catch {}
})();

/* ── v0.41 A3 Radar Filter dashboard tab: per-zone entity whitelist/blacklist presets.
     Consumes /api/radar-filters (A_API) CRUD; supporter-gated via S3 __supporterHint.
     The existing #radarFilterHint div (declared in the global hint area above the views)
     is the mount for the supporter hint: tab activation renders the hint there for
     non-supporters, or loads + renders the preset list for supporters. The gate is
     re-checked on every render so a mid-session supporter-code change flips the view.
     PRESERVE ALL OTHER BEHAVIOR — additive IIFE; no existing tab/IIFE is touched. */
// RADAR-FILTER-JS-START
(() => {
  const RF_CAP = 20;
  const RF_WARN = 10;

  // Editor state: array of {match, whitelist[], blacklist[], _open}
  let __presetsCache = [];
  // null = not yet loaded; array after first /entities fetch (cached for the entity dropdown)
  let __entitiesCache = null;

  function esc(s){ return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }

  function isSupporter() {
    try { return !!(window.__supporterGate && window.__supporterGate.isSupporter()); }
    catch (e) { return false; }
  }

  function setStatus(cardEl, msg, cls) {
    const el = cardEl?.querySelector('.rf-status');
    if (!el) return;
    el.textContent = msg || '';
    el.className = 'rf-status' + (cls ? ' ' + cls : '');
  }

  function updateCapChip() {
    const chip = document.getElementById('rfCapChip');
    if (!chip) return;
    const n = __presetsCache.length;
    chip.textContent = 'presets: ' + n + ' / ' + RF_CAP;
    chip.classList.remove('warn', 'full');
    if (n >= RF_CAP) chip.classList.add('full');
    else if (n >= RF_WARN) chip.classList.add('warn');
    const btn = document.getElementById('btnRfNewPreset');
    if (btn) btn.disabled = n >= RF_CAP;
  }

  function chipHtml(pattern, kind) {
    return '<span class="rf-chip ' + kind + '">' + esc(pattern) +
      '<button type="button" class="rf-chip-x" data-pat="' + esc(pattern) + '" data-kind="' + kind + '" aria-label="remove">&times;</button>' +
      '</span>';
  }

  function chipListHtml(arr, kind) {
    if (!arr || !arr.length) return '<span class="hint-row" style="opacity:.5">none</span>';
    return arr.map(p => chipHtml(p, kind)).join('');
  }

  function renderPresets() {
    const host = document.getElementById('rfPresetList');
    if (!host) return;
    if (!__presetsCache.length) {
      host.innerHTML = '<div class="rf-empty">No radar filter presets yet. Click <b>+ New preset</b> to scope the radar by zone code and entity metadata.</div>';
      updateCapChip();
      return;
    }
    host.innerHTML = __presetsCache.map((p, i) => {
      const open = !!p._open;
      const wl = p.whitelist || [];
      const bl = p.blacklist || [];
      return '<div class="rf-card" data-i="' + i + '">' +
        '<div class="rf-card-head">' +
          '<input type="text" class="rf-match" value="' + esc(p.match || '') + '" placeholder="match (zone code glob, e.g. *_town)">' +
          '<div class="rf-chip-list" title="whitelist">' + chipListHtml(wl, 'wl') + '</div>' +
          '<div class="rf-chip-list" title="blacklist">' + chipListHtml(bl, 'bl') + '</div>' +
          '<button type="button" class="rf-up" title="move up">&#9650;</button>' +
          '<button type="button" class="rf-dn" title="move down">&#9660;</button>' +
          '<button type="button" class="rf-del" title="delete">&times;</button>' +
          '<button type="button" class="rf-toggle" title="' + (open ? 'collapse' : 'expand') + '">' + (open ? '&#9660;' : '&#9654;') + '</button>' +
        '</div>' +
        '<div class="rf-card-body"' + (open ? '' : ' hidden') + '>' +
          '<label>Match (zone code glob)</label>' +
          '<input type="text" class="rf-match-full" value="' + esc(p.match || '') + '" placeholder="match (zone code glob, e.g. *_town)">' +
          '<label>Whitelist patterns</label>' +
          '<div class="rf-chip-list rf-wl-full">' + chipListHtml(wl, 'wl') + '</div>' +
          '<label>Blacklist patterns</label>' +
          '<div class="rf-chip-list rf-bl-full">' + chipListHtml(bl, 'bl') + '</div>' +
          '<div class="rf-entity-row">' +
            '<input type="text" class="rf-add-input" placeholder="metadata pattern to add (e.g. Metadata/Monsters/Boss)">' +
            '<button type="button" class="rf-add-wl">+ Whitelist</button>' +
            '<button type="button" class="rf-add-bl">+ Blacklist</button>' +
          '</div>' +
          '<div class="rf-entity-row">' +
            '<select class="rf-entity-select"><option value="">\u2014 Add current-zone entity \u2014</option></select>' +
            '<button type="button" class="rf-add-cur-wl">Add to whitelist</button>' +
            '<button type="button" class="rf-add-cur-bl">Add to blacklist</button>' +
          '</div>' +
          '<div class="rf-card-footer">' +
            '<button type="button" class="rf-save">Save</button>' +
            '<span class="rf-status"></span>' +
          '</div>' +
        '</div>' +
      '</div>';
    }).join('');

    // Wire each card's controls
    host.querySelectorAll('.rf-card').forEach(card => {
      const i = +card.dataset.i;
      const preset = __presetsCache[i];
      if (!preset) return;

      // Accordion toggle: clicking the head (but not its inputs/buttons) expands/collapses
      const head = card.querySelector('.rf-card-head');
      head.addEventListener('click', (ev) => {
        if (ev.target.closest('input,button,select,label')) return;
        preset._open = !preset._open;
        renderPresets();
      });
      card.querySelector('.rf-toggle').addEventListener('click', (ev) => {
        ev.stopPropagation();
        preset._open = !preset._open;
        renderPresets();
      });

      // Sync the two match inputs (header summary + body full) into the preset live;
      // never committed until Save is clicked.
      const matchInput = card.querySelector('.rf-match');
      const matchFull = card.querySelector('.rf-match-full');
      matchInput.addEventListener('input', () => {
        preset.match = matchInput.value;
        if (matchFull && matchFull.value !== matchInput.value) matchFull.value = matchInput.value;
      });
      matchFull.addEventListener('input', () => {
        preset.match = matchFull.value;
        if (matchInput.value !== matchFull.value) matchInput.value = matchFull.value;
      });

      // Chip removal (delegated per-button)
      card.querySelectorAll('.rf-chip-x').forEach(btn => {
        btn.addEventListener('click', (ev) => {
          ev.stopPropagation();
          const pat = btn.getAttribute('data-pat');
          const kind = btn.getAttribute('data-kind');
          if (kind === 'wl') preset.whitelist = (preset.whitelist || []).filter(x => x !== pat);
          else preset.blacklist = (preset.blacklist || []).filter(x => x !== pat);
          renderPresets();
        });
      });

      // Add-pattern input + Whitelist/Blacklist buttons
      const addInput = card.querySelector('.rf-add-input');
      function addPattern(kind) {
        const v = (addInput.value || '').trim();
        if (!v) return;
        const arr = kind === 'wl' ? (preset.whitelist || (preset.whitelist = [])) : (preset.blacklist || (preset.blacklist = []));
        if (!arr.includes(v)) arr.push(v);
        addInput.value = '';
        renderPresets();
      }
      card.querySelector('.rf-add-wl').addEventListener('click', () => addPattern('wl'));
      card.querySelector('.rf-add-bl').addEventListener('click', () => addPattern('bl'));
      addInput.addEventListener('keydown', (ev) => {
        if (ev.key === 'Enter') { ev.preventDefault(); addPattern('wl'); }
      });

      // "Add current-zone entity" helper: populate the dropdown from the cached /entities
      // list (loaded lazily on first tab activation). Picking an entity + clicking Add to...
      // appends its metadata to the corresponding chip list.
      populateEntitySelect(card.querySelector('.rf-entity-select'));
      card.querySelector('.rf-add-cur-wl').addEventListener('click', () => addCurrentEntity('wl', card));
      card.querySelector('.rf-add-cur-bl').addEventListener('click', () => addCurrentEntity('bl', card));

      // Move up / down (reorders __presetsCache; Save commits the new order)
      card.querySelector('.rf-up').addEventListener('click', (ev) => {
        ev.stopPropagation();
        if (i > 0) {
          const t = __presetsCache[i - 1];
          __presetsCache[i - 1] = __presetsCache[i];
          __presetsCache[i] = t;
          renderPresets();
        }
      });
      card.querySelector('.rf-dn').addEventListener('click', (ev) => {
        ev.stopPropagation();
        if (i < __presetsCache.length - 1) {
          const t = __presetsCache[i + 1];
          __presetsCache[i + 1] = __presetsCache[i];
          __presetsCache[i] = t;
          renderPresets();
        }
      });

      // Delete (local only — Save commits the trimmed list)
      card.querySelector('.rf-del').addEventListener('click', (ev) => {
        ev.stopPropagation();
        __presetsCache.splice(i, 1);
        renderPresets();
      });

      // Save: build the full RadarFilterFile from current UI state (preserving order) and POST.
      card.querySelector('.rf-save').addEventListener('click', (ev) => {
        ev.stopPropagation();
        savePresets(card);
      });
    });

    updateCapChip();
  }

  function populateEntitySelect(sel) {
    if (!sel) return;
    if (!__entitiesCache) {
      // Trigger a lazy load; the fetch callback will refresh every select in the DOM.
      loadEntities();
      return;
    }
    const opts = ['<option value="">\u2014 Add current-zone entity \u2014</option>'];
    for (const e of __entitiesCache) {
      const meta = e.metadata || '';
      const label = (e.name || meta || String(e.id ?? '')) + ' \u00b7 ' + meta;
      opts.push('<option value="' + esc(meta) + '">' + esc(label) + '</option>');
    }
    sel.innerHTML = opts.join('');
  }

  async function loadEntities() {
    try {
      const r = await fetch('/entities', { cache: 'no-store' });
      if (!r.ok) throw 0;
      const arr = await r.json();
      __entitiesCache = Array.isArray(arr) ? arr.slice(0, 200) : [];
    } catch (e) {
      __entitiesCache = [];
    }
    // Refresh any selects already in the DOM (cards rendered before the fetch resolved)
    document.querySelectorAll('.rf-entity-select').forEach(populateEntitySelect);
  }

  function addCurrentEntity(kind, cardEl) {
    const sel = cardEl.querySelector('.rf-entity-select');
    if (!sel || !sel.value) {
      setStatus(cardEl, 'Pick an entity first.', 'err');
      return;
    }
    const i = +cardEl.dataset.i;
    const preset = __presetsCache[i];
    if (!preset) return;
    const v = sel.value;
    const arr = kind === 'wl' ? (preset.whitelist || (preset.whitelist = [])) : (preset.blacklist || (preset.blacklist = []));
    if (!arr.includes(v)) arr.push(v);
    sel.value = '';
    renderPresets();
    setStatus(cardEl, 'Added "' + v + '" to ' + (kind === 'wl' ? 'whitelist' : 'blacklist') + ' (Save to commit).', 'ok');
  }

  // Page-load / tab-activate: GET /api/radar-filters -> populate #rfPresetList
  // (or render the supporter hint if !supporter — see renderHintOrList).
  async function loadPresets() {
    const host = document.getElementById('rfPresetList');
    try {
      const r = await fetch('/api/radar-filters', { cache: 'no-store' });
      if (!r.ok) throw 0;
      const data = await r.json();
      const presets = (data && data.presets) ? data.presets : [];
      __presetsCache = presets.map(p => ({
        match: p.match || '',
        whitelist: Array.isArray(p.whitelist) ? p.whitelist.slice() : [],
        blacklist: Array.isArray(p.blacklist) ? p.blacklist.slice() : [],
        _open: false,
      }));
      renderPresets();
    } catch (e) {
      if (host) host.innerHTML = '<div class="rf-error">Failed to load radar filter presets (network error).</div>';
      updateCapChip();
    }
  }

  // Build the full RadarFilterFile from current UI state (preserving order) -> POST.
  // No optimistic UI: always re-fetch after Save. 400/403 responses show an inline
  // status message on the failing card.
  async function savePresets(cardEl) {
    const file = {
      schemaVersion: 1,
      presets: __presetsCache.map(p => ({
        match: p.match || '',
        whitelist: p.whitelist || [],
        blacklist: p.blacklist || [],
      })),
    };
    if (cardEl) setStatus(cardEl, 'Saving\u2026', '');
    try {
      const r = await fetch('/api/radar-filters', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(file),
      });
      if (r.ok) {
        const data = await r.json();
        const presets = (data && data.presets) ? data.presets : [];
        __presetsCache = presets.map(p => ({
          match: p.match || '',
          whitelist: Array.isArray(p.whitelist) ? p.whitelist.slice() : [],
          blacklist: Array.isArray(p.blacklist) ? p.blacklist.slice() : [],
          _open: false,
        }));
        renderPresets();
        if (cardEl) setStatus(cardEl, '\u2713 saved', 'ok');
      } else if (r.status === 400 || r.status === 403) {
        let msg = 'Save rejected (HTTP ' + r.status + ').';
        try { const j = await r.json(); if (j && j.error) msg = j.error; } catch (_) {}
        if (cardEl) setStatus(cardEl, msg, 'err');
      } else {
        if (cardEl) setStatus(cardEl, 'Save failed (HTTP ' + r.status + ').', 'err');
      }
    } catch (e) {
      if (cardEl) setStatus(cardEl, 'Save failed (network error).', 'err');
    }
  }

  // Supporter gate check every render (in case the code changes mid-session):
  // non-supporter -> render the S3 hint into #radarFilterHint and unhide it;
  // supporter -> hide the hint mount and load presets.
  function renderHintOrList() {
    const hintMount = document.getElementById('radarFilterHint');
    if (!isSupporter()) {
      if (hintMount) {
        try { window.__supporterHint.render(hintMount); } catch (e) {}
        hintMount.hidden = false;
      }
      const list = document.getElementById('rfPresetList');
      if (list) list.innerHTML = '';
      return;
    }
    if (hintMount) {
      hintMount.hidden = true;
      hintMount.innerHTML = '';
    }
    loadPresets();
  }

  function init() {
    const newBtn = document.getElementById('btnRfNewPreset');
    if (newBtn) newBtn.addEventListener('click', () => {
      if (__presetsCache.length >= RF_CAP) return;
      __presetsCache.push({ match: '', whitelist: [], blacklist: [], _open: true });
      renderPresets();
    });

    // Tab activation: check supporter gate -> render hint OR load presets.
    // Re-check the gate on every click so a mid-session supporter-code change flips the view.
    // /entities is fetched once on first activation to populate the per-card dropdown.
    let __entitiesLoaded = false;
    document.querySelectorAll('.tab[data-tab="radar-filter"]').forEach(t => t.addEventListener('click', () => {
      renderHintOrList();
      if (!__entitiesLoaded) { __entitiesLoaded = true; loadEntities(); }
    }));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
// RADAR-FILTER-JS-END

/* v0.41 B3 Zone-Aware Layouts auto-swap: polls /api/state every 2 seconds,
   on zone change applies the matching overlay-layout preset (supporter-gated). */
(() => {
  let _lastAppliedZone = null;
  let _layoutsCache = null;
  let _pollTimer = null;

  async function refreshLayouts() {
    try {
      const r = await fetch('/api/overlay-layouts', { cache: 'no-store' });
      if (!r.ok) return;
      _layoutsCache = await r.json();
    } catch (e) { /* silent */ }
  }

  function matchZone(pattern, zoneCode) {
    if (!pattern || !zoneCode) return false;
    const escaped = pattern
      .replace(/\*/g, '\x00STAR\x00')
      .replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
      .replace(/\x00STAR\x00/g, '.*');
    return new RegExp('^' + escaped + '$', 'i').test(zoneCode);
  }

  function applyLayoutForZone(zoneCode) {
    try {
      if (!window.__supporterGate || !window.__supporterGate.isSupporter()) return;
      if (!_layoutsCache || !_layoutsCache.presets || !_layoutsCache.presets.length) return;
      const preset = _layoutsCache.presets.find(p => matchZone(p.match, zoneCode));
      if (!preset || !preset.panels) {
        // No preset matches current zone: reset all panels to CSS default
        const allPanels = window.__panelInventory && window.__panelInventory.list();
        if (allPanels) {
          allPanels.forEach(slug => {
            const el = window.__panelInventory.get(slug);
            if (el) {
              el.style.display = '';
              el.style.transform = '';
            }
          });
        }
        return;
      }
      for (const [slug, panelState] of Object.entries(preset.panels)) {
        const el = window.__panelInventory.get(slug);
        if (!el) continue;
        if (panelState.Visible === false) {
          el.style.display = 'none';
        } else if (panelState.Visible === true) {
          el.style.display = '';
        }
        if (panelState.X != null || panelState.Y != null) {
          const x = panelState.X != null ? panelState.X : 0;
          const y = panelState.Y != null ? panelState.Y : 0;
          el.style.transform = 'translate(' + x + 'px, ' + y + 'px)';
        }
      }
    } catch (e) { /* silent */ }
  }

  async function pollZoneAndApply() {
    try {
      const r = await fetch('/api/state', { cache: 'no-store' });
      if (!r.ok) return;
      const s = await r.json();
      const areaCode = s.areaCode || '';
      if (areaCode !== _lastAppliedZone) {
        _lastAppliedZone = areaCode;
        applyLayoutForZone(areaCode);
      }
    } catch (e) { /* silent */ }
  }

  // Startup: ensure supporter gate is fresh, fetch layouts, run initial zone check, then poll.
  (async () => {
    if (window.__supporterGate) {
      try { await window.__supporterGate.refresh(); } catch (e) {}
    }
    await refreshLayouts();
    await pollZoneAndApply();
    _pollTimer = setInterval(pollZoneAndApply, 2000);
  })();

  window.__reloadOverlayLayouts = refreshLayouts;
})();

/* ── v0.41 B4 Overlay Layouts dashboard tab: zone-aware panel visibility/position presets.
     Consumes /api/overlay-layouts (B_API) CRUD; supporter-gated via S3 __supporterHint.
     The existing #overlayLayoutHint div (declared in the global hint area above the views)
     is the mount for the supporter hint: tab activation renders the hint there for
     non-supporters, or loads + renders the layout list for supporters. Capture-current
     snapshots live panel visibility via window.__panelInventory (B2). Save rebuilds the
     full OverlayLayoutFile from every card and POSTs, then calls window.__reloadOverlayLayouts()
     (B3) so the live auto-swap picks up the new presets. PRESERVE ALL OTHER BEHAVIOR —
     additive IIFE; no existing tab/IIFE is touched. */
// OVERLAY-LAYOUTS-JS-START
(() => {
  const LO_CAP = 10;
  const LO_WARN = 8;

  // Editor state: array of {name, match, panels:{slug:{visible,x,y}}, _open}
  let __layoutsCache = [];

  function esc(s){ return String(s ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;' }[c])); }

  function isSupporter() {
    try { return !!(window.__supporterGate && window.__supporterGate.isSupporter()); }
    catch (e) { return false; }
  }

  function setStatus(cardEl, msg, cls) {
    const el = cardEl?.querySelector('.lo-status');
    if (!el) return;
    el.textContent = msg || '';
    el.className = 'lo-status' + (cls ? ' ' + cls : '');
  }

  // Normalize a wire panel-state into {visible,x,y}; accepts both camelCase (the
  // actual wire format from the camelCase server serializer) and PascalCase
  // (defensive — matches the B3 applyLayoutForZone access pattern).
  function normPanel(p) {
    if (!p) return { visible: null, x: null, y: null };
    return {
      visible: p.visible != null ? !!p.visible : (p.Visible != null ? !!p.Visible : null),
      x: p.x != null ? p.x : (p.X != null ? p.X : null),
      y: p.y != null ? p.y : (p.Y != null ? p.Y : null),
    };
  }

  function updateCapChip() {
    const chip = document.getElementById('loCapChip');
    if (!chip) return;
    const n = __layoutsCache.length;
    chip.textContent = 'layouts: ' + n + ' / ' + LO_CAP;
    chip.classList.remove('warn', 'full');
    if (n >= LO_CAP) chip.classList.add('full');
    else if (n >= LO_WARN) chip.classList.add('warn');
    const btn = document.getElementById('btnLoNewLayout');
    if (btn) btn.disabled = n >= LO_CAP;
  }

  function countOverridden(preset) {
    return preset && preset.panels ? Object.keys(preset.panels).length : 0;
  }

  function availablePanelSlugs(preset) {
    const all = (window.__panelInventory && typeof window.__panelInventory.list === 'function')
      ? window.__panelInventory.list() : [];
    const used = preset && preset.panels ? Object.keys(preset.panels) : [];
    return all.filter(s => !used.includes(s));
  }

  function renderLayouts() {
    const host = document.getElementById('loList');
    if (!host) return;
    if (!__layoutsCache.length) {
      host.innerHTML = '<div class="lo-empty">No overlay layout presets yet. Click <b>+ New layout</b> or <b>Capture current</b> to snapshot panel visibility by zone.</div>';
      updateCapChip();
      return;
    }
    host.innerHTML = __layoutsCache.map((p, i) => {
      const open = !!p._open;
      const overridden = countOverridden(p);
      const panelSlugs = Object.keys(p.panels || {});
      const rows = panelSlugs.map(slug => {
        const st = normPanel(p.panels[slug]);
        return '<div class="lo-panel-row" data-slug="' + esc(slug) + '">' +
          '<span class="lo-panel-key" title="' + esc(slug) + '">' + esc(slug) + '</span>' +
          '<label class="lo-vis"><input type="checkbox" class="lo-visible"' + (st.visible === true ? ' checked' : '') + '>visible</label>' +
          '<input type="number" class="lo-x" value="' + esc(st.x != null ? st.x : '') + '" placeholder="x">' +
          '<input type="number" class="lo-y" value="' + esc(st.y != null ? st.y : '') + '" placeholder="y">' +
          '<button type="button" class="lo-panel-del" title="remove panel">&times;</button>' +
        '</div>';
      }).join('');
      const addOpts = ['<option value="">\u2014 add panel \u2014</option>']
        .concat(availablePanelSlugs(p).map(s => '<option value="' + esc(s) + '">' + esc(s) + '</option>'));
      return '<div class="lo-card" data-i="' + i + '">' +
        '<div class="lo-card-head">' +
          '<input type="text" class="lo-name" value="' + esc(p.name || '') + '" placeholder="layout name">' +
          '<input type="text" class="lo-match" value="' + esc(p.match || '') + '" placeholder="match (zone code glob, e.g. *_town)">' +
          '<span class="lo-summary">' + overridden + ' panel' + (overridden === 1 ? '' : 's') + ' overridden</span>' +
          '<button type="button" class="lo-del" title="delete">&times;</button>' +
          '<button type="button" class="lo-toggle" title="' + (open ? 'collapse' : 'expand') + '">' + (open ? '&#9660;' : '&#9654;') + '</button>' +
        '</div>' +
        '<div class="lo-card-body"' + (open ? '' : ' hidden') + '>' +
          '<label>Panel overrides</label>' +
          (panelSlugs.length ? rows : '<span class="hint-row" style="opacity:.5">no panel overrides — add one below</span>') +
          '<div class="lo-add-row">' +
            '<select class="lo-add-select">' + addOpts.join('') + '</select>' +
            '<button type="button" class="lo-add-panel">+ Add panel</button>' +
          '</div>' +
          '<div class="lo-card-footer">' +
            '<button type="button" class="lo-save">Save</button>' +
            '<span class="lo-status"></span>' +
          '</div>' +
        '</div>' +
      '</div>';
    }).join('');

    // Wire each card's controls
    host.querySelectorAll('.lo-card').forEach(card => {
      const i = +card.dataset.i;
      const preset = __layoutsCache[i];
      if (!preset) return;

      // Accordion toggle: clicking the head (but not its inputs/buttons) expands/collapses
      const head = card.querySelector('.lo-card-head');
      head.addEventListener('click', (ev) => {
        if (ev.target.closest('input,button,select,label')) return;
        preset._open = !preset._open;
        renderLayouts();
      });
      card.querySelector('.lo-toggle').addEventListener('click', (ev) => {
        ev.stopPropagation();
        preset._open = !preset._open;
        renderLayouts();
      });

      // Name + match inputs commit live into the preset (Save persists).
      const nameInput = card.querySelector('.lo-name');
      const matchInput = card.querySelector('.lo-match');
      nameInput.addEventListener('input', () => { preset.name = nameInput.value; });
      matchInput.addEventListener('input', () => { preset.match = matchInput.value; });

      // Per-row: visible checkbox + x/y inputs + remove-panel button
      card.querySelectorAll('.lo-panel-row').forEach(row => {
        const slug = row.getAttribute('data-slug');
        const st = preset.panels[slug] || (preset.panels[slug] = { visible: null, x: null, y: null });
        const visBox = row.querySelector('.lo-visible');
        const xIn = row.querySelector('.lo-x');
        const yIn = row.querySelector('.lo-y');
        visBox.addEventListener('change', () => { st.visible = visBox.checked; });
        xIn.addEventListener('input', () => { st.x = xIn.value === '' ? null : Number(xIn.value); });
        yIn.addEventListener('input', () => { st.y = yIn.value === '' ? null : Number(yIn.value); });
        row.querySelector('.lo-panel-del').addEventListener('click', (ev) => {
          ev.stopPropagation();
          delete preset.panels[slug];
          renderLayouts();
        });
      });

      // "+ Add panel" — pulls from window.__panelInventory.list() (excluding already-added)
      const addSel = card.querySelector('.lo-add-select');
      card.querySelector('.lo-add-panel').addEventListener('click', (ev) => {
        ev.stopPropagation();
        const slug = addSel.value;
        if (!slug) { setStatus(card, 'Pick a panel to add first.', 'err'); return; }
        if (!preset.panels) preset.panels = {};
        if (!preset.panels[slug]) preset.panels[slug] = { visible: null, x: null, y: null };
        preset._open = true;
        renderLayouts();
        setStatus(card, 'Added "' + slug + '" (Save to commit).', 'ok');
      });

      // Delete (local only — Save commits the trimmed list)
      card.querySelector('.lo-del').addEventListener('click', (ev) => {
        ev.stopPropagation();
        __layoutsCache.splice(i, 1);
        renderLayouts();
      });

      // Save: build the full OverlayLayoutFile from current UI state (all cards) and POST.
      card.querySelector('.lo-save').addEventListener('click', (ev) => {
        ev.stopPropagation();
        saveAllLayouts(card);
      });
    });

    updateCapChip();
  }

  // Page-load / tab-activate: GET /api/overlay-layouts -> populate #loList
  // (or render the supporter hint if !supporter — see renderHintOrList).
  async function loadLayouts() {
    const host = document.getElementById('loList');
    try {
      const r = await fetch('/api/overlay-layouts', { cache: 'no-store' });
      if (!r.ok) throw 0;
      const data = await r.json();
      const presets = (data && data.presets) ? data.presets : [];
      __layoutsCache = presets.map(p => {
        const panels = {};
        if (p && p.panels) {
          for (const [slug, st] of Object.entries(p.panels)) {
            panels[slug] = normPanel(st);
          }
        }
        return {
          name: p.name || '',
          match: p.match || '',
          panels: panels,
          _open: false,
        };
      });
      renderLayouts();
    } catch (e) {
      if (host) host.innerHTML = '<div class="lo-error">Failed to load overlay layouts (network error).</div>';
      updateCapChip();
    }
  }

  // Build the full OverlayLayoutFile from current UI state (all cards, preserving order) -> POST.
  // camelCase keys match the server's camelCase serializer so the POST round-trips correctly.
  // On success calls window.__reloadOverlayLayouts() (B3) so the live auto-swap re-fetches.
  async function saveAllLayouts(cardEl) {
    const file = {
      schemaVersion: 1,
      presets: __layoutsCache.map(p => {
        const panels = {};
        for (const [slug, st] of Object.entries(p.panels || {})) {
          panels[slug] = {
            visible: st.visible == null ? null : !!st.visible,
            x: st.x == null ? null : st.x,
            y: st.y == null ? null : st.y,
          };
        }
        return { name: p.name || '', match: p.match || '', panels: panels };
      }),
    };
    if (cardEl) setStatus(cardEl, 'Saving\u2026', '');
    try {
      const r = await fetch('/api/overlay-layouts', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(file),
      });
      if (r.ok) {
        const data = await r.json();
        const presets = (data && data.presets) ? data.presets : [];
        __layoutsCache = presets.map(p => {
          const panels = {};
          if (p && p.panels) {
            for (const [slug, st] of Object.entries(p.panels)) {
              panels[slug] = normPanel(st);
            }
          }
          return {
            name: p.name || '',
            match: p.match || '',
            panels: panels,
            _open: false,
          };
        });
        renderLayouts();
        if (cardEl) setStatus(cardEl, '\u2713 saved', 'ok');
        // Tell the B3 auto-swap to re-fetch the updated presets.
        try { if (typeof window.__reloadOverlayLayouts === 'function') window.__reloadOverlayLayouts(); } catch (e) {}
      } else if (r.status === 400 || r.status === 403) {
        let msg = 'Save rejected (HTTP ' + r.status + ').';
        try { const j = await r.json(); if (j && j.error) msg = j.error; } catch (_) {}
        if (cardEl) setStatus(cardEl, msg, 'err');
      } else {
        if (cardEl) setStatus(cardEl, 'Save failed (HTTP ' + r.status + ').', 'err');
      }
    } catch (e) {
      if (cardEl) setStatus(cardEl, 'Save failed (network error).', 'err');
    }
  }

  // Capture-current: iterate window.__panelInventory.list() and read each panel's
  // live visibility (getComputedStyle display==='none' -> visible=false, else true).
  // Populates a fresh draft preset (no X/Y — visibility only); user names + saves.
  function captureCurrent() {
    if (!isSupporter()) { renderHintOrList(); return; }
    if (__layoutsCache.length >= LO_CAP) return;
    const panels = {};
    try {
      const slugs = (window.__panelInventory && typeof window.__panelInventory.list === 'function')
        ? window.__panelInventory.list() : [];
      for (const slug of slugs) {
        const el = window.__panelInventory.get(slug);
        let visible = null;
        if (el) {
          try { visible = window.getComputedStyle(el).display !== 'none'; } catch (e) { visible = null; }
        }
        panels[slug] = { visible: visible, x: null, y: null };
      }
    } catch (e) { /* non-fatal — capture what we can */ }
    __layoutsCache.push({
      name: '',
      match: '',
      panels: panels,
      _open: true,
    });
    renderLayouts();
  }

  // Supporter gate check every render (in case the code changes mid-session):
  // non-supporter -> render the S3 hint into #overlayLayoutHint and unhide it;
  // supporter -> hide the hint mount and load layouts.
  function renderHintOrList() {
    const hintMount = document.getElementById('overlayLayoutHint');
    if (!isSupporter()) {
      if (hintMount) {
        try { window.__supporterHint.render(hintMount); } catch (e) {}
        hintMount.hidden = false;
      }
      const list = document.getElementById('loList');
      if (list) list.innerHTML = '';
      return;
    }
    if (hintMount) {
      hintMount.hidden = true;
      hintMount.innerHTML = '';
    }
    loadLayouts();
  }

  function init() {
    const newBtn = document.getElementById('btnLoNewLayout');
    if (newBtn) newBtn.addEventListener('click', () => {
      if (!isSupporter()) { renderHintOrList(); return; }
      if (__layoutsCache.length >= LO_CAP) return;
      __layoutsCache.push({ name: '', match: '', panels: {}, _open: true });
      renderLayouts();
    });

    const captureBtn = document.getElementById('btnLoCapture');
    if (captureBtn) captureBtn.addEventListener('click', captureCurrent);

    // Tab activation: check supporter gate -> render hint OR load layouts.
    // Re-check the gate on every click so a mid-session supporter-code change flips the view.
    document.querySelectorAll('.tab[data-tab="layouts"]').forEach(t => t.addEventListener('click', () => {
      renderHintOrList();
    }));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
// OVERLAY-LAYOUTS-JS-END

// v0.41 C4 Nav Destinations dashboard tab IIFE — saved position bookmarks
const NavDestinations = (() => {
  const NAV_CAP = 50;
  let __navCache = [];

  async function loadDestinations() {
    try {
      const r = await fetch('/api/nav-destinations', { cache: 'no-store' });
      if (!r.ok) { __navCache = []; renderDestinations(); return; }
      const j = await r.json();
      __navCache = (j && j.destinations) || [];
      renderDestinations();
    } catch (e) {
      __navCache = [];
      renderDestinations();
    }
  }

  async function saveDestination(dest) {
    try {
      const r = await fetch('/api/nav-destinations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(dest)
      });
      if (r.ok) {
        await loadDestinations();
      }
    } catch (e) {}
  }

  async function deleteDestination(id) {
    try {
      const r = await fetch('/api/nav-destinations/' + encodeURIComponent(id), { method: 'DELETE' });
      if (r.ok) {
        await loadDestinations();
      }
    } catch (e) {}
  }

  async function captureCurrentPosition() {
    try {
      const stateResp = await fetch('/api/state', { cache: 'no-store' });
      if (!stateResp.ok) return;
      const stateData = await stateResp.json();
      const areaCode = stateData.areaCode || '';
      const playerGrid = stateData.playerGrid || { x: 0, y: 0 };
      if (!areaCode) return;
      const newDest = {
        zoneCode: areaCode,
        name: 'New destination',
        x: playerGrid.x || 0,
        y: playerGrid.y || 0
      };
      __navCache.push(newDest);
      renderDestinations();
    } catch (e) {}
  }

  function renderDestinations() {
    const list = document.getElementById('navList');
    if (!list) return;
    const chip = document.getElementById('navCapChip');
    if (chip) {
      chip.textContent = 'destinations: ' + __navCache.length + ' / ' + NAV_CAP;
      chip.classList.toggle('warn', __navCache.length >= NAV_CAP * 0.8);
      chip.classList.toggle('full', __navCache.length >= NAV_CAP);
    }
    if (__navCache.length === 0) {
      list.innerHTML = '<div class="nav-empty">No destinations saved. Use "Capture current position" or add manually.</div>';
      return;
    }
    list.innerHTML = __navCache.map((d, i) =>
      '<div class="nav-row" data-i="' + i + '">' +
      '<input class="nav-cell nav-zone" data-field="zoneCode" value="' + esc(d.zoneCode || '') + '" placeholder="zone">' +
      '<input class="nav-cell nav-name" data-field="name" value="' + esc(d.name || '') + '" placeholder="name">' +
      '<input class="nav-cell nav-x" type="number" data-field="x" value="' + (d.x || 0) + '" placeholder="x">' +
      '<input class="nav-cell nav-y" type="number" data-field="y" value="' + (d.y || 0) + '" placeholder="y">' +
      '<button class="nav-del" data-del="' + i + '">✕</button>' +
      '</div>'
    ).join('');
    $$('#navList .nav-row').forEach(row => {
      const i = +row.dataset.i;
      row.querySelectorAll('[data-field]').forEach(fld => {
        fld.onchange = () => {
          const field = fld.dataset.field;
          if (field === 'x' || field === 'y') __navCache[i][field] = parseFloat(fld.value) || 0;
          else __navCache[i][field] = fld.value;
          saveDestination(__navCache[i]);
        };
      });
      row.querySelector('[data-del]').onclick = () => {
        __navCache.splice(i, 1);
        renderDestinations();
      };
    });
  }

  function renderHintOrList() {
    const hintMount = document.getElementById('navDestinationHint');
    if (!window.__supporterGate || !window.__supporterGate.isSupporter()) {
      if (hintMount) {
        try { window.__supporterHint.render(hintMount); } catch (e) {}
        hintMount.hidden = false;
      }
      const list = document.getElementById('navList');
      if (list) list.innerHTML = '';
      return;
    }
    if (hintMount) {
      hintMount.hidden = true;
      hintMount.innerHTML = '';
    }
    loadDestinations();
  }

  function init() {
    const newBtn = document.getElementById('btnNavNew');
    if (newBtn) newBtn.addEventListener('click', () => {
      if (!window.__supporterGate || !window.__supporterGate.isSupporter()) { renderHintOrList(); return; }
      if (__navCache.length >= NAV_CAP) return;
      __navCache.push({ zoneCode: '', name: '', x: 0, y: 0 });
      renderDestinations();
    });

    const captureBtn = document.getElementById('btnNavCapture');
    if (captureBtn) captureBtn.addEventListener('click', captureCurrentPosition);

    document.querySelectorAll('.tab[data-tab="nav"]').forEach(t => t.addEventListener('click', () => {
      renderHintOrList();
    }));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();

  return { renderHintOrList, loadDestinations };
})();
// NAV-DESTINATIONS-JS-END

/* v0.41 C3 Nav Destinations overlay chip: floating discoverability strip.
   Reads saved destinations for the current zone and renders clickable chips.
   Supporter-gated — non-supporters see nothing. Polls every 2s via refreshNavDestChips. */
(() => {
  async function refreshNavDestChips() {
    try {
      // Supporter gate: early return + clear if not supporter
      if (!window.__supporterGate || !window.__supporterGate.isSupporter()) {
        const mount = document.getElementById('navDestChips');
        if (mount) mount.innerHTML = '';
        return;
      }
      // Fetch area code from /api/state
      const stateResp = await fetch('/api/state', { cache: 'no-store' });
      if (!stateResp.ok) return;
      const stateData = await stateResp.json();
      const areaCode = stateData.areaCode || '';
      if (!areaCode) return;

      // Fetch destinations for this zone
      const destResp = await fetch('/api/nav-destinations?zone=' + encodeURIComponent(areaCode), { cache: 'no-store' });
      if (!destResp.ok) return;
      const destData = await destResp.json();
      const destinations = (destData && destData.destinations) || [];

      // Render chips into #navDestChips
      const mount = document.getElementById('navDestChips');
      if (!mount) return;
      if (!destinations.length) {
        mount.innerHTML = '';
        return;
      }
      mount.innerHTML = destinations.map(d =>
        '<span class="nav-dest-chip">→ ' + esc(d.name || d) + '</span>'
      ).join('');
    } catch (e) {
      // Silent try/catch — network or parse errors are non-fatal
    }
  }

  // Initial call on load
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', refreshNavDestChips);
  } else {
    refreshNavDestChips();
  }

  // Module-scoped 2s poll interval
  setInterval(refreshNavDestChips, 2000);
})();

/* ── v0.41 D3 Session Widget: floating metric chip strip + dashboard editor.
     Renders selected metric chips into #sessionWidget (positioned fixed overlay).
     Supporter-gated — non-supporters see nothing via the widget render, and the
     dashboard editor tab shows a supporter hint instead. ── */
(() => {
  const CHIP_DEFS = {
    'drops':               { label: 'Drops',       field: ['session','dropCount'] },
    'xp-gained':           { label: 'XP gained',   field: ['session','sessionXpDelta'] },
    'bosses-killed':       { label: 'Bosses',      field: ['session','bossesKilledThisSession'] },
    'deaths':              { label: 'Deaths',      field: ['session','deaths'] },
    'time-in-zone':        { label: 'Time in zone',field: ['session','zoneElapsed'] },
    'avg-map-clear-time':  { label: 'Maps/hr',     field: ['session','mapsPerHour'] },
  };

  function resolveField(obj, path) {
    for (const seg of path) {
      if (obj == null) return null;
      obj = obj[seg];
    }
    return obj;
  }

  function formatChipValue(id, raw) {
    if (raw == null) return '—';
    switch (id) {
      case 'time-in-zone': {
        const s = parseInt(raw, 10);
        if (isNaN(s)) return String(raw);
        const m = Math.floor(s / 60);
        const sec = s % 60;
        return m + 'm ' + sec + 's';
      }
      case 'avg-map-clear-time': {
        const v = parseFloat(raw);
        return isNaN(v) ? '—' : v.toFixed(1);
      }
      default: return String(raw);
    }
  }

  function esc(s) { return String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }

  async function fetchConfig() {
    try {
      const r = await fetch('/api/session-widget', { cache: 'no-store' });
      if (!r.ok) return null;
      return await r.json();
    } catch (e) { return null; }
  }

  async function refreshWidget() {
    try {
      // Supporter gate: early return + hide
      if (!window.__supporterGate || !window.__supporterGate.isSupporter()) {
        const mount = document.getElementById('sessionWidget');
        if (mount) mount.hidden = true;
        return;
      }

      // Fetch config and state
      const [config, stateData] = await Promise.all([
        fetchConfig(),
        fetch('/api/state', { cache: 'no-store' }).then(r => r.ok ? r.json() : null).catch(() => null)
      ]);

      const chips = (config && config.chips) || [];
      const mount = document.getElementById('sessionWidget');
      if (!mount) return;

      if (!chips.length || !stateData) {
        mount.hidden = true;
        return;
      }

      const x = (config && config.x != null) ? config.x : 12;
      const y = (config && config.y != null) ? config.y : 12;
      mount.style.left = x + 'px';
      mount.style.top = y + 'px';

      mount.innerHTML = chips.map(id => {
        const def = CHIP_DEFS[id];
        if (!def) return '';
        const raw = resolveField(stateData, def.field);
        const val = formatChipValue(id, raw);
        return '<span class="sw-chip"><span class="sw-label">' + esc(def.label) + ':</span> <span class="sw-value">' + esc(val) + '</span></span>';
      }).join('');

      mount.hidden = false;
    } catch (e) {
      const mount = document.getElementById('sessionWidget');
      if (mount) mount.hidden = true;
    }
  }

  async function loadWidgetEditor() {
    const config = await fetchConfig();
    const chips = (config && config.chips) || [];
    document.querySelectorAll('[data-widget-chip]').forEach(cb => {
      cb.checked = chips.indexOf(cb.getAttribute('data-widget-chip')) >= 0;
    });
    const xInp = document.getElementById('widgetX');
    const yInp = document.getElementById('widgetY');
    if (xInp) xInp.value = (config && config.x != null) ? config.x : 12;
    if (yInp) yInp.value = (config && config.y != null) ? config.y : 12;
  }

  async function saveWidgetEditor() {
    const chips = [];
    document.querySelectorAll('[data-widget-chip]:checked').forEach(cb => {
      chips.push(cb.getAttribute('data-widget-chip'));
    });
    const x = parseInt(document.getElementById('widgetX')?.value, 10) || 0;
    const y = parseInt(document.getElementById('widgetY')?.value, 10) || 0;
    const body = { chips, x, y };
    try {
      const r = await fetch('/api/session-widget', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      const status = document.getElementById('widgetStatus');
      if (r.ok) {
        if (status) { status.textContent = '✓ Saved'; status.className = 'widget-status ok'; }
        refreshWidget();
      } else {
        if (status) { status.textContent = 'Save failed'; status.className = 'widget-status err'; }
      }
    } catch (e) {
      const status = document.getElementById('widgetStatus');
      if (status) { status.textContent = 'Save failed (network)'; status.className = 'widget-status err'; }
    }
  }

  function renderHintOrEditor() {
    const hintMount = document.getElementById('sessionWidgetHint');
    if (!window.__supporterGate || !window.__supporterGate.isSupporter()) {
      if (hintMount) {
        try { window.__supporterHint.render(hintMount); } catch (e) {}
        hintMount.hidden = false;
      }
      return;
    }
    if (hintMount) {
      hintMount.hidden = true;
      hintMount.innerHTML = '';
    }
    loadWidgetEditor();
  }

  // ── wire-up ──
  function init() {
    const saveBtn = document.getElementById('btnWidgetSave');
    if (saveBtn) saveBtn.addEventListener('click', saveWidgetEditor);

    document.querySelectorAll('.tab[data-tab="widget"]').forEach(t => t.addEventListener('click', () => {
      renderHintOrEditor();
    }));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();

  // 2s poll for widget refresh
  setInterval(refreshWidget, 2000);
})();
