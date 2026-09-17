// Evidence-derived state. Unknown vocabulary is retained, never an operational fault by itself.
export const fields = ['PrincipalID','OrderID','ParcelID','ControlFlag','GrossWeight','CarrierID','CartonType','LastScanPos','WeightStart','WeightBrutto','VRNewHeight','PlcTarget','TrackingId','Status'];
const numeric = new Set(['GrossWeight','WeightStart','WeightBrutto','VRNewHeight']);
export const meanings = {NEW:'A parcel record was created.',PENDING:'The parcel is being processed.',WEIGHTERR:'The start-weight check failed; the parcel requires workstation handling.',WEIGHTOK:'The start-weight check was accepted.',WORKSTATION:'The parcel was assigned to a workstation.',CLOSED:'The material-flow controller recorded the outbound diversion as complete.',FAULTY:'The parcel record is marked faulty; the cause requires the associated evidence.'};
export function millis(value) {
  const m = /^(\d{4})[.-](\d\d)[.-](\d\d)[T ](\d\d):(\d\d):(\d\d)\.(\d{3})/.exec(value || '');
  return m ? Date.UTC(...m.slice(1).map((v,i)=>Number(v)-(i===1?1:0))) : NaN;
}
export function dbValues(raw) {
  if (!/(?:INSERT|UPDATE) \(tudata\)/i.test(raw)) return null;
  const values = {};
  for (const m of raw.matchAll(/\b(\w+)=([^,]*)(?:,|$)/g)) {
    const key = fields.find(k=>k.toLowerCase()===m[1].toLowerCase()) || m[1];
    const value = m[2].trim();
    values[key] = numeric.has(key) && /^-?\d+$/.test(value) && Number.isSafeInteger(Number(value)) ? Number(value) : value;
  }
  return values;
}
export function prepareObservations(events, base) {
  return events.map((e,index)=>{
    const raw=e.raw??e.Raw??'', timestamp=e.timestamp??e.Timestamp;
    const values=dbValues(raw), explicit=e.location??e.Location??values?.LastScanPos??raw.match(/\bSC_[A-Z0-9_]+\b/)?.[0];
    const layer=values?'Database':(e.layer??e.Layer??'Unclassified');
    const sourceFile=e.sourceFile??e.SourceFile??'';
    const logMatch=raw.match(/ - ([^-]+?)\\s+- /);
    const methodMatch=raw.match(/ - [^-]+ - ([A-Za-z_][\\w.]*(?:\\([^)]*\\))?)/);
    return {t:(millis(timestamp)-millis(base))/1000,Timestamp:timestamp,raw,values,LocationId:explicit||null,Layer:layer,
      system:e.System||sourceFile.split(/[\\/]/).at(-1).split('_')[0],sourceFile,logLevel:e.logLevel??logMatch?.[1]?.trim()??null,method:e.method??methodMatch?.[1]??null,
      line:e.line??e.Line,index};
  }).sort((a,b)=>a.t-b.t||a.index-b.index);
}
export function stateAt(observations,time) {
  const values={}, provenance={};
  for(const e of observations){if(Math.round(e.t*1000)>Math.round(time*1000))break;if(e.values)for(const [k,v] of Object.entries(e.values)){values[k]=v;provenance[k]={timestamp:e.Timestamp,file:e.sourceFile,line:e.line};}}
  return {values,provenance};
}
export function groupsAt(observations,time,location) {
  const past=observations.filter(e=>Math.round(e.t*1000)<=Math.round(time*1000)&&e.LocationId===location);
  const anchor=past.at(-1)?.t;
  const groups=new Map();
  for(const e of past.filter(e=>e.t===anchor)) {
    if(!groups.has(e.Layer))groups.set(e.Layer,[]);
    const entries=groups.get(e.Layer);
    const existing=entries.find(x=>x.event.raw===e.raw&&x.event.system===e.system);
    if(existing)existing.evidence.push(e);else entries.push({event:e,evidence:[e]});
  }
  return [...groups].map(([layer,entries])=>({layer,entries}));
}
export function explain(e) {
  if(e.values)return 'The parcel record was saved. '+(meanings[e.values.Status]||'The recorded status is '+(e.values.Status||'not supplied')+'.');
  if(e.Layer==='Other'||e.Layer==='Unclassified'){
    const origin=[e.system,e.logLevel,e.method].filter(Boolean).join(' · ');
    return `This is an unmapped source event${origin?` from ${origin}`:''}. “Other” is our middleware fallback for a log source that has not yet been assigned to a known layer; it is not a Fiege protocol layer. The original file, category, method and raw evidence remain attached.`;
  }
  const raw=e.raw||'';
  if(/OnScannerData|Scanner Daten/.test(raw))return 'The scanner observation was received and forwarded to the material-flow logic.';
  if(/SendTaskToPlc|Fahrziel/.test(raw))return 'The material-flow logic issued a destination instruction to the conveyor controller.';
  if(/Ausschleuse Quittung|OnPlcAcknowledge/.test(raw))return 'The conveyor controller reported the actual routing outcome. This is separate from issuing the instruction.';
  if(/OnPlcCloseAcknowledge/.test(raw))return 'The controller reported the parcel’s outbound completion.';
  if(e.Layer==='Transport')return 'The communication connection recorded a telegram exchange. A send alone does not confirm physical movement.';
  if(e.Layer==='LVS')return 'The warehouse-system process recorded a message exchange for this parcel.';
  return 'A '+e.Layer+' observation was recorded. Its detailed meaning has not yet been mapped; the original evidence is available.';
}
export function findings(observations){
  const result=[];
  for(const e of observations){
    if(!Number.isFinite(e.t))result.push({severity:'data',message:'An event timestamp cannot be parsed.',event:e});
    if(e.values?.Status==='WEIGHTERR')result.push({severity:'warning',message:meanings.WEIGHTERR,event:e});
    if(e.values?.Status==='FAULTY')result.push({severity:'warning',message:meanings.FAULTY,event:e});
  }
  return result;
}
