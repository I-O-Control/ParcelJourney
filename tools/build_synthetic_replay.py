"""Local synthetic harness: selected SC1 rules ported, not a running PLC/service.
All source inputs remain read-only. Geometry and timings are schematic fixtures.
"""
import json, math, base64
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
g = {'__file__':str(ROOT/'tools/build_equipment_replay.py')}
# Reuse only the legacy graph definition, without overwriting real-log reports.
legacy=(ROOT/'tools/build_equipment_replay.py').read_text(encoding='utf-8')
exec(compile(legacy.split('parcels=[];observed=Counter()')[0],g['__file__'],'exec'),g)
N, edges = g['nodes'], g['edges']
node, edge = g['node'], g['edge']
# Replace the complete sealer region: selection now precedes every controlled tap.
removed = {'SC_VL1','SC_VL2','RETURN','MERGE','NOREAD'} | {f'SEAL{i}' for i in range(1,8)}
edges[:] = [e for e in edges if e['a'] not in removed and e['b'] not in removed]
node('SC_VL1','Select sealer 1–4',820,170,'decision')
node('SC_VL2','Select sealer 5–7',1460,170,'decision')
node('RETURN','No compatible sealer: return',2040,550,'junction')
node('MERGE','Sealed / bypass merge',2040,650,'junction')
node('NOREAD','Target 08 · no-read',2040,380,'exception')
edge('SC_WAVL','SC_VL1'); edge('SC_VL1','SC_VL2');edge('SC_VL2','RETURN',[[2040,170]])
edge('RETURN','SC_WAVL',[[720,550],[720,170]])
edge('SC_WAVL','MERGE',[[700,170],[700,650]])
edge('MERGE','PACK',[[2100,650],[2100,710],[400,710]])
for i in range(1,8):
    x=900+(i-1)*155
    node(f'SEAL{i}',f'Sealer {i}',x,380,'sealer')
    a='SC_VL1' if i<=4 else 'SC_VL2'
    edge(a,f'SEAL{i}',[[x,170]])
    edge(f'SEAL{i}','MERGE',[[x,650]])
    assert N[a]['x'] < x
edge('SC_VL1','NOREAD',[[2040,170]]);edge('SC_VL2','NOREAD',[[2040,170]])
# Explicit workstation visit, rather than treating allocation as manual work.
for i in range(1,11):
    x=60+(i-1)*61
    node(f'AP{i}',f'Desk {i}',x,470,'sealer','Target 1–10; role assignment is a test fixture, not verified live configuration.')
    edge('SC_AP',f'AP{i}',[[310,410],[x,410]])
    edge(f'AP{i}','SC_UMR',[[x,510],[570,510]])

# Audit found the old inter-hall connections retraced the next hall's belt.
# Give transfers a separate approach corridor; code establishes destinations,
# not these exact coordinates. Preserve the user-confirmed equipment anchors.
for e in edges:
    if e['id'] in ('SC_H4RU>SC_H3T1','SC_H3RU>SC_H2T1'):
        a,b=N[e['a']],N[e['b']]
        y=b['y']-65
        e['points']=[[a['x'],a['y']],[1970,a['y']],[1970,y],[190,y],[190,b['y']],[b['x'],b['y']]]
        e['basis']='Schematic transfer corridor: removes same-belt reversal; exact physical return corridor unverified.'
        e['purpose']='Transfer to next shipping hall'
    if e['a'] in ('SC_H4RU','SC_H3RU') and '_CHUTE' in e['b']:
        a,b=N[e['a']],N[e['b']]
        e['points']=[[a['x'],a['y']],[a['x'],a['y']+55],[b['x'],a['y']+55],[b['x'],b['y']]]
        e['purpose']='Leave shipping belt for chute bank'
        e['basis']='Schematic branch after chute decision; avoids reverse travel over upstream scanners.'
    if e['id']=='SC_H2T9>SHIP':e['purpose']='Shipping recirculation to infeed'
    if e['id']=='RETURN>SC_WAVL':e['purpose']='Sealer recirculation to allocation'
    if e['id']=='MERGE>PACK':e['purpose']='Transfer from sealing to packaging'
    if e['id']=='OUT>SHIP':e['purpose']='Transfer from packaging to shipping'

