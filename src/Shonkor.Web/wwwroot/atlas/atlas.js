const state = { project: '' };
const H = () => ({ 'X-Project-Name': state.project || '' });
const $ = s => document.querySelector(s);
const fmt = n => (n ?? 0).toLocaleString('en-US');
const TERR = [
  { key:'Class', nm:'Classes', c:'#1f4d44' }, { key:'Method', nm:'Methods', c:'#b14a26' },
  { key:'Interface', nm:'Interfaces', c:'#5a5230' }, { key:'MarkdownSection', nm:'Docs', c:'#a9803a' }
];
const PALETTE = ['#1f4d44','#b14a26','#5a5230','#a9803a','#2f6c66','#8f3d1e','#6b5a2c','#3a5a52','#94552a','#4a4636'];
// Interaction records (decisions, milestones, tasks, questions) live in the Journal, never the graph.
const INTERACTION = new Set(['Task','Decision','Question','Milestone']);
function escapeHtml(s){ return (s==null?'':String(s)).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }

async function boot(){
  try{
    const p = await (await fetch('/api/projects')).json();
    state.projects = p.projects || p.Projects || [];
    state.activeProject = p.activeProject || p.ActiveProject || '';
    state.project = state.activeProject || (state.projects[0]?.name) || '';
    $('#proj-name').textContent = state.project || 'none';
    renderProjMenu();
  }catch(e){ $('#proj-name').textContent = 'offline'; }
  loadStats(); loadDiagnostics();
}
async function loadStats(){
  try{
    const s = await (await fetch('/api/stats',{headers:H()})).json();
    $('#f-nodes').textContent = fmt(s.totalNodes); $('#f-edges').textContent = fmt(s.totalEdges);
    const calls = (s.edgesByRelation && s.edgesByRelation.CALLS) || 0;
    $('#f-calls').textContent = calls > 0 ? 'semantic · CALLS' : 'syntactic mode';
    $('#f-sem').textContent = calls > 0 ? 'semantic ✓' : 'semantic ✗';
    $('#es-sub').textContent = `${fmt(s.totalNodes)} nodes · ${fmt(s.totalEdges)} edges`;
    state.byType = s.nodesByType || {};
    renderTerritories(state.byType);
  }catch(e){ $('#es-sub').textContent = 'Could not load graph stats.'; }
}
function renderTerritories(byType){
  $('#territories').innerHTML = TERR.map(t =>
    `<div class="terr" style="--c:${t.c}"><div class="nm">${t.nm}</div><div class="ct">${fmt(byType[t.key]||0)}</div><div class="lb">${t.key}</div></div>`).join('');
}
async function loadDiagnostics(){
  try{
    const d = await (await fetch('/api/diagnostics?minSeverity=warning',{headers:H()})).json();
    const list = d.diagnostics || [];
    $('#f-diag').textContent = `${fmt(d.total ?? list.length)} diagnostics`;
    const el = $('#diaglist');
    el.innerHTML = list.length === 0 ? '<div class="empty">No warnings or errors flagged.</div>' :
      list.slice(0,40).map(x => { const sv=(x.severity||'Info').toLowerCase();
        return `<div class="diag ${sv}"><span class="sv">${escapeHtml(x.severity||'')}</span> <span class="cd">${escapeHtml(x.code||'')}</span><div class="ms">${escapeHtml(x.message||'')}</div></div>`;
      }).join('');
  }catch(e){ $('#f-diag').textContent='— diagnostics'; $('#diaglist').innerHTML='<div class="empty">Diagnostics unavailable.</div>'; }
}

/* ===================== GRAPH ENGINE ===================== */
const G = (() => {
  const canvas = $('#graph'), ctx = canvas.getContext('2d'), tip = $('#tip');
  let W=0, H2=0, DPR = Math.min(2, window.devicePixelRatio||1);
  const nodes = new Map();           // id -> node
  let links = [];                    // {s,t}
  const modules = new Map();         // name -> {color, nodes:[], cx,cy,r}
  let view = { x:0, y:0, k:1 };
  let frozen = true, hover = null;

  function resize(){
    const r = canvas.getBoundingClientRect(); W=r.width; H2=r.height;
    canvas.width = W*DPR; canvas.height = H2*DPR;
    draw();
  }
  function moduleOf(n){
    const f = n.filePath||''; if(!f){ return n.type||'misc'; }
    const parts = f.split(/[\\/]/).filter(Boolean);
    const helix = parts.findIndex(p=>/^(feature|foundation|project)$/i.test(p));
    if(helix>=0) return (parts[helix].charAt(0).toUpperCase()+parts[helix].slice(1).toLowerCase()) + (parts[helix+1]?'/'+parts[helix+1]:'');
    const si = parts.findIndex(p=>p.toLowerCase()==='src');
    if(si>=0 && parts[si+1]) return parts[si+1];
    return parts[0]||(n.type||'misc');
  }
  function colorFor(mod){
    let m = modules.get(mod);
    if(!m){ m = { color: PALETTE[modules.size % PALETTE.length], nodes:[] }; modules.set(mod, m); }
    return m.color;
  }

  function setData(rawNodes, rawEdges, cap=520){
    nodes.clear(); modules.clear(); links = [];
    const capped = rawNodes.slice(0, cap);
    const ids = new Set();
    for(const n of capped){
      const id = n.id||n.Id; if(!id||nodes.has(id)) continue;
      if(INTERACTION.has(n.type||n.Type||'')) continue; // records belong in the Journal, not the map
      const mod = moduleOf({filePath:n.filePath||n.FilePath, type:n.type||n.Type});
      colorFor(mod);
      const node = { id, name:n.name||n.Name||id, type:n.type||n.Type||'', filePath:n.filePath||n.FilePath||'',
        startLine:n.startLine||n.StartLine, summary:n.summary||n.Summary, mod, deg:0,
        x:(Math.random()-0.5)*600, y:(Math.random()-0.5)*600 };
      node.color = modules.get(mod).color; modules.get(mod).nodes.push(node);
      nodes.set(id, node); ids.add(id);
    }
    for(const e of rawEdges){
      const s=e.sourceId||e.SourceId, t=e.targetId||e.TargetId;
      if(ids.has(s)&&ids.has(t)&&s!==t){ links.push({s,t}); nodes.get(s).deg++; nodes.get(t).deg++; }
    }
    layout();
  }

  function layout(){
    const arr = [...nodes.values()];
    if(arr.length===0){ draw(); return; }
    const mods = [...modules.keys()];
    const ring = 360 + arr.length*0.7;
    const mc = new Map();
    mods.forEach((m,i)=>{ const a = i/mods.length*Math.PI*2; mc.set(m,{x:Math.cos(a)*ring, y:Math.sin(a)*ring}); });
    const lk = links.map(l=>({source:l.s, target:l.t}));
    const sim = d3.forceSimulation(arr)
      .force('charge', d3.forceManyBody().strength(-26).distanceMax(260))
      .force('link', d3.forceLink(lk).id(d=>d.id).distance(34).strength(.25))
      .force('x', d3.forceX(d=>mc.get(d.mod).x).strength(.12))
      .force('y', d3.forceY(d=>mc.get(d.mod).y).strength(.12))
      .force('collide', d3.forceCollide(7))
      .stop();
    const ticks = Math.min(300, 90 + arr.length);
    for(let i=0;i<ticks;i++) sim.tick();
    frozen = true;
    for(const n of arr) n.r = 3 + Math.min(7, Math.sqrt(n.deg));
    for(const [name,m] of modules){
      if(m.nodes.length===0){ m.cx=0;m.cy=0;m.r=0; continue; }
      let cx=0,cy=0; for(const n of m.nodes){cx+=n.x;cy+=n.y;} cx/=m.nodes.length; cy/=m.nodes.length;
      let rad=0; for(const n of m.nodes) rad=Math.max(rad, Math.hypot(n.x-cx,n.y-cy)); m.cx=cx;m.cy=cy;m.r=rad+18;
    }
    fit(); renderLegend(); $('#f-shown').textContent = `${arr.length} shown`;
    $('#legend').style.display=''; $('#zoomlabel').style.display=''; $('#scale').style.display='';
    $('#scalen').textContent = arr.length;
    $('#emptyStage').style.display = arr.length ? 'none' : '';
  }

  function fit(){
    const arr=[...nodes.values()]; if(!arr.length) return;
    if(!W){ const r=canvas.getBoundingClientRect(); W=r.width; H2=r.height; canvas.width=W*DPR; canvas.height=H2*DPR; }
    let minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity;
    for(const n of arr){ minX=Math.min(minX,n.x);minY=Math.min(minY,n.y);maxX=Math.max(maxX,n.x);maxY=Math.max(maxY,n.y); }
    const pad=90, gw=(maxX-minX)||1, gh=(maxY-minY)||1;
    view.k = Math.min((W-pad)/gw, (H2-pad)/gh, 1.45);
    view.x = W/2 - (minX+maxX)/2*view.k; view.y = H2/2 - (minY+maxY)/2*view.k;
    draw();
  }
  const sx = x => x*view.k + view.x, sy = y => y*view.k + view.y;
  const ux = px => (px - view.x)/view.k, uy = py => (py - view.y)/view.k;

  function draw(){
    ctx.setTransform(DPR,0,0,DPR,0,0);
    ctx.clearRect(0,0,W,H2);
    const arr=[...nodes.values()]; if(!arr.length) return;
    const k=view.k;
    const vis = {x0:ux(0),y0:uy(0),x1:ux(W),y1:uy(H2)};
    // territories (soft fills + labels)
    for(const [name,m] of modules){
      if(!m.r) continue;
      ctx.beginPath(); ctx.arc(sx(m.cx),sy(m.cy),m.r*k,0,Math.PI*2);
      ctx.fillStyle = hexA(m.color, .07); ctx.fill();
      ctx.lineWidth=1; ctx.strokeStyle=hexA(m.color,.32); ctx.stroke();
      if(k < 1.7 && m.nodes.length>=2){
        ctx.font='italic 500 14px Fraunces, serif'; ctx.fillStyle=m.color; ctx.textAlign='center';
        ctx.fillText(name, sx(m.cx), sy(m.cy)-m.r*k-7);
        ctx.font='10px "Spline Sans Mono", monospace'; ctx.fillStyle='#4a463a';
        ctx.fillText(m.nodes.length+' nodes', sx(m.cx), sy(m.cy)-m.r*k+7);
      }
    }
    // edges (culled, faint)
    ctx.lineWidth=Math.min(1,.6*k); ctx.strokeStyle='rgba(74,70,58,.30)';
    ctx.beginPath();
    for(const l of links){
      const a=nodes.get(l.s),b=nodes.get(l.t); if(!a||!b) continue;
      if(Math.max(a.x,b.x)<vis.x0||Math.min(a.x,b.x)>vis.x1||Math.max(a.y,b.y)<vis.y0||Math.min(a.y,b.y)>vis.y1) continue;
      ctx.moveTo(sx(a.x),sy(a.y)); ctx.lineTo(sx(b.x),sy(b.y));
    }
    ctx.stroke();
    // nodes (culled)
    for(const n of arr){
      if(n.x<vis.x0-20||n.x>vis.x1+20||n.y<vis.y0-20||n.y>vis.y1+20) continue;
      const r=n.r*Math.max(.7,Math.min(k,1.8));
      ctx.beginPath(); ctx.arc(sx(n.x),sy(n.y),r,0,Math.PI*2);
      ctx.fillStyle = n===hover ? '#1c1a14' : n.color; ctx.fill();
      ctx.lineWidth=1; ctx.strokeStyle='#efe7d6'; ctx.stroke();
    }
    // labels at high zoom (LOD), only for bigger/near nodes
    if(k >= 1.7){
      ctx.font='11px "Spline Sans Mono", monospace'; ctx.fillStyle='#1c1a14'; ctx.textAlign='left';
      for(const n of arr){
        if(n.deg<3 && k<2.6) continue;
        if(n.x<vis.x0||n.x>vis.x1||n.y<vis.y0||n.y>vis.y1) continue;
        ctx.fillText(n.name, sx(n.x)+n.r+3, sy(n.y)+3);
      }
    }
    $('#zoomlabel').textContent = k<1.7 ? 'territories · modules' : 'symbols · labelled';
  }
  function hexA(hex,a){ const n=parseInt(hex.slice(1),16); return `rgba(${n>>16&255},${n>>8&255},${n&255},${a})`; }

  function renderLegend(){
    const top=[...modules.entries()].filter(m=>m[1].nodes.length).sort((a,b)=>b[1].nodes.length-a[1].nodes.length).slice(0,8);
    $('#legend').innerHTML = top.map(([n,m])=>`<div class="row"><i style="background:${m.color}"></i>${escapeHtml(n)} <span style="margin-left:auto;color:var(--ink-2)">${m.nodes.length}</span></div>`).join('');
  }

  function nodeAt(px,py){
    let best=null, bd=14*14;
    for(const n of nodes.values()){
      const dx=sx(n.x)-px, dy=sy(n.y)-py, d=dx*dx+dy*dy;
      if(d<bd){ bd=d; best=n; }
    }
    return best;
  }

  // interaction
  let dragging=false, moved=false, lx=0, ly=0;
  canvas.addEventListener('mousedown', e=>{ dragging=true; moved=false; lx=e.clientX; ly=e.clientY; canvas.classList.add('drag'); });
  window.addEventListener('mouseup', ()=>{ dragging=false; canvas.classList.remove('drag'); });
  canvas.addEventListener('mousemove', e=>{
    const r=canvas.getBoundingClientRect(), px=e.clientX-r.left, py=e.clientY-r.top;
    if(dragging){ moved=true; view.x+=e.clientX-lx; view.y+=e.clientY-ly; lx=e.clientX; ly=e.clientY; draw(); return; }
    const n=nodeAt(px,py);
    if(n!==hover){ hover=n; draw();
      if(n){ tip.style.opacity=1; tip.style.left=px+'px'; tip.style.top=py+'px'; tip.innerHTML=`<span class="ty">${escapeHtml(n.type)}</span> ${escapeHtml(n.name)}`; }
      else tip.style.opacity=0;
    } else if(n){ tip.style.left=px+'px'; tip.style.top=py+'px'; }
  });
  canvas.addEventListener('mouseleave', ()=>{ hover=null; tip.style.opacity=0; draw(); });
  canvas.addEventListener('click', e=>{
    if(moved) return;
    const r=canvas.getBoundingClientRect(); const n=nodeAt(e.clientX-r.left,e.clientY-r.top);
    if(n) pickNode(n);
  });
  canvas.addEventListener('dblclick', e=>{
    const r=canvas.getBoundingClientRect(); const n=nodeAt(e.clientX-r.left,e.clientY-r.top);
    if(n) expand(n);
  });
  canvas.addEventListener('wheel', e=>{
    e.preventDefault();
    const r=canvas.getBoundingClientRect(), px=e.clientX-r.left, py=e.clientY-r.top;
    const f=Math.exp(-e.deltaY*0.0012), nk=Math.max(.25,Math.min(6,view.k*f));
    view.x = px-(px-view.x)*(nk/view.k); view.y = py-(py-view.y)*(nk/view.k); view.k=nk; draw();
  }, {passive:false});
  $('#zin').onclick=()=>zoomBy(1.3); $('#zout').onclick=()=>zoomBy(1/1.3); $('#zfit').onclick=fit;
  function zoomBy(f){ const nk=Math.max(.25,Math.min(6,view.k*f)); view.x=W/2-(W/2-view.x)*(nk/view.k); view.y=H2/2-(H2/2-view.y)*(nk/view.k); view.k=nk; draw(); }

  new ResizeObserver(resize).observe(canvas);
  return { setData, addEdgesNodes:(n,e)=>setData([...[...nodes.values()],...n], [...links.map(l=>({sourceId:l.s,targetId:l.t})),...e]), nodes, fit };
})();

