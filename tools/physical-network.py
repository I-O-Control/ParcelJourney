"""Compile shared, directed belt segments; reject crossings; audit real captures.

Coordinates remain schematic. Reachability is compatibility, not proof of CAD
registration. No writes to capture sources or production systems.
"""
import argparse, collections, hashlib, importlib, json, re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def compile_network(model):
    raw, errors = [], []
    nodes = {n['id']: n for n in model['nodes']}
    for e in model['edges']:
        for name, p in ((e['a'], e['points'][0]), (e['b'], e['points'][-1])):
            if p != [nodes[name]['x'], nodes[name]['y']]:
                errors.append({'kind':'endpoint', 'edge':e['id'], 'node':name})
        for a,b in zip(e['points'],e['points'][1:]):
            a,b=tuple(a),tuple(b)
            if a==b: continue
            if a[0]!=b[0] and a[1]!=b[1]:
                errors.append({'kind':'diagonal','edge':e['id']}); continue
            raw.append((a,b,e['id']))
    cuts=[{a,b} for a,b,_ in raw]
    crossings={}
    for i,(a,b,ei) in enumerate(raw):
        for j in range(i):
            c,d,ej=raw[j]
            va=a[0]==b[0]; vb=c[0]==d[0]
            if va==vb:
                if a[0 if va else 1]!=c[0 if va else 1]: continue
                axis=1 if va else 0
                for p in (a,b,c,d):
                    if min(a[axis],b[axis])<=p[axis]<=max(a[axis],b[axis]): cuts[i].add(p)
                    if min(c[axis],d[axis])<=p[axis]<=max(c[axis],d[axis]): cuts[j].add(p)
            else:
                p=(a[0],c[1]) if va else (c[0],a[1])
                def contains(u,v): return all(min(u[k],v[k])<=p[k]<=max(u[k],v[k]) for k in (0,1))
                if contains(a,b) and contains(c,d):
                    cuts[i].add(p); cuts[j].add(p)
                    if p not in (a,b) and p not in (c,d):
                        crossings.setdefault(p,set()).update((ei,ej))
    errors += [{'kind':'perpendicular-crossing','point':p,'routes':sorted(es)} for p,es in crossings.items()]
    segments={}; routes=collections.defaultdict(list, {e['id']:[] for e in model['edges']})
    for (a,b,e),ps in zip(raw,cuts):
        ps=sorted(ps,key=lambda p:abs(p[0]-a[0])+abs(p[1]-a[1]))
        for u,v in zip(ps,ps[1:]):
            key=tuple(sorted((u,v)))
            s=segments.setdefault(key,{'id':'belt-'+str(len(segments)+1),'points':[list(key[0]),list(key[1])],'width':29.25,'routes':[],'directions':set()})
            direction=1 if u==key[0] else -1
            s['directions'].add(direction)
            if e not in s['routes']: s['routes'].append(e)
            routes[e].append({'segment':s['id'],'direction':direction})
    for s in segments.values():
        s['directions']=sorted(s['directions'])
        if len(s['directions'])>1: errors.append({'kind':'opposing-flow','segment':s['id'],'routes':s['routes']})
    arms=collections.defaultdict(set)
    for s in segments.values():
        a,b=map(tuple,s['points'])
        arms[a].add(('x' if a[1]==b[1] else 'y',1 if b>a else -1))
        arms[b].add(('x' if a[1]==b[1] else 'y',-1 if b>a else 1))
    # Splitting an illegal cross into four endpoint segments must not hide it.
    for p,directions in arms.items():
        if len(directions)==4 and p not in crossings:
            errors.append({'kind':'four-way-crossing','point':p})
    # Check footprints of disconnected parallel segments, including the rails.
    vals=list(segments.values())
    for i,s in enumerate(vals):
        a,b=s['points']; va=a[0]==b[0]
        for t in vals[:i]:
            c,d=t['points']; vb=c[0]==d[0]
            if va!=vb: continue
            axis=1 if va else 0; fixed=1-axis
            gap=abs(a[fixed]-c[fixed]); overlap=min(max(a[axis],b[axis]),max(c[axis],d[axis]))-max(min(a[axis],b[axis]),min(c[axis],d[axis]))
            if 0<gap<39.25 and overlap>0:
                errors.append({'kind':'insufficient-clearance','segments':[s['id'],t['id']],'gap':gap})
    return {'segments':vals,'routes':dict(routes),'errors':errors,'coordinateStatus':'schematic; CAD registration pending','beltWidth':29.25,'minimumGap':10}