# User-confirmed orientation: entry/workstations turn clockwise about the
# sealer-loop entry interface. Downstream equipment coordinates stay fixed.
entry_section={'SC_START','SC_WAAP','SC_AP','SC_UMR','SC_WAVL'}|{f'AP{i}' for i in range(1,11)}
def turn_entry(point):
    x,y=point
    return [740-(y-170),170+(x-740)]
for e in edges:
    if e['a'] in entry_section and e['b'] in entry_section:
        e['points']=[turn_entry(pt) for pt in e['points']]
for id in entry_section:
    N[id]['x'],N[id]['y']=turn_entry([N[id]['x'],N[id]['y']])
    N[id]['section']='entry'
    N[id]['coordinateBasis']='Schematic, entry section rotated 90° clockwise per user layout correction; interface at sealer-loop entry retained.'
for e in edges:
    assert e['points'][0]==[N[e['a']]['x'],N[e['a']]['y']],e['id']
    assert e['points'][-1]==[N[e['b']]['x'],N[e['b']]['y']],e['id']

def weight(expected, actual):
    if expected<=0:return 'PENDING'
    tolerance=round(expected*(.06 if expected<5000 else .05))
    return 'WEIGHTOK' if abs(actual-expected)<=tolerance else 'WEIGHTERR'

def verify(side, top, record=True, cached=True):
    if not cached:return 'PF'
    if side=='NOREAD' or top=='NOREAD':return 'NR'
    if not record:return 'ND'
    return 'WG' if top in 'TRACK123456' else 'PF'

def sealer(footprint, rows, first=True):
    if footprint not in ('FP1','FP2','FP3','FP4'):return None
    eligible=[r for r in rows if r['compatible'] and ((r['lane']<=4)==first)]
    return min(eligible,key=lambda r:r['age'])['lane'] if eligible else None

cases=[]
def case(name, **kw):cases.append(dict(name=name,**kw))
for i in range(1,8):case(f'Compatible sealer {i}',seal=i)
for i in range(1,6):case(f'Packaging line {i}',pack=i)
for h,count in [(4,8),(3,7),(2,9)]:
    for i in range(1,count+1):case(f'Hall {h} telescope {i}',dest=f'H{h}_EXIT{i}')
for h,count in [(4,4),(3,3)]:
    for i in range(1,count+1):case(f'Hall {h} chute {i}',dest=f'H{h}_CHUTE{i}')
for name,kw in [
 ('Underweight to clearing',dict(actual=8000,desk=1)),
 ('Overweight to clearing',dict(actual=12000,desk=1)),
 ('Weight upper boundary accepted',dict(actual=10500)),
 ('Weight lower boundary accepted',dict(actual=9500)),
 ('Small parcel six-percent boundary',dict(expected=1000,actual=1060)),
 ('Small parcel outside tolerance',dict(expected=1000,actual=1061,desk=1)),
 ('No expected weight: pending validation',dict(expected=0)),
 ('Invalid scale value to clearing',dict(actual=-1,desk=1)),
 ('Premium workstation',dict(desk=2,flag='1')),
 ('Random inspection workstation',dict(desk=3,flag='2')),
 ('Missing parcel record to clearing',dict(desk=1,missing=True)),
 ('Entry no-read to clearing',dict(desk=1,noread=True)),
 ('Manual closure bypasses sealers',dict(desk=4,manual=True)),
 ('No workstation available: held',dict(desk=1,hold=True)),
 ('Workstation available after wait',dict(desk=1,wait=True)),
 ('No compatible sealer: loop then available',dict(loop=True)),
 ('Unknown footprint: recirculate, unresolved',dict(unknown=True)),
 ('Sealer scanner no-read target 08',dict(sealnr=True)),
 ('Side no-read: NR',dict(side='NOREAD')),
 ('Top no-read: NR',dict(top='NOREAD')),
 ('Wrong tracking ID: PF',dict(top='WRONG')),
 ('Missing side correlation: PF',dict(cached=False)),
 ('Missing verification DB record: ND',dict(record=False)),
 ('Shipping target unavailable: recirculate',dict(shiploop=True)),
 ('Shipping retries exhausted: NIO',dict(nio=True))]:case(name,**kw)