/* ===================== DATA LOADING ===================== */
function setLoading(on,msg){ const l=$('#loading'); l.textContent=msg||'charting…'; l.style.opacity=on?1:0; }

let searchTimer;
$('#q').addEventListener('input', e => {
  clearTimeout(searchTimer); const q=e.target.value.trim();
  if(q.length<2) return; searchTimer=setTimeout(()=>chart(q),260);
});
$('#q').addEventListener('keydown', e=>{ if(e.key==='Enter'){ clearTimeout(searchTimer); const q=e.target.value.trim(); if(q) chart(q); }});

async function chart(q){
  setLoading(true,'searching…');
  try{
    const hits = await (await fetch(`/api/search?q=${encodeURIComponent(q)}&limit=80`,{headers:H()})).json();
    const items = Array.isArray(hits)?hits:[];
    if(items.length===0){ setLoading(false); $('#es-title').textContent='No territory found'; $('#es-sub').textContent=`Nothing matched “${q}”.`; $('#emptyStage').style.display=''; return; }
    const seeds = items.map(r=>(r.node||r.Node||r).id||(r.node||r.Node||r).Id).filter(Boolean).slice(0,60);
    setLoading(true,'charting territory…');
    const sg = await (await fetch(`/api/subgraph?seeds=${encodeURIComponent(seeds.join(','))}&hops=1`,{headers:H()})).json();
    G.setData(sg.nodes||sg.Nodes||[], sg.edges||sg.Edges||[]);
    setLoading(false);
  }catch(e){ setLoading(false); console.error(e); }
}
async function expand(n){
  setLoading(true,'expanding…');
  try{
    const sg = await (await fetch(`/api/subgraph?seeds=${encodeURIComponent(n.id)}&hops=1`,{headers:H()})).json();
    const newN=[...G.nodes.values()].map(x=>({id:x.id,name:x.name,type:x.type,filePath:x.filePath,startLine:x.startLine,summary:x.summary}));
    G.addEdgesNodes([...(sg.nodes||sg.Nodes||[])], (sg.edges||sg.Edges||[]));
    setLoading(false);
  }catch(e){ setLoading(false); }
}

function shortLoc(n){ const f=n.filePath||''; const base=f?f.split(/[\\/]/).pop():n.id; return n.startLine?`${base}:${n.startLine}`:base; }

// Single entry for graph-node clicks: routes to trace target when tracing, else inspect.
function pickNode(n){
  if(window.__traceMode){ runTrace(window.__traceFrom, n); return; }
  inspect(n);
}

function inspect(n){
  window.__sel = n;
  $('#insp').innerHTML = `
    <div class="kind">${escapeHtml(n.type||'node')} · ${escapeHtml(n.mod||'')}</div>
    <h2>${escapeHtml(n.name||'—')}</h2>
    <div class="loc">${escapeHtml(shortLoc(n))}</div>
    ${ n.summary ? `<div class="sum">${escapeHtml(n.summary)}</div>` : '' }
    <div class="act">
      <button id="b-exp" data-act="expand">⟲ expand</button>
      <button id="b-ref" data-act="refs">↳ references</button>
      <button id="b-trc" data-act="trace">⤳ trace</button>
      <button id="b-cap" data-act="capsule">▢ capsule</button>
    </div>
    <div class="detail" id="detail">
      <div class="grp">Connections</div>
      <div class="loc" style="color:var(--ink-3)">${n.deg||0} edge(s) in the current view. Use <b>references</b> for the full picture.</div>
    </div>`;
}

// Load a node into the inspector by id; pull it into the graph first if it isn't charted yet.
async function focusNode(id, meta){
  if(G.nodes.has(id)){ inspect(G.nodes.get(id)); return; }
  setLoading(true,'loading node…');
  try{
    const sg = await (await fetch(`/api/subgraph?seeds=${encodeURIComponent(id)}&hops=1`,{headers:H()})).json();
    G.addEdgesNodes([...(sg.nodes||sg.Nodes||[])], (sg.edges||sg.Edges||[]));
  }catch(e){}
  setLoading(false);
  inspect(G.nodes.get(id) || { id, name:meta?.name||id, type:meta?.type||'', filePath:meta?.filePath, startLine:meta?.startLine, summary:meta?.summary, deg:0 });
}

// Index-based navigation targets — avoids fragile JSON-in-onclick escaping.
let _navT = [];
function goNav(i){ const x=_navT[i]; if(x) focusNode(x.id, x); }
async function loadReferences(n){
  const d=$('#detail'); d.innerHTML='<div class="spin">Loading references…</div>';
  try{
    const r = await (await fetch(`/api/node/references?id=${encodeURIComponent(n.id)}`,{headers:H()})).json();
    const inc=r.incoming||[], out=r.outgoing||[];
    _navT = [...inc, ...out];
    const row = (x,i) => `<button class="ref" style="border-left-color:var(--teal)" data-act="nav" data-i="${i}">
      <div class="rel">${escapeHtml(x.relation||'')}</div><div class="rn">${escapeHtml(x.name||x.id)}</div><div class="rt">${escapeHtml(x.type||'')}</div></button>`;
    d.innerHTML = `
      <div class="grp">Incoming <span class="n">${inc.length}</span></div>
      ${inc.length? inc.map((x,i)=>row(x,i)).join('') : '<div class="loc" style="color:var(--ink-3)">No incoming references.</div>'}
      <div class="grp">Outgoing <span class="n">${out.length}</span></div>
      ${out.length? out.map((x,i)=>row(x,inc.length+i)).join('') : '<div class="loc" style="color:var(--ink-3)">No outgoing references.</div>'}`;
  }catch(e){ d.innerHTML='<div class="loc" style="color:var(--ink-3)">References unavailable.</div>'; }
}

