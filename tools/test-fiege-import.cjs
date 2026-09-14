const assert=require('node:assert/strict'),fs=require('node:fs');
const m=JSON.parse(fs.readFileSync(require('node:path').join(__dirname,'../analysis/fiege-replay-model.json'),'utf8'));
assert.equal(m.parcels.length,10);
assert.equal(new Set(m.parcels.map(p=>p.id)).size,10);
assert.equal(m.sourceManifest.length,226);
const edges=new Map(m.edges.map(e=>[e.id,e]));
for(const p of m.parcels){
  assert.equal(p.source,'real-logs');assert(!p.id.startsWith('SIM'));
  assert.equal(p.events[0].LocationId,'SC_START');
  assert(p.proof.registration.RawLine.includes('INSERT (tudata)'));
  assert(p.proof.closed.RawLine.includes('Status=CLOSED'));
  assert.equal(p.proof.laterMovement,0);
  assert(p.proof.checkedThrough>p.proof.lastOccurrence);
  assert(p.aliases.includes(p.id));assert(p.aliases.length>1);
  for(const e of p.events){
    assert(e.Timestamp);assert(e.Evidence.length);
    assert(e.Evidence.every(x=>x.LineNumber>0&&x.FileName.endsWith('.log')&&x.RawLine.startsWith(e.Timestamp)));
    assert(!e.EventType.startsWith('Synthetic'));
  }
  for(let i=1;i<p.events.length;i++)assert(p.events[i].t>=p.events[i-1].t);
  for(const l of p.legs){
    assert(l.end>l.start);assert(edges.has(l.edges[0]));
    assert(p.stops.some(s=>s.id===l.b&&s.arrival===l.end));
    assert(edges.get(l.edges[0]).evidence.length);
  }
}
assert(edges.has('SC_H4RU>SC_H3RU'));
assert(edges.has('SC_H3RU>SC_H3T1'));
assert(edges.has('SC_H2T9>SC_H4T1'));
assert(!edges.has('SC_H4RU>SC_H3T1'));
for(const id of ['VR_2220-1','VR_2340-1','VR_2370-1','VR_2405-1','VR_2440-1','SC_LINE3','SC_LINE4','SC_LINE5']){
 assert(m.nodes.some(n=>n.id===id));
 assert(m.parcels.some(p=>p.stops.some(s=>s.id===id)));
}
const engine=require('./replay-engine.js');
assert.equal(engine.sample(m,m.parcels[0],0).nextStop.id,'SC_WAAP');
for(const line of [3,4,5]){
 const side=m.nodes.find(n=>n.id===`SC_ETIOSL${line}`),top=m.nodes.find(n=>n.id===`SC_ETIOTL${line}`);
 assert.deepEqual([side.x,side.y],[top.x,top.y]);
 assert.equal(side.equipment.split('-')[0],top.equipment.split('-')[0]);
}
console.log('PASS: 10 source-backed complete lifecycles, 226-file audit, aliases, evidence, recorded route order and co-located readers.');
