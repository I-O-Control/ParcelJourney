import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import ReplayEngine from '../replay-engine.js';
import { buildTrailSegments } from './trail.mjs';
import { routeEntries, routeStatus, contextualStation } from './route-labels.mjs';
import { fields, meanings, stateAt, groupsAt, explain, millis } from './parcel-state.mjs';
import { footprint, gridSlice } from './footprint.mjs';

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
  controls.mouseButtons.LEFT=THREE.MOUSE.PAN;
  controls.mouseButtons.RIGHT=THREE.MOUSE.ROTATE;
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
    ['ENTRY & QUALITY', 8, -12, 20, 2.5, 0x22363d],
    ['SEALING', 15, 2.7, 43, 13.8, 0x25313b],
    ['PACKAGING', 7, 15, 37, 26.8, 0x283339],
    ['HALL 04', 3.2, 29.5, 36, 36, 0x21383a],
    ['HALL 03', 3.2, 36.7, 36, 42.8, 0x213039],
    ['HALL 02', 3.2, 43.5, 39, 49, 0x25323b]
  ];
  const plantBounds=new THREE.Box3();
  const footprintPoints=[...model.nodes.map(n=>[n.x*S,n.y*S]),...model.edges.flatMap(e=>e.points.map(p=>[p[0]*S,p[1]*S])),...sections.flatMap(([,x1,z1,x2,z2])=>[[x1,z1],[x1,z2],[x2,z1],[x2,z2]])];
  const outline=footprint(footprintPoints);
  for(const n of model.nodes)plantBounds.expandByPoint(world([n.x,n.y],1.3));
  for(const e of model.edges)for(const p of e.points)plantBounds.expandByPoint(world(p,0));
  for(const [,x1,z1,x2,z2] of sections){plantBounds.expandByPoint(new THREE.Vector3(x1,0,z1));plantBounds.expandByPoint(new THREE.Vector3(x2,0,z2));}
  plantBounds.expandByVector(new THREE.Vector3(.9,.2,.9));
  const plantCenter=plantBounds.getCenter(new THREE.Vector3());
  const floorMaterial = new THREE.MeshStandardMaterial({ color: 0x16252e, roughness: 1 });
  const floorShape=new THREE.Shape(outline.map(([x,z])=>new THREE.Vector2(x,-z)));
  const floor=new THREE.Mesh(new THREE.ExtrudeGeometry(floorShape,{depth:.16,bevelEnabled:false}),floorMaterial);
  floor.rotation.x=-Math.PI/2;floor.position.y=-.48;scene.add(floor);
  for(const [,x1,z1,x2,z2,color] of sections)box(color,(x1+x2)/2,-.27,(z1+z2)/2,x2-x1,.12,z2-z1);
  const gridPoints=[];
  for(let x=Math.ceil(plantBounds.min.x);x<=plantBounds.max.x;x++){const span=gridSlice(outline,0,x);if(span)gridPoints.push(new THREE.Vector3(x,-.19,span[0]),new THREE.Vector3(x,-.19,span[1]));}
  for(let z=Math.ceil(plantBounds.min.z);z<=plantBounds.max.z;z++){const span=gridSlice(outline,1,z);if(span)gridPoints.push(new THREE.Vector3(span[0],-.19,z),new THREE.Vector3(span[1],-.19,z));}
  const grid=new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(gridPoints),new THREE.LineBasicMaterial({color:0x435778,transparent:true,opacity:.16}));
  scene.add(grid);
  const staticMaterials=new Map();
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
  const equipmentColors = { scanner:0x69bcff,scale:0xebc46b,decision:0xa78bfa,sealer:0x22c5a0,strapper:0x22c5a0,printer:0xf07840,exit:0x84cc86,exception:0xef6676,junction:0x8fa3be };
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
    mesh.computeBoundingSphere();scene.add(mesh);staticMaterials.set(color,mesh.material);
  }
  // Project section names from their floor gutters. HTML stays legible above geometry.
  const sectionItems=sections.map(([name,x1,z1,x2,z2])=>{
    const el=document.createElement('span');el.className='section-label';el.textContent=name;
    $('section-labels').append(el);
    return {el,point:name==='ENTRY & QUALITY'?new THREE.Vector3(x1+4,.35,z1+.5):new THREE.Vector3(x2-3,.35,z2-.45)};
  });
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
  let overview=true,baseSpan=36,palette={};
  function requestFrame(){dirty=true;if(!frameId&&!inFrame)frameId=requestAnimationFrame(frame);}
  function setFollow(value){following=value;$('follow').classList.toggle('selected',value);$('follow').setAttribute('aria-pressed',String(value));}
  function centerOn(point){const shift=point.clone().sub(controls.target);camera.position.add(shift);controls.target.copy(point);controls.update();}
  function safeTop(){const toolbar=document.querySelector('.view-tools');return toolbar.offsetTop+toolbar.offsetHeight+12;}
  function fitCamera(){
    camera.zoom=1;camera.updateMatrixWorld(true);
    const corners=[];
    for(const [x,z] of outline)for(const y of [0,1.5])corners.push(new THREE.Vector3(x,y,z).applyMatrix4(camera.matrixWorldInverse));
    const xs=corners.map(p=>p.x),ys=corners.map(p=>p.y),w=host.clientWidth,h=host.clientHeight;
    const minX=Math.min(...xs),maxX=Math.max(...xs),minY=Math.min(...ys),maxY=Math.max(...ys);
    const usableW=Math.max(1,w-32),usableH=Math.max(1,h-safeTop()-35);
    const units=Math.max((maxX-minX)/usableW,(maxY-minY)/usableH)*1.04;
    baseSpan=units*h/2;
    const right=new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld,0),up=new THREE.Vector3().setFromMatrixColumn(camera.matrixWorld,1);
    const shift=right.multiplyScalar((minX+maxX)/2).add(up.multiplyScalar((minY+maxY)/2+(safeTop()-35)*units/2));
    camera.position.add(shift);controls.target.add(shift);
    camera.left=-units*w/2;camera.right=units*w/2;camera.top=baseSpan;camera.bottom=-baseSpan;
    camera.updateProjectionMatrix();controls.update();requestFrame();
  }
  function setView(top,full=false){
    isTop=top;
    $('iso').classList.toggle('selected',!top);$('top').classList.toggle('selected',top);
    $('iso').setAttribute('aria-pressed',String(!top));$('top').setAttribute('aria-pressed',String(top));
    if(full){setFollow(false);controls.target.copy(plantCenter);camera.zoom=1;overview=true;}
    const delta=top?new THREE.Vector3(0,85,.01):new THREE.Vector3(14,90,65);
    camera.position.copy(controls.target).add(delta);camera.updateProjectionMatrix();controls.update();
    if(overview)fitCamera();requestFrame();
  }
  function resize(){
    const w=host.clientWidth,h=host.clientHeight;renderer.setSize(w,h);
    if(overview)fitCamera();
    else{camera.left=-baseSpan*w/h;camera.right=baseSpan*w/h;camera.top=baseSpan;camera.bottom=-baseSpan;camera.updateProjectionMatrix();requestFrame();}
  }
  new ResizeObserver(resize).observe(host);
  controls.addEventListener('change',requestFrame);
  controls.addEventListener('start',()=>{setFollow(false);overview=false;});
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
    if(raycaster.intersectObject(parcel,true).length){openParcelData();}else if(hovered){inspectStation(hovered);}requestFrame();
  });
  host.addEventListener('pointercancel',()=>{down=null;dragging=false;hovered=null;});
  const routeLayer=$('route-labels'),leaders=$('route-leaders');
  const typeNames={scanner:'Scanner',scale:'Weight check',decision:'Routing decision',sealer:'Processing',strapper:'Strapper',printer:'Label printer',exit:'Exit',exception:'Exception',junction:'Transfer'};
  let routeMap=new Map(),inspected=null,calloutKey='',calloutContent='',cardHeight=140,layerIndex=0,layerEvents=[];
  const card=document.createElement('div');card.className='station-callout';card.tabIndex=0;card.setAttribute('role','button');
  const explanationCard=document.createElement('div');explanationCard.className='station-explanation-callout';explanationCard.hidden=true;
  explanationCard.setAttribute('role','dialog');explanationCard.setAttribute('aria-label','Parcel data at replay time');
  const badge=document.createElement('span'),name=document.createElement('strong'),code=document.createElement('small'),result=document.createElement('div'),destination=document.createElement('p');
  const layerNav=document.createElement('div');layerNav.className='layer-nav';layerNav.setAttribute('role','tablist');
  badge.className='callout-phase';destination.className='callout-next';
  card.append(layerNav,badge,name,code,result,destination);routeLayer.append(card,explanationCard);
  const leader=document.createElementNS('http://www.w3.org/2000/svg','path');leader.classList.add('context-leader');leaders.append(leader);
  let activeContext=null,selectedObservation=null;
  const projectPoint=(point)=>{const v=point.clone().project(camera);return {x:(v.x+1)/2*host.clientWidth,y:(1-v.y)/2*host.clientHeight,depth:v.z};};
  function clockAt(){const first=chosen.events[0],base=millis(first.Timestamp);return Number.isFinite(base)?new Date(base+(t-first.t)*1000).toISOString().replace('T',' ').replace('Z',''):'Time unavailable';}
  function openParcelData(){pause();explanationCard.hidden=false;renderParcelData();requestFrame();}
  card.onclick=e=>{if(e.target.closest('button,details,summary'))return;openParcelData();};
  card.onkeydown=e=>{if(e.target===card&&(e.key==='Enter'||e.key===' ')){e.preventDefault();openParcelData();}};
  $('tag-current').style.pointerEvents='auto';$('tag-current').onclick=openParcelData;
  document.addEventListener('pointerdown',e=>{if(!card.contains(e.target)&&!explanationCard.contains(e.target)&&!$('tag-current').contains(e.target)){explanationCard.hidden=true;requestFrame();}});
  document.addEventListener('keydown',e=>{if(e.key==='Escape'){explanationCard.hidden=true;requestFrame();}});
  function renderParcelData(){
    const snapshot=stateAt(chosen.observations||[],t);explanationCard.replaceChildren();
    const heading=document.createElement('strong');heading.textContent='Parcel data · '+clockAt();explanationCard.append(heading);
    const close=document.createElement('button');close.textContent='Close';close.onclick=()=>{explanationCard.hidden=true;requestFrame();};explanationCard.append(close);
    const dl=document.createElement('dl');
    const labels={PrincipalID:'Customer',OrderID:'Order',ParcelID:'Parcel',ControlFlag:'Control flag',GrossWeight:'Expected weight (g)',CarrierID:'Carrier',CartonType:'Carton type',LastScanPos:'Last recorded scan',WeightStart:'Start weight (g)',WeightBrutto:'Gross weight (g)',VRNewHeight:'Reducer height (source units)',PlcTarget:'Commanded target',TrackingId:'Tracking ID',Status:'Processing status'};
    for(const key of fields){
      const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=labels[key];
      const value=snapshot.values[key];dd.textContent=value===undefined?'Not yet available':value===-1?'Not measured (source: -1)':value===''?'Empty in record':key==='Status'?(meanings[value]||'Unmapped status: '+value):String(value);
      const source=snapshot.provenance[key];if(source)dd.title=source.timestamp+' · '+source.file+':'+source.line;
      dl.append(dt,dd);
    }
    explanationCard.append(dl);
    const group=layerEvents[layerIndex];
    for(const entry of group?.entries||[]){
      const detail=document.createElement('details'),summary=document.createElement('summary');summary.textContent='Evidence · '+entry.event.sourceFile+':'+entry.event.line;detail.append(summary);
      const pre=document.createElement('pre');pre.textContent=entry.evidence.map(e=>e.Timestamp+' '+e.sourceFile+':'+e.line+'\n'+e.raw).join('\n\n');detail.append(pre);explanationCard.append(detail);
    }
    const known=(chosen.findings||[]).filter(f=>f.event.t<=t);if(known.length){const p=document.createElement('p');p.textContent=known.length+' recorded warnings up to this time. Latest: '+known.at(-1).message;explanationCard.append(p);}
  }
  function renderLayerEvent(){
    const group=layerEvents[layerIndex];layerNav.replaceChildren();result.replaceChildren();
    layerEvents.forEach((g,i)=>{const tab=document.createElement('button');tab.type='button';tab.className='layer-tab';tab.setAttribute('role','tab');tab.setAttribute('aria-selected',String(i===layerIndex));tab.textContent=g.layer==='Database'?'Db':g.layer;tab.dataset.layer=g.layer;
      tab.onclick=e=>{e.stopPropagation();pause();layerIndex=i;renderLayerEvent();};layerNav.append(tab);});
    for(const entry of group?.entries||[]){
      const p=document.createElement('p');p.textContent=explain(entry.event);result.append(p);
      if(entry.evidence.length>1){const details=document.createElement('details'),summary=document.createElement('summary');summary.textContent=entry.evidence.length+' identical observations in '+entry.event.system;details.append(summary);const text=document.createElement('p');text.textContent=entry.evidence.map(e=>e.sourceFile+':'+e.line).join('\n');details.append(text);result.append(details);}
    }
    if(!group)result.textContent='No station observation has been recorded at this moment.';
    if(!explanationCard.hidden)renderParcelData();
  }
  // Equipment inspection remains separate from the parcel's replay-time record.
  function inspectStation(node){
    inspected=node;$('station-detail').hidden=false;$('station-title').textContent=node.label;
    $('station-code').textContent=node.id+' · '+(typeNames[node.kind]||node.kind);
    const entry=routeMap.get(node.id),info=entry?routeStatus(entry,state):null;
    $('station-result').textContent=info?.last?.Summary||'No observation at this equipment yet.';
    $('station-visits').textContent=info?info.visits+' recorded visits':'';
    $('station-explanation').textContent='';
  }
  $('station-close').onclick=()=>{inspected=null;$('station-detail').hidden=true;};
  function createRouteLabels(){
    routeMap=new Map(routeEntries(chosen,nodes).map(entry=>[entry.node.id,entry]));
    calloutKey='';calloutContent='';card.classList.remove('visible');card.tabIndex=-1;
  }
  function updateRouteLabels(){
    const selected=!playing&&selectedObservation&&Math.round(selectedObservation.t*1000)===Math.round(t*1000)?selectedObservation:null;
    const moving=!!state.leg&&!selected;
    const context=selected?{node:nodes.get(selected.LocationId),key:'event:'+selected.t,last:selected,next:chosen.stops.find(s=>s.arrival>t),phase:'recorded-event'}:moving?{node:nodes.get(state.leg.b),phase:'travelling',key:'leg:'+state.leg.start,next:{id:state.leg.b},remaining:state.leg.end-t,visit:0}:contextualStation(chosen,state,nodes);activeContext=context;
    const width=host.clientWidth,height=host.clientHeight,top=safeTop();
    const anchor=context?projectPoint(moving?parcel.position:world([context.node.x,context.node.y],1.1)):null;
    const visible=anchor&&anchor.depth>=-1&&anchor.depth<=1&&anchor.x>=0&&anchor.x<=width&&anchor.y>=top&&anchor.y<=height-30;
    card.classList.toggle('visible',!!visible);card.tabIndex=visible?0:-1;
    card.setAttribute('aria-hidden',String(!visible));leader.style.display=visible?'':'none';
    let cardBox=null;
    if(visible){
      const groupKey=context.key+'|'+(context.last?.t??'')+'|'+moving;
      if(groupKey!==calloutKey){layerIndex=0;calloutKey=groupKey;}
      layerEvents=moving?[]:groupsAt(chosen.observations||[],t,context.node.id);
      if(layerIndex>=layerEvents.length)layerIndex=0;
      const textKey=groupKey+'|'+t+'|'+layerIndex;
      if(textKey!==calloutContent){
        badge.textContent=clockAt();
        name.textContent=moving?'Next target · '+context.node.label:context.node.label;
        code.textContent=context.node.id+' · '+(typeNames[context.node.kind]||context.node.kind)+(selected?' · Recorded event context':'');
        renderLayerEvent();
        if(moving){layerNav.replaceChildren();result.textContent='Last confirmed position: '+(nodes.get(state.leg.a)?.label||state.leg.a)+'. Travelling toward '+context.node.label+'. Next recorded arrival in '+Math.max(0,context.remaining).toFixed(1)+' s. Movement is schematic.';}
        destination.textContent=moving?'Next expected action: observation at '+context.node.label:context.next?'Next position → '+nodes.get(context.next.id).label:'End of the recorded journey';
        card.dataset.tone=layerEvents[layerIndex]?.entries.some(x=>x.event.values?.Status==='WEIGHTERR')?'exception':'active';
        calloutContent=textKey;cardHeight=card.offsetHeight;
      }
      const cw=card.offsetWidth,ch=cardHeight,px=projectPoint(parcel.position);
      const choices=[{x:anchor.x-cw/2,y:anchor.y-ch-40},{x:anchor.x+24,y:anchor.y-ch/2},{x:anchor.x-cw-24,y:anchor.y-ch/2},{x:anchor.x-cw/2,y:anchor.y+60}];
      const clamp=c=>({x:Math.max(10,Math.min(width-cw-10,c.x)),y:Math.max(top,Math.min(height-ch-30,c.y))});
      const candidates=choices.map(clamp);
      const score=c=>(px.x>c.x-25&&px.x<c.x+cw+25&&px.y>c.y-35&&px.y<c.y+ch+20?10000:0)+Math.hypot(c.x+cw/2-anchor.x,c.y+ch/2-anchor.y);
      candidates.sort((a,b)=>score(a)-score(b));const p=candidates[0];cardBox={...p,w:cw,h:ch};
      card.style.left=p.x+'px';card.style.top=p.y+'px';
      if(!explanationCard.hidden){const gap=12;let ex=p.x+cw+gap,ey=p.y;if(ex+330>width-10){ex=Math.max(10,p.x-330-gap);}explanationCard.style.left=ex+'px';explanationCard.style.top=ey+'px';}
      leader.setAttribute('d','M'+anchor.x+','+anchor.y+' L'+Math.max(p.x,Math.min(p.x+cw,anchor.x))+','+Math.max(p.y,Math.min(p.y+ch,anchor.y)));
    }
    const placed=[];
    for(const item of sectionItems){
      const p=projectPoint(item.point),w=item.el.offsetWidth||96,h=22;
      let x=p.x-w/2,y=p.y;
      if(placed.some(b=>Math.abs(b.x-x)<w&&Math.abs(b.y-y)<h))y+=h+3;
      const overlapsCard=cardBox&&x<cardBox.x+cardBox.w&&x+w>cardBox.x&&y<cardBox.y+cardBox.h&&y+h>cardBox.y;
      item.el.hidden=p.depth<-1||p.depth>1||x<8||x+w>width-8||y<top||y+h>height-30||!!overlapsCard;
      item.el.style.transform='translate('+x+'px,'+y+'px)';placed.push({x,y});
    }
  }
  function placeTag(id, point, text){
    const tag=$(id);if(!point){tag.style.display='none';return;}
    const v=point.clone().project(camera),x=(v.x+1)/2*host.clientWidth,y=(1-v.y)/2*host.clientHeight-20;
    if(v.z<-1||v.z>1||x<90||x>host.clientWidth-90||y<safeTop()||y>host.clientHeight-50){tag.style.display='none';return;}
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
    $('next').textContent=state.done?'End of this replay':nodes.get(state.leg?.b||state.nextStop?.id)?.label||'Final operation';
    $('expectation').textContent=state.next;$('seek').value=t;$('time').textContent=fmt(t);
    if(activeIndex!==state.index){
      routeButtons.forEach((b,i)=>{b.classList.toggle('active',i===state.index);b.classList.toggle('past',i<state.index);b.setAttribute('aria-current',i===state.index?'step':'false');});activeIndex=state.index;
    }
    $('play').textContent=playing?'Ⅱ Pause':'▶ Play';
  }
  function update(){
    state=ReplayEngine.sample(model,chosen,t);
    parcel.position.copy(world(state.xy,.535));halo.position.copy(world(state.xy,.30));
    const color=state.done?(state.phase==='completed'?palette.success:state.phase==='held'?palette.warning:palette.exception):palette.trace;
    halo.material.color.set(color);trail.material.color.set(color);
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
  function seek(value){pause();t=Math.max(0,Math.min(chosen.duration,value));selectedObservation=chosen.events.find(e=>Math.round(e.t*1000)===Math.round(t*1000))||null;lastUI=-Infinity;requestFrame();}
  function load(index){
    pause();chosen=model.parcels[index];selectedObservation=null;t=0;activeIndex=-1;hovered=null;inspected=null;pendingPointer=null;$('station-detail').hidden=true;explanationCard.hidden=true;prepareTrail(chosen);createRouteLabels();lastUI=-Infinity;
    $('scenario').value=index;$('parcel-id').textContent=chosen.id;$('parcel-name').textContent=chosen.name;
    $('seek').max=chosen.duration;$('duration').textContent=fmt(chosen.duration);$('event-count').textContent=`${chosen.events.length} events`;
    routeButtons.length=0;$('timeline').replaceChildren();
    const moments=[];const byMoment=new Map();
    chosen.events.forEach(e=>{const key=Math.round(e.t*1000)+'|'+(e.LocationId||'');let group=byMoment.get(key);if(!group){group={event:e,events:[]};byMoment.set(key,group);moments.push(group);}group.events.push(e);});
    moments.forEach(group=>{const e=group.event;const wrap=document.createElement('div');wrap.className='timeline-group';const b=document.createElement('button');b.className='event event-group';b.title=group.events.length>1?group.events.length+' simultaneous events':e.Summary;const time=document.createElement('time');time.textContent=fmt(e.t);const label=document.createElement('span');label.textContent=nodes.get(e.LocationId).label;b.append(time,label);if(group.events.length>1){const count=document.createElement('em');count.textContent=group.events.length+' events';b.append(count);}b.onclick=()=>seek(e.t);routeButtons.push(b);wrap.append(b);
      if(group.events.length>1){const expand=document.createElement('button');expand.type='button';expand.className='event-expand';expand.textContent='Show individual events';const children=document.createElement('div');children.className='timeline-children';children.hidden=true;expand.onclick=ev=>{ev.stopPropagation();children.hidden=!children.hidden;expand.textContent=children.hidden?'Show individual events':'Hide individual events';};group.events.forEach(child=>{const cb=document.createElement('button');cb.className='event event-child';cb.title=child.Summary;const ct=document.createElement('time');ct.textContent=fmt(child.t);const cl=document.createElement('span');cl.textContent=(child.Layer==='Database'?'Db':child.Layer)+' · '+(child.Summary||child.EventType);cb.append(ct,cl);cb.onclick=()=>seek(child.t);children.append(cb);routeButtons.push(cb);});wrap.append(expand,children);} $('timeline').append(wrap);});
    requestFrame();
  }
  model.parcels.forEach((p,i)=>{const o=document.createElement('option');o.value=i;o.textContent=`${p.id} · ${p.name}`;$('scenario').append(o);});
  $('scenario').onchange=()=>load(Number($('scenario').value));
  $('search-form').onsubmit=e=>{e.preventDefault();const i=model.parcels.findIndex(p=>[p.id,...(p.aliases||[])].some(id=>id.toLowerCase()===$('search').value.trim().toLowerCase()));$('search-status').textContent=i<0?'Parcel ID not found.':'';if(i>=0)load(i);};
  $('play').onclick=()=>{if(t>=chosen.duration){t=0;prepareTrail(chosen);}playing=!playing;lastTime=performance.now();statsTime=lastTime;frames=0;lastUI=-Infinity;requestFrame();};
  $('restart').onclick=()=>load(Number($('scenario').value));
  $('seek').oninput=()=>seek(Number($('seek').value));
  $('previous').onclick=()=>seek([...chosen.events].reverse().find(e=>e.t<t-.001)?.t??0);
  $('next-event').onclick=()=>seek(chosen.events.find(e=>e.t>t+.001)?.t??chosen.duration);
  $('speed').onchange=()=>{lastTime=performance.now();requestFrame();};
  $('iso').onclick=()=>setView(false);
  $('top').onclick=()=>setView(true);
  $('fit').onclick=()=>setView(isTop,true);
  $('focus').onclick=()=>{overview=false;camera.zoom=3;camera.updateProjectionMatrix();centerOn(world(state.xy,0));setFollow(true);requestFrame();};
  $('follow').onclick=()=>{setFollow(!following);requestFrame();};
  document.addEventListener('visibilitychange',()=>{if(document.hidden)pause();lastTime=performance.now();});
  document.addEventListener('keydown',e=>{if(['INPUT','SELECT','BUTTON','TEXTAREA'].includes(e.target.tagName))return;if(e.code==='Space'){e.preventDefault();$('play').click();}if(e.key==='Home')$('fit').click();});
  renderer.domElement.addEventListener('webglcontextlost',e=>{e.preventDefault();pause();fail(new Error('The graphics context was lost. Reload this page or use the 2D viewer.'));});
  reduced.addEventListener('change',requestFrame);
  const systemTheme=matchMedia('(prefers-color-scheme: light)');
  let themeChoice='system';try{themeChoice=localStorage.getItem('parceljourney-theme')||'system';}catch{}
  function applyTheme(){
    const light=themeChoice==='light'||themeChoice==='system'&&systemTheme.matches;
    document.body.classList.toggle('light',light);document.body.dataset.theme=light?'light':'dark';
    $('theme').textContent='Theme: '+themeChoice;
    const css=getComputedStyle(document.body);
    palette=Object.fromEntries(['map','surface','control','line','tube','trace','success','warning','exception'].map(k=>[k,css.getPropertyValue('--'+k).trim()]));
    renderer.setClearColor(palette.map);floorMaterial.color.set(palette.tube);grid.material.color.set(palette.line);
    for(const [original,material] of staticMaterials){
      if(sections.some(s=>s[5]===original))material.color.set(palette.surface).lerp(new THREE.Color(palette.tube),.65);
      else if([0x344752,0x263640,0x172b35,0x273b44].includes(original))material.color.set(palette.control);
      else if([0x58717a,0x7b9195,0x728789].includes(original))material.color.set(light?'#7899b5':'#526b89');
    }
    carton.material.color.set(palette.trace);
    requestFrame();
  }
  $('theme').onclick=()=>{themeChoice=themeChoice==='system'?'light':themeChoice==='light'?'dark':'system';try{if(themeChoice==='system')localStorage.removeItem('parceljourney-theme');else localStorage.setItem('parceljourney-theme',themeChoice);}catch{}applyTheme();};
  systemTheme.addEventListener('change',()=>{if(themeChoice==='system')applyTheme();});
  addEventListener('storage',e=>{if(e.key==='parceljourney-theme'){themeChoice=e.newValue||'system';applyTheme();}});
  applyTheme();resize();setView(false,true);load(0);
  $('scenario').disabled=false;$('play').disabled=false;$('loading').hidden=true;
}
function fail(error){$('loading').hidden=true;$('error').hidden=false;$('error-message').textContent=error.message||'WebGL2 is required for this view.';console.error(error);}
boot().catch(fail);