function armTrace(n){
  window.__traceFrom = n; window.__traceMode = true;
  document.getElementById('b-trc')?.classList.add('on');
  $('#detail').innerHTML = `<div class="grp">Trace path</div>
    <div class="hint">From <b>${escapeHtml(n.name||n.id)}</b> — now click any node in the map to trace the shortest connection. <a href="#" data-act="cancel-trace" style="color:var(--ink-2)">cancel</a></div>`;
}
function cancelTrace(){ window.__traceMode=false; document.getElementById('b-trc')?.classList.remove('on'); if(window.__sel) inspect(window.__sel); }
async function runTrace(from, to){
  window.__traceMode=false; document.getElementById('b-trc')?.classList.remove('on');
  if(!from || !to || from.id===to.id){ if(window.__sel) inspect(window.__sel); return; }
  const d=$('#detail'); d.innerHTML=`<div class="grp">Trace path</div><div class="spin">Finding path from <b>${escapeHtml(from.name||from.id)}</b>…</div>`;
  // Path search is BFS over a dense graph — cap hops and abort gracefully so the UI never hangs.
  const ac=new AbortController(); const to_=setTimeout(()=>ac.abort(), 12000);
  try{
    const r = await (await fetch(`/api/path?from=${encodeURIComponent(from.id)}&to=${encodeURIComponent(to.id)}&maxHops=2`,{headers:H(),signal:ac.signal})).json();
    clearTimeout(to_);
    if(!r.found){ d.innerHTML=`<div class="grp">Trace path</div><div class="loc" style="color:var(--ink-3)">${escapeHtml(r.message||'No direct path within 2 hops — these symbols connect only indirectly.')}</div>`; return; }
    const arrow = s => s.direction==null?'◉':(s.direction==='forward'?'→':'←');
    _navT = (r.steps||[]).slice();
    d.innerHTML = `<div class="grp">Path <span class="n">${r.hops} hop${r.hops===1?'':'s'}</span></div>` +
      (r.steps||[]).map((s,i)=>`<div class="step" data-act="nav" data-i="${i}">
        <div class="sd">${arrow(s)} ${escapeHtml(s.relation||'start')}</div><div class="sn">${escapeHtml(s.name||s.id)}</div><div class="st-t">${escapeHtml(s.type||'')}</div></div>`).join('');
  }catch(e){ clearTimeout(to_);
    d.innerHTML = e.name==='AbortError'
      ? '<div class="grp">Trace path</div><div class="loc" style="color:var(--ink-3)">Path search timed out — these nodes are likely far apart. Try tracing between closer symbols.</div>'
      : '<div class="loc" style="color:var(--ink-3)">Trace failed.</div>'; }
}

async function loadCapsule(n){
  const d=$('#detail'); d.innerHTML='<div class="spin">Synthesizing capsule…</div>';
  try{
    // Hops=1 keeps capsules focused — hub nodes at hops=2 synthesize megabytes of markdown.
    const r = await (await fetch('/api/capsule',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:JSON.stringify({Query:n.name||n.id, Hops:1})})).json();
    const md = r.markdown||r.Markdown||''; window.__capmd = md;
    const big = md.length > 24000;
    const shown = big ? md.slice(0,24000) : md;
    d.innerHTML = `<div class="grp">Context capsule <span class="n">${r.nodeCount??r.NodeCount??0} nodes</span></div>
      ${big?`<div class="loc" style="color:var(--ink-3);margin-bottom:6px">Large capsule (${(md.length/1024|0)} KB) — preview truncated; copy gives the full text.</div>`:''}
      <div class="capsule"><pre id="capmd">${escapeHtml(shown)}${big?'\n\n… (truncated)':''}</pre>
      <button class="copybtn" data-act="copy" data-src="capmd">copy markdown</button></div>`;
  }catch(e){ d.innerHTML='<div class="loc" style="color:var(--ink-3)">Capsule synthesis failed.</div>'; }
}

/* ===================== TOAST ===================== */
let _toastT;
function toast(msg, ms=2400){ const t=$('#toast'); t.textContent=msg; t.classList.add('show'); clearTimeout(_toastT); _toastT=setTimeout(()=>t.classList.remove('show'), ms); }

/* ===================== PROJECT SWITCHER ===================== */
function renderProjMenu(){
  const m=$('#projmenu');
  const items = (state.projects||[]).map(p=>{
    const nm=p.name||p.Name||p; const sel=nm===state.project?'sel':'';
    return `<div class="pm-i ${sel}" data-p="${escapeHtml(nm)}">${escapeHtml(nm)}</div>`;
  }).join('') || '<div class="pm-i">No projects</div>';
  m.innerHTML = `<div class="pm-h">Projects</div>${items}<div class="pm-sep"></div>` +
    `<div class="pm-act" id="pm-settings">⚙ Manage projects<span class="sub">add · semantic · plugins in Settings</span></div>`;
  m.querySelectorAll('.pm-i[data-p]').forEach(el=>el.onclick=()=>switchProject(el.getAttribute('data-p')));
  const sb=m.querySelector('#pm-settings'); if(sb) sb.onclick=e=>{ e.stopPropagation(); m.classList.remove('open'); openStation('settings'); };
}
$('#proj').onclick = e=>{ e.stopPropagation(); const m=$('#projmenu'); const open=!m.classList.contains('open'); if(open) renderProjMenu(); m.classList.toggle('open', open); };
document.addEventListener('click', ()=>$('#projmenu').classList.remove('open'));
function switchProject(name){
  if(!name || name===state.project){ $('#projmenu').classList.remove('open'); return; }
  state.project = name; $('#proj-name').textContent = name; $('#projmenu').classList.remove('open');
  G.setData([],[]); $('#emptyStage').style.display=''; $('#es-title').textContent='Atlas'; $('#q').value='';
  $('#insp').innerHTML = `<div class="kind">inspector</div><div class="empty">Search for a symbol or click a node to inspect it.</div><div class="lbl">Diagnostics</div><div id="diaglist"><div class="empty">Loading…</div></div>`;
  loadStats(); loadDiagnostics();
  if($('#station').classList.contains('open')) openStation(_station);
  toast(`Switched to ${name}`);
}

// Reindex (re-scan source into the graph) the active project. Incremental — only changed files
// are reparsed — but still a long-running scan on big projects; the call resolves when it finishes.
let _indexing=false;
function setReindexBusy(on){
  const b=$('#reidx'); if(!b) return;
  b.classList.toggle('busy', on); $('#reidx-l').textContent = on ? 'Reindexing…' : 'Reindex';
}
async function reindexProject(){
  if(_indexing || !state.project) return;
  _indexing=true; setReindexBusy(true);
  toast(`Reindexing ${state.project}… this can take a while`, 600000);
  try{
    const r=await fetch('/api/index',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:'{}'});
    if(r.status===409){ toast('An index scan is already running for this project.', 4000); }
    else {
      const j=await r.json().catch(()=>({}));
      if(r.ok){
        const st=j.stats||j.Stats||{}; const res=j.result||j.Result||{};
        const scanned=(res.filesScanned??res.FilesScanned);
        toast(`Reindexed ${state.project} — ${fmt(st.totalNodes||0)} nodes${scanned!=null?` · ${fmt(scanned)} files scanned`:''}`, 5000);
        loadStats(); loadDiagnostics();
        if($('#station').classList.contains('open') && (_station==='diag'||_station==='journal'||_station==='settings')) openStation(_station);
      } else toast(typeof j==='string'?j:'Reindex failed', 4000);
    }
  }catch(e){ toast('Reindex failed — is the project reachable?', 4000); }
  _indexing=false; setReindexBusy(false);
}
$('#reidx').onclick = reindexProject;

/* ===================== STATIONS ===================== */
let _station='atlas';
document.querySelectorAll('.rail .st').forEach(el=>{
  el.onclick=()=>{ const v=el.getAttribute('data-view'); v==='atlas' ? closeStation() : openStation(v); };
});
$('#st-close').onclick=closeStation;
function setRail(v){ document.querySelectorAll('.rail .st').forEach(s=>s.classList.toggle('active', s.getAttribute('data-view')===v)); }
function closeStation(){ _station='atlas'; setRail('atlas'); $('#station').classList.remove('open'); }
function openStation(v){
  _station=v; setRail(v); const st=$('#station'); st.classList.add('open');
  const body=$('#st-body');
  if(v==='hunt'){ $('#st-title').textContent='Hunt'; $('#st-sub').textContent='full-text search across the graph'; renderHunt(body); }
  else if(v==='capsule'){ $('#st-title').textContent='Capsule'; $('#st-sub').textContent='synthesize token-optimised context'; renderCapsuleStation(body); }
  else if(v==='diag'){ $('#st-title').textContent='Diagnostics'; $('#st-sub').textContent='graph integrity findings'; renderDiagStation(body); }
  else if(v==='insights'){ $('#st-title').textContent='Insights'; $('#st-sub').textContent='hotspots · clusters · surprising links'; renderInsights(body); }
  else if(v==='journal'){ $('#st-title').textContent='Journal'; $('#st-sub').textContent='decisions · milestones · tasks · questions'; renderJournal(body); }
  else if(v==='ask'){ $('#st-title').textContent='Ask'; $('#st-sub').textContent='chat with the graph · grounded by Ollama'; renderAsk(body); }
  else if(v==='settings'){ $('#st-title').textContent='Settings'; $('#st-sub').textContent='projects · indexing · plugins'; renderSettings(body); }
}

