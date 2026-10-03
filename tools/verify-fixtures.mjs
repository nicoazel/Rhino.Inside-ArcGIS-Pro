import { readFile, readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';

const root = new URL('../tests/data/', import.meta.url);
const manifest = JSON.parse(await readFile(new URL('SYNTHETIC.json', root), 'utf8'));
const digest = value => createHash('sha256').update(value).digest('hex');
const names = ['Boundary_Multipart_Polygon', 'Multipatch_Single_Building', 'Point_Multi_Mixed',
  'Polygon_MixedMultiPart_Parcels', 'Polygons_Single_Buildings', 'Polyline_MultipartMix_Streets'];
const expected = names.flatMap(name => ['.cpg', '.dbf', '.prj', '.shp', '.shx'].map(extension => `SHP/${name}${extension}`)).sort();
if (manifest.schemaVersion !== 1 || manifest.license !== 'MIT' || manifest.pyshpVersion !== '2.3.1' ||
    !Array.isArray(manifest.files) || JSON.stringify(manifest.files.map(f => f.path).sort()) !== JSON.stringify(expected)) {
  throw new Error('Synthetic fixture manifest is incomplete or uses an unsupported generator dependency.');
}
const source = (await readFile(new URL('../tools/generate-fixtures.py', import.meta.url), 'utf8')).replaceAll('\r\n', '\n');
if (digest(source) !== manifest.generatorSha256) throw new Error('Fixture generator changed; regenerate the synthetic data and provenance.');
const files = (await readdir(new URL('SHP/', root))).map(file => `SHP/${file}`).sort();
if (JSON.stringify(files) !== JSON.stringify(expected)) throw new Error('Unexpected or missing GIS files; the public fixtures must contain only the six generated sets.');
for (const entry of manifest.files) {
  if (digest(await readFile(new URL(entry.path, root))) !== entry.sha256) throw new Error(`Synthetic fixture hash mismatch: ${entry.path}`);
}
console.log('Verified 30 synthetic fixture files against the generator and provenance manifest.');
