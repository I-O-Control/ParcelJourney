"""Offline structural and interpolation checks; does not claim browser UI QA."""
from pathlib import Path
import json, re, subprocess, math
ROOT=Path(__file__).resolve().parents[1]
m=json.loads((ROOT/'analysis/synthetic-replay-model.json').read_text(encoding='utf-8'))
n={v['id']:v for v in m['nodes']}; edges={e['id']:e for e in m['edges']}
checks=0
for p in m['parcels']:
    assert p['stops'][0]['id']=='SC_START' and p['events'][0]['t']==0
    for l in p['legs']:
        e=edges[l['edges'][0]]
        assert e['a']==l['a'] and e['b']==l['b']
        assert e['points'][0]==[n[l['a']]['x'],n[l['a']]['y']]
        assert e['points'][-1]==[n[l['b']]['x'],n[l['b']]['y']]
        assert l['start']<l['end']<=p['duration']
        checks+=1
    for a,b in zip(p['legs'],p['legs'][1:]):
        assert a['b']==b['a'] and a['end']<=b['start']
    for event in p['events']:assert event['LocationId'] in n
for i in range(1,8):
    decision='SC_VL1' if i<=4 else 'SC_VL2'
    assert n[decision]['x']<n[f'SEAL{i}']['x']
html=(ROOT/'analysis/equipment-replay.html').read_text(encoding='utf-8')
script=re.search(r'<script>(.*?)</script>',html,re.S)[1]
subprocess.run(['node','--check'],input=script,encoding='utf-8',check=True,capture_output=True)
ids=set(re.findall(r'\bid="([^"]+)"',html))
ids.update(re.findall(r"\.id='([^']+)'",script))
assert set(re.findall(r"\$\('([^']+)'\)",script))<=ids
assert not re.search(r'<(?:script|link)[^>]+(?:src|href)=',html)
# Execute the actual UI interpolation functions, independently of any browser.
functions=script[script.index('function pointAt('):script.index('const fmt=')]
test=functions+'''\nconst points=[[0,0],[100,0],[100,100]];
for(let k=0;k<=200;k++){const p=pointAt(points,k/200);if(!p.every(Number.isFinite))throw Error('nonfinite');if(k<=100&&Math.abs(p[1])>1e-9)throw Error('off belt');if(k>100&&Math.abs(p[0]-100)>1e-9)throw Error('off belt');const q=partial(points,k/200).at(-1);if(Math.hypot(p[0]-q[0],p[1]-q[1])>1e-9)throw Error('trail endpoint');}
console.log('201 corner interpolation / liquid endpoint checks passed');'''
r=subprocess.run(['node'],input=test,encoding='utf-8',check=True,capture_output=True)
print(r.stdout.strip())
print(f"PASS: {len(m['parcels'])} entry starts; {checks} connected timed legs; 7 upstream sealer decisions; JS syntax; DOM references; offline dependencies.")
print('Browser interaction / screenshot verification NOT performed.')