/* --- Hunt --- */
let _huntT=[], _huntType='';
function renderHunt(body){
  const types=Object.keys(state.byType||{}).sort((a,b)=>(state.byType[b]||0)-(state.byType[a]||0)).slice(0,10);
  body.innerHTML = `
    <div class="sin"><input id="hunt-q" type="text" placeholder="Search symbols, methods, docs…" autocomplete="off"><button id="hunt-go">Search</button></div>
    <div class="chips" id="hunt-chips">
      <span class="chip ${_huntType===''?'on':''}" data-t="">all</span>
      ${types.map(t=>`<span class="chip ${_huntType===t?'on':''}" data-t="${escapeHtml(t)}">${escapeHtml(t)} <span style="opacity:.6">${state.byType[t]}</span></span>`).join('')}
    </div>
    <div id="hunt-res"><div class="sempty">Type a query and hit Search to hunt the graph.</div></div>`;
  body.querySelector('#hunt-go').onclick=runHunt;
  body.querySelector('#hunt-q').onkeydown=e=>{ if(e.key==='Enter') runHunt(); };
  body.querySelectorAll('#hunt-chips .chip').forEach(c=>c.onclick=()=>{ _huntType=c.getAttribute('data-t'); body.querySelectorAll('.chip').forEach(x=>x.classList.remove('on')); c.classList.add('on'); runHunt(); });
  setTimeout(()=>body.querySelector('#hunt-q')?.focus(), 40);
}
async function runHunt(){
  const q=$('#hunt-q').value.trim(); const res=$('#hunt-res'); if(q.length<2){ res.innerHTML='<div class="sempty">Enter at least 2 characters.</div>'; return; }
  res.innerHTML='<div class="sempty">Hunting…</div>';
  try{
    const url=`/api/search?q=${encodeURIComponent(q)}&limit=60${_huntType?`&type=${encodeURIComponent(_huntType)}`:''}`;
    const hits=await (await fetch(url,{headers:H()})).json();
    const items=(Array.isArray(hits)?hits:[]).map(r=>r.node||r.Node||r);
    _huntT=items;
    res.innerHTML = items.length===0 ? '<div class="sempty">No matches.</div>' :
      items.map((n,i)=>{ const f=n.filePath||n.FilePath||''; const base=f?f.split(/[\\/]/).pop():''; const ln=n.startLine||n.StartLine;
        return `<div class="hitrow" style="border-left-color:var(--teal)" data-act="hunt-pick" data-i="${i}">
          <div><div class="hn">${escapeHtml(n.name||n.Name||n.id||n.Id)}</div><div class="ht">${escapeHtml(n.type||n.Type||'')}</div></div>
          <div class="hl">${escapeHtml(base)}${ln?':'+ln:''}</div></div>`; }).join('');
  }catch(e){ res.innerHTML='<div class="sempty">Search failed.</div>'; }
}
function huntPick(i){ const n=_huntT[i]; if(!n) return; const id=n.id||n.Id; closeStation(); focusNode(id, {name:n.name||n.Name, type:n.type||n.Type, filePath:n.filePath||n.FilePath, startLine:n.startLine||n.StartLine, summary:n.summary||n.Summary}); }

/* --- Insights (whole-graph topology: hotspots · clusters · surprising connections) --- */
let _insTab='hotspots', _insMode='modularity', _insNodes=[], _insPairs=[];
function insNodeIx(n){ _insNodes.push(n); return _insNodes.length-1; }
function insWireNodes(scope){ scope.querySelectorAll('[data-nix]').forEach(el=>el.onclick=()=>{ const n=_insNodes[+el.getAttribute('data-nix')]; if(n&&n.id){ closeStation(); focusNode(n.id, n); } }); }
function renderInsights(body){
  body.innerHTML = `<div class="chips" id="ins-tabs">
    ${[['hotspots','Hotspots'],['clusters','Clusters'],['surprising','Surprising']].map(([t,l])=>`<span class="chip ${_insTab===t?'on':''}" data-t="${t}">${l}</span>`).join('')}
  </div><div id="ins-body"><div class="sempty">Loading…</div></div>`;
  body.querySelectorAll('#ins-tabs .chip').forEach(c=>c.onclick=()=>{ _insTab=c.getAttribute('data-t'); renderInsights(body); });
  const b=body.querySelector('#ins-body');
  ({hotspots:insHotspots, clusters:insClusters, surprising:insSurprising}[_insTab]||insHotspots)(b);
}
async function insHotspots(b){
  _insNodes=[]; b.innerHTML='<div class="sempty">Ranking change-risk hotspots by betweenness centrality…</div>';
  try{
    const r=await (await fetch('/api/insights/hotspots?limit=20',{headers:H()})).json();
    const hs=r.hotspots||[];
    if(!hs.length){ b.innerHTML='<div class="sempty">No coupling edges to rank — index a project with references/calls first.</div>'; return; }
    b.innerHTML=`<div class="grp">Top ${hs.length} by betweenness centrality — the nodes with the widest change blast radius.</div>`+
      hs.map(h=>{ const base=(h.filePath||'').split(/[\\/]/).pop(); const ix=insNodeIx({id:h.id,name:h.name,type:h.type,filePath:h.filePath,startLine:h.startLine});
        return `<div class="hitrow" data-nix="${ix}"><div><div class="hn">${escapeHtml(h.name)}</div><div class="ht">${escapeHtml(h.type)}</div></div>
          <div class="hl">btw ${h.betweenness} · deg ${h.degree}${base?'<br>'+escapeHtml(base):''}</div></div>`; }).join('');
    insWireNodes(b);
  }catch(e){ b.innerHTML='<div class="sempty">Hotspot analysis failed.</div>'; }
}
async function insClusters(b){
  _insNodes=[]; b.innerHTML=`<div class="chips" style="margin-bottom:8px">${['modularity','components'].map(m=>`<span class="chip ${_insMode===m?'on':''}" data-m="${m}">${m}</span>`).join('')}</div><div id="ins-cl"><div class="sempty">Clustering…</div></div>`;
  b.querySelectorAll('[data-m]').forEach(c=>c.onclick=()=>{ _insMode=c.getAttribute('data-m'); insClusters(b); });
  const cl=b.querySelector('#ins-cl');
  try{
    const r=await (await fetch(`/api/insights/clusters?mode=${_insMode}&maxSmall=15`,{headers:H()})).json();
    if(!r.count){ cl.innerHTML='<div class="sempty">Empty graph — nothing to cluster.</div>'; return; }
    const note=_insMode==='components'?' — small clusters are isolated / likely-dead modules':'';
    cl.innerHTML=`<div class="grp">${r.count} ${_insMode} cluster(s); largest = ${r.largest} nodes. Smallest first${note}.</div>`+
      (r.clusters||[]).map(c=>{ const tags=(c.members||[]).map(m=>{ const ix=insNodeIx({id:m.id,name:m.name,type:m.type,filePath:m.filePath}); return `<span class="inode" data-nix="${ix}">${escapeHtml(m.name)}</span>`; }).join('');
        return `<div class="hitrow" style="display:block"><div class="hn">[${c.size} node${c.size===1?'':'s'}]${c.more?` <span style="opacity:.6">+${c.more} more</span>`:''}</div><div style="margin-top:5px">${tags}</div></div>`; }).join('');
    insWireNodes(cl);
  }catch(e){ cl.innerHTML='<div class="sempty">Cluster analysis failed.</div>'; }
}
async function insSurprising(b){
  _insNodes=[]; _insPairs=[]; b.innerHTML='<div class="sempty">Finding embedding-similar pairs with no direct edge…</div>';
  try{
    const r=await (await fetch('/api/surprising-connections',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:JSON.stringify({MinSimilarity:0.85,Limit:25})})).json();
    const pairs=r.pairs||[];
    if(!pairs.length){ b.innerHTML=`<div class="sempty">${escapeHtml(r.note||'No surprising connections found.')}</div>`; return; }
    b.innerHTML=`<div class="grp">${pairs.length} embedding-similar pair(s) with no direct edge — candidate missing links or duplication (inferred, not proven).</div>`+
      pairs.map((p,i)=>{ _insPairs.push(p); const aix=insNodeIx({id:p.sourceId,name:p.sourceName}); const bix=insNodeIx({id:p.targetId,name:p.targetName});
        return `<div class="hitrow" style="display:block"><div class="hn"><span class="inode" data-nix="${aix}">${escapeHtml(p.sourceName)}</span> ~ <span class="inode" data-nix="${bix}">${escapeHtml(p.targetName)}</span></div>
          <div class="ht">similarity ${Number(p.similarity).toFixed(3)} · <a href="#" class="inx" data-ex="${i}">explain</a></div>
          <div class="smd" id="ins-ex-${i}" style="display:none"></div></div>`; }).join('');
    insWireNodes(b);
    b.querySelectorAll('[data-ex]').forEach(el=>el.onclick=async(ev)=>{ ev.preventDefault(); const i=+el.getAttribute('data-ex'); const p=_insPairs[i]; const box=b.querySelector('#ins-ex-'+i);
      box.style.display='block'; box.textContent='Explaining (local LLM)…';
      try{ const x=await (await fetch('/api/surprising-connections/explain',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:JSON.stringify({SourceId:p.sourceId,TargetId:p.targetId})})).json(); box.innerHTML=mdLite((x.explanation||'No explanation.')+'\n\n_(inferred hypothesis)_'); }
      catch(e){ box.textContent='Explanation failed (is Ollama running?).'; } });
  }catch(e){ b.innerHTML='<div class="sempty">Surprising-connection detection failed.</div>'; }
}

/* --- Capsule station --- */
function renderCapsuleStation(body){
  body.innerHTML = `
    <div class="sin"><input id="cap-q" type="text" placeholder="Describe what you need context for…" autocomplete="off"><button id="cap-go">Synthesize</button></div>
    <div id="cap-out"><div class="sempty">Enter a query to synthesize a context capsule (seeds from the best matches).</div></div>`;
  body.querySelector('#cap-go').onclick=runCapStation;
  body.querySelector('#cap-q').onkeydown=e=>{ if(e.key==='Enter') runCapStation(); };
  setTimeout(()=>body.querySelector('#cap-q')?.focus(), 40);
}
async function runCapStation(){
  const q=$('#cap-q').value.trim(); const out=$('#cap-out'); if(q.length<2){ out.innerHTML='<div class="sempty">Enter at least 2 characters.</div>'; return; }
  out.innerHTML='<div class="sempty">Synthesizing…</div>';
  try{
    const r=await (await fetch('/api/capsule',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:JSON.stringify({Query:q, Hops:1})})).json();
    const md=r.markdown||r.Markdown||''; window.__capmd2=md;
    out.innerHTML = `<div class="grp">${r.nodeCount??r.NodeCount??0} nodes · ${r.edgeCount??r.EdgeCount??0} edges · ${(md.length/1024).toFixed(1)} KB</div>
      <div class="smd"><pre>${escapeHtml(md)}</pre></div>
      <button class="copybtn" style="margin-top:8px" data-act="copy" data-src="capmd2">copy markdown</button>`;
  }catch(e){ out.innerHTML='<div class="sempty">Capsule synthesis failed.</div>'; }
}

