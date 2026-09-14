"""Reproducible offline import of completed Fiege full-log journeys."""
import argparse, collections, datetime as dt, hashlib, json, re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
STAMP = re.compile(r'^\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d\.\d{3}')
DB = re.compile(r"(?:INSERT|UPDATE) \(tudata\) (.*)")
SCAN = re.compile(r"(OnScannerData|OnPlcAcknowledge): received Data: '([^']+)'")

def read(path):
    return path.read_text(encoding='cp1252', errors='replace').splitlines()

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('source', type=Path)
    args = ap.parse_args()
    files = sorted(args.source.rglob('*.log'))
    if not files: raise SystemExit(f'No log files found in {args.source}')
    (ROOT/'tmp').mkdir(exist_ok=True)
    records = collections.defaultdict(list)
    aliases = collections.defaultdict(set)
    manifest = []
    for f in files:
        lines = read(f)
        stamps = [s[:23] for s in lines if STAMP.match(s)]
        manifest.append(dict(file=f.name, sha256=hashlib.sha256(f.read_bytes()).hexdigest(), first=min(stamps) if stamps else '', last=max(stamps) if stamps else ''))
        if not f.name.startswith('ProcLogic'): continue
        for num, line in enumerate(lines, 1):
            m = DB.search(line)
            if not m: continue
            fields = dict(re.findall(r'(\w+)=([^,]*),', m[1]))
            pid = fields.get('ParcelID')
            if not pid: continue
            records[pid].append(dict(time=line[:23], file=f.name, line=num, raw=line, fields=fields))
            aliases[pid].add(pid)
            tid = fields.get('TrackingId', '').strip()
            if tid and tid != 'NOREAD': aliases[pid].add(tid)
    lookup = collections.defaultdict(set)
    for pid, ids in aliases.items():
        for ident in ids: lookup[ident].add(pid)
    scans = collections.defaultdict(list)
    equipment = {}
    for f in files:
        if not f.name.startswith('ProcLogic'): continue
        for line in read(f):
            m = SCAN.search(line)
            if m:
                parts = re.split(r'[|/]', m[2])
                if len(parts)>2 and parts[1].startswith('SC_'): equipment[parts[0]]=parts[1]
    for f in files:
        if not f.name.startswith('ProcLogic'): continue
        for num, line in enumerate(read(f), 1):
            m = SCAN.search(line)
            if not m: continue
            parts = re.split(r'[|/]', m[2])
            if len(parts) < 3: continue
            parts[1] = parts[1] or equipment.get(parts[0], '')
            if not parts[1].startswith('SC_'): continue
            payload = parts[2]
            sorter = re.match(r'SORTER\s+\w+\s+(\d{10})(\d{10})', payload)
            ident = sorter[2] if sorter else payload
            if len(lookup[ident]) != 1: continue
            pid = next(iter(lookup[ident]))
            scans[pid].append(dict(time=line[:23], file=f.name, line=num, raw=line, scanner=parts[1], equipment=parts[0], kind=m[1], target=parts[3] if len(parts)>3 else ''))
    candidates = []
    for pid, rows in records.items():
        rows.sort(key=lambda r:(r['time'],r['file'],r['line']))
        ss = sorted(scans[pid], key=lambda r:(r['time'],r['file'],r['line']))
        if not ss or ss[0]['scanner'] != 'SC_START' or ss[0]['kind'] != 'OnScannerData': continue
        if rows[0]['fields'].get('Status') != 'NEW' or 'INSERT (tudata)' not in rows[0]['raw']: continue
        if rows[-1]['fields'].get('Status') != 'CLOSED': continue
        end = rows[-1]['time']
        if ss[-1]['time'] > end: continue
        if end > '2026.09.11 14:30:00.000': continue
        route = [s['scanner'] for s in ss if s['kind']=='OnScannerData']
        candidates.append(dict(id=pid, rows=rows, scans=ss, aliases=sorted(aliases[pid]), route=route, end=end))
    # Prefer varied scanner sequences, destinations, workstation visits and loops.
    selected=[]; features=set()
    while candidates and len(selected)<10:
        def feats(c):
            r=c['route']; return set(r)|{f'{a}>{b}' for a,b in zip(r,r[1:])}|{str(c['rows'][-1]['fields'].get('LastScanPos'))+' EXIT'}
        c=max(candidates,key=lambda c:(len(feats(c)-features),len(set(c['route'])),c['id']))
        selected.append(c);features |= feats(c);candidates.remove(c)
    if len(selected)!=10: raise SystemExit(f'Only {len(selected)} complete candidates; refusing to replace the release model')
    # Search every supplied log, including connections and later rotations, by
    # both identities. Keep the complete matching source evidence for review.
    audit = collections.defaultdict(list)
    ids = {ident:c['id'] for c in selected for ident in c['aliases']}
    pattern = re.compile('|'.join(re.escape(s) for s in sorted(ids,key=len,reverse=True)))
    for f in files:
        for num,line in enumerate(read(f),1):
            if not STAMP.match(line): continue
            for pid in {ids[m[0]] for m in pattern.finditer(line)}:
                audit[pid].append(dict(time=line[:23],file=f.name,line=num,raw=line))
    for c in selected:
        evidence=sorted(audit[c['id']],key=lambda r:(r['time'],r['file'],r['line']))
        c['audit']=evidence
        c['laterEvidence']=[e for e in evidence if e['time']>c['end']]
        # Recover logic-log omissions from the PLC's explicit scanner RPC.
        # Correlate the same read across layers, avoiding duplicate visits.
        rpc=re.compile(r"RPCCallSend(Scanner|Ack)DataToLogicModule - ScannerID='([^']+)' ScannerName='([^']+)' Barcode='([^']+)'(?: IstZiel='([^']+)')?")
        for e in evidence:
            m=rpc.search(e['raw'])
            if not m: continue
            kind='OnScannerData' if m[1]=='Scanner' else 'OnPlcAcknowledge'
            instant=dt.datetime.strptime(e['time'],'%Y.%m.%d %H:%M:%S.%f')
            matches=[s for s in c['scans'] if s['scanner']==m[3] and s['kind']==kind and abs((dt.datetime.strptime(s['time'],'%Y.%m.%d %H:%M:%S.%f')-instant).total_seconds())<0.5]
            if not matches:
                c['scans'].append(dict(e,scanner=m[3],equipment=m[2],kind=kind,target=m[5] or '',recoveredFromPLC=True))
        c['scans'].sort(key=lambda r:(r['time'],r['file'],r['line']))
        c['route']=[s['scanner'] for s in c['scans'] if s['kind']=='OnScannerData']
    result=dict(manifest=manifest, candidates=len(candidates)+len(selected), selected=selected)
    (ROOT/'tmp/fiege-index.json').write_text(json.dumps(result,ensure_ascii=False),encoding='utf-8')
    for c in selected: print(c['id'],c['end'],' -> '.join(c['route']))
    print('Candidates:',result['candidates'],'Files:',len(files))

if __name__ == '__main__': main()
