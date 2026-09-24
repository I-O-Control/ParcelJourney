"""Build both offline viewers from the audited real-log import (no simulated events)."""
import base64, datetime as dt, json, re
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
def timestamp(s): return dt.datetime.strptime(s,'%Y.%m.%d %H:%M:%S.%f')
def evidence(r): return dict(FileName=r['file'],LineNumber=r['line'],RawLine=r['raw'])

def build():
    data=json.loads((ROOT/'tmp/fiege-index.json').read_text(encoding='utf-8'))
    layout=json.loads((ROOT/'topology/replay-layout.json').read_text())
    nodes={n['id']:n for n in layout['nodes']}
    # Hall 3 chute decision precedes the Hall 3 telescopes in the real logs.
    # Keep its three outputs as one vertical chute bank on its left.  The prior
    # layout moved the decision to the approach side but left these endpoints
    # at their old, right-hand coordinates, creating a misleading cross-plant
    # loop.  This is a readable schematic arrangement, not surveyed CAD data.
    nodes['SC_H3RU'].update(x=190,y=1810,coordinateBasis='Schematic approach position; order verified by real logs')
    h3_chute_layout={
        'H3_CHUTE1':(-100,1660), # NIO / chute 1
        'H3_CHUTE2':(-100,1810),
        'H3_CHUTE3':(-100,1960),
    }
    for chute,(x,y) in h3_chute_layout.items():
        nodes[chute].update(x=x,y=y,coordinateBasis='Schematic Hall 3 chute bank; vertically stacked left of the logged chute decision')
    # Side/top reads share an equipment base and occur 15–31 ms apart. They
    # are two read heads at one station, not a 190-unit conveyor journey.
    for line in range(1,6):
        side=nodes[f'SC_ETIOSL{line}'];top=nodes[f'SC_ETIOTL{line}']
        top.update(x=side['x'],y=side['y'],coordinateBasis='Co-located side/top reader assembly; shared equipment base in logs')
    # The floor layout already contains the complete schematic conveyor graph.
    # Keep it as the physical substrate; observed parcel transitions only add
    # evidence to these edges or create a clearly marked reference edge when a
    # database position is not part of the floor export.
    edges={e['id']:dict(e, evidence=[], topology='floor-layout', observationStatus='Unobserved floor connection') for e in layout.get('edges',[])}
    # Coordinate corrections above must also move the physical path endpoints.
    # Otherwise the graph is logically connected while the rendered belt stops
    # at the equipment's former position.
    for edge in edges.values():
        edge['points'][0]=[nodes[edge['a']]['x'],nodes[edge['a']]['y']]
        edge['points'][-1]=[nodes[edge['b']]['x'],nodes[edge['b']]['y']]
    # The three outcomes share the short departure belt from the chute
    # decision, then split on separate horizontal levels.  This explicitly
    # replaces the obsolete geometry inherited from the former right-hand
    # chute bank and prevents any diagonal/cross-plant route from being drawn.
    for chute,(_,y) in h3_chute_layout.items():
        edge=edges[f'SC_H3RU>{chute}']
        edge['points']=[[190,1810],[40,1810],[40,y],[-100,y]]
        edge['basis']='Schematic Hall 3 chute bank: common departure belt, then vertically separated outputs left of the chute decision'
    # These two retained floor edges also used the decision's former
    # right-hand coordinate as an intermediate point.  Rebuild them from the
    # current endpoints so neither renderer can draw a diagonal conveyor.
    edges['SC_H3T7>SC_H3RU']['points']=[[1330,1880],[1330,1810],[190,1810]]
    edges['SC_H3T7>SC_H3RU']['basis']='Schematic orthogonal return from the final Hall 3 divert scanner to the Hall 3 chute decision'
    edges['SC_H3RU>SC_H2T1']['points']=[[190,1810],[190,2160],[250,2160],[250,2220]]
    edges['SC_H3RU>SC_H2T1']['basis']='Schematic orthogonal continuation from the Hall 3 chute decision to Hall 2'
    parcels=[];report=[]
    observed=set()
    # VR_* values are database-only virtual routing positions. They are not
    # scanner observations and must never become physical nodes or conveyor
    # edges in the replay geometry.
    refs=sorted({r['fields'].get('LastScanPos','') for c in data['selected'] for r in c['rows']}
                - set(nodes) - {r['fields'].get('LastScanPos','') for c in data['selected'] for r in c['rows'] if r['fields'].get('LastScanPos','').startswith('VR_')})
    for index,loc in enumerate(refs):
        if loc.startswith('SC_LINE'):
            anchor=nodes['SC_BWL'+loc.removeprefix('SC_LINE')]
            x,y=anchor['x'],anchor['y']
        else: x,y=900+150*index,500
        nodes[loc]=dict(id=loc,label=loc+' · recorded reference',x=x,y=y,kind='junction',equipment=loc,coordinateBasis='Database LastScanPos reference; schematic position, not a surveyed scanner')
    def connect(a,b):
        key=a+'>'+b
        if key in edges:
            # Reuse the floor segment identity, but let the observed route
            # geometry win when the replay has a more specific bend for it.
            na,nb=nodes[a],nodes[b];p=[na['x'],na['y']];q=[nb['x'],nb['y']];mid=[]
            if a=='SC_H4RU' and b=='SC_H3RU': mid=[[1970,p[1]],[1970,1810]]
            elif a=='SC_H3RU' and b.startswith('SC_H3T'): mid=[[190,1880]]
            elif a=='SC_H2T9' and b=='SC_H4T1': mid=[[2110,p[1]],[2110,1450],[250,1450]]
            elif a=='SC_H3T7' and b=='SC_H2T1': mid=[[1970,p[1]],[1970,2160],[250,2160]]
            elif a in ('SC_VL1','SC_VL2') and b=='SC_WAVL': mid=[[2040,170],[2040,550],[740,550]]
            elif b.startswith('SC_BWL'): mid=[[p[0],650],[400,650],[400,q[1]]]
            elif a.startswith('SC_ETIOT') and b.startswith('SC_H4'): mid=[[2040,p[1]],[2040,1450],[q[0],1450]]
            elif p[0]!=q[0] and p[1]!=q[1]: mid=[[p[0],q[1]]]
            edges[key]['points']=[p,*mid,q]
            edges[key]['observationStatus']='Observed by selected parcel logs'
            return key
        na,nb=nodes[a],nodes[b];p=[na['x'],na['y']];q=[nb['x'],nb['y']]
        mid=[]
        if a=='SC_H4RU' and b=='SC_H3RU': mid=[[1970,p[1]],[1970,1810]]
        elif a=='SC_H3RU' and b.startswith('SC_H3T'): mid=[[190,1880]]
        elif a=='SC_H2T9' and b=='SC_H4T1': mid=[[2110,p[1]],[2110,1450],[250,1450]]
        elif a=='SC_H3T7' and b=='SC_H2T1': mid=[[1970,p[1]],[1970,2160],[250,2160]]
        elif a in ('SC_VL1','SC_VL2') and b=='SC_WAVL': mid=[[2040,170],[2040,550],[740,550]]
        elif b.startswith('SC_BWL'): mid=[[p[0],650],[400,650],[400,q[1]]]
        elif a.startswith('SC_ETIOT') and b.startswith('SC_H4'): mid=[[2040,p[1]],[2040,1450],[q[0],1450]]
        elif p[0]!=q[0] and p[1]!=q[1]: mid=[[p[0],q[1]]]
        edges[key]=dict(id=key,a=a,b=b,points=[p,*mid,q],basis='Observed scanner succession; schematic interpolation, intermediate equipment not asserted',evidence=[],topology='observed-transition',observationStatus='Observed by selected parcel logs')
        return key
    for c in data['selected']:
        assert all(e['file'].startswith(('ConMFRtoLVS','ProcLVS')) and ('Sent:' in e['raw'] or 'Sending data' in e['raw']) and (timestamp(e['time'])-timestamp(c['end'])).total_seconds()<1 for e in c['laterEvidence']),c['id']
        assert any('Ausschleuse Meldung' in r['raw'] and 'CLOSE' in r['raw'] for r in c['audit'])
        assert any('SendeAusschleusungAnFWMS: received Data:' in r['raw'] for r in c['audit'])
        start=c['scans'][0]['time']; origin=timestamp(start)
        events=[];stops=[];legs=[]
        # Scanner arrivals establish movement. Acknowledgements/DB writes are
        # observations, never extra visits or fabricated processing operations.
        rows=[dict(r,source='scan') for r in c['scans']]+[dict(r,source='db') for r in c['rows']]
        rows.sort(key=lambda r:(r['time'],0 if r.get('kind')=='OnScannerData' else 1,r['file'],r['line']))
        seen=set()
        for r in rows:
            t=(timestamp(r['time'])-origin).total_seconds()
            if t<0: continue
            key=(r['time'],r['raw'])
            if key in seen: continue
            seen.add(key)
            if r['source']=='scan':
                loc=r['scanner']; observed.add(loc)
                assert loc in nodes,loc
                nodes[loc]['equipment']=r['equipment']
                if r['kind']=='OnScannerData':
                    if not stops or stops[-1]['id']!=loc:
                        if stops:
                            prev=stops[-1];eid=connect(prev['id'],loc)
                            assert t>prev['departure'],(c['id'],r)
                            legs.append(dict(a=prev['id'],b=loc,start=prev['departure'],end=t,edges=[eid],positionBasis='Interpolated between logged observations'))
                            edges[eid]['evidence'].append(evidence(r))
                        stops.append(dict(id=loc,arrival=t,departure=t))
                    summary='Scanner read · '+loc
                else: summary='PLC acknowledgement → '+r['target']+' · '+loc
                # Delayed acknowledgements at an older scanner do not move the parcel back.
                location=stops[-1]['id']
                if loc==location: stops[-1]['departure']=t
                event_type=r['kind']
            else:
                f=r['fields'];loc=f.get('LastScanPos','');location=stops[-1]['id']
                if loc in refs and loc!=location:
                    observed.add(loc)
                    prev=stops[-1]
                    if t>prev['departure']:
                        eid=connect(prev['id'],loc)
                        legs.append(dict(a=prev['id'],b=loc,start=prev['departure'],end=t,edges=[eid],positionBasis='Interpolated to recorded database reference'))
                        edges[eid]['evidence'].append(evidence(r))
                    else:
                        assert (nodes[loc]['x'],nodes[loc]['y'])==(nodes[location]['x'],nodes[location]['y'])
                    stops.append(dict(id=loc,arrival=t,departure=t));location=loc
                virtual = loc.startswith('VR_')
                summary='Database '+f.get('Status','')+' · '+(f'Routing tab {loc}' if virtual else loc)+' · PLC '+f.get('PlcTarget','')
                if virtual and f.get('VRNewHeight') not in (None, '', '-1'):
                    summary += ' · height '+f.get('VRNewHeight','')
                if loc==location: stops[-1]['departure']=t
                event_type='Database state'
            events.append(dict(t=t,LocationId=location,ObservedLocationId=loc,Summary=summary,EventType=event_type,Timestamp=r['time'],Evidence=[evidence(r)],values=(r.get('fields') if r['source']=='db' else None),routingTab=(loc if r['source']=='db' and loc.startswith('VR_') else None)))
        # A physical station moment includes scanner, database and PLC records that
        # can be separated by only a few milliseconds. Carry the latest tudata
        # snapshot into the first event of that moment so the UI can show the
        # complete parcel identity without inventing a later journey step.
        for i,e in enumerate(events):
            snapshot={}
            for other in events:
                if other['t']<=e['t']+0.5 and other['LocationId']==e['LocationId']:
                    snapshot.update(other.get('values') or {})
            if snapshot: e['values']=snapshot
        final=c['rows'][-1]; destination=next(re.search(r'ChuteID=(\w+)',r['raw'])[1] for r in reversed(c['audit']) if 'SendeAusschleusungAnFWMS: received Data:' in r['raw'])
        closed=(timestamp(c['end'])-origin).total_seconds()
        tail=c['laterEvidence']
        for r in tail:
            t=(timestamp(r['time'])-origin).total_seconds()
            events.append(dict(t=t,LocationId=stops[-1]['id'],Summary='LVS exit notification sent · '+destination,EventType='LVS notification',Timestamp=r['time'],Evidence=[evidence(r)]))
        duration=events[-1]['t'];stops[-1]['departure']=duration
        desc=f"{final['fields']['CarrierID']} · line {next(s[-1] for s in c['route'] if s.startswith('SC_BWL'))} · exit {destination}"
        completion=f"CLOSED · exit {destination} confirmed by PLC / FWMS"
        proof=dict(start=evidence(c['scans'][0]),registration=evidence(c['rows'][0]),closed=evidence(final),aliases=c['aliases'],filesChecked=len(data['manifest']),checkedThrough=max(f['last'] for f in data['manifest']),lastOccurrence=c['audit'][-1]['time'],laterMovement=0,laterNotificationCount=len(tail),scope='Complete lifecycle within supplied logs; intermediate unobserved stations are not invented')
        parcels.append(dict(id=c['id'],name=desc,events=events,stops=stops,legs=legs,duration=duration,completeness=completion,outcome='completed',fixture={},source='real-logs',aliases=c['aliases'],proof=proof))
        report.append(f"## {c['id']} — {desc}\n\nStart: {start}. CLOSED: {c['end']}. Last occurrence: {proof['lastOccurrence']}.\n\nAliases: {', '.join(c['aliases'])}.\n\nStart evidence: {proof['start']['FileName']}:{proof['start']['LineNumber']}.\nClosure: {proof['closed']['FileName']}:{proof['closed']['LineNumber']}.\n\nScanner reads (repeated reads retained): {' → '.join(c['route'])}\n\nLater movement: 0. Later exit-notification records: {len(tail)}.\n")
    for n in nodes.values(): n['observationStatus']='Observed in selected logs' if n['id'] in observed else 'Retained layout element; not established by selected logs'
    validation=dict(mode='real-logs',runs=len(parcels),scenarioFamilies=len(parcels),filesChecked=len(data['manifest']),completeLifecycles=True,laterMovement=0,scope='Real timestamps, scanner reads, PLC acknowledgements and DB states. Geometry and between-scan position remain schematic.')
    # Logs provide route evidence, not a complete CAD/topology inventory.
    # Keep the full floor topology in the rendered model; evidence status on
    # each edge tells the UI/analysis whether the selected parcels traversed it.
    model=dict(nodes=list(nodes.values()),edges=list(edges.values()),parcels=parcels,validation=validation,sourceManifest=data['manifest'])
    (ROOT/'analysis/fiege-replay-model.json').write_text(json.dumps(model,ensure_ascii=False),encoding='utf-8')
    (ROOT/'analysis/fiege-replay-validation.json').write_text(json.dumps(validation,indent=2),encoding='utf-8')
    (ROOT/'analysis/fiege-real-cases.md').write_text('# Ten completed Fiege journeys — 11 September 2026\n\n'+validation['scope']+'\n\nAll 226 supplied files searched by ParcelID and linked Tracking IDs. Start requires SC_START and INSERT/NEW; completion requires CLOSED, PLC exit confirmation and FWMS destination notification. Later references are classified, not silently discarded. No claim is made about files outside this supplied capture. Missing intermediate reads remain gaps; no synthetic workstation, sealer, printer or transit events are added. Unobserved layout elements are retained without claiming log confirmation.\n\n'+'\n'.join(report),encoding='utf-8')
    template=(ROOT/'tools/real-replay.html').read_text(encoding='utf-8')
    for key,file in [('__CORPORATE_CSS__','tools/corporate-ui.css'),('__PRODUCT_UI__','tools/product-ui.js')]: template=template.replace(key,(ROOT/file).read_text(encoding='utf-8'))
    old=(ROOT/'analysis/equipment-replay.html').read_text(encoding='utf-8')
    brand=re.search(r'data:image/png;base64,[A-Za-z0-9+/=]+',old)
    template=template.replace('__BRAND_IMAGE__',brand[0] if brand else '')
    engine=(ROOT/'tools/replay-engine.js').read_text(encoding='utf-8')+'\n'+(ROOT/'tools/label-layout.js').read_text(encoding='utf-8')
    inline=json.dumps(model,ensure_ascii=False).replace('<',r'\u003c').replace('>',r'\u003e')
    rendered=template.replace('__ENGINE__',engine).replace('__MODEL__',inline)
    # Keep the worker-facing view compact, while leaving the full evidence available
    # in the parcel detail card.  The evidence is opened by default so the actual
    # source line is never hidden behind a technical-only disclosure.
    ui_patch='''<style>
    .area-placeholder{position:absolute;z-index:1;pointer-events:none;padding:6px;border:0;background:transparent;color:#a9b7ca;text-transform:uppercase;letter-spacing:4px;font-size:13px;font-weight:750;opacity:.2;white-space:nowrap;text-shadow:0 2px 10px var(--bg);transform:translate(-50%,-50%)}
    </style><script>
    (()=>{
      const side=document.querySelector('aside');
      if(side){
        [...side.querySelectorAll('h2')].forEach(h=>{if(/Equipment [/] event detail/i.test(h.textContent)){h.hidden=true;if(h.nextElementSibling)h.nextElementSibling.hidden=true;}});
        [...side.querySelectorAll('h2')].forEach(h=>{if(/Recorded timeline/i.test(h.textContent))h.textContent='Journey timeline';});
      }
      const stage=document.getElementById('stage'),map=document.getElementById('map');
      if(stage&&map){
        const layer=document.createElement('div');layer.id='area-placeholders';stage.append(layer);
        const areas=[['ENTRY & QUALITY',180,-560],['SEALING',920,320],['PACKAGING',980,900],['HALLE 4',1420,1420],['HALLE 3',1420,1810],['HALLE 2',1420,2200]];
        const paint=()=>{const c=map.getScreenCTM(),r=stage.getBoundingClientRect(),degrees=+(document.getElementById('rotation')?.value||0);if(!c)return;layer.replaceChildren();for(const [name,x,y] of areas){const point=window.LabelLayout?LabelLayout.rotate([x,y],degrees):[x,y],q=new DOMPoint(point[0],point[1]).matrixTransform(c),d=document.createElement('div');d.className='area-placeholder';d.textContent=name;d.style.left=(q.x-r.left)+'px';d.style.top=(q.y-r.top)+'px';layer.append(d)}};
        new ResizeObserver(paint).observe(stage);window.addEventListener('resize',paint);window.addEventListener('parcel-map-changed',paint);setTimeout(paint,0);setTimeout(paint,300);
      }
      const openEvidence=()=>{const d=document.querySelector('#parcel-detail details');if(d)d.open=true};
      new MutationObserver(openEvidence).observe(document.body,{childList:true,subtree:true});openEvidence();
    })();
    </script>'''
    rendered=rendered.replace('</html>',ui_patch+'</html>')
    (ROOT/'analysis/equipment-replay.html').write_text(rendered,encoding='utf-8')
    print(json.dumps(validation,indent=2))

if __name__=='__main__': build()