/* --- Diagnostics station --- */
let _diagSev='warning';
function renderDiagStation(body){
  body.innerHTML = `
    <div class="chips" id="diag-tabs">
      ${['info','warning','error'].map(s=>`<span class="chip ${_diagSev===s?'on':''}" data-s="${s}">${s}+</span>`).join('')}
    </div>
    <div id="diag-full"><div class="sempty">Loading…</div></div>`;
  body.querySelectorAll('#diag-tabs .chip').forEach(c=>c.onclick=()=>{ _diagSev=c.getAttribute('data-s'); body.querySelectorAll('#diag-tabs .chip').forEach(x=>x.classList.remove('on')); c.classList.add('on'); loadDiagFull(); });
  loadDiagFull();
}
async function loadDiagFull(){
  const el=$('#diag-full'); el.innerHTML='<div class="sempty">Loading…</div>';
  try{
    const d=await (await fetch(`/api/diagnostics?minSeverity=${_diagSev}`,{headers:H()})).json();
    const list=d.diagnostics||[];
    el.innerHTML = `<div class="grp">${fmt(d.total??list.length)} finding(s) at ${_diagSev}+ </div>` + (list.length===0 ? '<div class="sempty">Clean — nothing flagged at this level.</div>' :
      list.map(x=>{ const sv=(x.severity||'Info').toLowerCase(); const node=x.nodeId||x.NodeId;
        return `<div class="diag ${sv}"><span class="sv">${escapeHtml(x.severity||'')}</span> <span class="cd">${escapeHtml(x.code||'')}</span>${node?` <span class="cd" style="opacity:.7">${escapeHtml(node)}</span>`:''}<div class="ms">${escapeHtml(x.message||'')}</div></div>`;
      }).join(''));
  }catch(e){ el.innerHTML='<div class="sempty">Diagnostics unavailable.</div>'; }
}

/* --- Plugins station --- */
let _plugins=[];
function badgeClass(state){ const s=(state||'').toLowerCase(); return s==='active'?'active':s==='failed'?'failed':s==='disabled'?'disabled':'installed'; }
function renderPlugins(body){
  body.innerHTML = `
    <div class="drop" id="pdrop"><b>Drop a plugin .zip here</b> or click to browse — installs inert, you activate it explicitly.
      <input type="file" id="pfile" accept=".zip" style="display:none"></div>
    <div id="plist"><div class="sempty">Loading plugins…</div></div>`;
  const drop=body.querySelector('#pdrop'), file=body.querySelector('#pfile');
  drop.onclick=()=>file.click();
  file.onchange=()=>{ if(file.files[0]) installPlugin(file.files[0]); };
  drop.ondragover=e=>{ e.preventDefault(); drop.style.borderColor='var(--russet)'; };
  drop.ondragleave=()=>drop.style.borderColor='';
  drop.ondrop=e=>{ e.preventDefault(); drop.style.borderColor=''; const f=e.dataTransfer.files[0]; if(f) installPlugin(f); };
  loadPlugins();
}
async function loadPlugins(){
  const el=$('#plist'); el.innerHTML='<div class="sempty">Loading plugins…</div>';
  try{
    const d=await (await fetch('/api/plugins',{headers:H()})).json();
    _plugins=d.plugins||d.Plugins||[];
    el.innerHTML = _plugins.length===0 ? '<div class="sempty">No plugins installed. Drop a .zip above to add one.</div>' :
      _plugins.map((p,i)=>{ const st=p.state||p.State||''; const bc=badgeClass(st); const active=(p.active??p.Active)||st.toLowerCase()==='active';
        const ext=(p.extensions||p.Extensions||[]).join(', ');
        return `<div class="pcard" style="border-left-color:var(--${bc==='active'?'ok':bc==='failed'?'err':bc==='disabled'?'ink-3':'gold'})">
          <div class="ph"><span class="pname">${escapeHtml(p.name||p.Name||p.id)}</span><span class="pver">v${escapeHtml(p.version||p.Version||'?')}</span><span class="pbadge ${bc}" style="margin-left:auto">${escapeHtml(st)}</span></div>
          ${(p.description||p.Description)?`<div class="pdesc">${escapeHtml(p.description||p.Description)}</div>`:''}
          ${ext?`<div class="pext">handles: ${escapeHtml(ext)}</div>`:''}
          ${(p.error||p.Error)?`<div class="pext" style="color:var(--err)">${escapeHtml(p.error||p.Error)}</div>`:''}
          <div class="pacts">
            ${active?`<button data-act="plugin" data-op="deactivate" data-i="${i}">deactivate</button>`:`<button class="primary" data-act="plugin" data-op="activate" data-i="${i}">activate</button>`}
            <button class="danger" data-act="plugin" data-op="uninstall" data-i="${i}">uninstall</button>
          </div></div>`; }).join('');
  }catch(e){ el.innerHTML='<div class="sempty">Could not load plugins.</div>'; }
}
async function installPlugin(f){
  if(!/\.zip$/i.test(f.name)){ toast('Please upload a .zip package'); return; }
  toast('Installing '+f.name+'…', 4000);
  const fd=new FormData(); fd.append('package', f);
  try{
    const r=await fetch('/api/plugins/install',{method:'POST', body:fd});
    const j=await r.json().catch(()=>({}));
    toast(r.ok ? (j.message||'Installed — activate it to load') : (j||'Install failed'), 3200);
  }catch(e){ toast('Install failed'); }
  loadPlugins();
}
async function pluginOp(op, i){
  const p=_plugins[i]; if(!p) return; const id=p.id||p.Id;
  if(op==='uninstall' && !confirm(`Uninstall “${p.name||p.Name||id}”? This removes its files.`)) return;
  toast(`${op}…`);
  try{
    const r = op==='uninstall'
      ? await fetch(`/api/plugins/${encodeURIComponent(id)}`,{method:'DELETE'})
      : await fetch(`/api/plugins/${encodeURIComponent(id)}/${op}`,{method:'POST'});
    const j=await r.json().catch(()=>({}));
    toast(j.message || (r.ok?`${op} ok`:`${op} failed`));
  }catch(e){ toast(`${op} failed`); }
  loadPlugins();
}

