"""Create synthetic parcel feeds covering the known Fiege routing outcomes."""
from pathlib import Path
import json, shutil, datetime

ROOT=Path(__file__).resolve().parents[1]
FEED=ROOT/'test-feed-parcel'

SCENARIOS=[
('01-normal-ar','Normal accepted telescopic divert','CLOSED','SC_VL1 -> SC_ETIINL1 -> SC_ETIOSL1 -> Hall 4 telescopic destination','AR'),
('02-normal-al','Normal accepted left divert','CLOSED','SC_START -> SC_WAAP -> SC_VL1 -> Hall 3 telescopic destination','AL'),
('03-straight-through','Straight-through transport','CLOSED','SC_START -> SC_WAAP -> SC_WAVL -> SC_H4T1','WG'),
('04-alternate-target','Alternate/second-right target','CLOSED','SC_VL2 -> SC_H4T4 -> SC_H4T5','RR'),
('05-labeler-line-1','Labeler line 1 with printer output','CLOSED','SC_ETIINL1 -> printer 1 -> SC_ETIOSL1 -> destination','AR'),
('06-labeler-line-5','Labeler line 5 with printer output','CLOSED','SC_ETIINL5 -> printer 5 -> SC_ETIOSL5 -> destination','AR'),
('07-no-read','No-read routed to NIO','NIO','SC_VL1 -> NR/NOREAD -> NIO chute 03091','NR'),
('08-weight-error','Weight error routed to NIO','NIO','SC_START -> SC_VL1 -> weight error -> NIO','NR'),
('09-plausibility-error','Plausibility error','NIO','SC_WAAP -> PF -> NIO','PF'),
('10-no-data','No-data parcel','NIO','SC_WAAP -> ND -> NIO','ND'),
('11-empty-carrier','Empty carrier','NIO','SC_WAAP -> LB -> NIO','LB'),
('12-wrong-divert','Wrong actual divert','MISROUTE','SC_VL1 target AR -> actual B2 at labeler output','B2'),
('13-missing-ack','PLC acknowledgement missing','PENDING','SC_VL1 -> target AR command -> no final acknowledgement','AR'),
('14-missing-db-row','Parcel missing from database','EXCEPTION','SC_VL1 -> no tudata row found','—'),
('15-repeat-at-wa','WA circulation limit exceeded','NIO','SC_WAAP repeated 4 rounds -> NIO chute 03091','NR'),
('16-late-reporting','Late external reporting after close','CLOSED','SC_VL1 -> accepted divert -> close -> late LVS report','AR'),
('17-duplicate-messages','Duplicate technical messages','CLOSED','duplicate scan messages deduplicated -> accepted divert','AR'),
('18-chute-return','Hall 3 chute/return destination','CLOSED','SC_H3RU03 -> chute 03 / return destination','03'),
]

def logs(i,pid,status,target,route):
    t=f'2026.09.10 09:{i:02d}:00.000'
    lines=[f"{t} - Daten - OnScannerData: received Data: '2050-1|SC_VL1|{pid}'"]
    if status=='EXCEPTION': lines += [f"{t[:-4]}100 - Daten - Zu Karton '{pid}' gibt es keinen Datensatz! Pos: SC_VL1"]
    elif status=='NIO': lines += [f"{t[:-4]}100 - Daten - ScanZuteilungVerschl??erlinien: Target:'2050-1|{target}|NIO'",f"{t[:-4]}200 - Daten - NIO routing: parcel '{pid}' sent to chute 03091"]
    else:
        lines += [f"{t[:-4]}100 - Daten - SendTaskToPlc: Parameter='2050-1|{pid}|{target}')"]
        if status != 'PENDING': lines += [f"{t[:-4]}300 - Daten - OnPlcAcknowledge: received Data: '2050-1|SC_VL1|{pid}|{target}'"]
        if 'printer' in route.lower() or 'labeler' in route.lower(): lines += [f"{t[:-4]}400 - Daten - ScanEtiEingang: '{pid}' DS x ParcelId '{pid}'",f"{t[:-4]}500 - Test - Printer job dispatched for '{pid}'"]
        if status=='MISROUTE': lines += [f"{t[:-4]}600 - ERROR - Paket '{pid}' wurde falsch ausgeschleust! Pos: 'SC_ETIOSL1' Soll: 'AR' Ist: 'B2'"]
        elif status=='CLOSED': lines += [f"{t[:-4]}800 - Daten - SaveToDB - 'UPDATE (tudata) ParcelID={pid}, LastScanPos=SC_VL1, PlcTarget={target}, Status=CLOSED"]
    return '\n'.join(lines)+'\n'

