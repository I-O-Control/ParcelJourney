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

// Replay seconds and schematic distance: independent of camera, frame rate and zoom.
export const CALLOUT_TIMING={approachSeconds:3,approachDistance:150,graceSeconds:1.25,graceDistance:90};
export function contextualStation(parcel,state,nodes,settings=CALLOUT_TIMING){
  let stop,index,phase;
  if(!state.leg){
    index=parcel.stops.findLastIndex(s=>s.arrival<=state.t);
    stop=parcel.stops[index];phase='at-station';
  }else{
    const target=nodes.get(state.leg.b);
    const remaining=state.leg.end-state.t;
    const distance=Math.hypot(state.xy[0]-target.x,state.xy[1]-target.y);
    if(remaining<=settings.approachSeconds&&distance<=settings.approachDistance){
      index=parcel.stops.findIndex(s=>s.id===state.leg.b&&Math.abs(s.arrival-state.leg.end)<.001);
      stop=parcel.stops[index];phase='approaching';
    }else{
      index=parcel.stops.findLastIndex(s=>s.arrival<=state.leg.start);
      stop=parcel.stops[index];
      const source=nodes.get(stop.id);
      if(state.t-state.leg.start>settings.graceSeconds||Math.hypot(state.xy[0]-source.x,state.xy[1]-source.y)>settings.graceDistance)return null;
      phase='departed-grace';
    }
  }
  if(!stop)return null;
  const last=phase==='approaching'?null:parcel.events.filter(e=>e.LocationId===stop.id&&e.t>=stop.arrival&&e.t<=state.t).at(-1)||null;
  return {node:nodes.get(stop.id),stop,index,phase,last,next:parcel.stops[index+1]||null,
    visit:parcel.stops.slice(0,index+1).filter(s=>s.id===stop.id).length,
    key:stop.id+':'+stop.arrival,remaining:phase==='approaching'?stop.arrival-state.t:Math.max(0,stop.departure-state.t)};
}