/* --- Settings (projects · indexing · plugins) --- */
let _setTab='projects';
const SET_SECTIONS=[
  {t:'projects', label:'Projects', icon:'<rect x="3" y="4" width="8" height="7" rx="1"/><rect x="13" y="9" width="8" height="11" rx="1"/><path d="M7 11v4h6"/>'},
  {t:'ai',       label:'AI',       icon:'<path d="M4 5h16v11H9l-4 4v-4H4z"/><path d="M8.5 10.5h.2 M12 10.5h.2 M15.5 10.5h.2"/>'},
  {t:'plugins',  label:'Plugins',  icon:'<rect x="4" y="4" width="6" height="6" rx="1"/><rect x="13" y="13" width="6" height="6" rx="1"/><path d="M10 7h5v6"/>'},
  {t:'admin',    label:'Admin',    icon:'<path d="M12 3l7 3v5c0 4-3 6.5-7 8-4-1.5-7-4-7-8V6z"/><circle cx="12" cy="10" r="1.8"/><path d="M9 15.5c0-1.6 6-1.6 6 0"/>'}
];
function renderSettings(body){
  body.innerHTML = `<div class="setwrap">
    <nav class="setnav" id="set-nav"><div class="nh">Settings</div>
      ${SET_SECTIONS.map(s=>`<button data-t="${s.t}" class="${_setTab===s.t?'on':''}"><svg viewBox="0 0 24 24">${s.icon}</svg>${s.label}</button>`).join('')}
    </nav>
    <div class="setmain" id="set-body"></div>
  </div>`;
  body.querySelectorAll('#set-nav button').forEach(b=>b.onclick=()=>{ _setTab=b.getAttribute('data-t'); renderSettings(body); });
  const sb=$('#set-body');
  if(_setTab==='plugins') renderPlugins(sb); else if(_setTab==='ai') renderSettingsAi(sb); else if(_setTab==='admin') renderSettingsAdmin(sb); else renderSettingsProjects(sb);
}
/* AI / Ollama settings — edits appsettings.Local.json via loopback-only /api/settings. */
async function renderSettingsAi(c){
  c.innerHTML = `<div class="secthead">AI &amp; tools</div><div id="ai-set"><div class="sempty">Loading…</div></div>`;
  let s;
  try{ const r=await fetch('/api/settings',{headers:H()}); if(!r.ok) throw new Error(r.status); s=await r.json(); }
  catch(e){ $('#ai-set').innerHTML='<div class="sempty">Settings are only editable from the local machine (loopback).</div>'; return; }
  const bool=(v,id)=>`<select id="${id}"><option value="true" ${v?'selected':''}>On</option><option value="false" ${!v?'selected':''}>Off</option></select>`;
  const src=(v)=>`<select id="ai-src"><option value="code" ${v==='code'?'selected':''}>code (recommended)</option><option value="summary" ${v==='summary'?'selected':''}>summary</option></select>`;
  $('#ai-set').innerHTML = `
    <div class="seglbl">Generation model (Ask AI) — Ollama</div>
    <div class="af"><input id="ai-gen-url" value="${escapeHtml(s.semanticAnalyzerUrl||'')}" placeholder="http://localhost:11434"><input id="ai-gen-model" value="${escapeHtml(s.semanticAnalyzerModel||'')}" placeholder="qwen2.5-coder"></div>
    <div class="seglbl" style="margin-top:10px">Embedding model (semantic/hybrid search) — Ollama</div>
    <div class="af"><input id="ai-emb-url" value="${escapeHtml(s.embeddingUrl||'')}" placeholder="http://localhost:11434"><input id="ai-emb-model" value="${escapeHtml(s.embeddingModel||'')}" placeholder="nomic-embed-text"></div>
    <div class="airow"><label>Embedding source</label>${src(s.embeddingSource)}</div>
    <div class="airow"><label>Exact C# resolution (default)</label>${bool(s.semanticCSharp,'ai-sem')}</div>
    <div class="airow"><label>Stream answers</label>${bool(s.streamingAnswers,'ai-stream')}</div>
    <div class="airow"><label>Enrichment batch size</label><input id="ai-bs" type="number" min="1" value="${s.enrichmentBatchSize}"></div>
    <div class="airow"><label>Enrichment parallelism</label><input id="ai-par" type="number" min="1" value="${s.enrichmentMaxParallelism}"></div>
    <div class="airow"><label>Drift interval (s) <span class="seglbl">restart needed</span></label><input id="ai-drift" type="number" min="0" value="${s.driftReconcileIntervalSeconds}"></div>
    <div style="margin-top:12px"><button id="ai-save">Save</button> <span id="ai-status" class="seglbl"></span></div>`;
  $('#ai-save').onclick=async()=>{
    const num=id=>{ const v=parseInt($('#'+id).value,10); return isNaN(v)?null:v; };
    const body={
      semanticAnalyzerUrl:$('#ai-gen-url').value.trim(), semanticAnalyzerModel:$('#ai-gen-model').value.trim(),
      embeddingUrl:$('#ai-emb-url').value.trim(), embeddingModel:$('#ai-emb-model').value.trim(),
      embeddingSource:$('#ai-src').value, semanticCSharp:$('#ai-sem').value==='true',
      streamingAnswers:$('#ai-stream').value==='true', enrichmentBatchSize:num('ai-bs'),
      enrichmentMaxParallelism:num('ai-par'), driftReconcileIntervalSeconds:num('ai-drift')
    };
    $('#ai-status').textContent='Saving…';
    try{
      const r=await fetch('/api/settings',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:JSON.stringify(body)});
      if(r.ok){ toast('AI settings saved'); $('#ai-status').textContent='Saved — applies on the next request (drift needs a restart).'; }
      else { $('#ai-status').textContent = r.status===403 ? 'Blocked (local-only / dev-only).' : ('Error: '+await r.text().catch(()=>r.status)); }
    }catch(e){ $('#ai-status').textContent='Save failed.'; }
  };
}
/* Admin — organizations, users & tokens (B2B SaaS). Ported from the legacy dashboard; API is /api/admin/*. */
let _admOrgs=[];
async function renderSettingsAdmin(c){
  c.innerHTML = `
    <div class="secthead">Organizations</div>
    <div class="af"><input id="adm-org" placeholder="New organization name"><button id="adm-org-add">Create</button></div>
    <div id="adm-orgs"><div class="sempty">Loading…</div></div>
    <div class="secthead" style="margin-top:20px">Users &amp; tokens</div>
    <div class="seglbl">Create a user and generate a Personal Access Token — it is shown once.</div>
    <div class="af" style="flex-wrap:wrap; margin-top:8px">
      <select id="adm-user-org"><option value="">Organization…</option></select>
      <input id="adm-user-name" placeholder="Username (e.g. jdoe)">
      <input id="adm-user-gh" placeholder="GitHub username (optional)">
      <button id="adm-user-add">Generate token</button>
    </div>
    <div id="adm-token" class="admtoken" hidden></div>
    <div class="seglbl" style="margin-top:14px">View users for organization</div>
    <div class="af"><select id="adm-filter-org"><option value="">Select organization…</option></select></div>
    <div id="adm-users"><div class="sempty">Pick an organization.</div></div>`;
  $('#adm-org-add').onclick=admCreateOrg;
  $('#adm-org').onkeydown=e=>{ if(e.key==='Enter') admCreateOrg(); };
  $('#adm-user-add').onclick=admCreateUser;
  $('#adm-filter-org').onchange=e=>admLoadUsers(e.target.value);
  await admLoadOrgs();
}
async function admLoadOrgs(){
  try{ const r=await fetch('/api/admin/orgs'); if(!r.ok) throw new Error(r.status); _admOrgs=await r.json(); }
  catch(e){ $('#adm-orgs').innerHTML='<div class="sempty">Admin API unavailable.</div>'; return; }
  const orgOpts=_admOrgs.map(o=>`<option value="${escapeHtml(o.id)}">${escapeHtml(o.name)}</option>`).join('');
  const fo=$('#adm-filter-org'), fv=fo.value;
  $('#adm-user-org').innerHTML='<option value="">Organization…</option>'+orgOpts;
  fo.innerHTML='<option value="">Select organization…</option>'+orgOpts; if(fv) fo.value=fv;
  $('#adm-orgs').innerHTML = _admOrgs.length
    ? _admOrgs.map(o=>`<div class="admrow"><span>${escapeHtml(o.name)}</span><span class="seglbl">${escapeHtml(o.id)}</span></div>`).join('')
    : '<div class="sempty">No organizations yet.</div>';
}
async function admCreateOrg(){
  const name=$('#adm-org').value.trim(); if(!name) return;
  try{ const r=await fetch('/api/admin/orgs',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})});
    if(r.ok){ $('#adm-org').value=''; toast('Organization created'); admLoadOrgs(); } else toast('Create failed');
  }catch(e){ toast('Create failed'); }
}
async function admCreateUser(){
  const organizationId=$('#adm-user-org').value, username=$('#adm-user-name').value.trim(), githubUsername=$('#adm-user-gh').value.trim();
  if(!organizationId||!username){ toast('Organization and username are required'); return; }
  try{
    const r=await fetch('/api/admin/users',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({organizationId,username,githubUsername})});
    const j=await r.json().catch(()=>({}));
    if(r.ok){
      $('#adm-user-name').value=''; $('#adm-user-gh').value='';
      const t=$('#adm-token'); t.hidden=false;
      t.innerHTML=`<b>Token generated — copy it now, it will not be shown again.</b><input readonly value="${escapeHtml(j.apiToken||'')}" data-act="select-all">`;
      toast('User created');
      if($('#adm-filter-org').value===organizationId) admLoadUsers(organizationId);
    } else toast(typeof j==='string'?j:'Create failed');
  }catch(e){ toast('Create failed'); }
}
async function admLoadUsers(orgId){
  const el=$('#adm-users'); if(!orgId){ el.innerHTML='<div class="sempty">Pick an organization.</div>'; return; }
  el.innerHTML='<div class="sempty">Loading…</div>';
  try{
    const r=await fetch('/api/admin/users/'+encodeURIComponent(orgId)); if(!r.ok) throw new Error(r.status);
    const users=await r.json();
    el.innerHTML = users.length
      ? users.map(u=>`<div class="admrow"><span>${escapeHtml(u.username)}<span class="seglbl"> · ${escapeHtml(u.gitHubUsername||'no github')}</span></span><button class="danger" data-uid="${escapeHtml(u.id)}">delete</button></div>`).join('')
      : '<div class="sempty">No users in this organization.</div>';
    el.querySelectorAll('button[data-uid]').forEach(b=>b.onclick=()=>admDeleteUser(b.getAttribute('data-uid'), orgId));
  }catch(e){ el.innerHTML='<div class="sempty">Failed to load users.</div>'; }
}
async function admDeleteUser(userId, orgId){
  if(!confirm('Delete this user? Their token is revoked immediately.')) return;
  try{ const r=await fetch('/api/admin/users/'+encodeURIComponent(userId),{method:'DELETE'});
    if(r.ok){ toast('User deleted'); admLoadUsers(orgId); } else toast('Delete failed');
  }catch(e){ toast('Delete failed'); }
}
/* Folder browser for the add-project path (loopback + dev-only /api/browse; a 403 just means "type the path"). */
async function openBrowse(){
  let m=$('#browse-modal');
  if(!m){
    m=document.createElement('div'); m.id='browse-modal'; m.className='bmodal';
    m.innerHTML=`<div class="bpanel">
      <div class="bhead"><b>Choose a directory</b><button id="b-close" title="Close">✕</button></div>
      <div class="bpath"><button id="b-up" title="Up one level">↑</button><input id="b-cur" readonly placeholder="drives"></div>
      <div class="blist" id="b-list"></div>
      <div class="bfoot"><span class="seglbl" id="b-sel">None selected</span><button id="b-pick">Use this folder</button></div>
    </div>`;
    document.body.appendChild(m);
    m.onclick=e=>{ if(e.target===m) m.remove(); };
    $('#b-close').onclick=()=>m.remove();
    $('#b-up').onclick=()=>browseGo($('#b-up').dataset.parent||'');
    $('#b-pick').onclick=()=>{ const p=$('#b-cur').value; if(p){ $('#ap-path').value=p; m.remove(); toast('Path set'); } else toast('Open a folder first'); };
  }
  browseGo('');
}
async function browseGo(path){
  const list=$('#b-list'); if(!list) return; list.innerHTML='<div class="sempty">Loading…</div>';
  try{
    const r=await fetch('/api/browse?path='+encodeURIComponent(path));
    if(r.status===403){ list.innerHTML='<div class="sempty">Folder browsing is available only on the local machine (loopback, Development). Type the absolute path instead.</div>'; return; }
    if(!r.ok){ list.innerHTML='<div class="sempty">Access denied or invalid path.</div>'; return; }
    const d=await r.json(); const cur=d.currentPath||'';
    $('#b-cur').value=cur; $('#b-sel').textContent=cur||'None selected';
    $('#b-up').dataset.parent=d.parentPath||''; $('#b-up').disabled=!cur;
    const items = cur ? (d.folders||[]).map(n=>({label:n, path:cur.replace(/[\\/]+$/,'')+'/'+n}))
                      : (d.drives||[]).map(dr=>({label:dr, path:dr}));
    if(!items.length){ list.innerHTML='<div class="sempty">'+(cur?'No subfolders.':'No drives found.')+'</div>'; return; }
    list.innerHTML=items.map(it=>`<div class="bitem" data-p="${escapeHtml(it.path)}"><svg viewBox="0 0 24 24" width="13" height="13"><path d="M3 6h6l2 2h10v11H3z" fill="none" stroke="currentColor" stroke-width="1.4"/></svg>${escapeHtml(it.label)}</div>`).join('');
    list.querySelectorAll('.bitem').forEach(el=>el.onclick=()=>browseGo(el.getAttribute('data-p')));
  }catch(e){ list.innerHTML='<div class="sempty">Browser API failed.</div>'; }
}
function semLabel(v){ return v===true?'Semantic (exact)':v===false?'Syntactic (fast)':'Global default'; }
function renderSettingsProjects(c){
  c.innerHTML = `
    <div class="addprj">
      <div class="secthead" style="margin:0">Add a project</div>
      <div class="seglbl" style="margin-top:2px">Point Shonkor at a source directory to index it into its own graph.</div>
      <div class="af"><input id="ap-name" placeholder="Name (e.g. MyApp)"><input id="ap-path" placeholder="Absolute path to the source root"><button id="ap-browse" class="ghost" title="Browse folders">Browse</button><button id="ap-go">Add</button></div>
    </div>
    <div class="secthead">Projects <span class="seglbl">${(state.projects||[]).length}</span></div>
    <div id="prj-list"><div class="sempty">Loading…</div></div>`;
  c.querySelector('#ap-go').onclick=addProject;
  c.querySelector('#ap-browse').onclick=openBrowse;
  c.querySelector('#ap-path').onkeydown=e=>{ if(e.key==='Enter') addProject(); };
  paintProjects();
}
function paintProjects(){
  const el=$('#prj-list'); if(!el) return;
  const list=state.projects||[];
  el.innerHTML = list.length===0 ? '<div class="sempty">No projects registered yet.</div>' :
    list.map((p,i)=>{ const nm=p.name||p.Name; const path=p.path||p.Path||''; const active=nm===state.activeProject;
      const sem=(p.semanticCSharp ?? p.SemanticCSharp); const isCur=nm===state.project;
      const exts=(p.externalTypePrefixes ?? p.ExternalTypePrefixes ?? []);
      const opt=(val,lbl)=>`<option value="${val}" ${String(sem)===val?'selected':''}>${lbl}</option>`;
      return `<div class="prj ${active?'act':''}">
        <div class="ph"><span class="pn">${escapeHtml(nm)}</span>${active?'<span class="pbadge active">active</span>':isCur?'<span class="pbadge view">viewing</span>':''}</div>
        <div class="pp">${escapeHtml(path)}</div>
        <div class="prow">
          <span class="seglbl">Indexing</span>
          <select data-chg="semantic" data-i="${i}">
            ${opt('null','Global default')}${opt('true','Semantic (exact)')}${opt('false','Syntactic (fast)')}
          </select>
          <div class="pbtns">
            ${active?'':`<button data-act="proj-active" data-i="${i}">set active</button>`}
            ${isCur?'':`<button data-act="proj-view" data-i="${i}">view</button>`}
            <button class="danger" data-act="proj-del" data-i="${i}">delete</button>
          </div>
        </div>
        <div class="prow extrow">
          <span class="seglbl" title="Namespaces to treat as external/third-party so config references to them are Info, not Warning. Comma-separated; a trailing dot scopes to a namespace, e.g. Dianoga.">External types</span>
          <input class="extp" id="extp-${i}" placeholder="e.g. Dianoga., Acme.Modules." value="${escapeHtml(exts.join(', '))}">
          <button class="extsave" data-act="ext-save" data-i="${i}">save</button>
        </div>
      </div>`; }).join('');
}
async function refreshProjects(){
  try{ const p=await (await fetch('/api/projects')).json();
    state.projects=p.projects||p.Projects||[]; state.activeProject=p.activeProject||p.ActiveProject||'';
  }catch(e){}
}
async function addProject(){
  const name=$('#ap-name').value.trim(), path=$('#ap-path').value.trim();
  if(!name || !path){ toast('Name and path are required'); return; }
  toast(`Adding ${name}…`);
  try{
    const r=await fetch('/api/projects',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({Name:name, Path:path})});
    const j=await r.json().catch(()=>({}));
    if(r.ok){ toast(j.message||`Added ${name}`); await refreshProjects(); renderProjMenu(); paintProjects();
      $('#ap-name').value=''; $('#ap-path').value=''; }
    else toast(typeof j==='string'?j:'Add failed — check the path exists', 4000);
  }catch(e){ toast('Add failed'); }
}
async function setSemantic(i, value){
  const p=(state.projects||[])[i]; if(!p) return; const nm=p.name||p.Name;
  const body = value==='true' ? {SemanticCSharp:true} : value==='false' ? {SemanticCSharp:false} : {SemanticCSharp:null};
  try{
    const r=await fetch(`/api/projects/${encodeURIComponent(nm)}/semantic`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    const j=await r.json().catch(()=>({}));
    if(r.ok){ if('semanticCSharp' in p) p.semanticCSharp=body.SemanticCSharp; p.SemanticCSharp=body.SemanticCSharp;
      toast(j.message||`${nm}: ${semLabel(body.SemanticCSharp)} — reindex to apply`, 4500); }
    else toast('Update failed');
  }catch(e){ toast('Update failed'); }
}
async function setExternalTypes(i){
  const p=(state.projects||[])[i]; if(!p) return; const nm=p.name||p.Name;
  const raw=($('#extp-'+i)?.value||'');
  const prefixes=raw.split(/[\n,]/).map(s=>s.trim()).filter(Boolean);
  try{
    const r=await fetch(`/api/projects/${encodeURIComponent(nm)}/external-types`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({Prefixes:prefixes})});
    const j=await r.json().catch(()=>({}));
    if(r.ok){ const saved=j.externalTypePrefixes||j.ExternalTypePrefixes||prefixes;
      p.externalTypePrefixes=saved; p.ExternalTypePrefixes=saved;
      const inp=$('#extp-'+i); if(inp) inp.value=saved.join(', ');
      toast(j.message||`${nm}: ${saved.length} external prefix(es) — reindex to apply`, 4500); }
    else toast(typeof j==='string'?j:'Update failed');
  }catch(e){ toast('Update failed'); }
}
async function setActiveProjectUI(i){
  const p=(state.projects||[])[i]; if(!p) return; const nm=p.name||p.Name;
  try{
    const r=await fetch('/api/projects/active',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({Name:nm})});
    if(r.ok){ state.activeProject=nm; toast(`${nm} is now the active project`); switchProject(nm); paintProjects(); }
    else toast('Could not set active');
  }catch(e){ toast('Could not set active'); }
}
function viewProject(i){ const p=(state.projects||[])[i]; if(p) switchProject(p.name||p.Name); paintProjects(); }
async function deleteProjectUI(i){
  const p=(state.projects||[])[i]; if(!p) return; const nm=p.name||p.Name;
  if(!confirm(`Remove project “${nm}” from Shonkor? (Its source files are untouched; only the registry entry + graph DB reference are removed.)`)) return;
  try{
    const r=await fetch(`/api/projects/${encodeURIComponent(nm)}`,{method:'DELETE'});
    const j=await r.json().catch(()=>({}));
    if(r.ok){ toast(j.message||`Removed ${nm}`); await refreshProjects(); renderProjMenu(); paintProjects(); }
    else toast('Delete failed');
  }catch(e){ toast('Delete failed'); }
}

