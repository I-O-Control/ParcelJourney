"""Confidential, local-only derived model. Sources are opened read-only.

Coordinates are schematic design choices. Observed transitions are temporal
constraints, not proof of physical adjacency. Simulations test this model only.
"""
from pathlib import Path
from collections import deque, Counter
from datetime import datetime
import json, re, math

ROOT = Path(__file__).resolve().parents[1]
SOURCE = Path(r'C:\Users\porfy\source\repos\Fiege Logistik\Fiege Logistik\Budapest\trunk\MFR_Service\Plugins')
telegram = (SOURCE/'Io.Mfc.PlcSC1/PlcTelegram/TelegramData.cs').read_text(encoding='utf-8-sig')
ids = {name: pos for pos,name in re.findall(r'\{ "(\d{4}-[12])", "(SC_\w+)" \}', telegram)}
raw = json.loads((ROOT/'analysis/verified-real-journeys.json').read_text(encoding='utf-8'))
nodes, edges = {}, []
def node(id, label, x,y,kind='scanner', note=''):
    nodes[id] = dict(id=id,label=label,x=x,y=y,kind=kind,equipment=ids.get(id),note=note,
                     coordinateBasis='Estimated schematic position; not surveyed CAD coordinates')
    return id
def edge(a,b,via=(),basis='Estimated connection from section arrangement'):
    points=[[nodes[a]['x'],nodes[a]['y']], *via, [nodes[b]['x'],nodes[b]['y']]]
    edges.append(dict(id=f'{a}>{b}',a=a,b=b,points=points,basis=basis))
def chain(seq):
    for a,b in zip(seq,seq[1:]): edge(a,b)

node('SC_START','Entry scan + start scale',100,170,'scale')
node('SC_WAAP','Workstation decision',310,170,'decision')
node('SC_AP','Workstation allocation',310,350,'decision')
node('SC_UMR','Quality / manual-close decision',570,350,'decision',
     'Version conflict: PLC name table maps 3020-1; current SC1 logic describes quality point 1460. Placement is provisional.')
node('SC_WAVL','Sealer-loop entry decision',740,170,'decision')
node('SC_VL1','Sealer allocation 1–4',950,170,'decision')
node('SC_VL2','Sealer allocation 5–7',1360,170,'decision')
chain(['SC_START','SC_WAAP','SC_WAVL','SC_VL1','SC_VL2'])
chain(['SC_WAAP','SC_AP','SC_UMR'])
edge('SC_UMR','SC_WAVL',[[650,350],[650,170]])
node('RETURN','Sealer recirculation',1650,550,'junction')
edge('SC_VL2','RETURN',[[1650,170]])
edge('RETURN','SC_WAVL',[[740,550]])
node('MERGE','Sealer output merge',1650,650,'junction')
for i in range(1,8):
    x=825+(i-1)*118
    node(f'SEAL{i}',f'Sealer {i}',x,410,'sealer','Numbered sealer targets 01–07 are selected in ScannerLogic.cs; individual branch geometry is estimated.')
    a='SC_VL1' if i<=4 else 'SC_VL2'
    edge(a,f'SEAL{i}',[[x,170]])
    edge(f'SEAL{i}','MERGE',[[x,650]])
node('NOREAD','No-read / sealer exception',1750,410,'exception','Target 08 is a code-defined no-read route; physical exit location is estimated.')
edge('SC_VL2','NOREAD',[[1750,170]])
node('PACK','Packaging distribution',400,770,'decision','Packaging distribution is separate from the version-dependent SC_UMR quality role.')
edge('MERGE','PACK',[[1850,650],[1850,710],[400,710]])
node('OUT','Packaging output merge',1730,1250,'junction')
for row,i in enumerate([5,2,1,3,4]):
    y=850+row*100
    seq=[]
    for id,label,x,kind in [(f'STRAP{i}',f'Strapper {i}',570,'strapper'),(f'SC_BWL{i}',f'Gross scale {i}',790,'scale'),(f'SC_ETIINL{i}',f'Print trigger {i}',1010,'scanner'),(f'PRINT{i}',f'Label applicator {i}',1220,'printer'),(f'SC_ETIOSL{i}',f'Side verification {i}',1430,'scanner'),(f'SC_ETIOTL{i}',f'Top verification {i}',1620,'scanner')]:
        node(id,label,x,y,kind); seq.append(id)
    edge('PACK',seq[0],[[400,y]])
    chain(seq); edge(seq[-1],'OUT',[[1730,y]])
node('SHIP','Shipping loop infeed',250,1450,'junction')
edge('OUT','SHIP',[[1800,1250],[1800,1390],[250,1390]])
previous='SHIP'
for h,count,y in [(4,8,1540),(3,7,1880),(2,9,2220)]:
    for i in range(1,count+1):
        x=250+(i-1)*180
        s=f'SC_H{h}T{i}'; node(s,f'H{h} divert scanner {i}',x,y,'decision')
        if i==1: edge(previous,s,[[nodes[previous]['x'],y]])
        else: edge(previous,s)
        t=f'H{h}_EXIT{i}'; node(t,f'Hall {h} telescope {i}',x,y+130,'exit'); edge(s,t)
        previous=s
    if h in [4,3]:
        s=f'SC_H{h}RU';node(s,f'Hall {h} chute decision',1780,y,'decision');edge(previous,s);previous=s
        for i in range(1,5 if h==4 else 4):
            c=f'H{h}_CHUTE{i}';node(c,f'H{h} chute {i}'+(' / NIO' if h==3 and i==1 else ''),1660+(i-1)*90,y+230,'exception');edge(s,c,[[nodes[c]['x'],y]])
edge(previous,'SHIP',[[2020,2220],[2020,1450]])

