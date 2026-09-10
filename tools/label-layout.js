/* Screen-space label layout shared with deterministic layout tests. */
const LabelLayout=(()=>{
  const overlap=(a,b)=>a.x<b.x+b.w+4&&a.x+a.w+4>b.x&&a.y<b.y+b.h+4&&a.y+a.h+4>b.y;
  function crosses(a,b,rect){
    // Segment/rectangle clipping, inclusive. Also handles vertical/zero-length segments.
    const r={x:rect.x-5,y:rect.y-5,w:rect.w+10,h:rect.h+10};
    let low=0,high=1;const dx=b[0]-a[0],dy=b[1]-a[1];
    for(const [p,q] of [[-dx,a[0]-r.x],[dx,r.x+r.w-a[0]],[-dy,a[1]-r.y],[dy,r.y+r.h-a[1]]]){
      if(p===0){if(q<0)return false;continue}
      const u=q/p;if(p<0)low=Math.max(low,u);else high=Math.min(high,u);
      if(low>high)return false;
    }
    return true;
  }
  function arrange(items,width,height,blocked=[],segments=[],previous=[]){
    const placed=[],obstacles=[...blocked,...items.map(n=>({x:n.x-14,y:n.y-14,w:28,h:28}))];
    for(const n of items){
      const w=Math.min(170,Math.max(80,n.label.length*7+16)),h=n.label.length>22?46:28;
      let box=null;
      const old=previous.find(b=>b.id===n.id);
      if(old){const c={x:old.x+n.x-old.anchor[0],y:old.y+n.y-old.anchor[1],w,h};
        if(c.x>=8&&c.y>=85&&c.x+w<=width-8&&c.y+h<=height-85&&!obstacles.some(o=>overlap(c,o))&&!segments.some(s=>crosses(s[0],s[1],c)))box=c;
      }
      for(const gap of [22,46,76,110]){
        if(box)break;
        for(const [x,y] of [[n.x-w/2,n.y-gap-h],[n.x+gap,n.y-h/2],[n.x-w/2,n.y+gap],[n.x-gap-w,n.y-h/2]]){
          const c={x,y,w,h};
          if(x<8||y<85||x+w>width-8||y+h>height-85)continue;
          if(obstacles.some(o=>overlap(c,o)))continue;
          if(segments.some(s=>crosses(s[0],s[1],c)))continue;
          box=c;break;
        }
        if(box)break;
      }
      if(box){placed.push({...box,id:n.id,label:n.label,anchor:[n.x,n.y]});obstacles.push(box)}
    }
    return placed;
  }
  function rotate(point,degrees){const a=degrees*Math.PI/180,c=Math.cos(a),s=Math.sin(a);return [point[0]*c-point[1]*s,point[0]*s+point[1]*c]}
  function bounds(points,degrees=0,padding=65){const ps=points.map(p=>rotate(p,degrees)),xs=ps.map(p=>p[0]),ys=ps.map(p=>p[1]);const x=Math.min(...xs)-padding,y=Math.min(...ys)-padding;return[x,y,Math.max(...xs)-x+padding,Math.max(...ys)-y+padding]}
  return{arrange,overlap,crosses,rotate,bounds};
})();
if(typeof module!=='undefined')module.exports=LabelLayout;
