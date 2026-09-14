import { build } from 'esbuild';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('../../', import.meta.url));
const output = path.join(root, 'ParcelJourney.App/Viewer3d');
await mkdir(output, { recursive: true });
await build({
  entryPoints: [path.join(root, 'tools/viewer3d/viewer.js')],
  outfile: path.join(output, 'viewer.js'), bundle: true, minify: true,
  format: 'iife', target: 'es2022', legalComments: 'eof'
});
// Restore the offline artifacts from the committed viewer, with no customer source tree.
const html = await readFile(path.join(root, 'analysis/equipment-replay.html'), 'utf8');
const match = html.match(/const M=(\{[^\r\n]+\});/);
if (!match) throw new Error('Bundled replay model not found');
const model = JSON.parse(match[1]);
if(model.validation?.mode !== 'real-logs' || model.parcels.length !== 10) throw new Error('Release requires the 10 audited real Fiege parcels. Run import_fiege_logs.py and build_fiege_replay.py.');
await writeFile(path.join(root, 'analysis/fiege-replay-model.json'), JSON.stringify(model));
await writeFile(path.join(root, 'analysis/fiege-replay-validation.json'), JSON.stringify(model.validation, null, 2));
await writeFile(path.join(output, 'THREE-LICENSE.txt'), await readFile(new URL('./node_modules/three/LICENSE', import.meta.url), 'utf8'));
console.log(`Bundled offline 3D viewer; ${model.parcels.length} scenarios.`);
