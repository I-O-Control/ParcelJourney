const fs=require('fs');
const model=JSON.parse(fs.readFileSync('analysis/fiege-replay-model.json','utf8'));
const nodes=new Map(model.nodes.map(n=>[n.id,n]));
const adjacency=new Map(model.nodes.map(n=>[n.id,new Set()]));
const failures=[];
for(const edge of model.edges){
  const a=nodes.get(edge.a),b=nodes.get(edge.b),first=edge.points[0],last=edge.points.at(-1);
  if(!a||!b)failures.push(`${edge.id}: unknown endpoint`);
  else {
    adjacency.get(edge.a).add(edge.b);adjacency.get(edge.b).add(edge.a);
    if(first[0]!==a.x||first[1]!==a.y)failures.push(`${edge.id}: path does not start at ${edge.a}`);
    if(last[0]!==b.x||last[1]!==b.y)failures.push(`${edge.id}: path does not end at ${edge.b}`);
  }
  if(edge.points.length<2)failures.push(`${edge.id}: path has no physical span`);
  for(let i=1;i<edge.points.length;i++){
    const previous=edge.points[i-1],current=edge.points[i];
    if(previous[0]!==current[0]&&previous[1]!==current[1])failures.push(`${edge.id}: diagonal conveyor segment ${previous} → ${current}`);
  }
}
const start=model.nodes[0]?.id,seen=new Set(start?[start]:[]),queue=start?[start]:[];
while(queue.length){for(const next of adjacency.get(queue.shift())||[])if(!seen.has(next)){seen.add(next);queue.push(next)}}
for(const node of model.nodes){if(!adjacency.get(node.id)?.size)failures.push(`${node.id}: equipment is not attached to a conveyor`);if(!seen.has(node.id))failures.push(`${node.id}: disconnected from plant network`)}
if(failures.length){console.error(failures.join('\n'));process.exit(1)}
console.log(`PASS: ${model.nodes.length} equipment points attached through ${model.edges.length} modeled connections; one connected plant graph; every rendered path terminates at its declared equipment.`);
