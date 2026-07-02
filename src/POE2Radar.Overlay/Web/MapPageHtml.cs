namespace POE2Radar.Overlay.Web;

internal static class MapPageHtml
{
    public const string Page = """
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>POE2GPS Minimap</title>
<style>
  html,body{margin:0;height:100%;background:#0a0a0c;overflow:hidden}
  #c{display:block;width:100vw;height:100vh}
  #hud{position:fixed;left:8px;top:6px;color:#8a8a90;font:12px Consolas,monospace;text-shadow:0 1px 2px #000;pointer-events:none}
  #z{position:fixed;right:8px;top:6px;display:flex;gap:6px}
  #z button{font:14px monospace;background:rgba(0,0,0,.5);color:#ccb96a;border:1px solid #554;border-radius:4px;width:28px;height:26px;cursor:pointer}
</style></head><body>
<canvas id="c"></canvas>
<div id="hud">connecting…</div>
<div id="z"><button id="vt" title="Isometric / Top-down">◇</button><button id="zo">&minus;</button><button id="zi">+</button></div>
<script>
  const cv=document.getElementById('c'),ctx=cv.getContext('2d'),hud=document.getElementById('hud');
  const TAU=Math.PI*2;
  const COS=0.780430, SIN=0.625243;                         // cos/sin(38.7°) — mirrors MapProjection.cs
  let mapView=localStorage.getItem('poe2gps.mapView')||'iso'; // 'iso' (default, matches in-game) | 'top'
  let scale=4, terrain=null, tw=0, th=0, thash=null, player={x:0,y:0}, ents=[], areaLabel='—';
  const RC={Normal:'#b9b9c0',Magic:'#6a8bff',Rare:'#ffd52e',Unique:'#ff7a1a'}; // monster rarity palette
  // Grid delta (dx,dy) -> canvas delta. Isometric matches the in-game overlay; top-down keeps the old axis-aligned look.
  function proj(dx,dy){ return mapView==='iso'
      ? { sx: scale*(dx-dy)*COS, sy: scale*(-(dx+dy))*SIN }
      : { sx: dx*scale,          sy: dy*scale }; }
  function fit(){ cv.width=innerWidth; cv.height=innerHeight; }
  addEventListener('resize',fit); fit();
  document.getElementById('zi').onclick=()=>{ scale=Math.min(16,scale+1); draw(); };
  document.getElementById('zo').onclick=()=>{ scale=Math.max(1,scale-1); draw(); };
  document.getElementById('vt').onclick=()=>{ mapView=(mapView==='iso'?'top':'iso'); localStorage.setItem('poe2gps.mapView',mapView); draw(); };
  async function j(u){const r=await fetch(u,{cache:'no-store'});if(!r.ok)throw 0;return r.json();}
  function buildTerrain(b64,w,h){
    const bin=atob(b64), off=document.createElement('canvas'); off.width=w; off.height=h;
    const octx=off.getContext('2d'), img=octx.createImageData(w,h), d=img.data;
    for(let i=0;i<w*h;i++){ const o=i*4;
      if(bin.charCodeAt(i)!==0){ d[o]=34; d[o+1]=38; d[o+2]=52; d[o+3]=255; } else { d[o+3]=0; } }
    octx.putImageData(img,0,0); return off;
  }
  async function loadTerrain(){
    try{ const m=await j('/api/map'); if(!m.ready){ terrain=null; thash=null; return; }
      if(m.areaHash!==thash){ terrain=buildTerrain(m.walkable,m.width,m.height); tw=m.width; th=m.height; thash=m.areaHash; }
    }catch(e){}
  }
  async function tick(){
    try{
      const s=await j('/state'); if(s.player) player=s.player;
      if(s.areaHash!==thash) await loadTerrain();
      ents=await j('/entities?limit=600').catch(()=>[]);
      areaLabel=(s.areaName||s.areaCode||'—');
    }catch(e){ hud.textContent='waiting for game…'; }
    draw();
  }
  function draw(){
    ctx.setTransform(1,0,0,1,0,0);                          // reset any prior transform
    ctx.clearRect(0,0,cv.width,cv.height);
    const cx=cv.width/2, cy=cv.height/2;
    if(terrain){
      ctx.imageSmoothingEnabled=false;
      if(mapView==='iso'){
        // Warp the grid-space bitmap into isometric screen-space (same affine as the in-game overlay).
        const p00=proj(-player.x,-player.y);
        ctx.save();
        ctx.setTransform(COS*scale, -SIN*scale, -COS*scale, -SIN*scale, cx+p00.sx, cy+p00.sy);
        ctx.drawImage(terrain, 0, 0, tw, th);
        ctx.restore();
      } else {
        ctx.drawImage(terrain, cx-player.x*scale, cy-player.y*scale, tw*scale, th*scale);
      }
    }
    for(const e of ents){
      if(e.hpMax>0 && e.hpCur<=0) continue;                 // skip corpses
      const d=proj(e.x-player.x, e.y-player.y);
      const x=cx+d.sx, y=cy+d.sy;
      if(x<-4||y<-4||x>cv.width+4||y>cv.height+4) continue;  // off-canvas cull
      let col='#8a8a90';
      if(e.poi) col='#e0b341';
      else if(e.hpMax>0) col=RC[e.rarity]||'#cc5555';         // has health = monster
      else if(e.friendly) col='#55aadd';
      ctx.fillStyle=col; ctx.beginPath();
      ctx.arc(x,y, e.rarity==='Unique'?4:e.rarity==='Rare'?3:2.4, 0, TAU); ctx.fill();
    }
    ctx.fillStyle='#39d353'; ctx.beginPath(); ctx.arc(cx,cy,4,0,TAU); ctx.fill();
    ctx.strokeStyle='#0a0'; ctx.lineWidth=1.5; ctx.stroke();
    hud.textContent=areaLabel+'  ·  '+ents.length+'  dots  ·  '+(mapView==='iso'?'iso':'top')+'  ·  z'+scale;
  }
  tick(); setInterval(tick,1000);
</script></body></html>
""";
}