def main():
    if FEED.exists(): shutil.rmtree(FEED)
    FEED.mkdir()
    manifest=[]
    for i,(slug,title,status,route,target) in enumerate(SCENARIOS,1):
        pid=f'9100000{i:03d}'; d=FEED/slug; d.mkdir()
        (d/'ProcLogic.log').write_text(logs(i,pid,status,target,route),encoding='utf-8')
        (d/'ProcPLC.log').write_text(f"2026.09.10 09:{i:02d}:01.000 - PLC - route={target} parcel={pid} status={status}\n",encoding='utf-8')
        (d/'ConMFRtoLVS.log').write_text(f"2026.09.10 09:{i:02d}:02.000 - LVS - parcel={pid} outcome={status}\n" if status in ('CLOSED','MISROUTE') else '',encoding='utf-8')
        expected={'parcelId':pid,'orderId':f'920000000000000{i:02d}','trackingId':f'TRK{pid}','scenario':slug,'title':title,'expectedOutcome':status,'routeDescription':route,'target':target,'source':'synthetic Fiege behavior feed'}
        (d/'expected.json').write_text(json.dumps(expected,indent=2),encoding='utf-8'); manifest.append(expected)
    (FEED/'manifest.json').write_text(json.dumps({'name':'Fiege ParcelJourney test feed','readOnlySources':True,'scenarios':manifest},indent=2),encoding='utf-8')
    (FEED/'README.md').write_text('# ParcelJourney test feed\n\nEach scenario contains ProcLogic.log, ProcPLC.log, ConMFRtoLVS.log, and expected.json. Run the viewer from the ParcelTracer project or launch the generated HTML file.\n',encoding='utf-8')
    svg=(ROOT/'analysis/scada/fiege-ungarn-belt-geometry.svg').read_text(encoding='utf-8')
    data=json.dumps(manifest,ensure_ascii=False).replace('</','<\\/')
    html=f'''<!doctype html><meta charset="utf-8"><title>ParcelJourney Test Feed</title><style>
body{{margin:0;background:#101820;color:#e8eef5;font:14px Segoe UI,Arial;display:grid;grid-template-columns:minmax(600px,1fr) 390px;height:100vh;overflow:hidden}}main{{overflow:auto;padding:10px}}aside{{background:#172531;border-left:1px solid #405466;padding:18px;overflow:auto}}h1{{font-size:20px;margin:0 0 10px}}h2{{font-size:15px;color:#8fd9cc;margin:18px 0 8px}}select,button,input{{background:#223746;color:#e8eef5;border:1px solid #587181;padding:8px}}select{{width:100%}}.controls{{display:flex;gap:8px;align-items:center;margin:8px 0}}.controls button{{cursor:pointer}}.map{{min-width:900px;position:relative}}.map svg{{width:100%;height:auto;display:block}}.journey-line{{fill:none;stroke:#ff3b30;stroke-width:10;stroke-linecap:round;stroke-linejoin:round;filter:url(#journeyGlow);stroke-dasharray:22 16;animation:flow 1s linear infinite}}.journey-dot{{fill:#ff3b30;stroke:#ffd6d2;stroke-width:3;filter:url(#journeyGlow)}}@keyframes flow{{to{{stroke-dashoffset:-38}}}}.step{{border-left:3px solid #35b7a3;padding:8px;margin:6px 0;background:#203541}}.step.active{{border-left-color:#ff3b30;background:#4b2b32}}.time{{color:#9ab1bf;font-size:12px}}.detail{{color:#b9c9d3;line-height:1.4}}.outcome{{font-size:18px;font-weight:600;margin:10px 0;color:#f4b942}}code{{color:#bfe8e1}}</style><main><h1>Fiege ParcelJourney — real-time test feed</h1><div class="map">{svg}<svg id="journeyOverlay" viewBox="0 0 1305 1855" style="position:absolute;left:0;top:0;pointer-events:none"><defs><filter id="journeyGlow"><feGaussianBlur stdDeviation="5" result="blur"/><feMerge><feMergeNode in="blur"/><feMergeNode in="SourceGraphic"/></feMerge></filter></defs><path id="journeyLine" class="journey-line" d="M80 1260 L260 1260 L430 1120 L650 1120 L850 900 L1040 900 L1180 650"/><circle id="journeyDot" class="journey-dot" cx="80" cy="1260" r="13"/></svg></div></main><aside><label for="scenario">Select a parcel scenario</label><select id="scenario"></select><div class="controls"><button id="play" type="button">Pause journey</button><label>Speed <input id="speed" type="range" min="1" max="5" value="2"></label></div><div id="info"></div><h2>Expected route</h2><div id="route"></div><h2>Feed files</h2><div class="detail">Each selected scenario has <code>ProcLogic.log</code>, <code>ProcPLC.log</code>, <code>ConMFRtoLVS.log</code>, and <code>expected.json</code> in the test-feed-parcel folder.</div></aside><script>const scenarios={data};const select=document.querySelector('#scenario');const steps=document.querySelector('#route');let playing=true,progress=0,last=performance.now();scenarios.forEach((s,i)=>{{let o=document.createElement('option');o.value=i;o.textContent=(i+1)+'. '+s.title+' ['+s.expectedOutcome+']';select.append(o)}});function render(){{let s=scenarios[select.value];progress=0;document.querySelector('#info').innerHTML='<h2>'+s.parcelId+'</h2><div class="outcome">Expected outcome: '+s.expectedOutcome+'</div><div class="detail">Order: '+s.orderId+'<br>Tracking: '+s.trackingId+'<br>PLC target: '+s.target+'</div>';steps.innerHTML='<div class="step active">'+s.routeDescription+'</div>';}}select.onchange=render;document.querySelector('#play').onclick=()=>{{playing=!playing;document.querySelector('#play').textContent=playing?'Pause journey':'Play journey'}};function tick(now){{let dt=(now-last)/1000;last=now;if(playing)progress=(progress+dt*.10*Number(document.querySelector('#speed').value))%1;let dot=document.querySelector('#journeyDot'),x=80+1100*progress,y=1260-610*progress+90*Math.sin(progress*10);dot.setAttribute('cx',x);dot.setAttribute('cy',y);requestAnimationFrame(tick)}}render();requestAnimationFrame(tick);</script>'''
    (FEED/'parcel-journey-viewer.html').write_text(html,encoding='utf-8')
    print(f'created {len(manifest)} scenarios in {FEED}')

if __name__=='__main__': main()
