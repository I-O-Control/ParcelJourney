"""Find immediate geometric reversals, which are not ordinary return loops."""
from pathlib import Path
import json,math
root=Path(__file__).resolve().parents[1]
m=json.loads((root/'analysis/synthetic-replay-model.json').read_text(encoding='utf-8'))
edges={e['id']:e for e in m['edges']}
reversals=[];segments=0
for p in m['parcels']:
    points=[]
    for leg in p['legs']:
        for id in leg['edges']:
            for pt in edges[id]['points']:
                if not points or points[-1][0]!=pt:points.append((pt,id))
    for a,b,c in zip(points,points[1:],points[2:]):
        u=[b[0][i]-a[0][i] for i in (0,1)];v=[c[0][i]-b[0][i] for i in (0,1)]
        segments+=1
        cross=u[0]*v[1]-u[1]*v[0];dot=u[0]*v[0]+u[1]*v[1]
        if abs(cross)<1e-7 and dot<0:reversals.append(dict(parcel=p['id'],edges=[b[1],c[1]],point=b[0]))
report=dict(parcels=len(m['parcels']),turnsChecked=segments,immediateReversals=len(reversals),examples=reversals[:15],meaning='Detects an immediate 180-degree retrace; does not establish surveyed conveyor topology.')
(root/'analysis/route-direction-audit.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report,indent=2))
assert not reversals,'Routes still contain immediate same-line reversals'
