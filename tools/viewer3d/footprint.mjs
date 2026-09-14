// Convex footprint of physical points plus a small safety margin.
export function footprint(points,padding=.9){
  const expanded=points.flatMap(([x,z])=>[[-1,-1],[-1,1],[1,-1],[1,1]].map(([dx,dz])=>[x+dx*padding,z+dz*padding]));
  const sorted=[...new Map(expanded.map(p=>[p.join(','),p])).values()].sort((a,b)=>a[0]-b[0]||a[1]-b[1]);
  const cross=(a,b,c)=>(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);
  const half=items=>{const out=[];for(const p of items){while(out.length>=2&&cross(out.at(-2),out.at(-1),p)<=0)out.pop();out.push(p);}return out.slice(0,-1);};
  return [...half(sorted),...half(sorted.toReversed())];
}
export function gridSlice(polygon,axis,value){
  const hits=[];
  for(let i=0;i<polygon.length;i++){
    const a=polygon[i],b=polygon[(i+1)%polygon.length];
    if((a[axis]<=value&&b[axis]>value)||(b[axis]<=value&&a[axis]>value))hits.push(a[1-axis]+(b[1-axis]-a[1-axis])*(value-a[axis])/(b[axis]-a[axis]));
  }
  return hits.length>=2?[Math.min(...hits),Math.max(...hits)]:null;
}
