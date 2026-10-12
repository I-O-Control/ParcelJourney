/* Separate draft: the active replay changes only after a validated save. */
(() => {
  const $ = id => document.getElementById(id), svg = $('canvas'), ns = 'http://www.w3.org/2000/svg';
  let layout, baseline, model, revision, mode = 'select', selection = null, handle = null, start = null, drag = null, paletteDrag = null, dirty = false;
  let view = [0, 0, 2200, 2600], undo = [], redo = [];
  let suggestedPoints = [];
  const categories = [
    ['scanner','▥','Scanners'],['decision','⑂','Decisions'],['junction','◇','Junctions'],
    ['scale','⚖','Scales'],['sealer','◉','Sealers'],['strapper','▤','Strappers'],
    ['printer','▧','Printers'],['exit','⇥','Destinations'],['exception','!','Exceptions']
  ];
  const categoryFor = kind => categories.find(c=>c[0]===kind)||['junction','◇','Junctions'];
  const clone = v => JSON.parse(JSON.stringify(v));
  const message = text => $('status').textContent = text;
  const node = id => layout.nodes.find(n => n.id === id);
  const selected = () => selection && layout[selection.type].find(x => x.id === selection.id);
  const point = e => { const p = new DOMPoint(e.clientX, e.clientY).matrixTransform(svg.getScreenCTM().inverse()); return [p.x, p.y]; };
  const snapped = p => p.map(v => $('snap').checked ? Math.round(v / 10) * 10 : Math.round(v));
  const checkpoint = () => { undo.push(JSON.stringify(layout)); if (undo.length > 100) undo.shift(); redo = []; };
  const changed = () => { dirty = JSON.stringify(layout) !== baseline; draw(); };
  const create = (name, attrs, parent) => { const e = document.createElementNS(ns, name); for (const [k,v] of Object.entries(attrs)) e.setAttribute(k,v); parent.append(e); return e; };
  function renderPalette(){
    const host=$('palette'),filter=$('pointSearch').value.trim().toLowerCase();host.replaceChildren();
    for(const [kind,glyph,title] of categories){
      const points=suggestedPoints.map(n=>node(n.id)||n).filter(n=>categoryFor(n.kind)[0]===kind&&n.id!=='UNOBSERVED_POSITION'&&(!filter||(n.id+' '+n.label+' '+(n.equipment||'')).toLowerCase().includes(filter)));
      const group=document.createElement('details');group.className='point-group';group.dataset.kind=kind;group.open=!!filter;
      const summary=document.createElement('summary'),icon=document.createElement('span'),name=document.createElement('span'),count=document.createElement('span');
      icon.className='category-icon';icon.textContent=glyph;name.className='category-label';name.textContent=title;count.className='category-count';count.textContent=String(points.length);summary.append(icon,name,count);group.append(summary);
      const list=document.createElement('div');list.className='point-list';
      for(const suggestion of points){const item=node(suggestion.id),card=document.createElement('div'),cardIcon=document.createElement('span'),label=document.createElement('span'),state=document.createElement('small');
        card.className='point-card';card.draggable=false;card.dataset.point=suggestion.id;card.title=`${suggestion.id}${suggestion.equipment?' · '+suggestion.equipment:''}`;
        cardIcon.className='category-icon';cardIcon.textContent=glyph;label.className='point-name';label.textContent=suggestion.label||suggestion.id;state.textContent=item?'map':'+';card.append(cardIcon,label,state);
        card.onclick=()=>{if(paletteDrag?.moved)return;if(!item){message('Drag this point onto the map to place it.');return;}selection={type:'nodes',id:item.id};view=[item.x-400,item.y-300,800,600];draw();inspect();};
        card.onpointerdown=e=>{if(e.button!==0)return;paletteDrag={id:suggestion.id,label:suggestion.label||suggestion.id,origin:[e.clientX,e.clientY],moved:false};e.preventDefault();};list.append(card);
      }
      group.append(list);group.ondragover=e=>{e.preventDefault();group.classList.add('drag-over');};group.ondragleave=e=>{if(!group.contains(e.relatedTarget))group.classList.remove('drag-over');};
      group.ondrop=e=>{e.preventDefault();group.classList.remove('drag-over');const id=e.dataTransfer.getData('text/parcel-point'),existing=node(id);if(!id)return;checkpoint();if(existing){existing.kind=kind;selection={type:'nodes',id};}else{const source=model.nodes.find(n=>n.id===id);layout.nodes.push({...clone(source),kind});selection={type:'nodes',id};}changed();inspect();renderPalette();message(`${id} moved to ${title}.`);};
      host.append(group);
    }
  }
  function fit() { const ps = [...layout.nodes.map(n => [n.x,n.y]), ...layout.edges.flatMap(e => e.points)]; const xs=ps.map(p=>p[0]),ys=ps.map(p=>p[1]); view=[Math.min(...xs)-120,Math.min(...ys)-120,Math.max(300,Math.max(...xs)-Math.min(...xs)+240),Math.max(300,Math.max(...ys)-Math.min(...ys)+240)]; draw(); }
  function moveStation(n, p) { n.x=p[0]; n.y=p[1]; for(const e of layout.edges){if(e.a===n.id)e.points[0]=p.slice();if(e.b===n.id)e.points[e.points.length-1]=p.slice();} }
  function draw() {
    svg.setAttribute('viewBox',view.join(' '));
    for(const id of ['edges','nodes','handles','preview'])$(id).replaceChildren();
    for(const e of layout.edges)create('path',{d:e.points.map((p,i)=>(i?'L':'M')+p.join(',')).join(' '),class:'edge'+(selection?.id===e.id&&selection.type==='edges'?' selected':''),'data-edge':e.id,'marker-end':'url(#arrow)'},$('edges'));
    for(const n of layout.nodes){if(n.id==='UNOBSERVED_POSITION')continue;const [kind,glyph]=categoryFor(n.kind);const g=create('g',{transform:`translate(${n.x} ${n.y})`,class:'node'+(selection?.id===n.id&&selection.type==='nodes'?' selected':''),'data-node':n.id},$('nodes'));create('rect',{x:-18,y:-15,width:36,height:30,rx:kind==='decision'?3:7},g);create('text',{x:0,y:6,'text-anchor':'middle',class:'point-glyph'},g).textContent=glyph;create('text',{x:25,y:5},g).textContent=n.label;}
    if(drag?.type==='connect'){const a=node(drag.from),b=drag.current;if(a&&b)create('path',{d:`M${a.x},${a.y} L${b[0]},${b[1]}`,class:'preview','marker-end':'url(#arrow)'},$('preview'));}
    const e=selected();if(selection?.type==='edges'&&e)e.points.forEach((p,i)=>{if(i>0&&i<e.points.length-1)create('circle',{cx:p[0],cy:p[1],r:8,class:'handle'+(i===handle?' chosen':''),'data-handle':i},$('handles'));});
    $('undo').disabled=!undo.length;$('redo').disabled=!redo.length;
    $('counts').textContent=`${layout.nodes.filter(n=>n.id!=='UNOBSERVED_POSITION').length} points · ${layout.edges.length} belts${dirty?' · Unsaved changes':''}`;
  }
  function field(label, value, apply, type='text') { const l=document.createElement('label');l.textContent=label;const input=document.createElement('input');input.type=type;input.id='property-'+label.toLowerCase().replaceAll(' ','-');l.htmlFor=input.id;input.value=value??'';input.onchange=()=>{if(type==='number'&&(!Number.isFinite(input.valueAsNumber)||Math.abs(input.valueAsNumber)>1000000)){message('Enter a finite coordinate within ±1,000,000.');inspect();return;}checkpoint();apply(type==='number'?input.valueAsNumber:input.value);changed();inspect();};$('properties').append(l,input);return input; }
  function button(text, action){const b=document.createElement('button');b.textContent=text;b.style.margin='12px 6px 0 0';b.onclick=action;$('properties').append(b);return b;}
  function inspect(){
    $('properties').replaceChildren();const item=selected();$('heading').textContent=!item?'Layout inspector':selection.type==='nodes'?'Station':'Directed connection';if(!item)return;
    const info=document.createElement('small');info.textContent=item.id;$('properties').append(info);
    if(selection.type==='nodes'){
      const idField=field('Station ID',item.id,v=>{v=v.trim();if(!v||layout.nodes.some(n=>n!==item&&n.id===v)){message('Choose a unique, nonempty station ID.');return;}const old=item.id;item.id=v;selection.id=v;for(const e of layout.edges){if(e.a===old)e.a=v;if(e.b===old)e.b=v;}});idField.disabled=model.nodes.some(n=>n.id===item.id);field('Label',item.label,v=>item.label=v);field('Equipment code',item.equipment,v=>item.equipment=v);
      field('X',item.x,v=>moveStation(item,[v,item.y]),'number');field('Y',item.y,v=>moveStation(item,[item.x,v]),'number');
      const label=document.createElement('label');label.textContent='Category';const row=document.createElement('div');row.className='category-icon';row.title=categoryFor(item.kind)[2];row.textContent=categoryFor(item.kind)[1];const s=document.createElement('select');for(const [kind,,title] of categories){const o=document.createElement('option');o.value=kind;o.textContent=title;s.append(o);}s.value=categoryFor(item.kind)[0];s.onchange=()=>{checkpoint();item.kind=s.value;changed();inspect();renderPalette();};$('properties').append(label,row,s);
      button('Delete station',()=>{if(model.parcels.some(p=>p.stops.some(s=>s.id===item.id))){message('This station is referenced by the loaded capture. Keep its ID and move it instead.');return;}checkpoint();layout.nodes=layout.nodes.filter(n=>n.id!==item.id);layout.edges=layout.edges.filter(e=>e.a!==item.id&&e.b!==item.id);selection=null;changed();inspect();});
    }else{
      const p=document.createElement('p');p.textContent=`${item.a} → ${item.b}`;$('properties').append(p);
      const hint=document.createElement('p');hint.textContent='Double-click the route to insert a bend at that position. Select and drag a white handle to move it.';$('properties').append(hint);
      if(handle!==null&&item.points[handle]){field('Bend X',item.points[handle][0],v=>item.points[handle][0]=v,'number');field('Bend Y',item.points[handle][1],v=>item.points[handle][1]=v,'number');button('Remove bend',()=>{checkpoint();item.points.splice(handle,1);handle=null;changed();inspect();});}
      button('Add bend',()=>{checkpoint();let i=1,longest=-1;for(let j=1;j<item.points.length;j++){const a=item.points[j-1],b=item.points[j],d=Math.hypot(b[0]-a[0],b[1]-a[1]);if(d>longest){longest=d;i=j;}}const a=item.points[i-1],b=item.points[i];item.points.splice(i,0,snapped([(a[0]+b[0])/2,(a[1]+b[1])/2]));handle=i;changed();inspect();});
      button('Right-angle route',()=>{checkpoint();const a=item.points[0],b=item.points.at(-1);item.points=[a,[b[0],a[1]],b];handle=null;changed();inspect();});
      button('Straight route',()=>{checkpoint();item.points=[item.points[0],item.points.at(-1)];handle=null;changed();inspect();});
      button('Delete connection',()=>{checkpoint();layout.edges=layout.edges.filter(e=>e.id!==item.id);selection=null;handle=null;changed();inspect();message('Connection removed. Saving checks that all recorded journeys still have a directed route.');});
    }
  }
  function unique(prefix,items){let i=1;while(items.some(x=>x.id===prefix+i))i++;return prefix+i;}
  function setMode(next){mode=next;start=null;drag=null;document.querySelectorAll('[data-mode]').forEach(b=>b.classList.toggle('active',b.dataset.mode===mode));message(mode==='node'?'Click the map to add a point.':mode==='connect'?'Drag from the start point to the end point. The belt arrow follows your drag.':'Select or drag points.');draw();}
  document.querySelectorAll('[data-mode]').forEach(b=>b.onclick=()=>setMode(b.dataset.mode));
  svg.onpointerdown=e=>{
    if(!layout||e.button!==0)return;const p=point(e),n=e.target.closest('[data-node]')?.dataset.node,edge=e.target.closest('[data-edge]')?.dataset.edge,h=e.target.dataset.handle;
    if(mode==='node'){checkpoint();const xy=snapped(p),id=unique('JUNCTION_',layout.nodes),added={id,label:id,x:xy[0],y:xy[1],kind:'junction',equipment:'',coordinateBasis:'User physical layout'};layout.nodes.push(added);suggestedPoints.push(clone(added));selection={type:'nodes',id};setMode('select');changed();inspect();renderPalette();return;}
    if(mode==='connect'&&n){if(!start){start=n;drag={type:'connect',from:n,current:p,origin:[e.clientX,e.clientY],moved:false};svg.setPointerCapture(e.pointerId);message(`Drawing from ${n}… release on the destination point.`);draw();return;}if(start===n){message('Choose a different end point.');return;}createBelt(start,n);return;}
    if(mode==='connect')return;
    if(h!==undefined){handle=Number(h);checkpoint();drag={type:'handle'};}
    else if(n){selection={type:'nodes',id:n};handle=null;checkpoint();const v=node(n);drag={type:'node',offset:[v.x-p[0],v.y-p[1]]};}
    else if(edge){selection={type:'edges',id:edge};handle=null;}
    else{drag={type:'pan',client:[e.clientX,e.clientY],view:view.slice(),scale:svg.getScreenCTM().a};}
    svg.setPointerCapture(e.pointerId);draw();inspect();
  };
  svg.onpointermove=e=>{if(!drag)return;const p=point(e);if(drag.type==='connect'){drag.current=p;if(Math.hypot(e.clientX-drag.origin[0],e.clientY-drag.origin[1])>6)drag.moved=true;draw();return;}if(drag.type==='node')moveStation(selected(),snapped([p[0]+drag.offset[0],p[1]+drag.offset[1]]));else if(drag.type==='handle')selected().points[handle]=snapped(p);else{view[0]=drag.view[0]-(e.clientX-drag.client[0])/drag.scale;view[1]=drag.view[1]-(e.clientY-drag.client[1])/drag.scale;}if(drag.type==='pan')draw();else changed();};
  document.addEventListener('pointermove',e=>{if(!paletteDrag)return;if(Math.hypot(e.clientX-paletteDrag.origin[0],e.clientY-paletteDrag.origin[1])>7)paletteDrag.moved=true;if(!paletteDrag.moved)return;const ghost=$('dragGhost');ghost.hidden=false;ghost.textContent=paletteDrag.label;ghost.style.left=`${e.clientX+14}px`;ghost.style.top=`${e.clientY+14}px`;document.querySelectorAll('.point-group').forEach(g=>g.classList.toggle('drag-over',g===document.elementFromPoint(e.clientX,e.clientY)?.closest('.point-group')));});
  document.addEventListener('pointerup',e=>{if(!paletteDrag)return;const pending=paletteDrag;paletteDrag=null;$('dragGhost').hidden=true;document.querySelectorAll('.point-group.drag-over').forEach(g=>g.classList.remove('drag-over'));if(!pending.moved)return;const hit=document.elementFromPoint(e.clientX,e.clientY),group=hit?.closest('.point-group'),svgHit=hit?.closest('#canvas'),kind=group?.dataset.kind;if(kind){const existing=node(pending.id);checkpoint();if(existing){existing.kind=kind;selection={type:'nodes',id:pending.id};}else{const source=model.nodes.find(n=>n.id===pending.id);if(!source)return;layout.nodes.push({...clone(source),kind});selection={type:'nodes',id:pending.id};}changed();inspect();renderPalette();message(`${pending.id} moved to ${categoryFor(kind)[2]}.`);return;}if(svgHit){const source=model.nodes.find(n=>n.id===pending.id);if(!source)return;const p=snapped(point(e));checkpoint();let target=node(pending.id);if(!target){target={...clone(source),kind:categoryFor(source.kind)[0]};layout.nodes.push(target);}moveStation(target,p);selection={type:'nodes',id:pending.id};changed();inspect();renderPalette();message(`${pending.id} placed on the map.`);}},true);
  svg.onpointerup=e=>{if(drag?.type==='connect'){const current=drag,endElement=document.elementFromPoint(e.clientX,e.clientY)?.closest?.('[data-node]'),end=endElement?.dataset.node;drag=null;if(current.moved&&end&&end!==current.from){start=null;draw();createBelt(current.from,end);}else if(!current.moved){start=current.from;draw();message(`Start: ${current.from}. Click or drag to the belt end point.`);}else{start=null;draw();message('No belt added. Release on an end point.');}return;}drag=null;inspect();};
  svg.onpointercancel=()=>{drag=null;start=null;draw();};
  function createBelt(from,to){const a=node(from),b=node(to);if(!a||!b||from===to)return;checkpoint();const id=unique('BELT_',layout.edges);layout.edges.push({id,a:from,b:to,points:[[a.x,a.y],[b.x,a.y],[b.x,b.y]],basis:'User drawn conveyor belt; direction follows start-to-end drawing'});selection={type:'edges',id};start=null;setMode('select');changed();inspect();renderPalette();message(`Conveyor direction: ${from} → ${to}.`);}
  function zoom(f,p=[view[0]+view[2]/2,view[1]+view[3]/2]){if(view[2]*f<50||view[2]*f>100000)return;view=[p[0]+(view[0]-p[0])*f,p[1]+(view[1]-p[1])*f,view[2]*f,view[3]*f];draw();}
  svg.addEventListener('wheel',e=>{e.preventDefault();zoom(e.deltaY>0?1.15:1/1.15,point(e));},{passive:false});
  svg.ondragover=e=>e.preventDefault();svg.ondrop=e=>{e.preventDefault();const id=e.dataTransfer.getData('text/parcel-point'),source=model.nodes.find(n=>n.id===id);if(!source)return;const p=snapped(point(e));checkpoint();let target=node(id);if(!target){target=clone(source);layout.nodes.push(target);}moveStation(target,p);selection={type:'nodes',id};changed();inspect();renderPalette();message(`${id} placed on the map.`);};
  $('zoomIn').onclick=()=>zoom(.8);$('zoomOut').onclick=()=>zoom(1.25);$('fit').onclick=fit;
  function history(from,to){if(!from.length)return;to.push(JSON.stringify(layout));layout=JSON.parse(from.pop());selection=null;handle=null;changed();inspect();}
  $('undo').onclick=()=>history(undo,redo);$('redo').onclick=()=>history(redo,undo);
  $('findButton').onclick=()=>{const q=$('find').value.toLowerCase(),n=layout.nodes.find(n=>n.id.toLowerCase()===q)||layout.nodes.find(n=>(n.id+' '+n.label).toLowerCase().includes(q));if(!n){message('No matching station.');return;}selection={type:'nodes',id:n.id};view=[n.x-400,n.y-300,800,600];draw();inspect();};
  $('find').onkeydown=e=>{if(e.key==='Enter')$('findButton').click();};$('pointSearch').oninput=renderPalette;
  $('restoreMissing').onclick=()=>{const missing=model.nodes.filter(n=>!node(n.id));if(!missing.length){message('All capture stations are already in this draft.');return;}checkpoint();layout.nodes.push(...clone(missing));changed();fit();message(`Added ${missing.length} missing stations at their source positions. Connect their physical routes before saving.`);};
  document.addEventListener('keydown',e=>{if(/INPUT|SELECT|TEXTAREA/.test(e.target.tagName))return;if(e.key==='Escape'){setMode('select');selection=null;handle=null;draw();inspect();}if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='z'){e.preventDefault();e.shiftKey?$('redo').click():$('undo').click();}});
  $('export').onclick=()=>{const url=URL.createObjectURL(new Blob([JSON.stringify(layout,null,2)],{type:'application/json'})),a=document.createElement('a');a.href=url;a.download='parceljourney-layout.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);message('Exported draft geometry. Save & return applies it to the viewers.');};
  $('import').onclick=()=>$('file').click();
  $('file').onchange=async()=>{try{const f=$('file').files[0];if(!f)return;if(f.size>8000000)throw Error('Layout file exceeds 8 MB.');const value=JSON.parse(await f.text());const r=await fetch('/api/layout/validate',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(value)});const result=await r.json();if(!r.ok)throw Error(result.error);checkpoint();layout=value;selection=null;handle=null;changed();fit();inspect();message('Imported into the draft. Save & return to apply.');}catch(e){message(e.message);}finally{$('file').value='';}};
  const returnTo=new URLSearchParams(location.search).get('return')==='3d'?'/3d':'/equipment-replay.html';$('back').href=returnTo;
  $('save').onclick=async()=>{ $('save').disabled=true;try{const session=await(await fetch('/api/session')).json();const r=await fetch('/api/layout',{method:'POST',headers:{'Content-Type':'application/json','X-ParcelJourney-Session':session.token,'If-Match':revision},body:JSON.stringify(layout)});const result=await r.json();if(!r.ok)throw Error(result.error);dirty=false;location.href=returnTo;}catch(e){message(e.message);}finally{$('save').disabled=false;}};
  window.addEventListener('beforeunload',e=>{if(dirty){e.preventDefault();e.returnValue='';}});
  fetch('/api/layout').then(r=>{if(!r.ok)throw Error('Cannot load layout.');return r.json();}).then(data=>{layout=data.layout;model=data.model;suggestedPoints=clone(model.nodes.filter(n=>n.id!=='UNOBSERVED_POSITION'));revision=data.revision;baseline=JSON.stringify(layout);renderPalette();fit();inspect();message('Ready · Drag points from the categorized list; drag a belt from start to end.');}).catch(e=>{message(e.message);$('save').disabled=true;});
})();
