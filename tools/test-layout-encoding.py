"""Exercise actual layout writer twice with Windows' legacy default encoding."""
import importlib.util, json, os, subprocess, sys, tempfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('repair',ROOT/'tools/repair-physical-layout.py')
repair=importlib.util.module_from_spec(spec);spec.loader.exec_module(repair)
before=json.loads((ROOT/'topology/replay-layout.json').read_text(encoding='utf8'))
geometry=[(e['id'],e['points']) for e in before['edges']]
with tempfile.TemporaryDirectory() as tmp:
    root=Path(tmp)
    for name in ['topology/replay-layout.json','ParcelJourney.App/Assets/replay-model.json']:
        p=root/name;p.parent.mkdir(parents=True,exist_ok=True)
        p.write_text(json.dumps(before,ensure_ascii=False),encoding='utf8')
    env=dict(os.environ,PYTHONUTF8='0',PYTHONIOENCODING='utf8')
    # Run the writer itself with only its workspace path redirected to fixtures.
    replacement='ROOT=Path('+repr(str(root))+')'
    code="from pathlib import Path; p=Path(%r); s=p.read_text(encoding='utf8').replace('ROOT=Path(__file__).resolve().parents[1]', %r); exec(compile(s,str(p),'exec'), {'__name__':'__main__','__file__':str(p)})" % (str(ROOT/'tools/repair-physical-layout.py'),replacement)
    env['PYTHONPATH']=str(ROOT/'tools')
    for _ in range(2):subprocess.run([sys.executable,'-c',code],env=env,check=True,capture_output=True)
    for name in ['topology/replay-layout.json','ParcelJourney.App/Assets/replay-model.json']:
        after=json.loads((root/name).read_text(encoding='utf8'))
        assert [(n['label'],n.get('note'),n.get('coordinateBasis')) for n in after['nodes']]==[(n['label'],n.get('note'),n.get('coordinateBasis')) for n in before['nodes']]
        assert geometry==[(e['id'],e['points']) for e in after['edges']]
        assert next(n['label'] for n in after['nodes'] if n['id']=='SC_VL1')=='Select sealer 1\u20134'
print('PASS: two real layout-writer passes preserve Unicode metadata and geometry with legacy Windows encoding defaults.')
