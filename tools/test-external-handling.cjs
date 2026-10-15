const assert = require('node:assert/strict');
const engine = require('./replay-engine.js');
const model = {nodes: [{id:'A',x:0,y:0,label:'A'}, {id:'B',x:100,y:100,label:'B'}], edges:[]};
const parcel = {duration:20, events:[{t:0,Summary:'A'},{t:10,Summary:'B'}], stops:[{id:'A',arrival:0,departure:2},{id:'B',arrival:10,departure:20}], legs:[{a:'A',b:'B',start:2,end:10,edges:[],routeStatus:'unknown',possibleExternalHandling:true}]};
for (const t of [2,5,9.999]) {
  const s=engine.sample(model,parcel,t);
  assert.equal(s.positionKnown,false);
  assert.equal(s.possibleExternalHandling,true);
  assert.equal(s.history.length,0);
  assert.match(s.current,/possible external handling/);
}
const arrived=engine.sample(model,parcel,10);
assert.equal(arrived.positionKnown,true);
assert.deepEqual(arrived.xy,[100,100]);
assert.equal(arrived.history.length,0);
parcel.legs[0].possibleExternalHandling=false;
assert.equal(engine.sample(model,parcel,5).phase,'route-unknown');
console.log('PASS: external-handling gaps have no interpolated position or connecting trail; parcel reappears at the next recorded arrival; ambiguous gaps retain their own label.');
