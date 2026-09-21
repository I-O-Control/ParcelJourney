"""Lossless column/dictionary encoding; unknown fields survive unchanged."""
import gzip
import json
from pathlib import Path

def encode(parcel):
    key = next(k for k in parcel if k.lower() == 'events')
    events = parcel[key]
    columns = list(dict.fromkeys(k for e in events for k in e))
    dictionaries = [[] for _ in columns]
    indexes = [{} for _ in columns]
    rows = []
    for event in events:
        row = []
        for i, name in enumerate(columns):
            if name not in event:
                row.append(-1)
                continue
            value = event[name]
            token = json.dumps(value, ensure_ascii=False, separators=(',', ':'))
            if token not in indexes[i]:
                indexes[i][token] = len(dictionaries[i])
                dictionaries[i].append(value)
            row.append(indexes[i][token])
        rows.append(row)
    return {'version': 2, 'metadata': {k: v for k, v in parcel.items() if k != key},
            'eventKey': key, 'columns': columns, 'dict': dictionaries, 'rows': rows}

def decode(feed):
    if feed['version'] != 2:
        raise ValueError('Unsupported compact version')
    result = dict(feed['metadata'])
    result[feed['eventKey']] = []
    for row in feed['rows']:
        if len(row) != len(feed['columns']):
            raise ValueError('Invalid row width')
        event = {}
        for i, ref in enumerate(row):
            if not isinstance(ref, int) or ref < -1 or ref >= len(feed['dict'][i]):
                raise ValueError('Invalid dictionary reference')
            if ref != -1:
                event[feed['columns'][i]] = feed['dict'][i][ref]
        result[feed['eventKey']].append(event)
    return result

def main(src, out):
    out = Path(out)
    parcel_dir = out.parent / 'compact-parcels'
    parcel_dir.mkdir(exist_ok=True)
    feeds, report = [], []
    for path in sorted(Path(src).glob('*.json')):
        original = json.loads(path.read_text(encoding='utf-8-sig'))
        feed = encode(original)
        assert decode(feed) == original, path.name
        content = json.dumps(feed, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
        packed = gzip.compress(content, mtime=0)
        (parcel_dir / (path.stem + '.pj.json.gz')).write_bytes(packed)
        feeds.append(feed)
        report.append({'parcel': path.stem, 'events': len(feed['rows']), 'sourceBytes': path.stat().st_size,
                       'compactGzipBytes': len(packed), 'allFieldsEqual': True})
    out.write_text(json.dumps({'version': 2, 'parcels': feeds}, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
    validation = {'scope': 'Exact normalized JSON values including raw evidence; not original JSON whitespace',
                  'parcels': report, 'sourceBytes': sum(p['sourceBytes'] for p in report),
                  'compactGzipBytes': sum(p['compactGzipBytes'] for p in report)}
    out.with_suffix('.validation.json').write_text(json.dumps(validation, indent=2), encoding='utf-8')
    print(json.dumps(validation, indent=2))
