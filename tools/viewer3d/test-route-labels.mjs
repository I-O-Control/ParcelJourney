import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { routeEntries, routeStatus, arrangeLabels } from './route-labels.mjs';
const require=createRequire(import.meta.url), engine=require('../replay-engine.js');
const model=JSON.parse(await readFile(new URL('../../analysis/synthetic-replay-model.json',import.meta.url),'utf8'));
const nodes=new Map(model.nodes.map(n=>[n.id,n]));
let samples=0,loopChecks=0;
for(const p of model.parcels){
  const entries=routeEntries(p,nodes);
  assert.equal(entries.length,new Set(p.stops.map(s=>s.id)).size);
  assert(entries.every(e=>p.stops.some(s=>s.id===e.node.id)));
  assert(p.events.every(e=>entries.some(entry=>entry.node.id===e.LocationId)));
  const times=[0,p.duration,...p.stops.flatMap(s=>[s.arrival,Math.max(0,s.arrival-.001),s.departure]),...p.legs.map(l=>(l.start+l.end)/2)];
  for(const t of [...times,...times.toReversed()]){
    const state=engine.sample(model,p,t);
    const infos=entries.map(e=>routeStatus(e,state));
    assert.equal(infos.filter(i=>i.status==='current').length,state.leg?0:1);
    if(state.leg)assert.equal(infos.filter(i=>i.status==='next').length,1);
    for(let i=0;i<entries.length;i++){
      const entry=entries[i],info=infos[i];
      assert.equal(info.visits,entry.stops.filter(s=>s.arrival<=state.t).length);
      assert.equal(info.last,entry.events.filter(e=>e.t<=state.t).at(-1));
      assert(!info.last||info.last.t<=state.t,'No future result disclosure');
      if(!info.visits)assert(['upcoming','next'].includes(info.status));
      if(info.status==='visited')assert(info.visits>0);
      if(info.visits>1)loopChecks++;
      samples++;
    }
  }
}
const overlaps=(a,b)=>a.x<b.x+b.w&&a.x+a.w>b.x&&a.y<b.y+b.h&&a.y+a.h>b.y;
let layouts=0;
for(const [width,height,top,count] of [[668,768,208,17],[1100,756,215,42],[668,768,208,42],[390,630,208,17]]){
  const items=Array.from({length:count},(_,i)=>({id:String(i),x:width/2+Math.sin(i)*80,y:top+80+i*4}));
  const blocked=[{x:width/2-43,y:height/2-36,w:86,h:60}];
  const boxes=arrangeLabels(items,width,height,top,blocked);
  assert.equal(boxes.length,count);
  assert.equal(new Set(boxes.map(b=>b.id)).size,count);
  for(const [i,b] of boxes.entries()){
    assert(b.x>=8&&b.y>=top&&b.x+b.w<=width-8&&b.y+b.h<=height-35,'Label within viewport');
    assert(!blocked.some(a=>overlaps(a,b)),'Parcel remains visible');
    assert(!boxes.slice(i+1).some(a=>overlaps(a,b)),`No label overlaps: ${width}x${height}, ${count} stations, ${b.id}`);
  }
  const repeat=arrangeLabels(items.map((item,i)=>({...item,previous:boxes[i]})),width,height,top,blocked);
  assert.deepEqual(repeat,boxes,'Stationary layout is stable');
  layouts++;
}
console.log(`PASS: ${samples} route-label states across ${model.parcels.length} scenarios; ${loopChecks} repeat-visit checks, forward/backward replay; ${layouts} non-overlapping desktop/mobile layouts.`);
