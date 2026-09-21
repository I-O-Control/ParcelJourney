const demoHeader=document.querySelector('header');const demoSubtitle=demoHeader?.querySelector('p');if(demoSubtitle)demoSubtitle.textContent='Real log replay demo';const demoNotice=document.querySelector('.notice');if(demoNotice)demoNotice.textContent='Real log replay demo · Event times are recorded; positions between observed equipment are schematic.';if(demoHeader&&!demoHeader.querySelector('.view3d-link')&&!demoHeader.querySelector('.header-right a[href^="/3d"]')){const view3d=document.createElement('a');view3d.className='view3d-link';view3d.href='/3d';view3d.textContent='Explore 3D ↗';view3d.style.cssText='margin-left:auto;color:var(--trace);font-size:13px;text-decoration:none';demoHeader.append(view3d)}
function updateMission(){
 if(!latestState||!p)return;
 const s=latestState,edge=s.leg?E[s.leg.edges[0]]:null;
 $('mission-phase').textContent=s.done?s.phase.toUpperCase():s.leg?'IN TRANSIT':'AT EQUIPMENT';
 $('mission-current').textContent=s.done?p.completeness:s.leg?(edge.purpose||'Conveyor transfer'):N[s.stop.id].label;
 const currentFact=operatorEvent(s.event);$('mission-description').textContent=s.leg?`${N[s.leg.a].label} → ${N[s.leg.b].label}`:currentFact.action+' · '+currentFact.role;
 const target=s.leg?s.leg.b:s.nextStop?.id;
 $('mission-next').textContent=s.done?'End of this replay':target?N[target].label:'Finishing operation';
 $('mission-expectation').textContent=s.next;
 $('mission-id').textContent=p.id;
 $('mission-time').textContent=fmt(t)+' / '+fmt(p.duration);
 $('mission-progress').textContent=`${Math.floor(t/p.duration*100)}% · ${$('speed').value}× · ${playing?'Playing':'Paused'}`;
 $('summary').textContent=p.name+'\n'+p.id+' · Journey replay';
 document.body.classList.toggle('running',playing);
}
const basePause=pause;pause=function(){basePause();document.body.classList.toggle('running',false);if(latestState)updateMission()};
const basePlay=$('play').onclick;$('play').onclick=()=>{basePlay();updateMission()};
$('speed').onchange=updateMission;
// A second presentation of the same transport path: regular direction arrows
// make return corridors legible without relying on left/right screen direction.
for(const edge of M.edges){const total=ReplayEngine.length(edge.points);for(let d=65;d<total-20;d+=150){const a=ReplayEngine.at(edge.points,(d-5)/total),b=ReplayEngine.at(edge.points,d/total);el('path',{d:path([a,b]),stroke:'#8196aa','stroke-width':1.5,'marker-end':'url(#arrow)','pointer-events':'none'},$('pipes'))}}
document.addEventListener('keydown',e=>{
 if(['INPUT','SELECT','TEXTAREA','BUTTON'].includes(e.target?.tagName)||e.ctrlKey||e.altKey||e.metaKey)return;
 if(e.code==='Space'){e.preventDefault();$('play').click()}
 if(e.key?.toLowerCase()==='f')$('follow').click();
 if(e.key==='Home')$('fit').click();
});
$('play').setAttribute('title','Play / pause (Space)');$('follow').setAttribute('title','Follow parcel (F)');$('fit').setAttribute('title','Fit complete plant (Home)');
