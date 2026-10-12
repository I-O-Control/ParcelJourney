"""Audit PJ01 scanner semantics and replay timing without changing source data."""
import argparse, collections, datetime, hashlib, io, json, re, urllib.request, zlib
from pathlib import Path

def var(stream):
    value = 0
    for shift in range(0, 63, 7):
        byte = stream.read(1)
        if not byte: raise ValueError('Truncated varint')
        value |= (byte[0] & 127) << shift
        if byte[0] < 128: return value
    raise ValueError('Varint overflow')

def decode(path, values):
    data = path.read_bytes()
    assert data[:4] == b'PJ01' and data[4] in (0, 1)
    assert hashlib.sha256(data[37:]).digest() == data[5:37], 'Payload checksum mismatch'
    stream = io.BytesIO(zlib.decompress(data[37:]) if data[4] else data[37:])
    header = json.loads(stream.read(var(stream)))
    events, timestamp = [], 0
    for _ in range(var(stream)):
        timestamp += var(stream)
        event = {'t': timestamp}
        for _ in range(var(stream)):
            key = values[var(stream)]
            tag = stream.read(1)[0]
            value = var(stream)
            assert tag in (0, 1, 2)
            event[key] = value if tag == 0 else values[value] if tag == 1 else stream.read(value).decode('utf8')
        events.append(event)
    assert not stream.read(), 'Trailing bytes'
    header['events'] = events
    return header

def audit(root, model):
    index = json.loads((root / 'index.json').read_text(encoding='utf8'))
    dictionary = json.loads((root / index['dictionary']).read_text(encoding='utf8'))
    assert dictionary['id'] == index['dictionary_id']
    # Preserve the exact JSON spelling used by the parser's dictionary writer.
    dictionary_text = (root / index['dictionary']).read_text(encoding='utf8')
    values_text = re.search(r'"values"\s*:\s*(\[.*\])\s*}', dictionary_text, re.S)[1]
    assert hashlib.sha256(values_text.encode('utf8')).hexdigest() == dictionary['id']
    parcels = [decode(root / entry['file'], dictionary['values']) for entry in index['entries']]
    source_scans, mismatches, timestamp_mismatches, identities = 0, [], [], []
    for parcel in parcels:
        for event in parcel['events']:
            raw = event.get('raw', '')
            if raw:
                stamp = datetime.datetime.strptime(raw[:23], '%Y.%m.%d %H:%M:%S.%f')
                wall = int(stamp.replace(tzinfo=datetime.timezone.utc).timestamp() * 1000)
                if event.get('time_basis') == 'local-wall-clock' and abs(wall-event['t']) > 1:
                    timestamp_mismatches.append({'parcel': parcel['parcel'], 't': event['t']})
            if event.get('kind') != 'scan': continue
            source_scans += 1
            match = re.search(r"OnScannerData: received Data: '([^|]+)\|([^|]*)\|([^']*)'", raw)
            if not match or match[1] != event.get('position') or match[2] != event.get('point') or match[3] != event.get('payload'):
                mismatches.append({'parcel': parcel['parcel'], 'source': event.get('source'), 'offset': event.get('offset')})
            if match and match[3].startswith('SORTER'):
                pair = re.search(r'ZP\s+(\d{10})(\d{10})', match[3])
                if pair and pair[2] != parcel['parcel']: identities.append({'parcel': parcel['parcel'], 'barcodeParcel': pair[2]})
    nodes = {n['id']: n for n in model['nodes']}
    transitions = collections.Counter()
    short, compressed, source_compare = [], [], []
    by_visit = {p['visitKey']: p for p in model['parcels']}
    for entry, parcel in zip(index['entries'], parcels):
        replay = by_visit.get(Path(entry['file']).stem)
        if not replay: continue
        raw_scans = [(e['t'], e.get('point')) for e in parcel['events'] if e.get('kind') == 'scan']
        replay_scans = [(e['CaptureFields']['t'], e['CaptureFields'].get('point')) for e in replay['events'] if e['CaptureFields'].get('kind') == 'scan']
        if raw_scans != replay_scans: source_compare.append(parcel['parcel'])
        for leg in replay['legs']:
            transitions[(leg['a'], leg['b'], leg.get('routeStatus'))] += 1
            stop = next(s for s in reversed(replay['stops']) if s['id'] == leg['a'] and s['arrival'] <= leg['start'])
            scan_interval = leg['end'] - stop['arrival']
            animation_interval = leg['end'] - leg['start']
            detail = {'parcel': replay['id'], 'a': leg['a'], 'b': leg['b'], 'scanIntervalSeconds': round(scan_interval, 3), 'animationSeconds': round(animation_interval, 3)}
            if animation_interval < .1: short.append(detail)
            if scan_interval > 1 and animation_interval < .1: compressed.append(detail)
    return {'dataset': str(root), 'dictionaryHashVerified': True, 'parcels': len(parcels), 'events': sum(len(p['events']) for p in parcels), 'scannerReads': source_scans,
        'rawScannerFieldMismatches': mismatches, 'rawWallTimestampMismatches': timestamp_mismatches, 'explicitBarcodeIdentityConflicts': identities,
        'replayScannerSequenceMismatches': source_compare, 'replayParcelsCompared': len(by_visit), 'legs': sum(transitions.values()),
        'unknownLegs': sum(count for (_, _, status), count in transitions.items() if status == 'unknown'),
        'sub100msLegs': len(short), 'compressedTravelLegs': compressed, 'shortLegExamples': short[:8],
        'transitions': [{'a': a, 'b': b, 'status': status, 'count': count} for (a, b, status), count in transitions.most_common()],
        'scope': 'Checks decoded scanner fields, explicit barcode ownership, wall timestamps and replay scan order. Does not prove every alias/callback association or surveyed physical geometry.'}

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('dataset', type=Path)
    parser.add_argument('--url', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    model = json.load(urllib.request.urlopen(args.url.rstrip('/') + '/3d/model.json'))
    report = audit(args.dataset, model)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf8')
    print(json.dumps({k: v for k, v in report.items() if k not in ('transitions', 'shortLegExamples', 'compressedTravelLegs')}, indent=2))
    print('Compressed travel legs:', len(report['compressedTravelLegs']))
