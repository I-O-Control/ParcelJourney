// Route membership is scenario-specific. Visits/results are derived only from replay time.
export function routeEntries(parcel, nodes) {
  const entries=new Map();
  for(const stop of parcel.stops){
    if(!entries.has(stop.id))entries.set(stop.id,{node:nodes.get(stop.id),stops:[],events:[]});
    entries.get(stop.id).stops.push(stop);
  }
  for(const event of parcel.events)entries.get(event.LocationId)?.events.push(event);
  return [...entries.values()].filter(entry=>entry.node);
}

export function routeStatus(entry, state) {
  const visits=entry.stops.filter(stop=>stop.arrival<=state.t).length;
  const current=!state.leg&&state.stop.id===entry.node.id;
  const next=!state.done&&(state.leg?.b||state.upcoming?.LocationId)===entry.node.id&&!current;
  const status=current?'current':next?'next':visits?'visited':'upcoming';
  const last=entry.events.filter(event=>event.t<=state.t).at(-1);
  const result=last?.Summary.match(/\b(WEIGHTOK|WEIGHTERR|PENDING)\b|→\s*(NR|PF|ND)\b/);
  return {status,visits,last,result:result?.[1]||result?.[2]||'',caption:current?(state.done?'Final stop':'Here now'):next?'Next stop':visits?'Visited':'Later on route'};
}

const overlap=(a,b)=>a.x<b.x+b.w+3&&a.x+a.w+3>b.x&&a.y<b.y+b.h+3&&a.y+a.h+3>b.y;
// Finite screen slots avoid expensive iterative layout. Every in-view route node gets a label.
export function arrangeLabels(items,width,height,top=180,blocked=[]) {
  let compact=false,w=144,h=36;
  function makeSlots(){
    const cols=Math.max(1,Math.floor((width-16)/(w+6))),result=[];
    for(let y=top;y+h<=height-35;y+=h+6)for(let col=0;col<cols;col++){
      const x=8+col*(width-16-w)/Math.max(1,cols-1);
      const slot={x,y,w,h};if(!blocked.some(b=>overlap(slot,b)))result.push(slot);
    }
    return result;
  }
  let slots=makeSlots();
  if(slots.length<items.length){compact=true;w=112;h=30;slots=makeSlots();}
  // Prefer two edge rails, leaving the conveyor and parcel visible in the middle.
  const rails=slots.filter(s=>s.x===8||Math.abs(s.x-(width-8-w))<.01);
  if(width>=550&&rails.length>=items.length)slots=rails;
  const placed=[];
  for(const item of items){
    let best=null,score=Infinity;
    for(const c of slots){
      const distance=Math.hypot(c.x+w/2-item.x,c.y+h/2-item.y);
      const previous=item.previous&&Math.abs(item.previous.x-c.x)<1&&Math.abs(item.previous.y-c.y)<1;
      const cost=distance-(previous?45:0);
      if(cost<score){score=cost;best=c;}
    }
    // At unusually tiny sizes, keep a visible label; the persistent route list also remains usable.
    best||={x:8,y:Math.max(top,height-h-40),w,h};
    const box={...best,id:item.id,compact};placed.push(box);slots=slots.filter(s=>s!==best);
  }
  return placed;
}
