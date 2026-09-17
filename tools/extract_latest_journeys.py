import json,re,sys
from pathlib import Path
from collections import defaultdict

root=Path(sys.argv[1]); out=Path(sys.argv[2]); out.mkdir(parents=True,exist_ok=True)
files=sorted(root.rglob('*.log'))
db=re.compile(r'(?:INSERT|UPDATE) \(tudata\) (.*)')
field=re.compile(r'(\w+)=([^,]*),')
packed=re.compile(r'ZP\s+(\d{10})(\d{10})')
parcel_field=re.compile(r"ParcelId\s*='?(\d{10})",re.I)
tracking_field=re.compile(r"TrackingI[Dd]\s*='?(\d{10,24})")
scan=re.compile(r"(?:OnScannerData|OnPlcAcknowledge): received Data: '([^']+)'")
stamp=re.compile(r'^(\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d\.\d{3})')
ids=defaultdict(set); rows=[]
for f in files:
    for n,line in enumerate(f.open('r',encoding='cp1252',errors='replace'),1):
        if not stamp.match(line): continue
        found=set(); m=db.search(line)
        if m:
            d={k:v.strip() for k,v in field.findall(m.group(1))}
            for k in ('ParcelID','OrderID','TrackingId'):
                if d.get(k) and d[k] != 'NOREAD': found.add(d[k])
        for m in (packed.search(line),parcel_field.search(line),tracking_field.search(line)):
            if m: found.update(x for x in m.groups() if x)
        s=scan.search(line)
        if s:
            p=s.group(1).split('|')
            if len(p)>=3:
                z=packed.search(p[2])
                if z: found.update(z.groups())
                else: found.add(p[2].strip())
        for x in found:
            if re.fullmatch(r'\d{10}',x): ids[x].add(x)
        rows.append((f,n,line,found))
chosen=sorted(ids)[:10]
for pid in chosen:
    aliases=set(ids[pid]); changed=True
    while changed:
        changed=False
        for _,_,_,found in rows:
            if aliases & found and not found <= aliases:
                aliases |= found; changed=True
    ev=[]
    for f,n,line,found in rows:
        if aliases & found:
            ev.append({'timestamp':stamp.match(line).group(1),'sourceFile':str(f),'line':n,'raw':line.rstrip('\r\n'),'identifiers':sorted(found & aliases),'layer':('Transport' if f.name.startswith('Con') else 'PLC' if f.name.startswith('ProcPLC') else 'Logic' if f.name.startswith('ProcLogic') else 'LVS' if f.name.startswith('ProcLVS') else 'Labeler' if f.name.startswith('ProcEtikettierer') else 'Other')})
    ev.sort(key=lambda x:(x['timestamp'],x['sourceFile'],x['line']))
    (out/(pid+'.json')).write_text(json.dumps({'parcelId':pid,'aliases':sorted(aliases),'firstSeen':ev[0]['timestamp'] if ev else None,'lastSeen':ev[-1]['timestamp'] if ev else None,'eventCount':len(ev),'events':ev,'sourcePartitions':sorted({Path(x['sourceFile']).parent.name if Path(x['sourceFile']).parent.name.startswith('KW_') else 'root' for x in ev})},ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'selected':chosen,'files':len(files),'journeys':len(chosen),'events':sum(len(json.loads((out/(p+'.json')).read_text())['events']) for p in chosen)}))
