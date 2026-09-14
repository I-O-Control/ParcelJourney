/* DOM contract test, not a browser or screenshot test. */
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const html=fs.readFileSync(path.join(__dirname,'../analysis/equipment-replay.html'),'utf8');
const registry=new Map(),all=[];
class Element {
 constructor(tag='div'){this.tag=tag;this.children=[];this.attributes={};this.dataset={};this.style={};this.value='';this.offsetWidth=285;this.offsetHeight=100;this.classes=new Set();this.classList={toggle:(c,on)=>on?this.classes.add(c):this.classes.delete(c),add:c=>this.classes.add(c),remove:c=>this.classes.delete(c)};all.push(this)}
 set id(v){this._id=v;registry.set(v,this)} get id(){return this._id}
 setAttribute(k,v){this.attributes[k]=v;if(k==='id')this.id=v}
 append(...ns){for(const n of ns){this.children.push(n);if(this.tag==='select'&&this.children.length===1)this.value=n.value!==''?n.value:n.textContent}}
 replaceChildren(...ns){this.children=[];this.append(...ns)}
 get firstChild(){return this.children[0]}
 addEventListener(){} setPointerCapture(){} getScreenCTM(){return{inverse(){return this}}}
 getBoundingClientRect(){return{x:0,y:0,left:0,top:0,width:1200,height:800}}
 click(){this.onclick?.()}
}
for(const match of html.matchAll(/<([a-z]+)[^>]*\bid="([^"]+)"[^>]*>/g)){const e=new Element(match[1]);e.id=match[2]}
registry.get('speed').value='1';registry.get('legend').append({textContent:''});
const controls=new Element(),body=new Element();
const document={body,hidden:false,getElementById:id=>registry.get(id),createElement:tag=>new Element(tag),createElementNS:(_,tag)=>new Element(tag),createTextNode:text=>({textContent:text}),querySelector:s=>s==='.controls'?controls:null,querySelectorAll:s=>s==='.event'?registry.get('events').children:[],addEventListener(){}};
let frame,clock=0;const window={addEventListener(){}};
const context=vm.createContext({document,window,location:{protocol:'file:'},DOMPoint:class{constructor(x,y){this.x=x;this.y=y}matrixTransform(){return this}},performance:{now:()=>clock},requestAnimationFrame:fn=>frame=fn,console});
vm.runInContext(html.match(/<script>([\s\S]*?)<\/script>/)[1],context);
const ui=window.replayTest;
assert.equal(registry.get('pick').children.length,10);
for(let i=0;i<10;i++){
 ui.load(i);assert.equal(ui.state.t,0);
 registry.get('play').click();clock+=1000;frame(clock);assert.equal(ui.state.t,1);
 const p=ui.model.parcels[i];ui.seek(p.duration);assert(registry.get('current').textContent.includes(p.completeness));
 assert.equal(registry.get('mission-current').textContent,p.completeness);
 assert.equal(registry.get('mission-next').textContent,'End of this replay');
 registry.get('reset').click();assert.equal(ui.state.t,0);assert.equal(registry.get('trail').children.length,0);
 assert.equal(registry.get('mission-current').textContent,'Entry scan + start scale');
 assert(registry.get('mission-next').textContent);
 registry.get('next').click();assert(ui.state.t>0);registry.get('previous').click();assert.equal(ui.state.t,0);
}
registry.get('search').value=ui.model.parcels[9].id;registry.get('find').click();assert.equal(registry.get('pick').value,9);
registry.get('fit').click();assert.equal(ui.state.following,false);
registry.get('follow').click();assert.equal(ui.state.following,true);
registry.get('history').checked=false;registry.get('history').onchange();
for(const angle of [37,90,180,-90,-123,0]){
 registry.get('rotation').value=angle;registry.get('rotation').oninput();
 const normalized=((angle+180)%360+360)%360-180;
 assert.equal(registry.get('pipes').attributes.transform,`rotate(${normalized})`);
 assert.equal(registry.get('trail').attributes.transform,`rotate(${normalized})`);
 ui.seek(20);const [x,y]=ui.state.xy,a=normalized*Math.PI/180;
 const expected=[x*Math.cos(a)-y*Math.sin(a),x*Math.sin(a)+y*Math.cos(a)];
 const transform=registry.get('parcel').attributes.transform;
 const actual=transform.slice(10,-1).split(',').map(Number);
 assert(Math.hypot(actual[0]-expected[0],actual[1]-expected[1])<.0001);
}
registry.get('rotate-reset').click();assert.equal(registry.get('pipes').attributes.transform,'rotate(0)');
registry.get('theme').click();assert(document.body.classes.has('light'));
registry.get('theme').click();assert(!document.body.classes.has('light'));
assert(!registry.get('status').textContent);
console.log('PASS: complete generated script initializes; 10 real parcel Play/Restart/Next/Previous/end-state cycles; ID lookup; fit/follow/history controls. DOM contract only, not visual browser QA.');
