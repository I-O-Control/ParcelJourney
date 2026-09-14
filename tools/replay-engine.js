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
    const t=Math.max(0,Math.min(parcel.duration,time));
    let index=0;for(let i=0;i<parcel.events.length;i++)if(parcel.events[i].t<=t)index=i;
    const event=parcel.events[index], upcoming=parcel.events[index+1]||null;
    const leg=parcel.legs.find(l=>t>=l.start&&t<l.end)||null;
    const visited=parcel.stops.filter(s=>s.arrival<=t),stop=visited[visited.length-1];
    const nextStop=parcel.stops.find(s=>s.arrival>t)||null;
    const done=t>=parcel.duration;
    const xy=leg?at(edges[leg.edges[0]].points,(t-leg.start)/(leg.end-leg.start)):[nodes[stop.id].x,nodes[stop.id].y];
    const history=[];let liquid=[];
    for(const l of parcel.legs) {
      if(l.start>=t)break;
      const ps=edges[l.edges[0]].points,f=Math.min(1,(t-l.start)/(l.end-l.start));
      history.push(slice(ps,0,f));
    }
    // The current leg stays bright throughout its arrival dwell. At departure,
    // it becomes history; the new leg grows from zero without losing history.
    // At the end highlight the entire *travelled* path, including held outcomes.
    const latestLeg=parcel.legs.filter(l=>l.start<t).at(-1);
    if(latestLeg) {
      const ps=edges[latestLeg.edges[0]].points;
      liquid=slice(ps,0,Math.min(1,(t-latestLeg.start)/(latestLeg.end-latestLeg.start)));
    }
    const bright=done?history:(liquid.length?[liquid]:[]);
    const phase=done?parcel.outcome||'completed':leg?'travelling':'processing';
    const current=done?parcel.completeness:leg?`Moving: ${nodes[leg.a].label} → ${nodes[leg.b].label}`:event.Summary;
    const next=done?'Replay ended here. No further movement is implied.':nextStop?`Next recorded position in ${(nextStop.arrival-t).toFixed(1)}s: ${nodes[nextStop.id].label}`:`Final observations: ${Math.max(0,parcel.duration-t).toFixed(1)}s remaining.`;
    return {t,xy,index,event,upcoming,leg,stop,nextStop,done,phase,current,next,history,liquid,bright};
  }
  return {sample,at,slice,length};
})();
if(typeof module!=='undefined')module.exports=ReplayEngine;
