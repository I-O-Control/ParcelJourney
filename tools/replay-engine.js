/* Pure replay state: shared by UI and all-scenario regression tests. */
const ReplayEngine = (() => {
  const length = ps => ps.slice(1).reduce((sum,b,i)=>sum+Math.hypot(b[0]-ps[i][0],b[1]-ps[i][1]),0);
  function at(ps, fraction) {
    let remaining=length(ps)*Math.max(0,Math.min(1,fraction));
    for(let i=1;i<ps.length;i++) {
      const a=ps[i-1],b=ps[i],d=Math.hypot(b[0]-a[0],b[1]-a[1]);
      if(d && remaining<=d) return [a[0]+(b[0]-a[0])*remaining/d,a[1]+(b[1]-a[1])*remaining/d];
      remaining-=d;
    }
    return ps[ps.length-1].slice();
  }
  function slice(ps, from, to) {
    const total=length(ps), out=[at(ps,from)];let travelled=0;
    for(let i=1;i<ps.length;i++) {
      travelled+=Math.hypot(ps[i][0]-ps[i-1][0],ps[i][1]-ps[i-1][1]);
      if(travelled>from*total && travelled<to*total)out.push(ps[i]);
    }
    out.push(at(ps,to));return out;
  }
  function sample(model,parcel,time) {
    const nodes=Object.fromEntries(model.nodes.map(n=>[n.id,n]));
    const edges=Object.fromEntries(model.edges.map(e=>[e.id,e]));
    const pointsFor=leg=>{
      if(leg.routeStatus==='co-located-read-head-change') {
        const a=nodes[leg.a],b=nodes[leg.b];
        if(a&&b&&a.x===b.x&&a.y===b.y)return [[a.x,a.y],[b.x,b.y]];
        return null;
      }
      if(!leg.edges?.length)return null;
      const route=[];let at=leg.a;
      for(const id of leg.edges){const edge=edges[id];if(!edge||edge.a!==at)return null;route.push(...edge.points.slice(route.length?1:0));at=edge.b;}
      return at===leg.b?route:null;
    };
    const routeUnknown=leg=>!pointsFor(leg)||leg.routeStatus==='unknown';
    const playbackPoints=leg=>pointsFor(leg)||[[nodes[leg.a].x,nodes[leg.a].y],[nodes[leg.b].x,nodes[leg.b].y]];
    const t=Math.max(0,Math.min(parcel.duration,time));
    let index=0;for(let i=0;i<parcel.events.length;i++)if(parcel.events[i].t<=t)index=i;
    const event=parcel.events[index], upcoming=parcel.events[index+1]||null;
    const leg=parcel.legs.find(l=>t>=l.start&&t<l.end)||null;
    const visited=parcel.stops.filter(s=>s.arrival<=t),stop=visited[visited.length-1];
    const nextStop=parcel.stops.find(s=>s.arrival>t)||null;
    const done=t>=parcel.duration;
    const xy=leg?(routeUnknown(leg)?[nodes[leg.a].x,nodes[leg.a].y]:at(playbackPoints(leg),(t-leg.start)/(leg.end-leg.start))):[nodes[stop.id].x,nodes[stop.id].y];
    const history=[];let liquid=[];
    for(const l of parcel.legs) {
      if(l.start>=t)break;
      if(routeUnknown(l))continue;
      const ps=playbackPoints(l),f=Math.min(1,(t-l.start)/(l.end-l.start));
      history.push(slice(ps,0,f));
    }
    // The current leg stays bright throughout its arrival dwell. At departure,
    // it becomes history; the new leg grows from zero without losing history.
    // At the end highlight the entire *travelled* path, including held outcomes.
    const latestLeg=parcel.legs.filter(l=>l.start<t).at(-1);
    if(latestLeg&&!routeUnknown(latestLeg)) {
      const ps=playbackPoints(latestLeg);
      liquid=slice(ps,0,Math.min(1,(t-latestLeg.start)/(latestLeg.end-latestLeg.start)));
    }
    const bright=done?history:(liquid.length?[liquid]:[]);
    const possibleExternalHandling=!!(leg?.possibleExternalHandling);
    const phase=done?parcel.outcome||'completed':leg?(routeUnknown(leg)?(possibleExternalHandling?'possible-external-handling':'route-unknown'):'travelling'):'processing';
    const current=done?parcel.completeness:leg?(routeUnknown(leg)?`Observed movement: ${nodes[leg.a].label} → ${nodes[leg.b].label}; ${possibleExternalHandling?'possible external handling':'physical route unknown'}`:`Moving: ${nodes[leg.a].label} → ${nodes[leg.b].label}`):event.Summary;
    const next=done?'Replay ended here. No further movement is implied.':leg&&routeUnknown(leg)?`Next recorded scanner: ${nodes[leg.b].label}. Conveyor route is not mapped.`:nextStop?`Next recorded position in ${(nextStop.arrival-t).toFixed(1)}s: ${nodes[nextStop.id].label}`:`Final observations: ${Math.max(0,parcel.duration-t).toFixed(1)}s remaining.`;
    const positionKnown=!(leg&&routeUnknown(leg))&&nodes[stop.id].physical!==false;
    return {t,xy,positionKnown,possibleExternalHandling,index,event,upcoming,leg,routeUnknown:leg?routeUnknown(leg):false,stop,nextStop,done,phase,current,next,history,liquid,bright};
  }
  return {sample,at,slice,length,pointsFor(model,leg){
    const edges=Object.fromEntries(model.edges.map(edge=>[edge.id,edge]));let at=leg.a,points=[];
    if(leg.routeStatus==='co-located-read-head-change') {
      const a=model.nodes.find(n=>n.id===leg.a),b=model.nodes.find(n=>n.id===leg.b);
      return a&&b&&a.x===b.x&&a.y===b.y?[[a.x,a.y],[b.x,b.y]]:null;
    }
    if(!leg.edges?.length)return null;
    for(const id of leg.edges){const edge=edges[id];if(!edge||edge.a!==at)return null;points.push(...edge.points.slice(points.length?1:0));at=edge.b;}
    return at===leg.b?points:null;
  }};
})();
if(typeof module!=='undefined')module.exports=ReplayEngine;
