import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { buildTrailSegments } from './trail.mjs';
const require=createRequire(import.meta.url), engine=require('../replay-engine.js');
const model=JSON.parse(await readFile(new URL('../../analysis/fiege-replay-model.json',import.meta.url),'utf8'));
const edges=new Map(model.edges.map(e=>[e.id,e]));
let samples=0,maxSegments=0;
for(const p of model.parcels){
 const segments=buildTrailSegments(edges,p);maxSegments=Math.max(maxSegments,segments.length);
 assert(segments.length<=1024);
 const times=[0,p.duration,...segments.flatMap(s=>[s.start,s.start+.00001,(s.start+s.end)/2,s.end])];
 // Reversed order includes backward seek and restarting after completion.
 for(const t of [...times,...times.toReversed()]){
  const state=engine.sample(model,p,t),tail=segments.filter(s=>s.start<t).at(-1);
  if(!tail)continue;
  const f=Math.min(1,(t-tail.start)/(tail.end-tail.start));
  const xy=tail.a.map((v,i)=>v+(tail.b[i]-v)*f);
  assert(Math.hypot(xy[0]-state.xy[0],xy[1]-state.xy[1])<1e-6,`${p.id} trail/parcel mismatch at ${t}`);
  samples++;
 }
}
console.log(`PASS: ${samples} 3D trail endpoints match shared replay positions across ${model.parcels.length} scenarios; maximum ${maxSegments} instances, including backward seeks.`);
