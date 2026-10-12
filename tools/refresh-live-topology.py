"""Refresh the schematic seed and scanner-order evidence from a verified PJ01 export."""
import collections, hashlib, importlib, json, sys
from pathlib import Path

root = Path(__file__).resolve().parents[1]
data = Path(sys.argv[1])
index = json.loads((data/'index.json').read_text(encoding='utf8'))
dictionary = json.loads((data/index['dictionary']).read_text(encoding='utf8'))
assert dictionary['id'] == index['dictionary_id']
decode = importlib.import_module('audit-capture-flow').decode
parcels = [decode(data/e['file'], dictionary['values']) for e in index['entries']]
counts = collections.Counter()
for parcel in parcels:
    previous = None
    for event in parcel['events']:
        if event.get('kind') != 'scan': continue
        current = event['point']
        if previous and previous != current: counts[previous,current] += 1
        previous = current
observations = {'dataset':'Data-live-1.8','dictionaryId':dictionary['id'],'indexSha256':hashlib.sha256((data/'index.json').read_bytes()).hexdigest(),
    'parcelHistories':len(parcels),'scannerReads':sum(e.get('kind')=='scan' for p in parcels for e in p['events']),
    'scope':'Scanner succession from the published export; logical order evidence, not surveyed belt geometry or proof of every alias association.',
    'transitions':[{'a':a,'b':b,'count':count} for (a,b),count in counts.most_common()]}
(root/'topology/live-1.8-route-observations.json').write_text(json.dumps(observations,indent=2)+'\n',encoding='utf8')
layout = json.loads((root/'topology/replay-layout.json').read_text(encoding='utf8'))
nodes = {n['id']:n for n in layout['nodes']}
nodes['SC_H3RU'].update(x=190,y=1810,note='Hall 3 chute scanner; repeated live scans place it before Hall 3 telescopes.')
for line in range(1,6):
    side,top = nodes[f'SC_ETIOSL{line}'],nodes[f'SC_ETIOTL{line}']
    top.update(x=side['x'],y=side['y'],coordinateBasis='Co-located side/top read heads; no physical conveyor travel between them')
obsolete = {'SC_H4RU>SC_H3T1','SC_H3T7>SC_H3RU','SC_H3RU>SC_H2T1'}
layout['edges'] = [e for e in layout['edges'] if e['id'] not in obsolete]
for e in layout['edges']:
    e['points'][0] = [nodes[e['a']]['x'],nodes[e['a']]['y']]
    e['points'][-1] = [nodes[e['b']]['x'],nodes[e['b']]['y']]
def connect(a,b,via,role='trunk'):
    if any(e['a']==a and e['b']==b for e in layout['edges']):return
    layout['edges'].append({'id':a+'>'+b,'a':a,'b':b,'points':[[nodes[a]['x'],nodes[a]['y']]]+via+[[nodes[b]['x'],nodes[b]['y']]],
        'routeType':role,'basis':'Live scanner order and source equipment roles; schematic corridor, physical survey pending'})
connect('SC_H4RU','SC_H3RU',[[nodes['SC_H4RU']['x'],1740],[190,1740]])
connect('SC_H3RU','SC_H3T1',[[190,nodes['SC_H3T1']['y']]])
connect('SC_H3T7','SC_H2T1',[[1650,nodes['SC_H3T7']['y']],[1650,2140],[nodes['SC_H2T1']['x'],2140]])
connect('SC_UMR','PACK',[[350,nodes['SC_UMR']['y']],[350,nodes['PACK']['y']]],'branch')
(root/'topology/replay-layout.json').write_text(json.dumps(layout,indent=2)+'\n',encoding='utf8')
print(json.dumps({'histories':len(parcels),'transitions':len(counts),'edges':len(layout['edges'])}))
