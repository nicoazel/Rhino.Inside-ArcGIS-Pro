import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { resolveSiteRepository, validatePublishedRelease } from '../tools/verify-site-release.mjs';

const version = '1.3.0';
const sourceCommit = 'a'.repeat(40);
const installerName = `RhinoInside.ArcGISPro-v${version}.esriAddinX`;
const gatewayName = `RhinoInside-Mcp-v${version}.zip`;
const installer = Buffer.from('safe synthetic installer bytes');
const gateway = Buffer.from('safe synthetic MCP gateway bytes');
const hash = createHash('sha256').update(installer).digest('hex');
const gatewayHash = createHash('sha256').update(gateway).digest('hex');
const names = [installerName, gatewayName, 'SHA256SUMS.txt', 'release-manifest.json', 'LICENSE', 'ACKNOWLEDGEMENTS.md', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md'];
const metadata = { version, status: 'published', repository: 'example/RhinoInside-ArcGIS', sourceCommit, asset: installerName, sha256: hash };
// GitHub's release object has tag_name and target_commitish, but no sourceCommit field.
const release = { draft: false, prerelease: false, tag_name: `v${version}`, target_commitish: 'main', assets: names.map(name => ({ name })) };
const validation = `# Validation record\n\n- Version: ${version}\n- Source commit: ${sourceCommit}\n- Installer: ${installerName}\n- Installer SHA-256: ${hash}\n- MCP gateway: ${gatewayName}\n- MCP gateway SHA-256: ${gatewayHash}\n- Compile build: PASS\n- Core tests: PASS\n- Embedded host run 1: PASS (fresh host and clean project)\n- Embedded host run 2: PASS (fresh host and clean project)\n- MCP coexistence: PASS\n- Visual acceptance: PASS\n- Package audit: PASS\n- Sanitized record: yes\n`;
const manifest = { version, status: 'published', sourceCommit, sourceHasUncommittedChanges: false, installer: installerName, sha256: hash, mcpGatewayAsset: gatewayName, mcpGatewaySha256: gatewayHash };
const baseAssets = () => new Map([
  [installerName, installer],
  [gatewayName, gateway],
  ['SHA256SUMS.txt', `${hash}  ${installerName}\n${gatewayHash}  ${gatewayName}\n`],
  ['release-manifest.json', JSON.stringify(manifest)],
  ['LICENSE', 'MIT License\nTHE SOFTWARE IS PROVIDED "AS IS"'],
  ['ACKNOWLEDGEMENTS.md', '# Acknowledgements\nArcRhino team'],
  ['THIRD_PARTY_NOTICES.md', '# Third-party notices\nRhino.Inside and Newtonsoft notices'],
  ['VALIDATION.md', validation]
]);

function verify(overrides = {}) {
  const assets = baseAssets();
  for (const [name, value] of Object.entries(overrides.assets ?? {})) {
    if (value === null) assets.delete(name); else assets.set(name, value);
  }
  return validatePublishedRelease({ metadata: { ...metadata, ...overrides.metadata }, repository: overrides.repository ?? metadata.repository, release: { ...release, ...overrides.release }, assets, tagCommit: overrides.tagCommit ?? sourceCommit });
}

test('accepts a complete published release bound to its actual tag, installer and gateway', () => {
  assert.equal('sourceCommit' in release, false);
  assert.deepEqual(verify(), { hash, gatewayHash, sourceCommit, repository: metadata.repository });
});

test('rejects installer byte tampering', () => {
  assert.throws(() => verify({ assets: { [installerName]: Buffer.from('tampered') } }), /SHA-256/);
});

test('rejects omitted, altered, duplicated or malformed checksum entries', () => {
  assert.throws(() => verify({ assets: { 'SHA256SUMS.txt': `${'0'.repeat(64)}  ${installerName}\n` } }), /checksum/);
  assert.throws(() => verify({ assets: { 'SHA256SUMS.txt': `${hash}  ${installerName}\n${'0'.repeat(64)}  ${installerName}\n${gatewayHash}  ${gatewayName}\n` } }), /checksum/);
  assert.throws(() => verify({ assets: { 'SHA256SUMS.txt': `${hash}  ${installerName}\nmalformed\n${gatewayHash}  ${gatewayName}\n` } }), /malformed/);
});

test('rejects gateway byte, manifest hash or checksum tampering', () => {
  assert.throws(() => verify({ assets: { [gatewayName]: Buffer.from('tampered') } }), /MCP gateway SHA-256/);
  assert.throws(() => verify({ assets: { 'release-manifest.json': JSON.stringify({ ...manifest, mcpGatewaySha256: '0'.repeat(64) }) } }), /MCP gateway SHA-256/);
  assert.throws(() => verify({ assets: { 'SHA256SUMS.txt': `${hash}  ${installerName}\n${'0'.repeat(64)}  ${gatewayName}\n` } }), /MCP gateway checksum/);
  assert.throws(() => verify({ assets: { 'release-manifest.json': JSON.stringify({ ...manifest, mcpGatewayAsset: undefined, mcpGatewaySha256: undefined }) } }), /requires a versioned MCP gateway/);
});

test('rejects manifest version, source or installer hash tampering', () => {
  for (const field of ['version', 'sourceCommit', 'sha256']) {
    const changed = { ...manifest, [field]: 'tampered' };
    assert.throws(() => verify({ assets: { 'release-manifest.json': JSON.stringify(changed) } }), /manifest/);
  }
});

test('rejects source or tag commit mismatch and invalid repository paths', () => {
  assert.throws(() => verify({ metadata: { sourceCommit: 'b'.repeat(40) } }), /version tag source commits/);
  assert.throws(() => verify({ tagCommit: 'b'.repeat(40) }), /version tag source commits/);
  assert.throws(() => verify({ repository: 'owner/repo/releases/tag/v1.3.0' }), /OWNER\/NAME/);
});

test('candidate and published repository configuration must agree', () => {
  assert.equal(resolveSiteRepository('nicoazel/Rhino.Inside-ArcGIS-Public', 'nicoazel/Rhino.Inside-ArcGIS-Public'), 'nicoazel/Rhino.Inside-ArcGIS-Public');
  assert.throws(() => resolveSiteRepository('nicoazel/Rhino.Inside-ArcGIS-Public', 'other/Public'), /does not match SITE_REPOSITORY/);
  assert.throws(() => resolveSiteRepository('owner/repo/releases', undefined), /OWNER\/NAME/);
});

test('requires all release notices and every passing sanitized validation field', () => {
  for (const name of ['LICENSE', 'ACKNOWLEDGEMENTS.md', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md']) {
    assert.throws(() => verify({ assets: { [name]: null } }), /Missing (?:release asset|or invalid)/);
  }
  for (const changed of [
    validation.replace('Compile build: PASS', 'Compile build: PENDING'),
    validation.replace('Embedded host run 2: PASS', 'Embedded host run 2: PENDING'),
    validation.replace('MCP coexistence: PASS', 'MCP coexistence: PENDING'),
    validation.replace(`MCP gateway SHA-256: ${gatewayHash}`, `MCP gateway SHA-256: ${'0'.repeat(64)}`),
    `${validation}\nLocal path: C:\\private\\run.log\n`,
    `${validation}\nUnrecorded diagnostic output\n`
  ]) assert.throws(() => verify({ assets: { 'VALIDATION.md': changed } }), /VALIDATION\.md|sanitized validation/);
});