def route(a,b):
    if a==b:return []
    q=deque([(a,[])]);seen={a}
    while q:
        n,p=q.popleft()
        for e in edges:
            if e['a']!=n or e['b'] in seen:continue
            pp=p+[e['id']]
            if e['b']==b:return pp
            seen.add(e['b']);q.append((e['b'],pp))
    return None

parcels=[];observed=Counter()
for j in raw['journeys']:
    ev=sorted(j['Events'],key=lambda e:e['Timestamp'])
    start=datetime.fromisoformat(ev[0]['Timestamp']).timestamp()
    for e in ev:e['t']=round(datetime.fromisoformat(e['Timestamp']).timestamp()-start,6)
    stops=[]
    for e in ev:
        loc=e.get('LocationId')
        # Database writes and lifecycle summaries are not new physical detections.
        if loc not in nodes or e['EventType'] in ['StatePersisted','ParcelLastSeen','ParcelEnteredSystem']:continue
        if stops and stops[-1]['id']==loc:
            stops[-1]['departure']=max(stops[-1]['departure'],e['t'])
        else:stops.append(dict(id=loc,arrival=e['t'],departure=e['t']))
    if not stops:
        loc=next((e['LocationId'] for e in ev if e.get('LocationId') in nodes),None)
        if loc:stops=[dict(id=loc,arrival=0,departure=0)]
    legs=[]
    for a,b in zip(stops,stops[1:]):
        path=route(a['id'],b['id']);observed[(a['id'],b['id'])]+=1
        assert path is not None,(a,b)
        legs.append(dict(a=a['id'],b=b['id'],start=a['departure'],end=b['arrival'],edges=path))
    parcels.append(dict(id=j['ParcelId'],events=ev,stops=stops,legs=legs,duration=ev[-1]['t'],
                        completeness='Recorded window only; entry and final delivery may be absent'))

# Deterministic coverage exercises test connections; these are not measured
# parcel probabilities or evidence that a physical route exists.
terminals=[n for n in nodes if not any(e['a']==n for e in edges)]
tests=[]
covered=Counter()
scenario_names=['normal dispatch','sealer recirculation','workstation / quality pass','sealer no-read target 08','shipping recirculation','shipping round-limit NIO']
for i in range(10000):
    case=i%len(scenario_names)
    end=terminals[(i//6)%len(terminals)]
    waypoints=['SC_START']
    if case==2:waypoints+=['SC_AP','SC_UMR']
    if case in [1,5]:waypoints+=['SC_VL2','RETURN','SC_WAVL']
    if case==3:
        end='NOREAD'
    elif end!='NOREAD':
        waypoints += [f'SEAL{1+(i//6)%7}',f'SC_BWL{1+(i//42)%5}','SHIP']
        if case in [4,5]:
            waypoints += ['SC_H2T9','SHIP']*(4 if case==5 else 1)
        if case==5:end='H3_CHUTE1'
    waypoints.append(end)
    path=[]
    for a,b in zip(waypoints,waypoints[1:]):
        segment=route(a,b)
        assert segment is not None,(a,b)
        path+=segment
    assert path is not None,end
    last='SC_START'
    for id in path:
        e=next(e for e in edges if e['id']==id);assert e['a']==last;last=e['b']
        assert all(math.isfinite(v) for p in e['points'] for v in p)
    covered.update(path)
    tests.append(dict(id=i+1,scenario=scenario_names[case],end=end,edges=path))
model=dict(nodes=list(nodes.values()),edges=edges,parcels=parcels,
           observedTransitions=[dict(a=a,b=b,count=n) for (a,b),n in observed.items()],
           validation=dict(exercises=10000,passed=10000,coveredEdges=len(covered),totalEdges=len(edges),uncoveredEdges=[e['id'] for e in edges if not covered[e['id']]],scenarios=scenario_names,meaning='Scenario route checks on estimated graph; not execution of production PLC or exhaustive code-path coverage'),
           caveats=['Physical geometry is estimated using CAD section grouping.',
                    'A log timestamp measures an action, not continuous parcel position.',
                    'Database writes and PLC acknowledgements remain actions at a station, not new machines.',
                    'SC_UMR mapping differs across source versions.'],source=raw['sourceFile'])
out=ROOT/'analysis'
(out/'equipment-replay-model.json').write_text(json.dumps(model,ensure_ascii=False,indent=2),encoding='utf-8')
(out/'equipment-replay-validation.json').write_text(json.dumps(tests,indent=2),encoding='utf-8')
report=['# Ten recorded parcel windows','', 'Enter any ID below in equipment-replay.html. Times and events come from the recorded log; movement between actions is estimated. Final delivery is not established by these partial SC1 windows.','']
for p in parcels:
    report += [f'## {p["id"]}',f'Duration: {p["duration"]:.3f} seconds; {len(p["events"])} events.', 'Route: '+' → '.join(nodes[s['id']]['label'] for s in p['stops']), '']
    for e in p['events']:
        if e['EventType'] not in ['StatePersisted','ParcelEnteredSystem','ParcelLastSeen']:
            report.append(f'- {e["Timestamp"]}: {e["EventType"]} — {e["Summary"]}')
    report.append('')
(out/'ten-parcel-routes.md').write_text('\n'.join(report),encoding='utf-8')
template=(ROOT/'tools/equipment-replay.html').read_text(encoding='utf-8')
(out/'equipment-replay.html').write_text(template.replace('__MODEL__',json.dumps(model,ensure_ascii=False).replace('</','<\\/')),encoding='utf-8')
print(json.dumps(dict(nodes=len(nodes),edges=len(edges),parcels=len(parcels),events=sum(len(p['events']) for p in parcels),validation=model['validation'])))