parcels=[]
def make(c, repeat):
    stops=[]; events=[];legs=[];t=0
    pid=f'SIM-{cases.index(c)+1:03}-{repeat}'
    def visit(id,msg=None,dwell=2):
        nonlocal t
        if stops:
            a=stops[-1]['id'];path=g['route'](a,id)
            assert path is not None,(a,id)
            # Show every intervening physical point, not a jump to a waypoint.
            for eid in path:
                e=next(e for e in edges if e['id']==eid)
                length=sum(math.dist(a,b) for a,b in zip(e['points'],e['points'][1:]))
                duration=max(1,length/(55+repeat*5))
                legs.append(dict(a=e['a'],b=e['b'],start=t,end=t+duration,edges=[eid]))
                t+=duration
                if e['b']!=id:
                    stops.append(dict(id=e['b'],arrival=t,departure=t))
                    events.append(dict(t=t,LocationId=e['b'],Summary='Transit past '+N[e['b']]['label'],EventType='Synthetic transit',Timestamp='',Evidence=[]))
        stops.append(dict(id=id,arrival=t,departure=t+dwell))
        events.append(dict(t=t,LocationId=id,Summary=msg or N[id]['label'],EventType='Synthetic action',Timestamp='',Evidence=[]))
        t+=dwell
    def finish():
        state='completed' if outcome=='Completed synthetic route' else ('held' if c.get('hold') else 'unresolved' if c.get('unknown') else 'exception')
        return dict(id=pid,name=c['name'],events=events,stops=stops,legs=legs,duration=t,completeness=outcome,outcome=state,fixture=c,repeat=repeat)
    result=weight(c.get('expected',10000),c.get('actual',10000))
    identity='NOREAD; clearing required. ' if c.get('noread') else 'Missing record; FAULTY fixture. ' if c.get('missing') else ''
    visit('SC_START',identity+f"Entry registration; start scale {c.get('actual',10000)} g; expected {c.get('expected',10000)} g → {result}",4)
    desk=c.get('desk');visit('SC_WAAP','AR → workstations' if desk else 'WG → automatic processing')
    outcome='Completed synthetic route'
    if desk:
        visit('SC_AP','No PLC target; waiting for available workstation' if c.get('hold') or c.get('wait') else f'Allocate target {desk:02}',30 if c.get('wait') else 3)
        if c.get('hold'):outcome='Held: no available workstation';return finish()
        visit(f'AP{desk}','Synthetic operator processing / release; live role configuration unverified',12+repeat*3)
        visit('SC_UMR','Quality release; scanner identifier is version-dependent')
    visit('SC_WAVL','WG → manual-close bypass' if c.get('manual') else 'AR → sealer loop')
    if not c.get('manual'):
        lane=c.get('seal',1)
        rows=[dict(lane=lane,compatible=True,age=0)]
        selected=sealer('FP1',rows,lane<=4);assert selected==lane
        decision='NOREAD → target 08' if c.get('sealnr') else 'WG → no compatible target; try second allocation' if c.get('loop') or c.get('unknown') else f'Target {lane:02}' if lane<=4 else 'WG → second allocation'
        visit('SC_VL1',decision)
        if c.get('sealnr'):
            visit('NOREAD','NOREAD → target 08; downstream handling not established');outcome='No-read target 08';return finish()
        if c.get('loop') or c.get('unknown'):
            visit('SC_VL2','WG → no compatible target');visit('RETURN','Recirculation on synthetic return connection')
            visit('SC_WAVL','Re-enter sealer selection');visit('SC_VL1','Compatibility restored by test fixture' if not c.get('unknown') else 'Unknown footprint: still no target')
            if c.get('unknown'):outcome='Unresolved recirculation; not a completed exit';return finish()
        if lane>4:visit('SC_VL2',f'Target {lane:02}')
        visit(f'SEAL{lane}','Seal carton; processing time simulated',7+repeat)
    visit('MERGE');line=c.get('pack',1)
    visit('PACK',f'Packaging line {line}; line selection is a fixture, not an emulated PLC allocator')
    side='Side read not cached: simulated missing correlation' if not c.get('cached',True) else 'Side scanner: NOREAD cached' if c.get('side')=='NOREAD' else 'Cache side parcel read'
    for id,msg in [(f'STRAP{line}','Strapping cycle'),(f'SC_BWL{line}','Save gross weight / notify LVS; no rejection rule assumed'),(f'SC_ETIINL{line}','Request label print'),(f'PRINT{line}','Synthetic successful label application'),(f'SC_ETIOSL{line}',side)]:visit(id,msg,3)
    command=verify(c.get('side','PARCEL'),c.get('top','TRACK123456'),c.get('record',True),c.get('cached',True))
    visit(f'SC_ETIOTL{line}',f'Top/side correlation → {command}')
    if command!='WG':outcome=f'{command}: verification exception; downstream physical recovery unverified';return finish()
    visit('OUT');visit('SHIP','Carrier destination provided by synthetic configuration')
    if c.get('shiploop') or c.get('nio'):
        for _ in range(4 if c.get('nio') else 1):
            visit('SC_H2T9','No available matching target: continue');visit('SHIP','Shipping recirculation / retry')
    visit(c.get('dest','H3_CHUTE1' if c.get('nio') else 'H4_EXIT1'),'NIO after retry limit' if c.get('nio') else 'Selected shipping destination reached')
    if c.get('nio'):outcome='NIO: shipping retries exhausted; reject destination reached'
    return finish()