/* --- Ask (AI chat grounded in the graph, via Ollama /api/ask) --- */
let _chatHist=[], _chatBusy=false;
function mdLite(s){
  let h=escapeHtml(s);
  h=h.replace(/```([\s\S]*?)```/g,(m,c)=>`<pre><code>${c.replace(/^\n/,'')}</code></pre>`);
  h=h.replace(/`([^`]+)`/g,'<code>$1</code>');
  h=h.replace(/\*\*([^*]+)\*\*/g,'<strong>$1</strong>');
  return h.split(/\n{2,}/).map(p=>/<pre>/.test(p)?p:`<p>${p.replace(/\n/g,'<br>')}</p>`).join('');
}
function renderAsk(body){
  body.innerHTML = `<div class="chat">
    <div class="chat-msgs" id="chat-msgs"></div>
    <div class="chat-ctx" id="chat-ctx"></div>
    <form class="chat-in" id="chat-form">
      <textarea id="chat-q" rows="1" placeholder="Ask anything about ${escapeHtml(state.project||'this project')}…" autocomplete="off"></textarea>
      <button type="submit" id="chat-send">Send</button>
    </form></div>`;
  const msgs=$('#chat-msgs');
  if(_chatHist.length===0){
    msgs.innerHTML=`<div class="chat-empty">Ask about <b>${escapeHtml(state.project||'this project')}</b> — answers are grounded in the indexed graph and generated locally by Ollama.<br><br>e.g. “How does authentication work?” · “What calls the indexer?”</div>`;
  } else { _chatHist.forEach(m=>appendChat(m.role, m.role==='user'?escapeHtml(m.text):mdLite(m.text))); }
  const ta=$('#chat-q');
  ta.oninput=()=>{ ta.style.height='auto'; ta.style.height=Math.min(140,ta.scrollHeight)+'px'; };
  ta.onkeydown=e=>{ if(e.key==='Enter' && !e.shiftKey){ e.preventDefault(); $('#chat-form').requestSubmit(); } };
  $('#chat-form').onsubmit=e=>{ e.preventDefault(); sendChat(); };
  setTimeout(()=>ta.focus(),40);
}
function appendChat(role, html){
  const el=document.createElement('div'); el.className='cmsg '+role; el.innerHTML=html;
  const m=$('#chat-msgs'); m.appendChild(el); m.scrollTop=m.scrollHeight; return el;
}
const CHAT_STOP=new Set(['how','does','do','did','the','a','an','is','are','was','were','what','why','when','where','which','who','of','to','in','on','for','and','or','with','this','that','these','those','it','its','can','should','would','could','project','code','codebase','work','works','working','use','uses','using','about','from','into','within','tell','me','show','explain','my']);
function chatKeywords(q){ return [...new Set(q.toLowerCase().replace(/[^a-z0-9_\s]/g,' ').split(/\s+/).filter(w=>w.length>=3 && !CHAT_STOP.has(w)))]; }
// Ground each question in the most relevant nodes: the selection + current map + keyword search hits.
async function gatherContext(q){
  const ids=new Set();
  if(window.__sel?.id) ids.add(window.__sel.id);
  for(const n of G.nodes.values()){ ids.add(n.id); if(ids.size>=6) break; }
  const kws=chatKeywords(q); const terms=(kws.length?kws:[q.trim()]).slice(0,4);
  for(const t of terms){
    if(ids.size>=14) break;
    try{ const hits=await (await fetch(`/api/search?q=${encodeURIComponent(t)}&limit=6`,{headers:H()})).json();
      (Array.isArray(hits)?hits:[]).forEach(r=>{ const n=r.node||r.Node||r; const id=n.id||n.Id; if(id) ids.add(id); });
    }catch(e){}
  }
  return [...ids].slice(0,14);
}
/* Each failure class (#228) maps to the ONE thing the reader should go do about it. This is what the code is
   for: "Couldn't reach the AI backend" used to be shown for a wedged model, a too-small timeout and a garbage
   response alike — three unrelated remedies behind one sentence. */
const ERR_HINT={
  backend_unreachable:"Couldn't reach the AI backend. Start Ollama (or fix SemanticAnalyzer:OllamaUrl), then try again.",
  backend_unusable_response:"The AI backend replied with something unusable. Check the configured model — retrying will get the same reply.",
  backend_stalled:"The AI backend went silent mid-answer. It's likely wedged — restart it. If the model is merely slow, raise SemanticAnalyzer:StreamIdleTimeoutSeconds.",
  backend_timeout:"The AI backend took too long. Raise SemanticAnalyzer:TimeoutSeconds, or use a smaller model.",
  backend_error:"The AI backend failed while answering. Check its logs.",
  storage_failure:"The graph database failed while building the context for this answer."
};
function streamNotes(err, incomplete, unsupported){
  let h='';
  if(err) h+=`<div class="snote err">${escapeHtml(ERR_HINT[err.code] || err.message || 'The answer could not be streamed.')}</div>`;
  else if(incomplete) h+=`<div class="snote warn">The backend stopped before finishing — this answer is incomplete.</div>`;
  if(unsupported.length) h+=`<div class="snote warn">Cited ${unsupported.length>1?'sources that are':'a source that is'} not in the context: ${escapeHtml(unsupported.join(', '))}. Treat ${unsupported.length>1?'those claims':'that claim'} as ungrounded.</div>`;
  return h;
}
async function sendChat(){
  if(_chatBusy) return;
  const ta=$('#chat-q'); const q=(ta.value||'').trim(); if(!q) return;
  const empty=$('#chat-msgs .chat-empty'); if(empty) empty.remove();
  appendChat('user', escapeHtml(q)); _chatHist.push({role:'user',text:q});
  ta.value=''; ta.style.height='auto';
  _chatBusy=true; $('#chat-send').disabled=true;
  const thinking=appendChat('assistant', '<div class="think"><span class="dots"></span>Thinking…</div>');
  $('#chat-ctx').textContent='Gathering context…';
  const ids=await gatherContext(q);
  if(ids.length===0){ thinking.className='cmsg error'; thinking.textContent='No graph context found — index this project first (the Reindex button, top-right).'; _chatBusy=false; $('#chat-send').disabled=false; return; }
  $('#chat-ctx').textContent=`Grounded in ${ids.length} node(s).`;
  const recent=_chatHist.slice(-6);
  const composed=recent.length>1 ? recent.map(m=>`${m.role==='user'?'User':'Assistant'}: ${m.text}`).join('\n') : q;
  try{
    // Streamed answer as NDJSON frames (#231), one JSON object per line, so first tokens render immediately.
    // Only `token` frames carry the model's words; everything else is a signal it cannot author.
    const r=await fetch('/api/ask/stream',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:JSON.stringify({query:composed, nodeIds:ids})});
    if(r.ok && r.body){
      const reader=r.body.getReader(); const dec=new TextDecoder();
      let ans='', buf='', pending=false, err=null, incomplete=false, unsupported=[];
      thinking.className='cmsg assistant';
      const handle=f=>{
        if(typeof f.token==='string') ans+=f.token;
        else if(f.error) err=f.error;
        else if(f.unsupportedCitations) unsupported=f.unsupportedCitations||[];
        else if(f.done) incomplete=!f.complete;
      };
      const paint=()=>{ thinking.innerHTML=mdLite(ans||'…')+streamNotes(err,incomplete,unsupported); $('#chat-msgs').scrollTop=$('#chat-msgs').scrollHeight; };
      const drain=flush=>{
        let i;
        while((i=buf.indexOf('\n'))>=0){
          const line=buf.slice(0,i).trim(); buf=buf.slice(i+1);
          if(line) try{ handle(JSON.parse(line)); }catch(_){ /* a torn line is not an answer; drop it */ }
        }
        if(flush && buf.trim()){ try{ handle(JSON.parse(buf.trim())); }catch(_){} buf=''; }
      };
      while(true){
        const {done,value}=await reader.read(); if(done) break;
        buf+=dec.decode(value,{stream:true}); drain(false);
        if(!pending){ pending=true; requestAnimationFrame(()=>{ paint(); pending=false; }); }
      }
      buf+=dec.decode(); drain(true);
      if(!ans && !err) ans='No answer.';
      paint();
      // History gets the model's words ONLY. Under the old text/plain stream the "incomplete"/"unsupported"
      // prose was part of `ans`, so it was fed back verbatim as prior turns — the UI's own warnings became
      // input the model then had to make sense of.
      if(ans) _chatHist.push({role:'assistant',text:ans});
    }
    else { thinking.className='cmsg error'; thinking.textContent = r.status>=500 ? "Couldn't reach the AI backend. Make sure Ollama is running with the configured model, then try again." : `Error: ${await r.text().catch(()=>r.status)}`; }
  }catch(e){ thinking.className='cmsg error'; thinking.textContent="Couldn't reach the AI backend. Make sure Ollama is running, then try again."; }
  $('#chat-msgs').scrollTop=$('#chat-msgs').scrollHeight;
  _chatBusy=false; $('#chat-send').disabled=false;
}

/* --- Journal (interaction records, kept out of the graph) --- */
const REC_ORDER=['Decision','Milestone','Task','Question'];
const REC_COLOR={Decision:'#5a5230',Milestone:'#a9803a',Task:'#1f4d44',Question:'#b14a26'};
const STATUSES=['open','in_progress','done','resolved','accepted','superseded'];
let _journal=[], _jFilter='';
function recStatus(n){ const p=n.properties||n.Properties||{}; return p.status||p.Status||''; }
function recLoc(n){ const f=n.filePath||n.FilePath||''; const b=f?f.split(/[\\/]/).pop():''; const ln=n.startLine||n.StartLine; return b?(b+(ln?':'+ln:'')):''; }
async function renderJournal(body){
  body.innerHTML = `<div class="chips" id="j-tabs"></div><div id="j-list"><div class="sempty">Loading records…</div></div>`;
  loadJournal();
}
async function loadJournal(){
  const list=$('#j-list');
  try{
    const data=await (await fetch('/api/interactions',{headers:H()})).json();
    _journal=(Array.isArray(data)?data:[]).map(n=>({ id:n.id||n.Id, name:n.name||n.Name||n.id||n.Id, type:n.type||n.Type||'', summary:n.summary||n.Summary||'', _n:n }));
    const counts={}; _journal.forEach(r=>counts[r.type]=(counts[r.type]||0)+1);
    const tabs=[`<span class="chip ${_jFilter===''?'on':''}" data-f="">all <span style="opacity:.6">${_journal.length}</span></span>`]
      .concat(REC_ORDER.filter(t=>counts[t]).map(t=>`<span class="chip ${_jFilter===t?'on':''}" data-f="${t}">${t} <span style="opacity:.6">${counts[t]}</span></span>`));
    $('#j-tabs').innerHTML=tabs.join('');
    $('#j-tabs').querySelectorAll('.chip').forEach(c=>c.onclick=()=>{ _jFilter=c.getAttribute('data-f'); paintJournal(); });
    paintJournal();
  }catch(e){ list.innerHTML='<div class="sempty">Records unavailable.</div>'; }
}
function paintJournal(){
  $('#j-tabs').querySelectorAll('.chip').forEach(c=>c.classList.toggle('on', c.getAttribute('data-f')===_jFilter));
  const list=$('#j-list');
  const groups=_jFilter?[_jFilter]:REC_ORDER;
  let html='';
  for(const t of groups){
    const recs=_journal.filter(r=>r.type===t); if(!recs.length) continue;
    html+=`<div class="grp" style="color:${REC_COLOR[t]||'var(--ink-2)'}">${t}s <span class="n">${recs.length}</span></div>`;
    html+=recs.map(r=>{ const i=_journal.indexOf(r); const st=recStatus(r._n); const loc=recLoc(r._n); const c=REC_COLOR[t]||'#4a4636';
      const known=STATUSES.some(s=>s.toLowerCase()===st.toLowerCase());
      const allOpts = !st ? ['',...STATUSES] : (!known ? [st,...STATUSES] : STATUSES);
      const opts=allOpts.map(s=>`<option value="${escapeHtml(s)}" ${s.toLowerCase()===st.toLowerCase()?'selected':''}>${s===''?'— set status —':escapeHtml(s)}</option>`).join('');
      return `<div class="rec" style="border-left-color:${c}">
        <div class="rh"><span class="rname">${escapeHtml(r.name)}</span>${st?`<span class="rstatus" data-st="${escapeHtml(st.toLowerCase())}">${escapeHtml(st.replace(/_/g,' '))}</span>`:''}</div>
        ${r.summary?`<div class="rsum">${escapeHtml(r.summary)}</div>`:''}
        <div class="rfoot">${loc?`<span class="rloc">${escapeHtml(loc)}</span>`:'<span class="rloc" style="opacity:.5">no source</span>'}
          <label class="rset">status <select data-chg="rec-status" data-i="${i}">${opts}</select></label></div>
      </div>`; }).join('');
  }
  list.innerHTML = html || '<div class="sempty">No records yet — decisions, milestones, tasks and questions recorded via the MCP tools appear here.</div>';
}
async function setRecStatus(i, status){
  const r=_journal[i]; if(!r || !status) return;
  try{
    const res=await fetch('/api/interactions/status',{method:'POST',headers:{...H(),'Content-Type':'application/json'},body:JSON.stringify({Id:r.id, Status:status})});
    if(res.ok){ const p=r._n.properties||r._n.Properties||(r._n.properties={}); p.status=status; toast(`${r.type} → ${status.replace(/_/g,' ')}`); paintJournal(); }
    else toast('Status update failed');
  }catch(e){ toast('Status update failed'); }
}

/* --- Delegated actions (#271) ---------------------------------------------------------------------
   Every control that used to carry an inline onclick attribute now carries `data-act` (or `data-chg`) and is
   dispatched from here. That is what lets the CSP drop `'unsafe-inline'` from script-src: an inline handler is
   script-in-markup, so a policy that permits it permits ANY injected inline script — which is most of what a
   CSP is for. A nonce or hash cannot rescue them, because the markup is generated (`goNav(${i})`), and because
   browsers ignore `'unsafe-inline'` the moment a nonce or hash is present. It was all of them or none.

   Note this is only about handlers written as MARKUP. The ~40 `el.onclick = fn` assignments elsewhere in this
   file are ordinary JS and were never a CSP concern; they are untouched. */
const CLICK_ACTS = {
  expand:        () => expand(window.__sel),
  refs:          () => loadReferences(window.__sel),
  trace:         () => armTrace(window.__sel),
  capsule:       () => loadCapsule(window.__sel),
  'cancel-trace':() => cancelTrace(),
  nav:           el => goNav(+el.dataset.i),
  'hunt-pick':   el => huntPick(+el.dataset.i),
  plugin:        el => pluginOp(el.dataset.op, +el.dataset.i),
  'select-all':  el => el.select(),
  'proj-active': el => setActiveProjectUI(+el.dataset.i),
  'proj-view':   el => viewProject(+el.dataset.i),
  'proj-del':    el => deleteProjectUI(+el.dataset.i),
  'ext-save':    el => setExternalTypes(+el.dataset.i),
  // `this.textContent='copied ✓'` in the old attribute; `el` is the same button.
  copy: el => {
    const md = el.dataset.src === 'capmd2' ? window.__capmd2 : window.__capmd;
    navigator.clipboard.writeText(md || '').then(() => { el.textContent = 'copied ✓'; });
  }
};
const CHANGE_ACTS = {
  semantic:     el => setSemantic(+el.dataset.i, el.value),
  'rec-status': el => setRecStatus(+el.dataset.i, el.value)
};
document.addEventListener('click', e => {
  const el = e.target.closest('[data-act]'); if(!el) return;
  const fn = CLICK_ACTS[el.dataset.act]; if(!fn) return;
  // Only anchors need this: the one converted <a> carried `return false` to stop the "#" navigation.
  // Buttons and inputs must keep their default behaviour (an <input data-act="select-all"> still has to focus).
  if(el.tagName === 'A') e.preventDefault();
  fn(el, e);
});
document.addEventListener('change', e => {
  const el = e.target.closest('[data-chg]'); if(!el) return;
  const fn = CHANGE_ACTS[el.dataset.chg]; if(fn) fn(el, e);
});

document.addEventListener('keydown', e => {
  if((e.metaKey||e.ctrlKey) && e.key.toLowerCase()==='k'){ e.preventDefault(); closeStation(); $('#q').focus(); }
  if(e.key==='Escape'){ if($('#station').classList.contains('open')) closeStation(); }
});
boot();
