// Convert every replay leg into timed straight segments without changing its speed.
export function buildTrailSegments(edges, parcel) {
  const segments=[];
  for (const leg of parcel.legs) {
    if (!leg.edges?.length || leg.routeStatus==='unknown') continue;
    let at=leg.a;const points=[];
    for(const id of leg.edges){const edge=edges.get(id);if(!edge||edge.a!==at){points.length=0;break;}points.push(...edge.points.slice(points.length?1:0));at=edge.b;}
    if(!points.length||at!==leg.b)continue;
    const lengths=points.slice(1).map((b,i)=>Math.hypot(b[0]-points[i][0],b[1]-points[i][1]));
    const total=lengths.reduce((a,b)=>a+b,0);
    let distance=0;
    for(let i=1;i<points.length;i++) {
      const length=lengths[i-1];if(length===0)continue;
      segments.push({a:points[i-1],b:points[i],start:leg.start+(leg.end-leg.start)*distance/total,end:leg.start+(leg.end-leg.start)*(distance+length)/total});
      distance+=length;
    }
  }
  return segments;
}
