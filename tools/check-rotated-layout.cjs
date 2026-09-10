const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const layout=require('./label-layout.js'),replay=require('./replay-engine.js');
const root=path.join(__dirname,'..'),m=JSON.parse(fs.readFileSync(path.join(root,'analysis/synthetic-replay-model.json'),'utf8'));
const n=Object.fromEntries(m.nodes.map(n=>[n.id,n]));
assert.deepEqual([n.SC_START.x,n.SC_START.y],[740,-470]);
assert.deepEqual([n.SC_WAVL.x,n.SC_WAVL.y],[740,170]);
assert.deepEqual([n.SC_VL1.x,n.SC_VL1.y],[820,170]);
assert.deepEqual([n.SC_VL2.x,n.SC_VL2.y],[1460,170]);
assert.deepEqual([n.PACK.x,n.PACK.y],[400,770]);
assert.deepEqual([n.SC_BWL1.x,n.SC_BWL1.y],[790,1050]);
for(const e of m.edges){assert.deepEqual(e.points[0],[n[e.a].x,n[e.a].y]);assert.deepEqual(e.points.at(-1),[n[e.b].x,n[e.b].y])}
let seed=421337;const random=()=>((seed=(1664525*seed+1013904223)>>>0)/4294967296);
const snapshots=[];let labels=0;
for(const size of [[1200,800],[800,650],[480,650],[1536,760],[2048,1100]])for(const angle of [0,90,180,-90,37,-123])for(let k=0;k<8;k++){
 const p=m.parcels[Math.floor(random()*m.parcels.length)],state=replay.sample(m,p,random()*p.duration),[width,height]=size;
 const center=layout.rotate(state.xy,angle),scale=width/1100;
 const screen=pt=>{const q=layout.rotate(pt,angle);return[(q[0]-center[0])*scale+width/2,(q[1]-center[1])*scale+height/2]};
 const items=m.nodes.map(n=>({...n,x:screen([n.x,n.y])[0],y:screen([n.x,n.y])[1]})).filter(n=>n.x>0&&n.x<width&&n.y>0&&n.y<height);
 const routes=[...state.history];if(state.leg)routes.push(m.edges.find(e=>e.id===state.leg.edges[0]).points);
 const segments=routes.flatMap(ps=>ps.slice(1).map((b,i)=>[screen(ps[i]),screen(b)]));
 const blocked=[{x:width/2-29,y:height/2-29,w:58,h:58}];
 const boxes=layout.arrange(items,width,height,blocked,segments);
 for(let i=0;i<boxes.length;i++){
  const a=boxes[i];for(const b of boxes.slice(i+1))assert(!layout.overlap(a,b),'label overlap');
  for(const b of blocked)assert(!layout.overlap(a,b),'parcel/card overlap');
  for(const b of items)assert(!layout.overlap(a,{x:b.x-14,y:b.y-14,w:28,h:28}),'equipment overlap');
  for(const s of segments)assert(!layout.crosses(s[0],s[1],a),'route overlap');
  assert(a.x>=0&&a.y>=0&&a.x+a.w<=width&&a.y+a.h<=height);
 }
 labels+=boxes.length;
 snapshots.push({id:p.id,time:state.t,angle,width,height,boxes,items,parcel:screen(state.xy),pipes:m.edges.map(e=>e.points.map(screen)),route:state.history.map(ps=>ps.map(screen))});
}
const overview={width:1200,height:1000},bounds=layout.bounds(m.edges.flatMap(e=>e.points)),scale=Math.min(overview.width/bounds[2],overview.height/bounds[3]);
const screen=pt=>[(pt[0]-bounds[0])*scale,(pt[1]-bounds[1])*scale];
overview.pipes=m.edges.map(e=>e.points.map(screen));overview.items=m.nodes.map(n=>({...n,x:screen([n.x,n.y])[0],y:screen([n.x,n.y])[1]}));
fs.writeFileSync(path.join(root,'analysis/rotation-layout-samples.json'),JSON.stringify({overview,snapshots}));
console.log(`PASS: ${snapshots.length} sampled views across 6 angles / 5 viewport sizes including 1080p and 2K map areas; ${labels} label boxes clear of other labels, equipment, parcel and route; fixed downstream anchors; connected rotated section.`);
