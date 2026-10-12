const fs=require('node:fs'),assert=require('node:assert/strict');
const engine=require('./replay-engine.js');
const m=JSON.parse(fs.readFileSync(require('node:path').join(__dirname,'../analysis/fiege-replay-model.json'),'utf8'));
const near=(a,b)=>Math.hypot(a[0]-b[0],a[1]-b[1])<.001;
let samples=0,boundaries=0;
for(const p of m.parcels){
  assert.equal(engine.sample(m,p,0).stop.id,'SC_START');
  const times=new Set([0,p.duration,...p.events.map(e=>e.t)]);
  for(const l of p.legs)for(const t of [l.start-.00001,l.start,l.start+.00001,l.end-.00001,l.end,l.end+.00001])if(t>=0&&t<=p.duration)times.add(t);
  for(let t=0;t<p.duration;t+=.5)times.add(t);
  for(const t of times){
    const s=engine.sample(m,p,t);samples++;
    assert(s.xy.every(Number.isFinite));assert(s.event.t<=t);assert(!s.upcoming||s.upcoming.t>=t);
    if(s.liquid.length){assert(near(s.xy,s.liquid.at(-1)));assert(s.history.length>0)}
    if(!s.history.length)assert.equal(s.liquid.length,0);
    const latestLeg=p.legs.filter(l=>l.start<t).at(-1);
    if(s.history.length&&!s.done&&latestLeg&&latestLeg.routeStatus!=='unknown'&&latestLeg.edges?.length)assert(s.liquid.length>0,'arrival dwell preserves the last mapped belt');
    if(s.done)assert.deepEqual(s.bright,s.history,'final state highlights the entire travelled route');
    if(s.done){assert.equal(s.phase,p.outcome);assert.equal(s.current,p.completeness);assert(!s.next.includes('Then:'))}
    else assert(!s.current.includes('Completed synthetic route'));
  }
  for(const l of p.legs){
    const preceding=p.legs.filter(q=>q.end<=l.start).at(-1);
    if(!preceding||!(preceding.routeStatus==='unknown'||!preceding.edges?.length)){
      const before=engine.sample(m,p,l.start-.000001).xy,after=engine.sample(m,p,l.start+.000001).xy;
      assert(Math.hypot(before[0]-after[0],before[1]-after[1])<.01,`${p.id}: mapped route start remains continuous`);
    }
    if(l.routeStatus==='unknown'||!l.edges?.length){
      const mid=engine.sample(m,p,(l.start+l.end)/2),source=m.nodes.find(n=>n.id===l.a),target=m.nodes.find(n=>n.id===l.b);
      assert(mid.routeUnknown);assert.equal(mid.positionKnown,false,'unknown physical position must not be rendered as a stationary or flying parcel');assert.deepEqual(mid.xy,[source.x,source.y]);
      assert(near(engine.sample(m,p,l.end).xy,[target.x,target.y]));
    }else {
      const before=engine.sample(m,p,l.end-.000001).xy,after=engine.sample(m,p,l.end+.000001).xy;
      assert(Math.hypot(before[0]-after[0],before[1]-after[1])<.01,`${p.id}: mapped route end remains continuous`);
    }
    boundaries+=2;
  }
  if(p.fixture.loop||p.fixture.unknown)assert(p.events.find(e=>e.LocationId==='SC_VL1').Summary.startsWith('WG'));
  if(p.fixture.sealnr)assert(p.events.find(e=>e.LocationId==='SC_VL1').Summary.includes('08'));
  // Reverse seeking must be independent of the previous frame / selected parcel.
  const forward=engine.sample(m,p,p.duration*.31);engine.sample(m,p,p.duration);
  assert.deepEqual(engine.sample(m,p,p.duration*.31),forward);
}
const report={parcels:m.parcels.length,samples,boundaries,result:'PASS',checks:['all event boundaries','no position jumps','liquid ends at parcel','bright section persists during dwell','full route bright at end','correct terminal states','no future event labels','backward seek deterministic','recirculation/no-read decision messages']};
let unresolved=0;
for(const p of m.parcels)for(const leg of p.legs)if(leg.routeStatus==='unknown'||!leg.edges?.length){
  unresolved++;const t=(leg.start+leg.end)/2,state=engine.sample(m,p,t),source=m.nodes.find(n=>n.id===leg.a);
  assert.equal(state.routeUnknown,true,'unmapped scanner transition is identified');
  assert.deepEqual(state.xy,[source.x,source.y],'unknown transition does not draw or animate an invented route');
  assert.equal(state.leg.edges.length,0,'unknown transition has no synthetic geometry edge');
}
report.unresolvedTransitions=unresolved;report.checks.push('unmapped scanner transitions remain at last known position and are not drawn as physical routes');
console.log(JSON.stringify(report,null,2));