for c in cases:
    for repeat in range(1,4):parcels.append(make(c,repeat))

assert weight(10000,10500)=='WEIGHTOK' and weight(10000,10501)=='WEIGHTERR'
assert weight(0,100)=='PENDING' and weight(1000,1060)=='WEIGHTOK'
assert sealer('UNKNOWN',[],True) is None
assert sealer('FP1',[dict(lane=1,compatible=True,age=5),dict(lane=2,compatible=True,age=1)])==2
for p in parcels:
    assert p['stops'][0]['id']=='SC_START'
    assert all(a['t']<=b['t'] for a,b in zip(p['events'],p['events'][1:]))
    for a,b in zip(p['legs'],p['legs'][1:]):assert a['b']==b['a'] and a['end']<=b['start']
report=dict(scenarioFamilies=len(cases),runs=len(parcels),repetitions=3,entryChecks=len(parcels),continuousPaths=True,sealerBranchOrdering=True,
    scope='Python ports of selected SC1 rules plus explicit routing/configuration fixtures. Not original C# service execution or exhaustive plant fault coverage.',
    unresolved=['Actual PLC packaging allocation','Station role/occupancy configuration','SC_UMR version mismatch','Exact CAD coordinates','Verification reject recovery geometry','Printer/PLC/network/database failure state machines','Physical conveyor speed and dwell times'])
model=dict(nodes=list(N.values()),edges=edges,parcels=parcels,validation=report)
(ROOT/'analysis/synthetic-replay-model.json').write_text(json.dumps(model,ensure_ascii=False),encoding='utf-8')
(ROOT/'analysis/synthetic-replay-validation.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
template=(ROOT/'tools/synthetic-replay.html').read_text(encoding='utf-8')
template=template.replace('__CORPORATE_CSS__',(ROOT/'tools/corporate-ui.css').read_text(encoding='utf-8'))
template=template.replace('__PRODUCT_UI__',(ROOT/'tools/product-ui.js').read_text(encoding='utf-8'))
brand=Path(r'C:\Users\porfy\source\repos\IocOrchestrator\wwwroot\images\site-logo.png')
template=template.replace('__BRAND_IMAGE__','data:image/png;base64,'+base64.b64encode(brand.read_bytes()).decode('ascii'))
engine=(ROOT/'tools/replay-engine.js').read_text(encoding='utf-8')
engine+='\n'+(ROOT/'tools/label-layout.js').read_text(encoding='utf-8')
(ROOT/'analysis/synthetic-equipment-replay.html').write_text(template.replace('__ENGINE__',engine).replace('__MODEL__',json.dumps(model,ensure_ascii=False)),encoding='utf-8')
(ROOT/'analysis/synthetic-scenarios.md').write_text('# Synthetic parcel catalogue\n\n'+report['scope']+'\n\nCoordinates, durations, operator releases and configuration inputs are simulated. Each case runs three times. Exceptions stop where recovery is not established.\n\n'+'\n'.join(f"- `{p['id']}` — {p['name']} — {p['completeness']}" for p in parcels)+'\n\nUnresolved: '+', '.join(report['unresolved']),encoding='utf-8')
print(json.dumps(report,indent=2))
