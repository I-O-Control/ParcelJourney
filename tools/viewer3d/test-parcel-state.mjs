import assert from 'node:assert/strict';
import {prepareObservations,stateAt,groupsAt,findings,millis} from './parcel-state.mjs';
const start='2026.09.11 13:00:00.000';
const raw='SaveToDB UPDATE (tudata) ParcelID=00AB-9, TrackingId=XYZ-00, GrossWeight=0, Status=FUTURE_MODE, LastScanPos=SC_NEW, CustomField=preserved, ';
const events=[{timestamp:start,raw,layer:'Logic',sourceFile:'ProcLogic_Process.log',line:1},
  {timestamp:start,raw,layer:'Logic',sourceFile:'ProcLogic_Process.log',line:2},
  {timestamp:start,raw:'OnPlcAcknowledge SC_NEW',layer:'PLC',sourceFile:'PLC.log',line:3},
  {timestamp:'2026.09.11 13:00:00.001',raw:raw.replace('GrossWeight=0','GrossWeight=123'),layer:'Logic',line:4}];
const observations=prepareObservations(events,start);
assert.equal(stateAt(observations,-.001).values.GrossWeight,undefined);
assert.equal(stateAt(observations,0).values.GrossWeight,0);
assert.equal(stateAt(observations,.001).values.GrossWeight,123);
assert.equal(stateAt(observations,0).values.ParcelID,'00AB-9');
assert.equal(stateAt(observations,0).values.CustomField,'preserved');
assert.equal(findings(observations).length,0,'An unfamiliar status is not a fault');
const groups=groupsAt(observations,0,'SC_NEW');
assert.equal(groups.length,2);assert.equal(groups[0].entries[0].evidence.length,2);
assert.equal(millis('2026.09.12 00:00:00.000')-millis('2026.09.11 23:59:59.999'),1);
console.log('PASS: no future-state leakage; unknown fields/statuses; zero measurements; alphanumeric IDs; exact-time layer/duplicate grouping; midnight.');