def audit_capture(path,model):
    index=json.loads((path/'index.json').read_text(encoding='utf-8-sig'))
    dictionary=json.loads((path/index['dictionary']).read_text(encoding='utf-8-sig'))
    if dictionary['id']!=index['dictionary_id']: raise ValueError('Dictionary mismatch')
    decode=importlib.import_module('audit-capture-flow').decode
    adj=collections.defaultdict(list)
    for e in model['edges']: adj[e['a']].append(e['b'])
    known={n['id'] for n in model['nodes']}
    cache={}
    def reachable(a,b):
        if (a,b) in cache:return cache[a,b]
        seen={a}; q=collections.deque([a]); found=False
        while q:
            u=q.popleft()
            for v in adj[u]:
                if v==b: found=True; q.clear(); break
                if v not in seen:seen.add(v);q.append(v)
        cache[a,b]=found;return found
    result=[]; transitions=collections.Counter(); total=0
    for entry in index['entries']:
        p=decode(path/entry['file'],dictionary['values']); scans=[]
        for event in p['events']:
            if event.get('kind')=='scan': scans.append(event)
        issues=[]
        for a,b in zip(scans,scans[1:]):
            u,v=a.get('point'),b.get('point'); dt=b['t']-a['t']
            if dt<0:issues.append({'kind':'time-reversal','from':u,'to':v})
            if u==v:continue # repeated reads do not establish physical reentry
            transitions[u,v]+=1; total+=1
            if u not in known or v not in known:kind='unmapped-scanner'
            elif not reachable(u,v):kind='no-directed-path'
            else:continue
            issues.append({'kind':kind,'from':u,'to':v,'elapsedMs':dt,'source':b.get('raw_evidence_ref')})
        result.append({'parcel':p['parcel'],'file':entry['file'],'scans':len(scans),'status':'unresolved' if issues else 'graph-compatible' if scans else 'no-scans','issues':issues})
    return {'dataset':str(path),'indexSha256':hashlib.sha256((path/'index.json').read_bytes()).hexdigest(),'histories':len(result),'transitions':total,'statusCounts':dict(collections.Counter(p['status'] for p in result)),'scope':'Directed graph compatibility only; timing speeds and unique physical paths are not proven. Repeated reads are not treated as loops.','transitionCounts':[{'a':a,'b':b,'count':n} for (a,b),n in transitions.most_common()],'parcels':result}

def benchmark():
    def model(paths):
        nodes=[];edges=[]
        for i,ps in enumerate(paths):
            for suffix,p in [('a',ps[0]),('b',ps[-1])]:nodes.append({'id':str(i)+suffix,'x':p[0],'y':p[1]})
            edges.append({'id':str(i),'a':str(i)+'a','b':str(i)+'b','points':ps})
        return {'nodes':nodes,'edges':edges}
    cases=[('cross',[[[0,50],[100,50]],[[50,0],[50,100]]],'perpendicular-crossing'),('split-cross',[[[0,50],[50,50]],[[50,50],[100,50]],[[50,0],[50,50]],[[50,50],[50,100]]],'four-way-crossing'),('merge',[[[0,50],[100,50]],[[50,0],[50,50]]],None),('parallel-too-close',[[[0,0],[100,0]],[[0,20],[100,20]]],'insufficient-clearance'),('counterflow',[[[0,0],[100,0]],[[100,0],[0,0]]],'opposing-flow'),('loop',[[[0,0],[100,0],[100,100],[0,100],[0,0]]],None),('shared',[[[0,0],[100,0]],[[50,0],[100,0]]],None)]
    results=[]
    for name,paths,expected in cases:
        kinds={e['kind'] for e in compile_network(model(paths))['errors']}
        passed=(expected in kinds) if expected else not kinds
        results.append({'name':name,'passed':passed,'detected':sorted(kinds)})
    assert all(r['passed'] for r in results),results
    return results

def source_evidence(capture,model):
    contract=json.loads((capture/'flow-contract.json').read_text(encoding='utf8'))
    scada=json.loads((ROOT/'analysis/scada/fiege-ungarn-screen203-topology.json').read_text(encoding='utf8'))
    sources=[]
    for source in contract['sources']:
        p=Path(source['path'])
        sources.append({'id':source['id'],'path':str(p),'available':p.is_file(),'hashMatchesCaptureContract':p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest()==source['sha256']})
    anchors=[]
    for n in model['nodes']:
        equipment=n.get('equipment')
        if not equipment:continue
        base=str(equipment).split('-')[0]
        matches=[{'id':s['id'],'x':s['x'],'y':s['y'],'sourceOffset':s.get('sourceOffset')} for s in scada['nodes'] if re.match(r'Pos '+re.escape(base)+r'(?:[-_]|$)',s['label'])]
        registry=contract['registry'].get(str(equipment),{})
        anchors.append({'node':n['id'],'equipment':equipment,'registryScanner':registry.get('scanner'),'registryMatch':registry.get('scanner')==n['id'],'scadaCandidates':matches,'coordinateRegistration':'unresolved; equipment prefix matches are not a surveyed position'})
    return {'sources':sources,'anchors':anchors,'cad':json.loads((ROOT/'analysis/scada/fiege-cad-topology.json').read_text(encoding='utf8')),'note':'SCADA identifiers and CAD section roles support mapping. No automatic nearest-neighbour connections are accepted.'}

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--capture',action='append',default=[]);ap.add_argument('--output',default='analysis/physical-network');args=ap.parse_args()
    model=json.loads((ROOT/'topology/replay-layout.json').read_text(encoding='utf8'));out=ROOT/args.output;out.mkdir(parents=True,exist_ok=True)
    network=compile_network(model)
    (out/'network.json').write_text(json.dumps(network,indent=2),encoding='utf8')
    captures=[audit_capture(Path(p),model) for p in args.capture]
    report={'geometryErrors':network['errors'],'segments':len(network['segments']),'routeCount':len(network['routes']),'benchmark':benchmark(),'captures':captures}
    if args.capture: report['sourceEvidence']=source_evidence(Path(args.capture[-1]),model)
    (out/'report.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    print(json.dumps({'geometryErrors':len(network['errors']),'kinds':dict(collections.Counter(e['kind'] for e in network['errors'])),'captures':[{k:c[k] for k in ('dataset','histories','statusCounts')} for c in captures],'benchmark':report['benchmark']},indent=2))
    if network['errors']: raise SystemExit(1)

if __name__=='__main__':main()
