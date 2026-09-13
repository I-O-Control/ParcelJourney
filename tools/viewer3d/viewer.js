import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import ReplayEngine from '../replay-engine.js';
import { buildTrailSegments } from './trail.mjs';
import { routeEntries, routeStatus, arrangeLabels } from './route-labels.mjs';

const $ = id => document.getElementById(id);
const fmt = s => `${Math.floor(s / 60).toString().padStart(2, '0')}:${Math.floor(s % 60).toString().padStart(2, '0')}`;
const S = .02;
const world = (xy, y = .5) => new THREE.Vector3(xy[0] * S, y, xy[1] * S);
const reduced = matchMedia('(prefers-reduced-motion: reduce)');

async function boot() {
  const response = await fetch('/3d/model.json');
  if (!response.ok) throw new Error('The offline replay model could not be loaded.');
  const model = await response.json();
  const nodes = new Map(model.nodes.map(n => [n.id, n]));
  const edges = new Map(model.edges.map(e => [e.id, e]));
  const host = $('viewport');
  const renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance' });
  renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
  renderer.setClearColor(0x101a22);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.domElement.setAttribute('aria-label', 'Interactive 3D schematic conveyor plant');
  host.append(renderer.domElement);
  const scene = new THREE.Scene();
  const camera = new THREE.OrthographicCamera(-30, 30, 30, -30, .1, 500);
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.enableDamping = false;
  controls.minPolarAngle = .005;
  controls.maxPolarAngle = Math.PI * .47;
  controls.minZoom = .45;
  controls.maxZoom = 12;
  controls.screenSpacePanning = true;
  scene.add(new THREE.HemisphereLight(0xcceeff, 0x243139, 2.5));
  const sun = new THREE.DirectionalLight(0xffedce, 3);
  sun.position.set(-20, 60, -30);
  scene.add(sun);

  // Batch static equipment into a handful of draw calls. No live shadows/postprocessing.
  const cube = new THREE.BoxGeometry(1, 1, 1);
  const groups = new Map();
  const transform = new THREE.Object3D();
  function box(color, x, y, z, w, h, d, rotation = 0) {
    if (!groups.has(color)) groups.set(color, []);
    groups.get(color).push([x, y, z, w, h, d, rotation]);
  }
  const sections = [
    ['ENTRY & QUALITY', 8, -12, 20, 15, 0x22363d],
    ['SEALING', 15, 2.7, 43, 13.8, 0x25313b],
    ['PACKAGING', 7, 15, 37, 26.8, 0x283339],
    ['HALL 04', 3.2, 29.5, 36, 36, 0x21383a],
    ['HALL 03', 3.2, 36.7, 36, 42.8, 0x213039],
    ['HALL 02', 3.2, 43.5, 39, 49, 0x25323b]
  ];
  const floorMaterial = new THREE.MeshStandardMaterial({ color: 0x16252e, roughness: 1 });
  const floor = new THREE.Mesh(new THREE.BoxGeometry(52, .16, 67), floorMaterial);
  floor.position.set(22, -.4, 18);
  scene.add(floor);
  for (const [, x1, z1, x2, z2, color] of sections) box(color, (x1+x2)/2, -.27, (z1+z2)/2, x2-x1, .12, z2-z1);
  const grid = new THREE.GridHelper(66, 66, 0x29414c, 0x22353f);
  grid.position.set(22, -.19, 18);
  scene.add(grid);

  // Paths share physical stretches: deduplicate exact segments before creating belts.
  const segments = new Map();
  for (const e of model.edges) for (let i = 1; i < e.points.length; i++) {
    const a = e.points[i-1], b = e.points[i];
    const key = [a.join(','), b.join(',')].sort().join('|');
    if (!segments.has(key)) segments.set(key, [a,b]);
  }
  for (const [a,b] of segments.values()) {
    const dx=(b[0]-a[0])*S, dz=(b[1]-a[1])*S, length=Math.hypot(dx,dz);
    if (length < .001) continue;
    const x=(a[0]+b[0])*S/2, z=(a[1]+b[1])*S/2, theta=Math.atan2(dx,dz);
    box(0x344752,x,.05,z,.55,.33,length,theta);
    box(0x58717a,x,.235,z,.40,.065,length,theta);
    const ox=Math.cos(theta)*.27, oz=-Math.sin(theta)*.27;
    for (const sign of [-1,1]) box(0x7b9195,x+ox*sign,.29,z+oz*sign,.045,.13,length,theta);
    // Crossbars evoke rollers without hundreds of independently rendered objects.
    for (let d=.35;d<length;d+=.65) {
      const f=d/length;
      box(0x728789,a[0]*S+dx*f,.279,a[1]*S+dz*f,.38,.025,.028,theta);
    }
    for(let d=.4;d<length;d+=3) {
      const f=d/length;
      box(0x263640,a[0]*S+dx*f,-.09,a[1]*S+dz*f,.42,.4,.12,theta);
    }
  }
  const equipmentColors = { scanner:0x70b5cb,scale:0xd3b46f,decision:0x9c91c6,sealer:0x60b6a3,strapper:0x60b6a3,printer:0xd29c68,exit:0x92b991,exception:0xd67777,junction:0x76939f };
  const picks = [];
  for (const n of model.nodes) {
    const x=n.x*S,z=n.y*S,c=equipmentColors[n.kind];
    if (n.kind === 'scanner' || n.kind === 'decision') {
      box(c,x-.39,.68,z,.10,.85,.16); box(c,x+.39,.68,z,.10,.85,.16);
      box(c,x,1.10,z,.88,.12,.18); box(0x172b35,x,.97,z,.21,.12,.20);
    } else if (n.kind === 'sealer' || n.kind === 'strapper' || n.kind === 'printer') {
      box(c,x-.42,.55,z,.22,1.05,.68);box(c,x+.42,.55,z,.22,1.05,.68);
      box(c,x,1.10,z,1.06,.18,.70);box(0x172b35,x+.43,.83,z-.355,.13,.22,.03);
    } else if(n.kind === 'scale') {
      box(c,x,.28,z,.73,.16,.68);box(0x273b44,x+.4,.55,z,.09,.65,.08);
      box(c,x+.4,.87,z,.25,.20,.12);
    } else {
      box(c,x,.28,z,.68,.12,.68);
      if(n.kind==='exit'||n.kind==='exception')box(c,x,.07,z+.32,.73,.30,1.1);
    }
    // Invisible pick volumes stay independent of instancing/material batches.
    const hit = new THREE.Mesh(new THREE.BoxGeometry(1.1,1.3,1.1), new THREE.MeshBasicMaterial({ visible:false }));
    hit.position.set(x,.6,z);hit.userData.node=n;scene.add(hit);picks.push(hit);
  }
  for (const [color, instances] of groups) {
    const mesh = new THREE.InstancedMesh(cube,new THREE.MeshStandardMaterial({color,roughness:.78,metalness:.15}),instances.length);
    instances.forEach(([x,y,z,w,h,d,rotation],i)=>{
      transform.position.set(x,y,z);transform.scale.set(w,h,d);transform.rotation.set(0,rotation,0);transform.updateMatrix();mesh.setMatrixAt(i,transform.matrix);
    });
    mesh.computeBoundingSphere();scene.add(mesh);
  }
  // Six floor labels, drawn once into local textures.
  for (const [name,x1,z1,x2] of sections) {
    const canvas=document.createElement('canvas');canvas.width=512;canvas.height=64;
    const ctx=canvas.getContext('2d');ctx.font='500 28px Segoe UI';ctx.fillStyle='#78929e';ctx.fillText(name,12,42);
    const texture=new THREE.CanvasTexture(canvas);texture.colorSpace=THREE.SRGBColorSpace;
    const label=new THREE.Mesh(new THREE.PlaneGeometry(Math.min(10,x2-x1),1.25),new THREE.MeshBasicMaterial({map:texture,transparent:true,depthWrite:false}));
    label.rotation.x=-Math.PI/2;label.position.set(x1+5,-.12,z1+.9);scene.add(label);
  }
  const parcel = new THREE.Group();
  const carton = new THREE.Mesh(new THREE.BoxGeometry(.55,.46,.65),new THREE.MeshStandardMaterial({color:0xf5b86e,roughness:.85}));
  const tape = new THREE.Mesh(new THREE.BoxGeometry(.12,.465,.655),new THREE.MeshStandardMaterial({color:0xffdfad}));
  const slip = new THREE.Mesh(new THREE.BoxGeometry(.18,.01,.22),new THREE.MeshBasicMaterial({color:0xfff5dc}));slip.position.set(.16,.237,-.1);
  parcel.add(carton,tape,slip);scene.add(parcel);
  const halo = new THREE.Mesh(new THREE.RingGeometry(.46,.54,32),new THREE.MeshBasicMaterial({color:0xffc481,side:THREE.DoubleSide,transparent:true,opacity:.9,depthWrite:false}));
  halo.rotation.x=-Math.PI/2;scene.add(halo);
  const trail = new THREE.InstancedMesh(cube,new THREE.MeshBasicMaterial({color:0xffbb78}),1024);
  trail.instanceMatrix.setUsage(THREE.DynamicDrawUsage);trail.frustumCulled=false;trail.count=0;scene.add(trail);
  let trailSegments=[],lastTrailCount=-1,lastPartial=-1;
  function prepareTrail(p) {
    trailSegments=buildTrailSegments(edges,p);
    if(trailSegments.length>trail.instanceMatrix.count)throw new Error('Replay exceeds the trail capacity.');
    trail.count=0;lastTrailCount=-1;lastPartial=-1;
  }
  function writeTrail(i, fraction) {
    const {a,b}=trailSegments[i],dx=(b[0]-a[0])*S*fraction,dz=(b[1]-a[1])*S*fraction;
    transform.position.set(a[0]*S+dx/2,.325,a[1]*S+dz/2);
    transform.scale.set(.105,.035,Math.max(.00001,Math.hypot(dx,dz)));
    transform.rotation.set(0,Math.atan2(dx,dz),0);transform.updateMatrix();trail.setMatrixAt(i,transform.matrix);
  }
  function updateTrail(t) {
    let count=0;
    while(count<trailSegments.length&&trailSegments[count].start<t)count++;
    if(count<lastTrailCount){lastTrailCount=-1;lastPartial=-1;}
    for(let i=Math.max(0,lastTrailCount-1);i<count;i++)writeTrail(i,1);
    if(count){const seg=trailSegments[count-1];const f=Math.min(1,(t-seg.start)/(seg.end-seg.start));if(f<1||lastPartial===count-1)writeTrail(count-1,f);lastPartial=f<1?count-1:-1;}
    trail.count=count;trail.instanceMatrix.needsUpdate=true;lastTrailCount=count;
  }

  let chosen=model.parcels[0],t=0,playing=false,following=false,isTop=false,lastTime=performance.now(),lastUI=-Infinity,activeIndex=-1,state,hovered=null;
  let frameId=0,dirty=true,inFrame=false,frames=0,statsTime=performance.now(),fps=0;
  const routeButtons=[];
  function requestFrame(){dirty=true;if(!frameId&&!inFrame)frameId=requestAnimationFrame(frame);}
  function setFollow(value){following=value;$('follow').classList.toggle('selected',value);$('follow').setAttribute('aria-pressed',String(value));}
  function centerOn(point){const shift=point.clone().sub(controls.target);camera.position.add(shift);controls.target.copy(point);controls.update();}
  function setView(top,full=false){
    isTop=top;
    $('iso').classList.toggle('selected',!top);$('top').classList.toggle('selected',top);
    $('iso').setAttribute('aria-pressed',String(!top));$('top').setAttribute('aria-pressed',String(top));
    if(full){setFollow(false);controls.target.set(21,0,18);camera.zoom=1;}
    const delta=top?new THREE.Vector3(0,85,.01):new THREE.Vector3(48,65,60);
    camera.position.copy(controls.target).add(delta);camera.updateProjectionMatrix();controls.update();requestFrame();
  }
  function resize(){
    const w=host.clientWidth,h=host.clientHeight;renderer.setSize(w,h);
    const tools=document.querySelector('.view-tools');
    $('route-key').style.top=`${tools.offsetTop+tools.offsetHeight+10}px`;
    const aspect=w/h,span=Math.max(36,37/Math.max(.4,aspect));
    camera.left=-span*aspect;camera.right=span*aspect;camera.top=span;camera.bottom=-span;camera.updateProjectionMatrix();requestFrame();
  }
  new ResizeObserver(resize).observe(host);
  controls.addEventListener('change',requestFrame);
  controls.addEventListener('start',()=>setFollow(false));
  const raycaster=new THREE.Raycaster(),pointer=new THREE.Vector2();
  let down=null,pendingPointer=null,dragging=false,lastHoverTime=0;
  function pickAt(clientX,clientY){
    const r=host.getBoundingClientRect();pointer.set((clientX-r.left)/r.width*2-1,-(clientY-r.top)/r.height*2+1);
    raycaster.setFromCamera(pointer,camera);
    return raycaster.intersectObjects(picks,false)[0]?.object.userData.node||null;
  }
  host.addEventListener('pointerdown',e=>{down=[e.clientX,e.clientY];});
  host.addEventListener('pointermove',e=>{
    if(e.buttons){dragging=true;hovered=null;return;}
    pendingPointer=[e.clientX,e.clientY];requestFrame();
  });
  host.addEventListener('pointerleave',()=>{hovered=null;pendingPointer=null;requestFrame();});
  host.addEventListener('pointerup',e=>{
    dragging=false;
    if(!down||Math.hypot(e.clientX-down[0],e.clientY-down[1])>5){down=null;return;}
    down=null;hovered=pickAt(e.clientX,e.clientY);
    if(hovered)inspectStation(hovered);requestFrame();
  });
  host.addEventListener('pointercancel',()=>{down=null;dragging=false;hovered=null;});
  const routeLayer=$('route-labels'),leaders=$('route-leaders');
  const typeNames={scanner:'Scanner',scale:'Weight check',decision:'Routing decision',sealer:'Processing',strapper:'Strapper',printer:'Label printer',exit:'Exit',exception:'Exception',junction:'Transfer'};
  let routeMap=new Map(),labelItems=[],labelLayoutKey='',lastLayoutTime=-Infinity,lastRouteStatusKey='',inspected=null;
  const projectPoint=(point)=>{const v=point.clone().project(camera);return {x:(v.x+1)/2*host.clientWidth,y:(1-v.y)/2*host.clientHeight,depth:v.z};};
  function inspectStation(node){
    inspected=node;
    $('station-detail').hidden=false;
    $('station-title').textContent=node.label;
    $('station-code').textContent=`${node.id}${node.equipment?' · '+node.equipment:''} · ${typeNames[node.kind]||node.kind}`;
    const entry=routeMap.get(node.id),info=entry?routeStatus(entry,state):null;
    $('station-result').textContent=info?(info.last?`${fmt(info.last.t)} · ${info.last.Summary}`:'Later on this synthetic route. No event has occurred here yet.'):'Not on the selected parcel’s route.';
    $('station-visits').textContent=info?`${info.visits} visit${info.visits===1?'':'s'} so far${info.result?' · '+info.result:''}`:'';
  }
  $('station-close').onclick=()=>{inspected=null;$('station-detail').hidden=true;};
  function createRouteLabels(){
    routeMap=new Map(routeEntries(chosen,nodes).map(entry=>[entry.node.id,entry]));
    labelItems=[];routeLayer.replaceChildren();leaders.replaceChildren();
    for(const [id,entry] of routeMap){
      const button=document.createElement('button');button.className='route-label';button.dataset.node=id;
      const name=document.createElement('strong');name.textContent=entry.node.label;
      const code=document.createElement('span');code.className='route-code';code.textContent=id;
      const status=document.createElement('span');status.className='route-status';
      button.append(name,code,status);
      button.onclick=()=>inspectStation(entry.node);
      button.onpointerenter=()=>{hovered=entry.node;pendingPointer=null;requestFrame();};
      button.onpointerleave=()=>{hovered=null;requestFrame();};
      button.onfocus=()=>{hovered=entry.node;requestFrame();};
      button.onblur=()=>{hovered=null;requestFrame();};
      const line=document.createElementNS('http://www.w3.org/2000/svg','path');line.classList.add('route-leader');
      const dot=document.createElementNS('http://www.w3.org/2000/svg','circle');dot.setAttribute('r','2.5');
      leaders.append(line,dot);routeLayer.append(button);
      labelItems.push({id,entry,button,status,line,dot,point:world([entry.node.x,entry.node.y],1.1),previous:null});
    }
    lastRouteStatusKey='';labelLayoutKey='';lastLayoutTime=-Infinity;
  }
  function updateRouteLabels(now){
    const statusKey=[state.index,state.stop.arrival,state.leg?.b||'',state.done,chosen.id].join('|');
    if(statusKey!==lastRouteStatusKey){
      for(const item of labelItems){
        const info=routeStatus(item.entry,state);item.info=info;
        item.button.dataset.state=info.status;
        item.status.textContent=`${info.caption}${info.visits>1?' ×'+info.visits:''}${info.result?' · '+info.result:''}`;
        item.button.title=`${item.entry.node.label} · ${item.id}${item.entry.node.equipment?' · '+item.entry.node.equipment:''}\n${info.last?.Summary||'Later on this synthetic route'}\nClick for event details`;
        item.line.dataset.state=info.status;item.dot.dataset.state=info.status;
      }
      if(inspected)inspectStation(inspected);
      lastRouteStatusKey=statusKey;
    }
    const width=host.clientWidth,height=host.clientHeight;
    const top=$('route-key').offsetTop+$('route-key').offsetHeight+12;
    const current=projectPoint(parcel.position);
    const visible=[];
    for(const item of labelItems){
      item.screen=projectPoint(item.point);
      const {x,y,depth}=item.screen;
      item.visible=depth>=-1&&depth<=1&&x>=0&&x<=width&&y>=top&&y<=height-35;
      item.button.hidden=!item.visible;item.line.style.display=item.visible?'':'none';item.dot.style.display=item.visible?'':'none';
      if(item.visible)visible.push(item);
    }
    const priority={current:0,next:1,visited:2,upcoming:3};
    visible.sort((a,b)=>priority[a.info.status]-priority[b.info.status]);
    const key=[width,height,top,...visible.map(item=>item.id),statusKey,camera.zoom.toFixed(3)].join('|');
    // Reuse DOM and label offsets between layout passes; project anchors every drawn frame.
    if(key!==labelLayoutKey||now-lastLayoutTime>=160||!playing){
      const blocked=current.x>=0&&current.x<=width?[{x:current.x-43,y:current.y-36,w:86,h:60}]:[];
      const placed=arrangeLabels(visible.map(item=>({id:item.id,...item.screen,previous:item.previous})),width,height,top,blocked);
      for(const box of placed){const item=visible.find(item=>item.id===box.id);item.previous=box;item.box=box;item.button.classList.toggle('compact',box.compact);}
      labelLayoutKey=key;lastLayoutTime=now;
    }
    for(const item of visible){
      if(!item.previous)continue;
      const {x:ax,y:ay}=item.screen,w=item.box.w,h=item.box.h;
      const {x,y}=item.box;
      item.button.style.transform=`translate(${x}px,${y}px)`;
      const endX=Math.max(x,Math.min(x+w,ax)),endY=Math.max(y,Math.min(y+h,ay));
      item.line.setAttribute('d',`M${ax},${ay} L${endX},${endY}`);
      item.line.classList.toggle('highlighted',hovered?.id===item.id);
      item.dot.setAttribute('cx',ax);item.dot.setAttribute('cy',ay);
    }
    const countsText=`${visible.length}/${labelItems.length} route stations in view`;
    if($('route-count').textContent!==countsText)$('route-count').textContent=countsText;
  }
  function placeTag(id, point, text){
    const tag=$(id);if(!point){tag.style.display='none';return;}
    const v=point.clone().project(camera),x=(v.x+1)/2*host.clientWidth,y=(1-v.y)/2*host.clientHeight-20;
    if(v.z<-1||v.z>1||x<90||x>host.clientWidth-90||y<190||y>host.clientHeight-50){tag.style.display='none';return;}
    tag.style.display='block';tag.style.left=`${x}px`;tag.style.top=`${y}px`;if(tag.textContent!==text)tag.textContent=text;
  }
  function updateLabels(now){
    placeTag('tag-current',parcel.position,chosen.id);
    updateRouteLabels(now);
    const hover=$('tag-hover');
    if(!hovered){hover.style.display='none';return;}
    const p=projectPoint(world([hovered.x,hovered.y],1.5));
    const entry=routeMap.get(hovered.id),info=entry?routeStatus(entry,state):null;
    hover.textContent=`${hovered.label}\n${typeNames[hovered.kind]||hovered.kind} · ${hovered.id}${hovered.equipment?' · '+hovered.equipment:''}\n${info?.last?.Summary||(entry?'Later on selected route':'Not on this parcel’s route')}\nClick equipment for details`;
    hover.style.display='block';hover.style.left=`${Math.max(150,Math.min(host.clientWidth-150,p.x))}px`;hover.style.top=`${Math.max(310,Math.min(host.clientHeight-40,p.y-20))}px`;
  }
  function ui(){
    const status=state.done?state.phase.toUpperCase():state.leg?'IN TRANSIT':'AT EQUIPMENT';
    $('state-badge').textContent=status;$('percent').textContent=`${Math.floor(t/chosen.duration*100)}%`;
    $('progress-fill').style.width=`${t/chosen.duration*100}%`;
    $('current').textContent=state.done?chosen.completeness:state.leg?'Conveyor transfer':nodes.get(state.stop.id).label;
    $('description').textContent=state.leg?`${nodes.get(state.leg.a).label} → ${nodes.get(state.leg.b).label}`:state.event.Summary;
    $('next').textContent=state.done?'End of this replay':nodes.get(state.leg?.b||state.upcoming?.LocationId)?.label||'Final operation';
    $('expectation').textContent=state.next;$('seek').value=t;$('time').textContent=fmt(t);
    if(activeIndex!==state.index){
      routeButtons.forEach((b,i)=>{b.classList.toggle('active',i===state.index);b.classList.toggle('past',i<state.index);b.setAttribute('aria-current',i===state.index?'step':'false');});activeIndex=state.index;
    }
    $('play').textContent=playing?'Ⅱ Pause':'▶ Play';
  }
  function update(){
    state=ReplayEngine.sample(model,chosen,t);
    parcel.position.copy(world(state.xy,.535));halo.position.copy(world(state.xy,.30));
    const color=state.done?(state.phase==='completed'?0x74dbc2:state.phase==='held'?0xf5d675:0xef8585):0xffc481;
    halo.material.color.setHex(color);trail.material.color.setHex(color);
    if(state.leg){const e=edges.get(state.leg.edges[0]),f=Math.min(1,(t-state.leg.start)/(state.leg.end-state.leg.start)+.0001),q=ReplayEngine.at(e.points,f);const dx=q[0]-state.xy[0],dz=q[1]-state.xy[1];if(Math.hypot(dx,dz)>.00001)parcel.rotation.y=Math.atan2(dx,dz);}
    updateTrail(t);
    if(following)centerOn(world(state.xy,0));
  }
  function frame(now){
    frameId=0;inFrame=true;
    try {
      if(playing&&!document.hidden){t=Math.min(chosen.duration,t+(now-lastTime)/1000*Number($('speed').value));if(t>=chosen.duration)playing=false;dirty=true;}
      lastTime=now;
      if(dirty){
        update();
        if(now-lastUI>=100||!playing){ui();lastUI=now;}
        if(pendingPointer&&!dragging&&now-lastHoverTime>=60){hovered=pickAt(...pendingPointer);pendingPointer=null;lastHoverTime=now;}
        updateLabels(now);renderer.render(scene,camera);dirty=false;frames++;
        if(!playing)$('render-info').textContent=`PAUSED · ${renderer.info.render.calls} DRAW CALLS`;
        if(playing&&now-statsTime>=1000){fps=Math.round(frames*1000/(now-statsTime));$('render-info').textContent=`${fps} FPS · ${renderer.info.render.calls} DRAW CALLS`;frames=0;statsTime=now;}
      }
      inFrame=false;
      if((playing||pendingPointer)&&!document.hidden&&!frameId){dirty=true;frameId=requestAnimationFrame(frame);}
    } catch(error){inFrame=false;playing=false;fail(error);}
  }
  function pause(){playing=false;requestFrame();}
  function seek(value){pause();t=Math.max(0,Math.min(chosen.duration,value));lastUI=-Infinity;requestFrame();}
  function load(index){
    pause();chosen=model.parcels[index];t=0;activeIndex=-1;hovered=null;inspected=null;pendingPointer=null;$('station-detail').hidden=true;prepareTrail(chosen);createRouteLabels();lastUI=-Infinity;
    $('scenario').value=index;$('parcel-id').textContent=chosen.id;$('parcel-name').textContent=chosen.name;
    $('seek').max=chosen.duration;$('duration').textContent=fmt(chosen.duration);$('event-count').textContent=`${chosen.events.length} events`;
    routeButtons.length=0;$('timeline').replaceChildren();
    chosen.events.forEach((e,i)=>{const b=document.createElement('button');b.className='event';b.title=e.Summary;const time=document.createElement('time');time.textContent=fmt(e.t);const label=document.createElement('span');label.textContent=nodes.get(e.LocationId).label;b.append(time,label);b.onclick=()=>seek(e.t);routeButtons.push(b);$('timeline').append(b);});
    requestFrame();
  }
  model.parcels.forEach((p,i)=>{const o=document.createElement('option');o.value=i;o.textContent=`${p.id} · ${p.name}`;$('scenario').append(o);});
  $('scenario').onchange=()=>load(Number($('scenario').value));
  $('search-form').onsubmit=e=>{e.preventDefault();const i=model.parcels.findIndex(p=>p.id.toLowerCase()===$('search').value.trim().toLowerCase());$('search-status').textContent=i<0?'Parcel ID not found.':'';if(i>=0)load(i);};
  $('play').onclick=()=>{if(t>=chosen.duration){t=0;prepareTrail(chosen);}playing=!playing;lastTime=performance.now();statsTime=lastTime;frames=0;lastUI=-Infinity;requestFrame();};
  $('restart').onclick=()=>load(Number($('scenario').value));
  $('seek').oninput=()=>seek(Number($('seek').value));
  $('previous').onclick=()=>seek([...chosen.events].reverse().find(e=>e.t<t-.001)?.t??0);
  $('next-event').onclick=()=>seek(chosen.events.find(e=>e.t>t+.001)?.t??chosen.duration);
  $('speed').onchange=()=>{lastTime=performance.now();requestFrame();};
  $('iso').onclick=()=>setView(false);
  $('top').onclick=()=>setView(true);
  $('fit').onclick=()=>setView(isTop,true);
  $('focus').onclick=()=>{camera.zoom=3;camera.updateProjectionMatrix();centerOn(world(state.xy,0));setFollow(true);requestFrame();};
  $('follow').onclick=()=>{setFollow(!following);requestFrame();};
  document.addEventListener('visibilitychange',()=>{if(document.hidden)pause();lastTime=performance.now();});
  document.addEventListener('keydown',e=>{if(['INPUT','SELECT','BUTTON','TEXTAREA'].includes(e.target.tagName))return;if(e.code==='Space'){e.preventDefault();$('play').click();}if(e.key==='Home')$('fit').click();});
  renderer.domElement.addEventListener('webglcontextlost',e=>{e.preventDefault();pause();fail(new Error('The graphics context was lost. Reload this page or use the 2D viewer.'));});
  reduced.addEventListener('change',requestFrame);
  resize();setView(false,true);load(0);
  $('scenario').disabled=false;$('play').disabled=false;$('loading').hidden=true;
}
function fail(error){$('loading').hidden=true;$('error').hidden=false;$('error-message').textContent=error.message||'WebGL2 is required for this view.';console.error(error);}
boot().catch(fail);
