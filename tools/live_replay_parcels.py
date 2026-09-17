"""Replay a bounded recent log window as if the files were live."""
import json,re,sys
from pathlib import Path
from collections import defaultdict

root=Path(sys.argv[1]); out=Path(sys.argv[2]); limit=int(sys.argv[3]) if len(sys.argv)>3 else 40; wanted=int(sys.argv[4]) if len(sys.argv)>4 else 10
out.mkdir(parents=True,exist_ok=True); (out/'parcels').mkdir(exist_ok=True)
stamp=re.compile(r'^(\d{4}\.\d\d\.\d\d \d\d:\d\d:\d\d\.\d{3})'); db=re.compile(r'(?:INSERT|UPDATE) \(tudata\) (.*)'); fld=re.compile(r'(\w+)=([^,]*),'); packed=re.compile(r'ZP\s+(\d{10})(\d{10})'); pf=re.compile(r"ParcelId\s*='?(\d{10})",re.I); tf=re.compile(r"TrackingI[Dd]\s*='?(\d{10,24})"); scan=re.compile(r"(?:OnScannerData|OnPlcAcknowledge): received Data: '([^']+)'")
files=sorted(root.glob('*.log'),key=lambda p:p.stat().st_mtime,reverse=True)[:limit]
rows=[]; new_at={}; terminal=set(); aliases=defaultdict(set)
for f in files:
    for n,line in enumerate(f.open('r',encoding='cp1252',errors='replace'),1):
        sm=stamp.match(line)
        if not sm: continue
        found=set(); m=db.search(line); fields={}
        if m:
            fields={k:v.strip() for k,v in fld.findall(m.group(1))}
            for k in ('ParcelID','OrderID','TrackingId'):
                if fields.get(k) and fields[k]!='NOREAD': found.add(fields[k])
        for m in (packed.search(line),pf.search(line),tf.search(line)):
            if m: found.update(x for x in m.groups() if x)
        s=scan.search(line)
        if s:
            p=s.group(1).split('|'); payload=p[2] if len(p)>2 else ''
            z=packed.search(payload); found.update(z.groups() if z else ([payload.strip()] if payload.strip() and payload.strip()!='NOREAD' else []))
        parcel=fields.get('ParcelID')
        if parcel and fields.get('Status')=='NEW' and parcel not in new_at: new_at[parcel]=sm.group(1)
        if parcel and fields.get('Status') in ('CLOSED','NIO','NO_READ','ERROR'): terminal.add(parcel)
        rows.append((sm.group(1),f,n,line,found,fields))
chosen=[p for p in sorted(new_at,key=lambda p:new_at[p]) if p in terminal][:wanted]
for p in chosen: aliases[p].add(p)
rows.sort(key=lambda x:(x[0],str(x[1]),x[2]))
journeys={p:{'parcelId':p,'aliases':[p],'status':'NEW','isLive':True,'events':[],'firstSeen':new_at[p],'lastSeen':new_at[p],'sourceFiles':[]} for p in chosen}
for ts,f,n,line,found,fields in rows:
    for p in chosen:
        if ts < new_at[p]: continue
        if not (aliases[p] & found): continue
        aliases[p].update(found)
        j=journeys[p]; j['aliases']=sorted(aliases[p]); j['lastSeen']=ts
        if fields.get('Status'): j['status']=fields['Status']; j['isLive']=fields['Status'] not in ('CLOSED','NIO','NO_READ','ERROR')
        layer='Transport' if f.name.startswith('Con') else 'PLC' if f.name.startswith('ProcPLC') else 'Logic' if f.name.startswith('ProcLogic') else 'LVS' if f.name.startswith('ProcLVS') else 'Labeler' if f.name.startswith('ProcEtikettierer') else 'Other'
        location=fields.get('LastScanPos') or next((x for x in found if x.startswith('SC_')),None)
        j['events'].append({'timestamp':ts,'layer':layer,'sourceFile':str(f),'line':n,'raw':line.rstrip('\r\n'),'identifiers':sorted(found & aliases[p]),'status':fields.get('Status'),'location':location,'target':fields.get('PlcTarget'),'chuteId':fields.get('ChuteID')})
        if str(f) not in j['sourceFiles']: j['sourceFiles'].append(str(f))
        tmp=out/'parcels'/ (p+'.json.tmp'); tmp.write_text(json.dumps(j,ensure_ascii=False,indent=2),encoding='utf-8'); tmp.replace(out/'parcels'/(p+'.json'))
for p,j in journeys.items(): j['events'].sort(key=lambda e:e['timestamp']); j['sourcePartitions']=sorted({Path(x).parent.name if Path(x).parent.name.startswith('KW_') else 'root' for x in j['sourceFiles']}); j['completeness']='Exited' if not j['isLive'] else 'Active-at-window-end'; (out/'parcels'/(p+'.json')).write_text(json.dumps(j,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'filesRead':len(files),'selected':chosen,'journeys':{p:{'events':len(j['events']),'status':j['status'],'first':j['firstSeen'],'last':j['lastSeen']} for p,j in journeys.items()}}))
