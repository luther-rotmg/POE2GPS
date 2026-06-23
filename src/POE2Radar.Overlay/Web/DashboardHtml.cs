namespace POE2Radar.Overlay.Web;

/// <summary>
/// Self-contained web dashboard served at <c>GET /</c> by <see cref="ApiServer"/>. One inlined
/// HTML/CSS/JS document — no external assets beyond Google Fonts. The Console tab reads/writes
/// radar/visual settings via <c>/api/settings</c> (the only writes it makes — flags + calibration,
/// never flask/automation); the Filters tab manages watched/hidden lists via <c>/api/watched</c> /
/// <c>/api/hidden</c>; the Dashboard tab polls the same-origin read endpoints (<c>/state</c>,
/// <c>/entities</c>, <c>/landmarks</c>, <c>/api/nav</c>).
/// </summary>
internal static class DashboardHtml
{
    public const string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Overlay</title>
<!-- Self-contained: no external fonts/CDNs. Falls back to local system serif/mono fonts. -->
<style>
  :root{
    --bg:#0a0907; --bg2:#100d09; --panel:#15110b; --panel2:#1b1610;
    --line:#3a2f1d; --line-soft:#271f14;
    --ink:#e8dcc2; --ink-dim:#9c8e72; --ink-faint:#6b5f49;
    --gold:#c8a049; --gold-bright:#ecca7e; --gold-deep:#8a6d34;
    --blood:#9c342a; --blood-bright:#d6584a;
    --rare:#f1e36b; --magic:#7f93ff; --unique:#d2641e; --normal:#cdc6b4;
    --good:#79b06a; --poi:#4bb3c4;
    --shadow:0 18px 40px -20px rgba(0,0,0,.9);
  }
  *{box-sizing:border-box}
  html,body{height:100%}
  body{
    margin:0; background:
      radial-gradient(120% 90% at 50% -10%, #1a150d 0%, var(--bg) 55%) fixed,
      var(--bg);
    color:var(--ink);
    font-family:"IBM Plex Mono","Consolas",ui-monospace,monospace;
    font-size:13px; line-height:1.5;
    -webkit-font-smoothing:antialiased;
    overflow:hidden;
  }
  /* grain + vignette atmosphere */
  body::before{
    content:""; position:fixed; inset:0; pointer-events:none; z-index:999;
    background:radial-gradient(120% 120% at 50% 40%, transparent 58%, rgba(0,0,0,.55) 100%);
    mix-blend-mode:multiply;
  }
  body::after{
    content:""; position:fixed; inset:0; pointer-events:none; z-index:998; opacity:.045;
    background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='160'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='.9' numOctaves='2'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E");
  }

  .shell{display:grid; grid-template-rows:auto 1fr; height:100vh}

  /* ── masthead ── */
  header{
    display:flex; align-items:center; gap:20px; padding:14px 26px;
    border-bottom:1px solid var(--line);
    background:linear-gradient(180deg, rgba(30,24,14,.6), transparent);
  }
  .mark{display:flex; align-items:baseline; gap:12px}
  .mark h1{
    font-family:"Cinzel","Georgia",serif; font-weight:700; font-size:22px; margin:0;
    letter-spacing:.14em; color:var(--gold-bright);
    text-shadow:0 1px 0 #000, 0 0 22px rgba(200,160,73,.25);
  }
  .mark .sub{font-size:10px; letter-spacing:.42em; color:var(--ink-faint); text-transform:uppercase}
  .hgap{flex:1}
  .conn{display:flex; align-items:center; gap:9px; font-size:11px; letter-spacing:.1em; color:var(--ink-dim); text-transform:uppercase}
  .dot{width:9px; height:9px; border-radius:50%; background:var(--blood); box-shadow:0 0 0 0 rgba(214,88,74,.5); }
  .conn.live .dot{background:var(--good); animation:pulse 2.2s infinite}
  @keyframes pulse{0%{box-shadow:0 0 0 0 rgba(121,176,106,.5)}70%{box-shadow:0 0 0 7px rgba(121,176,106,0)}100%{box-shadow:0 0 0 0 rgba(121,176,106,0)}}
  .area-chip{
    font-family:"Cinzel","Georgia",serif; letter-spacing:.08em; color:var(--ink);
    border:1px solid var(--line); padding:5px 14px; border-radius:2px;
    background:var(--panel); font-size:13px;
  }
  .area-chip b{color:var(--gold-bright); font-weight:600}

  /* ── body grid ── */
  .body{display:grid; grid-template-columns:300px 1fr; gap:0; min-height:0}
  aside{
    border-right:1px solid var(--line); padding:22px 22px 0;
    overflow-y:auto; background:linear-gradient(180deg, rgba(20,16,10,.5), transparent 220px);
  }
  main{display:grid; grid-template-rows:auto 1fr; min-height:0; min-width:0}

  /* ── vitals ── */
  .vital{margin-bottom:18px}
  .vital .vlabel{display:flex; justify-content:space-between; font-size:10px; letter-spacing:.18em; text-transform:uppercase; color:var(--ink-dim); margin-bottom:6px}
  .vital .vlabel .num{color:var(--ink); font-weight:600}
  .bar{height:9px; border:1px solid var(--line); background:#0c0a07; border-radius:1px; overflow:hidden; position:relative}
  .bar > i{display:block; height:100%; transition:width .35s ease}
  .bar.hp > i{background:linear-gradient(90deg,#6e1f18,var(--blood-bright))}
  .bar.es > i{background:linear-gradient(90deg,#1f6e63,#33e0c4)}
  .bar.mana > i{background:linear-gradient(90deg,#23306e,var(--magic))}

  .sect{font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.22em; text-transform:uppercase; color:var(--gold); margin:24px 0 12px; display:flex; align-items:center; gap:10px}
  .sect::after{content:""; flex:1; height:1px; background:linear-gradient(90deg,var(--line),transparent)}

  .kv{display:flex; justify-content:space-between; padding:5px 0; border-bottom:1px dotted var(--line-soft); font-size:12px}
  .kv span:first-child{color:var(--ink-faint); letter-spacing:.04em}
  .kv span:last-child{color:var(--ink); font-weight:500}

  .tally{display:grid; grid-template-columns:1fr 1fr; gap:7px; margin-top:4px}
  .tally .t{border:1px solid var(--line-soft); background:var(--panel); padding:9px 10px; border-radius:2px}
  .tally .t .n{font-size:20px; font-weight:600; color:var(--gold-bright); font-family:"Cinzel","Georgia",serif; line-height:1}
  .tally .t .l{font-size:9px; letter-spacing:.16em; text-transform:uppercase; color:var(--ink-faint); margin-top:4px}

  /* ── zone leveling notes ── */
  .znotes{margin-top:12px; padding:11px 13px; border:1px solid var(--line-soft); border-left:2px solid var(--gold-deep); border-radius:2px; background:var(--panel); white-space:pre-wrap; font-size:11px; line-height:1.5; color:var(--ink-dim); max-height:240px; overflow:auto}
  .znotes .zt{font-family:"Cinzel","Georgia",serif; font-size:11px; letter-spacing:.1em; color:var(--gold-bright); margin-bottom:6px; white-space:normal}

  /* ── tabs ── */
  .tabs{display:flex; gap:2px; padding:14px 26px 0; border-bottom:1px solid var(--line)}
  .tab{
    font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.16em; text-transform:uppercase;
    color:var(--ink-faint); background:transparent; border:1px solid transparent; border-bottom:none;
    padding:9px 20px; cursor:pointer; border-radius:3px 3px 0 0; position:relative; top:1px;
  }
  .tab:hover{color:var(--ink-dim)}
  .tab.on{color:var(--gold-bright); background:var(--panel); border-color:var(--line); }
  .tab.on::after{content:""; position:absolute; left:0; right:0; bottom:-1px; height:2px; background:var(--panel)}

  .view{overflow:auto; padding:22px 26px; min-height:0}
  .view[hidden]{display:none}
  /* ── atlas tab ── */
  .arow{display:grid; grid-template-columns:minmax(200px,2fr) minmax(120px,1.4fr) 120px; gap:10px; align-items:center;
        padding:5px 10px; border-bottom:1px solid var(--line); font-size:13px}
  .arow.ahead{font-weight:600; color:var(--ink-dim); border-bottom:1px solid var(--line); position:sticky; top:0; background:var(--panel)}
  .arow.val{background:rgba(255,168,38,.07)}
  .arow .acode{font-family:ui-monospace,Consolas,monospace; color:var(--ink)}
  .arow.val .acode{color:var(--gold-bright)}
  .arow .aname{color:var(--ink-dim)}
  .arow .aid{display:inline-block; min-width:22px; color:var(--ink-dim); font-family:ui-monospace,Consolas,monospace}
  .rin{color:#6ee787; font-weight:600} .rno{color:var(--ink-dim); opacity:.5}
  .arow.nrow{grid-template-columns:60px minmax(90px,1fr) minmax(200px,2fr) 130px; cursor:pointer}
  .arow.nrow:hover{background:rgba(255,255,255,.04)}
  .arow.nrow.sel{background:rgba(60,220,255,.16); outline:1px solid var(--edge,#3cdcff)}
  .amono{font-family:ui-monospace,Consolas,monospace; color:var(--ink-dim); font-size:12px}
  .ntag{font-size:10px; font-weight:600; padding:0 6px; border-radius:8px; border:1px solid var(--line); margin-right:3px}
  .ntag.tc{color:#ff9f43;border-color:#a35a00} .ntag.tv{color:var(--ink-dim)} .ntag.tu{color:#6ee787;border-color:#2f6b3f}
  .ntag.tk{color:#73a6ff;border-color:#2a4a80} .ntag.ts{color:#c98bff;border-color:#5a3a80}
  .akind{font-size:11px; font-weight:600; padding:1px 8px; border-radius:10px; border:1px solid var(--line); color:var(--ink-dim)}
  .akind.k-boss{color:#ff7300; border-color:#ff7300} .akind.k-unique{color:#ff9f43; border-color:#a35a00}
  .akind.k-tower{color:#73a6ff; border-color:#2a4a80} .akind.k-merchant{color:#c98bff; border-color:#5a3a80}

  /* ── controls ── */
  .controls{display:flex; flex-wrap:wrap; gap:8px; align-items:center; margin-bottom:16px}
  .chip{
    font-size:11px; letter-spacing:.06em; color:var(--ink-dim);
    border:1px solid var(--line-soft); background:var(--panel); padding:6px 12px; border-radius:14px; cursor:pointer;
    transition:all .15s;
  }
  .chip:hover{border-color:var(--gold-deep); color:var(--ink)}
  .chip.on{background:var(--gold-deep); border-color:var(--gold); color:#1a140a; font-weight:600}
  .tier{color:var(--ink-faint)} .tier-top{color:var(--gold); font-weight:600}
  .chips{display:flex; flex-wrap:wrap; gap:6px; margin:4px 0 12px}
  input[type=search]{
    font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07;
    border:1px solid var(--line); border-radius:2px; padding:7px 12px; min-width:200px; flex:1;
  }
  input[type=search]:focus{outline:none; border-color:var(--gold-deep)}
  input[type=search]::placeholder{color:var(--ink-faint)}

  /* ── tables ── */
  table{width:100%; border-collapse:collapse; font-size:12px}
  thead th{
    text-align:left; font-weight:500; font-size:10px; letter-spacing:.14em; text-transform:uppercase;
    color:var(--ink-faint); padding:8px 10px; border-bottom:1px solid var(--line); position:sticky; top:-22px;
    background:var(--bg);
  }
  tbody td{padding:7px 10px; border-bottom:1px solid var(--line-soft); white-space:nowrap}
  tbody tr:hover{background:rgba(200,160,73,.05)}
  .meta{color:var(--ink-faint); font-size:11px; max-width:380px; overflow:hidden; text-overflow:ellipsis}
  .rar-Normal{color:var(--normal)} .rar-Magic{color:var(--magic)} .rar-Rare{color:var(--rare)} .rar-Unique{color:var(--unique)}
  .pill{font-size:9px; letter-spacing:.1em; text-transform:uppercase; padding:2px 7px; border-radius:10px; border:1px solid currentColor}
  .friendly{color:var(--good)} .hostile{color:var(--blood-bright)}
  .num-r{text-align:right; color:var(--ink-dim)}
  .hpbar{width:60px; height:6px; border:1px solid var(--line); border-radius:1px; overflow:hidden; display:inline-block; vertical-align:middle}
  .hpbar > i{display:block; height:100%; background:linear-gradient(90deg,#6e1f18,var(--blood-bright))}

  .lm{display:flex; align-items:center; gap:14px; padding:11px 14px; border:1px solid var(--line-soft); border-radius:3px; margin-bottom:8px; background:var(--panel)}
  .lm:hover{border-color:var(--gold-deep)}
  .lm .name{font-family:"Spectral","Georgia",serif; font-size:15px; color:var(--gold-bright); font-style:italic}
  .lm .path{font-size:10px; color:var(--ink-faint); overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .lm .dist{margin-left:auto; font-family:"Cinzel","Georgia",serif; color:var(--ink); font-size:14px; flex:none}
  .lm .dist small{color:var(--ink-faint); font-size:9px; letter-spacing:.1em; display:block; text-align:right}

  .empty{color:var(--ink-faint); text-align:center; padding:60px 0; font-style:italic; font-family:"Spectral","Georgia",serif; font-size:15px}
  ::-webkit-scrollbar{width:10px;height:10px}
  ::-webkit-scrollbar-thumb{background:var(--line); border-radius:5px; border:2px solid var(--bg)}
  ::-webkit-scrollbar-track{background:transparent}

  /* ── console / control panel ── */
  .panel-grid{display:grid; grid-template-columns:repeat(auto-fill,minmax(330px,1fr)); gap:22px; align-items:start}
  .card{border:1px solid var(--line); border-radius:4px; background:var(--panel); padding:18px 22px; box-shadow:var(--shadow)}
  .card h3{font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.2em; text-transform:uppercase; color:var(--gold); margin:0 0 8px}
  .card h3 .tag{color:var(--ink-faint); font-size:10px; letter-spacing:.1em}
  .row{display:flex; align-items:center; justify-content:space-between; gap:16px; padding:11px 0; border-bottom:1px dotted var(--line-soft)}
  .row:last-child{border-bottom:none}
  .row .rl{font-size:12px; color:var(--ink); min-width:0}
  .row .rl small{display:block; color:var(--ink-faint); font-size:10px; letter-spacing:.03em; margin-top:3px; line-height:1.4}
  .sw{position:relative; width:44px; height:23px; flex:none; cursor:pointer; display:inline-block}
  .sw input{opacity:0; width:0; height:0; position:absolute}
  .sw .track{position:absolute; inset:0; background:#0c0a07; border:1px solid var(--line); border-radius:12px; transition:.2s}
  .sw .knob{position:absolute; top:3px; left:3px; width:15px; height:15px; border-radius:50%; background:var(--ink-faint); transition:.2s}
  .sw input:checked ~ .track{background:var(--gold-deep); border-color:var(--gold)}
  .sw input:checked ~ .knob{transform:translateX(21px); background:var(--gold-bright); box-shadow:0 0 9px -1px var(--gold-bright)}
  .numin{font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:6px 9px; width:96px; text-align:right}
  .numin:focus{outline:none; border-color:var(--gold-deep)}
  .ro{color:var(--gold-bright); font-family:"Cinzel","Georgia",serif; font-size:14px}
  .hint-row{color:var(--ink-faint)!important; font-size:11px!important; font-style:italic}
  .saved{font-size:10px; letter-spacing:.18em; text-transform:uppercase; color:var(--good); opacity:0; transition:opacity .3s}
  .saved.show{opacity:1}

  /* ── icon / mechanic style editors ── */
  .stylerow{display:flex; align-items:center; gap:9px; padding:9px 0; border-bottom:1px dotted var(--line-soft); flex-wrap:wrap}
  .stylerow:last-child{border-bottom:none}
  .stylerow .nm{flex:1 1 110px; min-width:90px; font-size:12px; color:var(--ink)}
  .stylerow .sw{width:38px; height:20px}
  .stylerow .sw .knob{width:13px; height:13px}
  .stylerow .sw input:checked ~ .knob{transform:translateX(18px)}
  input[type=color]{width:30px; height:24px; padding:0; border:1px solid var(--line); background:#0c0a07; border-radius:2px; cursor:pointer; flex:none}
  input[type=range].op{width:78px; accent-color:var(--gold); flex:none}
  .opv{font-size:10px; color:var(--ink-faint); width:30px; text-align:right}
  .numin.sz{width:56px}
  .mechrow{border:1px solid var(--line-soft); border-radius:3px; background:var(--panel2); padding:10px 12px; margin-bottom:8px}
  .mechrow .top{display:flex; align-items:center; gap:9px; margin-bottom:8px}
  .mechrow .top input.mname{flex:1; font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:5px 9px}
  .mechrow .matchin{width:100%; font-family:inherit; font-size:11px; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:5px 9px; margin-bottom:8px}
  .mechrow .ctl{display:flex; align-items:center; gap:9px; flex-wrap:wrap}
  .mcats{display:flex; align-items:center; gap:6px; flex-wrap:wrap; margin-bottom:8px}
  .mcats-lbl{font-size:10px; letter-spacing:.06em; text-transform:uppercase; color:var(--ink-faint); margin-right:2px}
  .mcats-hint{font-size:10px; font-style:italic; color:var(--ink-faint)}
  .catchip{display:inline-flex; align-items:center; font-size:11px; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:10px; padding:2px 9px; cursor:pointer; user-select:none}
  .catchip:hover{border-color:var(--gold-deep)}
  .catchip.on{color:var(--bg); background:var(--gold); border-color:var(--gold-bright); font-weight:600}
  .catchip input{display:none}
  /* Display-rule rows: collapsed one-line header, expand to the full editor. */
  .drrow{padding:8px 12px}
  .drhead{display:flex; align-items:center; gap:9px; cursor:pointer}
  .drhead .sw{flex:none}
  .drcaret{color:var(--ink-faint); width:10px; font-size:10px; flex:none}
  .drswatch{width:15px; height:15px; flex:none; display:inline-flex}
  .drswatch svg{width:15px; height:15px; display:block}
  .drnm{font-weight:600; color:var(--ink); white-space:nowrap; flex:none; max-width:200px; overflow:hidden; text-overflow:ellipsis}
  .drsum{flex:1 1 auto; min-width:0; color:var(--ink-faint); font-size:11px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .drbadges{display:inline-flex; gap:4px; flex:none}
  .drbadge{font-size:9px; text-transform:uppercase; letter-spacing:.05em; color:var(--ink-dim); border:1px solid var(--line); border-radius:8px; padding:1px 6px; white-space:nowrap}
  .drbadge.hide{color:var(--blood-bright); border-color:var(--blood)}
  .drrow.off .drnm,.drrow.off .drsum,.drrow.off .drswatch{opacity:.45}
  .drbody{margin-top:10px; padding-top:10px; border-top:1px dotted var(--line-soft)}
  .drbody .top{align-items:center; margin-bottom:8px}
  .drord{display:inline-flex; gap:2px; flex:none}
  .drhead .delbtn{flex:none}
  .ordbtn{font-size:10px; line-height:1; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:3px 6px; cursor:pointer}
  .ordbtn:hover{color:var(--gold-bright); border-color:var(--gold-deep)}
  .drconds{display:flex; align-items:center; gap:10px; flex-wrap:wrap; margin-bottom:8px}
  .drsel{display:inline-flex; align-items:center; gap:5px; font-size:10px; letter-spacing:.05em; text-transform:uppercase; color:var(--ink-faint)}
  .drsel select{font-family:inherit; font-size:11px; text-transform:none; letter-spacing:0; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:3px 6px}
  .drsel select:hover{border-color:var(--gold-deep)}
  .drflag{display:inline-flex; align-items:center; gap:5px; font-size:11px; color:var(--ink-dim); cursor:pointer; user-select:none; white-space:nowrap}
  .dr-hideflag{color:var(--blood-bright)}
  .drrow.hideon{opacity:.72}
  .drrow.hideon .iconpick,.drrow.hideon .dr-color,.drrow.hideon .dr-op,.drrow.hideon .dr-size,.drrow.hideon .dr-label,.drrow.hideon .opv{opacity:.4; pointer-events:none}
  /* consolidated HP-bar card: per-rarity grid + shared geometry footer */
  .hpgrid{display:grid; grid-template-columns:30px 64px 1fr 30px 1fr; gap:9px 11px; align-items:center; padding:4px 0 2px}
  .hpgrid input[type=checkbox]{margin:0; justify-self:center}
  .hpgrid .hph{font-size:10px; letter-spacing:.06em; text-transform:uppercase; color:var(--ink-faint); text-align:right}
  .hpgrid .hph:first-child{text-align:left}
  .hpgrid .hpr{font-size:12px; color:var(--ink)}
  .hpgrid .numin{width:100%; min-width:0; padding:5px 8px}
  .hpgrid input[type=color]{width:100%}
  .hpshared{display:flex; gap:16px; flex-wrap:wrap; margin-top:10px; padding-top:11px; border-top:1px dotted var(--line-soft)}
  .hpshared label{display:flex; align-items:center; gap:7px; font-size:11px; color:var(--ink-dim)}
  .hpshared .numin{width:62px}
  .delbtn{font-family:inherit; font-size:11px; color:var(--blood-bright); background:transparent; border:1px solid var(--line); border-radius:2px; padding:4px 9px; cursor:pointer; flex:none}
  .trow-ctl{display:flex; align-items:center; gap:9px; flex:none}

  /* ── SVG icon picker (replaces the plain shape <select>): a button showing the chosen icon's
       silhouette + name, opening a shared popup grid of icon previews. ── */
  .iconpick{display:inline-flex; align-items:center; gap:6px; min-width:104px; background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:3px 7px; cursor:pointer; flex:none}
  .iconpick:hover{border-color:var(--gold-deep)}
  .iconpick .ipreview{width:15px; height:15px; flex:none; display:inline-flex; color:var(--ink)}
  .iconpick .ipreview svg{width:15px; height:15px; display:block}
  .iconpick .ipname{font-size:11px; color:var(--ink); white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .iconpick .ipcar{margin-left:auto; color:var(--ink-faint); font-size:8px}
  #iconPop{position:fixed; z-index:1000; display:none; background:var(--panel2); border:1px solid var(--gold-deep); border-radius:4px; box-shadow:var(--shadow); padding:8px; max-height:300px; overflow:auto}
  #iconPop.open{display:block}
  /* Add-rule picker modal: browse live entities + terrain tiles. */
  #pickPop{position:fixed; inset:0; z-index:1100; display:none; background:rgba(0,0,0,.62); padding:6vh 4vw}
  #pickPop.open{display:flex; justify-content:center; align-items:flex-start}
  .pickbox{display:flex; flex-direction:column; width:min(760px,100%); max-height:88vh; background:var(--panel); border:1px solid var(--gold-deep); border-radius:6px; box-shadow:var(--shadow); overflow:hidden}
  .pickhead{display:flex; align-items:center; gap:10px; padding:12px 14px; border-bottom:1px solid var(--line)}
  .pickhead #pickSearch{flex:1; font-family:inherit; font-size:13px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:3px; padding:8px 11px}
  .pickkinds{display:inline-flex; gap:3px}
  .pickclose{font-size:13px; color:var(--ink-dim); background:transparent; border:1px solid var(--line); border-radius:3px; padding:6px 10px; cursor:pointer}
  .pickclose:hover{color:var(--blood-bright); border-color:var(--blood)}
  .picklist{overflow:auto; padding:4px 0}
  .pickrow{display:flex; align-items:center; gap:10px; padding:7px 14px; cursor:pointer; border-bottom:1px dotted var(--line-soft)}
  .pickrow:hover{background:var(--panel2)}
  .pickbadge{flex:none; font-size:9px; text-transform:uppercase; letter-spacing:.05em; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:8px; padding:2px 7px; min-width:58px; text-align:center}
  .pickbadge.tile{color:var(--poi); border-color:var(--poi)}
  .pickbadge.entity{color:var(--gold)}
  .pickbadge.mod{color:#26d9c0; border-color:#1c9e8c}
  .pickcount{flex:none; font-size:10px; color:var(--ink-dim); font-family:"Cinzel","Georgia",serif}
  .picknm{flex:none; font-weight:600; color:var(--ink); max-width:230px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .picksub{flex:1; min-width:0; color:var(--ink-faint); font-size:11px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .pickrar{flex:none; font-size:10px; color:var(--rare)}
  .pickempty{padding:24px 14px; color:var(--ink-faint); font-style:italic; text-align:center}
  .pickfoot{padding:9px 14px; border-top:1px solid var(--line); color:var(--ink-faint); font-size:11px}
  /* Landmarks tab rows */
  .lmrow{display:flex; align-items:center; gap:10px; padding:6px 0; border-bottom:1px dotted var(--line-soft)}
  .lmbadge{flex:none; min-width:48px; text-align:center; font-size:9px; text-transform:uppercase; letter-spacing:.05em; color:var(--ink-dim); border:1px solid var(--line); border-radius:8px; padding:2px 6px}
  .lmbadge.user{color:var(--gold); border-color:var(--gold-deep)}
  .lmbadge.hidden{color:var(--blood-bright); border-color:var(--blood)}
  .lmarea{flex:none; min-width:64px; font-size:11px; color:var(--ink-dim); font-family:"Consolas",monospace}
  .lmlabel{flex:none; width:200px}
  .lmpath{flex:1; min-width:0; color:var(--ink-faint); font-size:11px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-family:"Consolas",monospace}
  .lmrow.sup .lmlabel,.lmrow.sup .lmpath{opacity:.5}
  .ipop-grid{display:grid; grid-template-columns:repeat(6,38px); gap:4px}
  .ipop-cell{display:flex; flex-direction:column; align-items:center; justify-content:center; gap:3px; width:38px; height:40px; border:1px solid transparent; border-radius:3px; cursor:pointer; color:var(--ink)}
  .ipop-cell:hover{border-color:var(--gold); background:#0c0a07}
  .ipop-cell.sel{border-color:var(--gold-bright); background:#0c0a07}
  .ipop-cell svg{width:20px; height:20px; display:block}
  .ipop-cell .cn{font-size:7px; line-height:1; color:var(--ink-faint); max-width:36px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .delbtn:hover{border-color:var(--blood-bright)}
  .chip{font-family:inherit; font-size:10px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:10px; padding:2px 8px; margin:0 4px 4px 0; cursor:pointer}
  .chip:hover{border-color:var(--gold-deep); color:var(--gold)}
  .addbtn{font-family:"Cinzel","Georgia",serif; font-size:11px; letter-spacing:.1em; color:var(--gold-bright); background:transparent; border:1px dashed var(--gold-deep); border-radius:3px; padding:8px 14px; cursor:pointer; width:100%; margin-top:4px}
  .addbtn:hover{background:rgba(200,160,73,.07)}

  /* ── dashboard nav list ── */
  .navrow{display:flex; align-items:center; gap:12px; padding:9px 12px; border:1px solid var(--line-soft); border-radius:3px; margin-bottom:6px; background:var(--panel); cursor:pointer}
  .navrow:hover{border-color:var(--gold-deep)}
  .navrow.sel{border-color:var(--gold); background:rgba(200,160,73,.07)}
  .navbtn{width:18px; height:18px; flex:none; border:1px solid var(--ink-faint); border-radius:50%; display:flex; align-items:center; justify-content:center; font-size:11px; color:#120d06; line-height:1}
  .navrow:not(.sel) .navbtn{color:var(--ink-faint)}
  .navname{flex:1; min-width:0; color:var(--ink); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-family:"Spectral","Georgia",serif; font-size:14px}
  .navrow.sel .navname{color:var(--gold-bright)}
  .navtag{font-size:9px; letter-spacing:.12em; text-transform:uppercase; color:var(--ink-faint); border:1px solid var(--line-soft); border-radius:10px; padding:2px 8px; flex:none}
  .navdist{font-family:"Cinzel","Georgia",serif; color:var(--ink-dim); font-size:13px; min-width:48px; text-align:right; flex:none}
  .gear-grid{display:flex; flex-wrap:wrap; gap:6px; padding:8px 0}
  .gcell{width:52px; height:52px; border-radius:3px; border:1px solid var(--line); display:flex; align-items:center; justify-content:center;
         font-size:13px; font-weight:700; color:#0c0a07; cursor:default; overflow:hidden}
  .gcell small{display:block}
</style>
</head>
<body>
<a id="updateBanner" href="#" target="_blank" rel="noopener" hidden
   style="display:none;align-items:center;gap:10px;padding:9px 16px;margin:0;background:#e0b341;color:#1a1400;font-weight:600;text-decoration:none">
  <span>&#x2B06; Update available</span><span id="updateMsg" style="font-weight:400"></span><span style="margin-left:auto;text-decoration:underline">Download &rarr;</span>
</a>
<div class="shell">
  <header>
    <div class="mark">
      <h1>OVERLAY</h1>
    </div>
    <div class="hgap"></div>
    <div class="area-chip" id="areaChip">— <b>·</b></div>
    <div class="conn" id="conn"><span class="dot"></span><span id="connTxt">offline</span></div>
  </header>

  <div class="body">
    <aside>
      <div class="vital">
        <div class="vlabel"><span>Life</span><span class="num" id="hpNum">—</span></div>
        <div class="bar hp"><i id="hpBar" style="width:0"></i></div>
      </div>
      <div class="vital">
        <div class="vlabel"><span>Energy Shield</span><span class="num" id="esNum">—</span></div>
        <div class="bar es"><i id="esBar" style="width:0"></i></div>
      </div>
      <div class="vital">
        <div class="vlabel"><span>Mana</span><span class="num" id="mpNum">—</span></div>
        <div class="bar mana"><i id="mpBar" style="width:0"></i></div>
      </div>

      <div class="sect">Zone</div>
      <div class="kv"><span>Area</span><span id="kAreaName">—</span></div>
      <div class="kv"><span>Area code</span><span id="kArea">—</span></div>
      <div class="kv"><span>Act / Level</span><span id="kAlvl">—</span></div>
      <div class="kv"><span>Map open</span><span id="kMap">—</span></div>
      <div id="zoneNotes" class="znotes" hidden></div>

      <div class="sect">Census</div>
      <div class="tally">
        <div class="t"><div class="n" id="cEnt">0</div><div class="l">Entities</div></div>
        <div class="t"><div class="n" id="cPoi">0</div><div class="l">Points of Int.</div></div>
        <div class="t"><div class="n" id="cMon">0</div><div class="l">Monsters</div></div>
        <div class="t"><div class="n" id="cLm">0</div><div class="l">Landmarks</div></div>
      </div>

      <div id="monoCard" hidden>
        <div class="sect">Monolith Rewards</div>
        <div id="monoList" class="znotes" style="display:block"></div>
      </div>

      <div id="dirCard" hidden>
        <div class="sect">Objective Director</div>
        <div id="dirList" class="znotes" style="display:block"></div>
      </div>

      <div style="height:24px"></div>
    </aside>

    <main>
      <div class="tabs">
        <button class="tab on" data-tab="filters">Rules</button>
        <button class="tab" data-tab="landmarks">Landmarks</button>
        <button class="tab" data-tab="atlas">Atlas</button>
        <button class="tab" data-tab="settings">Settings</button>
        <button class="tab" data-tab="director">Director</button>
            <button class="tab" data-tab="entatlas">Entity Atlas</button>
            <button class="tab" data-tab="gear">Gear &#9733;</button>
      </div>

      <section class="view" data-view="filters">
        <div class="panel-grid">
          <div class="card" style="grid-column:1/-1">
            <h3>Display Rules <span class="tag">&middot; one ordered ruleset &mdash; first match wins</span></h3>
            <div class="row"><div class="rl hint-row">The single source of truth for how every entity draws. Each entity is matched <b>top&ndash;to&ndash;bottom</b>; the <b>first enabled rule that matches</b> decides everything &mdash; its icon &amp; color, whether it&rsquo;s hidden, whether it shows an HP bar, and whether it&rsquo;s auto-pathed. Reorder with &#9650;/&#9660; to change precedence. A rule matches on any mix of <i>type, metadata terms, monster mods (auras/buffs), rarity, reaction, life, chest/POI/encounter state</i>; a blank condition means &ldquo;any&rdquo;. No more conflicting filters &mdash; if two rules could match, the higher one wins.</div></div>
            <div id="drList"></div>
            <div class="controls" style="margin:8px 0 0">
              <button class="addbtn" id="drPick" style="width:auto;margin:0;padding:9px 16px">+ Add from game data…</button>
              <button class="addbtn" id="drAdd" style="width:auto;margin:0;padding:9px 16px">+ Add blank rule</button>
            </div>
          </div>
          <div class="card" style="grid-column:1/-1">
            <h3>Hidden <span class="tag">&middot; cull entirely from radar, list &amp; nav</span></h3>
            <div class="row"><div class="rl hint-row">A stronger cut than a Hide rule: entities whose metadata contains a pattern (or matches a <code>*</code>/<code>?</code> glob) are removed <i>everywhere</i> &mdash; overlay, entity list, and navigation &mdash; before the display rules even run.</div></div>
            <div id="hideList" class="controls" style="margin:8px 0 14px"></div>
            <div class="controls" style="margin:0">
              <input type="search" id="hidePattern" placeholder="pattern or glob to hide (e.g. AbyssCrack, *Daemon*)">
              <button class="addbtn" id="hideAdd" style="width:auto;margin:0;padding:8px 16px">+ Hide</button>
            </div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsgF">&#10003; saved to config</span></div>
      </section>

      <section class="view" data-view="landmarks" hidden>
        <div class="panel-grid">
          <div class="card" style="grid-column:1/-1">
            <h3>Landmarks <span class="tag">&middot; curated map labels &mdash; view, fix, share</span></h3>
            <div class="row"><div class="rl hint-row">The built-in &ldquo;known&rdquo; map features (boss arenas, exits, loot, waypoints&hellip;), labelled per area. Rename a wrong label, add your own, or hide a bad entry. <b>Export</b> a corrected list to share or submit for baking into a release; <b>Import</b> to load one. (For how a tile <i>draws</i> — icon/color/hide — use a Tile rule on the Rules tab; this is just the labels.)</div></div>
            <div class="controls" style="margin:6px 0 12px">
              <input type="search" id="lmSearch" placeholder="filter by area / tile / label…">
              <button class="chip on" id="lmAreaOnly">This area only</button>
              <span style="flex:1"></span>
              <button class="addbtn" id="lmImport" style="width:auto;margin:0;padding:8px 14px">Import…</button>
              <button class="addbtn" id="lmExport" style="width:auto;margin:0;padding:8px 14px">Export</button>
            </div>
            <div id="lmList"></div>
            <div class="mechrow">
              <div class="top">
                <input class="mname" id="lmArea" placeholder="area (e.g. P2_3, or *)" style="max-width:150px">
                <input class="mname" id="lmPat" placeholder="tile path / pattern">
                <input class="mname" id="lmLabel" placeholder="label">
                <button class="addbtn" id="lmAdd" style="width:auto;margin:0;padding:8px 16px">+ Add</button>
              </div>
            </div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsgL">&#10003; saved to config</span></div>
      </section>

      <section class="view" data-view="atlas" hidden>
        <div class="panel-grid">
          <div class="card" style="grid-column:1/-1">
            <h3 style="display:flex;align-items:center;gap:10px">Atlas
              <span class="tag" id="atlasStatus">&mdash;</span>
              <span style="flex:1"></span>
              <button class="chip" id="atlasRefresh" title="Re-read the open Atlas">&#8635; Refresh</button>
              <button class="chip" id="atlasHelp" title="How it works" style="width:28px;padding:6px 0;text-align:center">?</button>
            </h3>

            <!-- help popover (collapsed by default) -->
            <div id="atlasHelpBox" hidden class="hint-row" style="margin:0 0 10px;padding:9px 11px;border:1px solid var(--line);border-radius:6px;line-height:1.6">
              Open the Atlas in-game, then <b>Refresh</b>. Each row is a map type or rolled content read from memory.
              Per row toggle <b>&#9745; Highlight</b> (ring it in-game), <b style="color:#3ddc97">&#8674; Nav</b> (draw a route to it),
              <b style="color:#e0b341">&#10148; Arrow</b> (edge pointer when off-screen) &mdash; independent. Click any column header to sort.
              Hover a tile in-game + press <b>F10</b> to inspect it.
            </div>

            <!-- quick presets -->
            <div class="controls" id="atlasPresets" style="gap:6px;margin:0 0 8px;flex-wrap:wrap">
              <span class="hint-row" style="opacity:.7;margin-right:2px">Quick&nbsp;set:</span>
              <button class="chip" data-preset="citadels">&#9733; Citadels</button>
              <button class="chip" data-preset="deadly">&#9760; Deadly Boss</button>
              <button class="chip" data-preset="bosses">Bosses</button>
              <button class="chip" data-preset="towers">Towers</button>
              <button class="chip" data-preset="uniques">Uniques</button>
            </div>

            <!-- active rules (removable chips) -->
            <div id="atlasActive" style="margin:0 0 8px"></div>

            <!-- group filter + search -->
            <div class="controls" style="gap:6px;margin:0 0 8px;flex-wrap:wrap">
              <button class="chip on" data-group="all">All</button>
              <button class="chip" data-group="Kind">Kind</button>
              <button class="chip" data-group="Content">Content</button>
              <button class="chip" data-group="Map">Map</button>
              <span style="flex:1"></span>
              <button class="chip" id="atlasHlSelOnly">Active only</button>
              <button class="chip" id="atlasHlClear">Clear all</button>
              <input type="search" id="atlasHlFilter" placeholder="search&hellip;" style="width:160px">
            </div>

            <div id="atlasHlTable" style="max-height:460px;overflow:auto;border:1px solid var(--line);border-radius:6px">
              <span class="hint-row" style="padding:8px;display:block">Open the Atlas in-game + Refresh to list filters.</span>
            </div>
          </div>
          <div class="card">
            <h3>Dynasty-support maps <small>Anomaly-boss maps that drop Lineage/Dynasty support gems &mdash; enable the highlight in Settings</small></h3>
            <div id="dynastyList" class="znotes" style="display:block"><div class="rl hint-row">Loading&hellip;</div></div>
          </div>
        </div>
      </section>

      <section class="view" data-view="settings" hidden>
        <div class="panel-grid">
          <div class="card">
            <h3>Radar Display</h3>
            <div class="row"><div class="rl">Show terrain<small>walkable-terrain bitmap</small></div>
              <label class="sw"><input type="checkbox" data-set="showTerrain"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Monolith reward panel<small>the nearby-monolith reward list drawn over the minimap (off by default)</small></div>
              <label class="sw"><input type="checkbox" data-set="showMonolithPanel"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Show player blip<small>blue dot marking your own position</small></div>
              <label class="sw"><input type="checkbox" data-set="showPlayerBlip"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Always show overlay<small>draw even when PoE2 isn&rsquo;t focused (e.g. while tweaking this dashboard)</small></div>
              <label class="sw"><input type="checkbox" data-set="alwaysShowOverlay"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Hide junk entities<small>suppress cosmetic / FX / daemon dots</small></div>
              <label class="sw"><input type="checkbox" data-set="hideJunk"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Navigation paths<small>draw A&#42; routes to selected landmarks</small></div>
              <label class="sw"><input type="checkbox" data-set="showPath"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Objective Director (experimental)<small>WIP &mdash; only routes content the radar already detects. Order: event &rarr; bosses &rarr; side zones &rarr; exit</small></div>
              <label class="sw"><input type="checkbox" data-set="enableDirector"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Curated landmark names<small>community labels (boss / reward / exits)</small></div>
              <label class="sw"><input type="checkbox" data-set="useCuratedLandmarks"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Hide from screen capture<small>stealth: keep the overlay out of screenshots / OBS / share-screen. Turn off to capture the overlay itself</small></div>
              <label class="sw"><input type="checkbox" data-set="excludeFromCapture"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Check for updates<small>one GitHub request at startup (the only outbound traffic) — turn off for zero network egress</small></div>
              <label class="sw"><input type="checkbox" data-set="checkForUpdates"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Gear scorer (experimental)<small>0&ndash;100 god-roll scoring of your inventory by stat weights — see the Gear &#9733; tab. Reads inventory only while on</small></div>
              <label class="sw"><input type="checkbox" data-set="enableGearScorer"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Target hotkeys<small>Ctrl+Alt+ ] next / [ prev / 1-9 slot / 0 clear &mdash; switch the active radar target</small></div>
              <label class="sw"><input type="checkbox" data-set="enableTargetHotkeys"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Controller target cycle<small>L3 = previous target, R3 = next (combat-dead buttons in PoE2)</small></div>
              <label class="sw"><input type="checkbox" data-set="enableControllerCycle"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Dynasty-support maps<small>highlight maps whose Anomaly bosses drop Lineage/Dynasty support gems (off by default)</small></div>
              <label class="sw"><input type="checkbox" data-set="highlightDynastyMaps"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Overlay FPS cap<small>lower = less load on the game; 60 is smooth for a radar (15&ndash;360)</small></div>
              <input class="numin" type="number" step="1" min="15" max="360" data-set="fpsCap"></div>
            <div class="row"><div class="rl">Contribute URL<small>your Cloudflare Worker endpoint; set this to enable one-click &ldquo;Contribute&rdquo;</small></div>
              <input class="numin" type="text" data-set="contributeUrl" placeholder="https://&hellip;workers.dev" style="width:240px"></div>
          </div>
          <div class="card">
            <h3>Monster HP Bars <span class="tag">&middot; by rarity</span></h3>
            <div class="row"><div class="rl hint-row">Toggle the bar on/off per rarity with the <b>On</b> checkbox &mdash; uncheck all to disable HP bars entirely, or leave only the rarities you want. The rest sets the bar <i>geometry</i> per rarity.</div></div>
            <div class="hpgrid">
              <span class="hph">On</span><span class="hph">Rarity</span><span class="hph">Width</span><span class="hph">Border</span><span class="hph">Thick</span>
              <input type="checkbox" data-set="hpBarNormal">
              <span class="hpr">Normal</span>
              <input class="numin" type="number" step="1" min="4" data-hp="widthNormal">
              <input type="color" class="i-color" data-hpcolor="borderColorNormal">
              <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderNormal">
              <input type="checkbox" data-set="hpBarMagic">
              <span class="hpr" style="color:var(--magic)">Magic</span>
              <input class="numin" type="number" step="1" min="4" data-hp="widthMagic">
              <input type="color" class="i-color" data-hpcolor="borderColorMagic">
              <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderMagic">
              <input type="checkbox" data-set="hpBarRare">
              <span class="hpr" style="color:var(--rare)">Rare</span>
              <input class="numin" type="number" step="1" min="4" data-hp="widthRare">
              <input type="color" class="i-color" data-hpcolor="borderColorRare">
              <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderRare">
              <input type="checkbox" data-set="hpBarUnique">
              <span class="hpr" style="color:var(--unique)">Unique</span>
              <input class="numin" type="number" step="1" min="4" data-hp="widthUnique">
              <input type="color" class="i-color" data-hpcolor="borderColorUnique">
              <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderUnique">
            </div>
            <div class="hpshared">
              <label>Height<input class="numin" type="number" step="1" min="1" max="30" data-hp="height"></label>
              <label>Offset X<input class="numin" type="number" step="1" data-hp="offsetX"></label>
              <label>Offset Y<input class="numin" type="number" step="1" data-hp="offsetY"></label>
            </div>
            <div class="row"><div class="rl hint-row">Bar fill follows the monster icon color; set border color &amp; thickness per rarity (thickness 0 = no border). Offset Y negative = above the mob.</div></div>
          </div>
          <div class="card">
            <h3>Terrain <span class="tag">&middot; walkable overlay</span></h3>
            <div class="row"><div class="rl">Interior fill<small>wash over walkable cells</small></div>
              <span class="trow-ctl">
                <input type="color" class="i-color" data-tcolor="interiorColor">
                <input type="range" class="op" min="0" max="100" data-topacity="interiorOpacity">
                <span class="opv" data-topv="interiorOpacity">—</span></span></div>
            <div class="row"><div class="rl" style="color:var(--poi)">Wall edge<small>outlines around rooms</small></div>
              <span class="trow-ctl">
                <input type="color" class="i-color" data-tcolor="edgeColor">
                <input type="range" class="op" min="0" max="100" data-topacity="edgeOpacity">
                <span class="opv" data-topv="edgeOpacity">—</span></span></div>
            <div class="row"><div class="rl hint-row">Edits rebuild the terrain bitmap; use &ldquo;Show terrain&rdquo; above to hide it entirely.</div></div>
          </div>
          <div class="card">
            <h3>Map Calibration</h3>
            <div class="row"><div class="rl">Scale multiplier<small>projection scale of the map overlay</small></div>
              <input class="numin" type="number" step="0.01" data-set="scaleMul"></div>
            <div class="row"><div class="rl">Offset X</div><input class="numin" type="number" step="1" data-set="offX"></div>
            <div class="row"><div class="rl">Offset Y</div><input class="numin" type="number" step="1" data-set="offY"></div>
            <div class="row"><div class="rl hint-row">Adjust here &mdash; changes apply live (no in-game hotkeys).</div></div>
          </div>
          <div class="card">
            <h3>Ground Item Labels</h3>
            <div class="row"><div class="rl">Enabled<small>draw name labels</small></div>
              <label class="sw"><input type="checkbox" data-gi="enabled"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl hint-row">Show a label for these categories:</div></div>
            <div class="chips" id="giCats">
              <span class="chip" data-gicat="Uniques">Uniques</span>
              <span class="chip" data-gicat="Currency">Currency</span>
              <span class="chip" data-gicat="Runes">Runes</span>
              <span class="chip" data-gicat="SoulCores">Soul Cores</span>
              <span class="chip" data-gicat="UncutGems">Uncut Gems</span>
              <span class="chip" data-gicat="Essences">Essences</span>
              <span class="chip" data-gicat="Fragments">Fragments</span>
              <span class="chip" data-gicat="Tablets">Tablets</span>
              <span class="chip" data-gicat="Delirium">Delirium</span>
              <span class="chip" data-gicat="Idols">Idols</span>
              <span class="chip" data-gicat="Abyss">Abyss</span>
              <span class="chip" data-gicat="Ritual">Ritual</span>
              <span class="chip" data-gicat="Breach">Breach</span>
              <span class="chip" data-gicat="Expedition">Expedition</span>
            </div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsg">&#10003; saved to config</span></div>
      </section>

        <section class="view" data-view="director" hidden>
          <div class="card">
            <h3>Needs cataloguing <small>notable POIs/landmarks you've seen that no objective covers yet</small></h3>
            <div class="row"><input id="dirSearch" class="numin" type="text" placeholder="filter…" style="width:200px"></div>
            <div id="dirCandidates" class="znotes" style="display:block"></div>
          </div>
          <div class="card">
            <h3>Catalog <small>active Director objectives (priority order)</small></h3>
            <div id="dirCatalog" class="znotes" style="display:block"></div>
          </div>
        </section>

        <section class="view" data-view="entatlas" hidden>
          <div class="card">
            <h3>Entity Atlas <small>name every entity you've seen; classify the notable ones</small></h3>
            <div class="row">
              <input id="eaSearch" class="numin" type="text" placeholder="filter…" style="width:200px">
              <button class="numin" id="eaExport">Export pack</button>
              <label class="numin" style="cursor:pointer">Import pack<input id="eaImport" type="file" accept="application/json" style="display:none"></label>
              <button class="numin" id="eaContribute" title="Contribute your discovered names + labels to the community master list (one click). With no Contribute URL set, opens the submission form instead.">Contribute names →</button>
              <span class="saved" id="savedMsgEa">&#10003; contributed — thank you!</span>
            </div>
          </div>
          <div class="card">
            <h3>Needs a name <small>entities with no friendly name yet (shows the raw path)</small></h3>
            <div id="eaUnnamed" class="znotes" style="display:block"></div>
          </div>
          <div class="card">
            <h3>Notable, uncatalogued <small>named/notable entities no objective covers yet</small></h3>
            <div id="eaNotable" class="znotes" style="display:block"></div>
          </div>
        </section>

        <section class="view" data-view="gear" hidden>
          <div class="card">
            <h3>Gear scorer &#9733; <small>experimental — turn on "Gear scorer" in Settings; scores your inventory 0&ndash;100 by your stat weights</small></h3>
            <div id="gStatus" class="row"><div class="rl hint-row">Loading&hellip;</div></div>
          </div>
          <div class="card">
            <h3>Items <small>highest score first; &#9733; = god roll. Each item lists its affixes + stat ids</small></h3>
            <div class="row"><div class="rl">View</div>
              <label class="sw" style="gap:8px"><span style="font-size:11px;color:var(--ink-faint)">List</span>
              <input type="checkbox" id="gGridToggle"><span class="track"></span><span class="knob"></span>
              <span style="font-size:11px;color:var(--ink-faint)">Grid</span></label></div>
            <div id="gGrid" class="gear-grid" style="display:none"></div>
            <div id="gItems" class="znotes" style="display:block"></div>
          </div>
          <div class="card">
            <h3>Weights <small>weight each stat id you care about; an item's score = your weighted roll sum vs the target</small></h3>
            <div class="row">
              <input id="gStatId" class="numin" type="text" placeholder="stat id (copy from an affix above)" style="width:220px">
              <input id="gWeight" class="numin" type="number" step="0.1" placeholder="weight" style="width:80px">
              <button class="numin" id="gSetWeight">Set</button>
              <button class="numin" id="gLoadStarter" title="replace your weights with the ladder-meta starter set">Load meta starter</button>
            </div>
            <div class="row"><div class="rl">Target<small>raw weighted total that = a score of 100</small></div><input id="gTarget" class="numin" type="number" style="width:90px"></div>
            <div class="row"><div class="rl">God-roll threshold<small>score (0&ndash;100) at/above which an item gets a &#9733;</small></div><input id="gThreshold" class="numin" type="number" style="width:90px"></div>
            <div id="gWeightList" class="znotes" style="display:block"></div>
          </div>
        </section>

    </main>
  </div>
</div>

<script>
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
  if(activeTab==='settings') loadSettings();
  if(activeTab==='filters') loadFilters();
  if(activeTab==='landmarks') loadLandmarks();
  if(activeTab==='atlas'){ if(!atlasData) loadAtlas(); else renderAtlas(); loadDynasty(); }
  if(activeTab==='director') loadDirector();
  if(activeTab==='entatlas') loadEntAtlas();
  if(activeTab==='gear') loadGear();
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
    renderHpBars(); renderTerrain(); renderGround();
  }catch(e){}
}

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
    else if(el.type==='number'){ el.onchange=()=>{const v=parseFloat(el.value); if(!isNaN(v)) saveSetting(k,v);}; } else { el.onchange=()=>saveSetting(k, el.value); }
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
      <input class="matchin dr-match" placeholder="match: metadata terms, comma-separated (blank = any)" value="${esc((r.match||[]).join(', '))}">
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
  list.innerHTML = items.length ? items.slice(0,600).map((it,i)=>
    `<div class="pickrow" data-i="${i}"><span class="pickbadge ${it.kind}">${it.kind==='tile'?'TILE':it.kind==='mod'?'MOD':esc(it.cat)}</span>`
    +`<span class="picknm">${esc(it.name)}</span><span class="picksub">${esc(it.sub)}</span>`
    +(it.kind==='mod'?`<span class="pickcount">×${it.count}</span>`:'')
    +(it.rarity&&it.rarity!=='NonMonster'?`<span class="pickrar">${esc(it.rarity)}</span>`:'')+`</div>`).join('')
    : `<div class="pickempty">No matches${(_pickEnts.length+_pickTiles.length===0)?' — are you in game?':''}.</div>`;
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
        const match = p.landmarkPath ? {landmarkPath:[p.landmarkPath]} : {metadata:[p.metadata]};
        postObjectives({add:Object.assign({id:p.signature,label:p.name,category:cat,priority:prio,enabled:true},match)});
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
}
function candRow(p){
  return '<div class="row" data-sig="'+esc(p.signature)+'">'
    + '<div class="rl">'+esc(p.name)+'<small>'+esc(p.category)+' · '+esc(p.zone||'?')+' · ×'+p.count+'</small></div>'
    + '<input class="numin dir-cat" list="labelVocab" placeholder="label…" style="width:130px">'
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
        try{ await fetch('/api/objectives',{method:'POST',headers:{'Content-Type':'application/json'},
             body:JSON.stringify({add:{id:'e:'+a.metadata,label:a.name,category:cat,priority:prio,enabled:true,metadata:[a.metadata]}})}); }catch(e){}
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
  return '<div class="row" data-m="'+esc(a.metadata)+'">'
    + '<div class="rl">'+esc(a.name)+'<small>'+esc(a.category)+' · '+esc(a.zone||'?')+' · ×'+a.count+'</small></div>'
    + '<input class="numin ea-cat" list="labelVocab" placeholder="label…" style="width:130px">'
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
let _eaContribUrl=null;
async function eaContribUrl(){ if(_eaContribUrl===null){ try{ const s=await getJSON('/api/settings'); _eaContribUrl=(s.contributeUrl||'').trim(); }catch(e){ _eaContribUrl=''; } } return _eaContribUrl; }
$('#eaContribute')?.addEventListener('click',async()=>{
  const url=await eaContribUrl();
  if(!url){ window.open('https://github.com/luther-rotmg/POE2GPS/issues/new?template=entity-name-submission.yml','_blank','noopener'); return; }
  if(!window._eaOkOnce){ if(!confirm('Share your discovered entity names + objectives publicly? This contains no character data.')) return; window._eaOkOnce=true; }
  try{ const r=await fetch('/api/contribute',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
    if(r.ok){ flashEa(); } else { alert('Contribute failed ('+r.status+').'); } }catch(e){ alert('Contribute failed.'); }
});

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
  $('#atlasStatus').textContent='reading…';
  try{ atlasData=await getJSON('/api/atlas'); }catch(e){ atlasData={located:false,note:'request failed'}; }
  renderAtlas();
}
function renderAtlas(){
  const d=atlasData; if(!d){ return; }
  const st=$('#atlasStatus'); const nd=d.nodes;
  if(!(nd&&nd.total)) st.textContent = d.note ? 'scanning…' : 'atlas closed — open it in-game + Refresh';
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
const CATCOL={Boss:'#e0533a',Mechanic:'#3ca0ff',Influence:'#a06cff',Hidden:'#ff5db1',Citadel:'#e0b341',Tower:'#2fb6a8',Temple:'#d98a2b',Vaal:'#c0395a',Unique:'#c678dd',Merchant:'#5aa9e6',Map:'#8a93a0'};
function catBadge(cat){ const c=CATCOL[cat]||'#8a93a0'; return '<span style="display:inline-block;padding:1px 8px;border-radius:10px;font-size:11px;font-weight:600;background:'+c+'26;color:'+c+';border:1px solid '+c+'66">'+esc(cat)+'</span>'; }
// Build the unified filter list (content + map) with {title,count,cat,group}.
function atlasFilterRows(d){
  const rows=[];
  // Kind rows first: tracking one (e.g. "Tower") rings + routes to EVERY map of that archetype.
  (d.allKinds||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Kind',cat:t.tag}));
  (d.allTags||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Content',cat:catContent(t.tag)}));
  (d.allMaps||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Map',cat:catMap(t.tag)}));
  return rows;
}
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
  let html='<div style="'+cell+';position:sticky;top:0;background:var(--panel,#1a1a1a);border-bottom:1px solid var(--line);font-weight:600;font-size:11px;text-transform:uppercase;opacity:.75">'
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
      +'<span class="hlnav" data-tag="'+esc(r.title)+'" title="toggle nav-to (route)" style="font-size:15px;cursor:pointer;color:'+(nav?'#3ddc97':'#4a525c')+'">&#8674;</span>'
      +'<span class="hlarw" data-tag="'+esc(r.title)+'" title="toggle off-screen arrow" style="font-size:15px;cursor:pointer;color:'+(arw?'#e0b341':'#4a525c')+'">➤</span>'
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

/* ── left rail ── */
function renderState(){
  const s=state; if(!s) return;
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
  }catch(e){}
}

wireSettings(); wireHpBars(); wireTerrain(); wireGround();
loadLabelVocab();
loadIcons().then(()=>{ loadSettings(); loadFilters(); }); // Rules is the default tab
tick(); setInterval(tick, 1000);
checkVersion();
</script>
</body>
</html>
""";
}
