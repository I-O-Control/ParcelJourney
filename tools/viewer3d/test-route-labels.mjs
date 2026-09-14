import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { routeEntries, routeStatus, contextualStation } from './route-labels.mjs';
const require=createRequire(import.meta.url), engine=require('../replay-engine.js');
const model=JSON.parse(await readFile(new URL('../../analysis/fiege-replay-model.json',import.meta.url),'utf8'));
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

let contexts=0,gaps=0;
for(const p of model.parcels){
  const times=[0,p.duration,...p.stops.flatMap(s=>[s.arrival,s.departure]),...p.legs.flatMap(l=>[l.start+.001,(l.start+l.end)/2,l.end-.001])];
  for(const time of [...times,...times.toReversed()]){
    const state=engine.sample(model,p,time),context=contextualStation(p,state,nodes);
    if(!context){assert(state.leg);gaps++;continue;}
    contexts++;
    if(context.phase==='approaching'){
      assert(state.leg);assert.equal(context.node.id,state.leg.b);
      assert(context.remaining<=3.001);assert.equal(context.last,null);
    }else{
      assert(context.stop.arrival<=state.t);
      assert(!context.last||context.last.t>=context.stop.arrival&&context.last.t<=state.t);
      if(context.phase==='departed-grace')assert(state.leg&&state.t-state.leg.start<=1.251);
    }
    assert(context.visit>=1);
  }
}
assert(gaps>0,'Long transfers hide station callouts');
console.log(`PASS: ${samples} route-label states across ${model.parcels.length} scenarios; ${contexts} contextual callouts, ${gaps} hidden transfer intervals; ${loopChecks} repeat-visit checks.`);
