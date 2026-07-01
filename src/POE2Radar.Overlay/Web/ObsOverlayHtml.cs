namespace POE2Radar.Overlay.Web;
internal static class ObsOverlayHtml
{
    public const string Page = """
<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>POE2GPS OBS</title>
<style>
  html,body{margin:0;background:transparent;color:#fff;font:600 20px/1.35 Consolas,monospace;
    text-shadow:0 1px 2px #000,0 0 4px #000}
  #wrap{position:fixed;padding:10px 14px;display:flex;flex-direction:column;gap:2px}
  .chip{background:rgba(0,0,0,var(--op,.4));border-radius:6px;padding:2px 8px}
  .k{opacity:.7;margin-right:6px}
</style></head><body><div id="wrap"></div>
<script>
  async function j(u){const r=await fetch(u,{cache:'no-store'});if(!r.ok)throw 0;return r.json();}
  let cfg={showSessionTimer:true,showZoneTimer:true,showArea:true,showKills:true,showMapsHr:true};
  async function loadCfg(){try{const s=await j('/api/settings');cfg=s.obsOverlay||cfg;applyStyle(cfg);}catch(e){}}
  function applyStyle(c){const w=document.getElementById('wrap');
    w.style.setProperty('--op',(c.panelOpacity??40)/100);
    w.style.color=c.textColor||'#fff'; w.style.transform='scale('+(c.scale||1)+')';
    w.style.transformOrigin=(c.corner||'top-left').includes('right')?'top right':'top left';
    const right=(c.corner||'').includes('right'), bottom=(c.corner||'').includes('bottom');
    w.style.left=right?'auto':'0'; w.style.right=right?'0':'auto';
    w.style.top=bottom?'auto':'0'; w.style.bottom=bottom?'0':'auto';
    w.style.alignItems=right?'flex-end':'flex-start';}
  function row(label,val){return '<div class="chip"><span class="k">'+label+'</span>'+val+'</div>';}
  async function tick(){
    try{const s=await j('/state');const se=s.session||{};const out=[];
      if(cfg.showSessionTimer&&se.sessionElapsed)out.push(row('SESS',se.sessionElapsed));
      if(cfg.showZoneTimer&&se.zoneElapsed)out.push(row('ZONE',se.zoneElapsed));
      if(cfg.showArea)out.push(row('AREA',(s.areaName||s.areaCode||'—')+' · '+(s.areaLevel||0)));
      if(cfg.showKills)out.push(row('KILLS','N'+(se.killsNormal||0)+' M'+(se.killsMagic||0)+' R'+(se.killsRare||0)+' U'+(se.killsUnique||0)));
      if(cfg.showMapsHr)out.push(row('MAPS/HR',(se.mapsPerHour||0).toFixed(1)));
      if(cfg.showXpEff&&se.xpEfficiency!==undefined)out.push(row('XP',(se.xpEfficiency>0?'+':'')+se.xpEfficiency));
      if(cfg.showObjective&&s.campaignGps)out.push(row('NEXT',s.campaignGps));
      document.getElementById('wrap').innerHTML=out.join('');
    }catch(e){}
  }
  loadCfg(); setInterval(loadCfg,5000); tick(); setInterval(tick,1000);
</script></body></html>
""";
}
