"""Reposition existing schematic corridors; preserve every route identity."""
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def repair(m):
    nodes={n['id']:n for n in m['nodes']}; edges={e['id']:e for e in m['edges']}
    nodes['RETURN'].update(x=2040,y=-120)
    nodes['SC_UMR'].update(x=560,y=200)
    nodes['H3_CHUTE2'].update(x=-100,y=1885)
    updates={
      'SC_VL2>RETURN':[[1460,170],[2040,170],[2040,-120]],
      'RETURN>SC_WAVL':[[2040,-120],[780,-120],[780,80],[740,80],[740,170]],
      'SC_WAVL>MERGE':[[740,170],[740,650],[2040,650]],
      'SC_VL1>NOREAD':[[820,170],[2180,170],[2180,380]],
      'SC_VL2>NOREAD':[[1460,170],[2180,170],[2180,380]],
      'SC_AP>SC_UMR':[[560,-260],[560,200]],
      'SC_UMR>SC_WAVL':[[560,200],[620,200],[620,120],[740,120],[740,170]],
      'SC_UMR>PACK':[[560,200],[560,710],[400,710],[400,770]],
      'SC_H3RU>H3_CHUTE2':[[190,1810],[40,1810],[40,1885],[-100,1885]],
    }
    for i in range(1,11):
        n=nodes[f'AP{i}']; updates[f'AP{i}>SC_UMR']=[[n['x'],n['y']],[400,n['y']],[400,120],[560,120],[560,200]]
    for eid,points in updates.items():
        edges[eid]['points']=points
    for e in m['edges']:
        e['points'][0]=[nodes[e['a']]['x'],nodes[e['a']]['y']]
        e['points'][-1]=[nodes[e['b']]['x'],nodes[e['b']]['y']]
    return m
if __name__=='__main__':
    import importlib
    audit=importlib.import_module('physical-network')
    paths=[ROOT/'topology/replay-layout.json',ROOT/'ParcelJourney.App/Assets/replay-model.json']
    for path in paths:
        before=json.loads(path.read_text(encoding='utf8')); after=repair(json.loads(path.read_text(encoding='utf8')))
        assert [(e['id'],e['a'],e['b']) for e in before['edges']]==[(e['id'],e['a'],e['b']) for e in after['edges']]
        errors=audit.compile_network(after)['errors']
        if errors:raise ValueError(json.dumps(errors,indent=2))
        path.write_text(json.dumps(after,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
    print('Existing routes repositioned; no crossing, opposing flow or parallel-clearance errors.')
