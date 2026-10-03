import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const HASH = /^[a-f0-9]{64}$/i;
const COMMIT = /^[a-f0-9]{40,64}$/i;
const OWNER = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/;
const REPOSITORY = /^[A-Za-z0-9._-]{1,100}$/;
const UNSANITIZED = /(?:[A-Za-z]:\\|\/(?:Users|home)\/[^\s]*|(?:gh[pousr]_|github_pat_)[A-Za-z0-9_]{12,}|Bearer\s+[A-Za-z0-9._-]{12,}|-----BEGIN (?:RSA |OPENSSH )?PRIVATE KEY-----)/i;

export function validateRepository(value) {
  if (typeof value !== 'string') throw new Error('Repository must be OWNER/NAME.');
  const parts = value.split('/');
  if (parts.length !== 2 || !OWNER.test(parts[0]) || !REPOSITORY.test(parts[1]) || parts[1] === '.' || parts[1] === '..' || parts[1].endsWith('.git')) {
    throw new Error('Repository must be a valid GitHub OWNER/NAME path.');
  }
  return value;
}

export function resolveSiteRepository(metadataRepository, configuredRepository) {
  const metadataTarget = metadataRepository ? validateRepository(metadataRepository) : undefined;
  const configuredTarget = configuredRepository ? validateRepository(configuredRepository) : undefined;
  if (metadataTarget && configuredTarget && metadataTarget !== configuredTarget) {
    throw new Error('Site repository metadata does not match SITE_REPOSITORY.');
  }
  if (!configuredTarget && !metadataTarget) throw new Error('Configure a GitHub repository in site/release.json or SITE_REPOSITORY.');
  return configuredTarget ?? metadataTarget;
}

function requireText(assets, name, predicate, label) {
  const raw = assets.get(name);
  const value = Buffer.isBuffer(raw) ? raw.toString('utf8') : raw;
  if (typeof value !== 'string' || !value.trim() || !predicate(value)) throw new Error(`Missing or invalid ${label} asset: ${name}`);
  return value;
}
function bytesOf(value) { return Buffer.isBuffer(value) ? value : Buffer.from(value); }

function requireChecksum(sums, filename, digest, label) {
  const lines = sums.split(/\r?\n/).filter(line => line.trim());
  const records = lines.map(line => {
    const match = line.match(/^([a-f0-9]{64})\s+\*?([^\s]+)$/i);
    if (!match) throw new Error(`SHA256SUMS.txt contains a malformed ${label} line.`);
    return { digest: match[1].toLowerCase(), filename: match[2] };
  });
  const matches = records.filter(item => item.filename === filename);
  if (matches.length !== 1 || matches[0].digest !== digest.toLowerCase()) {
    throw new Error(`SHA256SUMS.txt must contain exactly one matching ${label} checksum.`);
  }
}
function uniqueField(text, expression) {
  const matches = [...text.matchAll(expression)];
  return matches.length === 1 ? matches[0][1] : undefined;
}

export function validatePublishedRelease({ metadata, repository, release, assets, tagCommit }) {
  const target = validateRepository(repository);
  if (!metadata || !/^[0-9]+\.[0-9]+\.[0-9]+$/.test(metadata.version ?? '') || metadata.status !== 'published') {
    throw new Error('Only a versioned published site release can be verified.');
  }
  if (metadata.repository !== target) throw new Error('Site repository metadata does not match the configured public repository.');
  if (metadata.sourceCommit !== tagCommit) throw new Error('Site metadata and version tag source commits do not match.');
  if (!COMMIT.test(metadata.sourceCommit ?? '')) throw new Error('Published release requires a full source commit.');
  const expectedAsset = `RhinoInside.ArcGISPro-v${metadata.version}.esriAddinX`;
  if (metadata.asset !== expectedAsset || !HASH.test(metadata.sha256 ?? '')) throw new Error('Invalid published installer metadata.');
  if (!release || release.draft || release.prerelease || release.tag_name !== `v${metadata.version}`) throw new Error('The matching GitHub release must be published and stable.');
  if (COMMIT.test(release.target_commitish ?? '') && release.target_commitish !== metadata.sourceCommit) {
    throw new Error('Release target commit does not match configured source metadata.');
  }
  if (!Array.isArray(release.assets)) throw new Error('Release asset inventory is missing.');
  const uploaded = new Set(release.assets.map(asset => asset.name));
  for (const name of [expectedAsset, 'SHA256SUMS.txt', 'release-manifest.json', 'LICENSE', 'ACKNOWLEDGEMENTS.md', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md']) {
    if (!uploaded.has(name)) throw new Error(`Missing release asset: ${name}`);
  }
  const installer = assets.get(expectedAsset);
  if (installer === undefined) throw new Error('Installer bytes were not downloaded.');
  const hash = createHash('sha256').update(bytesOf(installer)).digest('hex');
  if (hash !== metadata.sha256.toLowerCase()) throw new Error('Installer SHA-256 does not match site/release.json.');

  const sums = requireText(assets, 'SHA256SUMS.txt', text => /\S/.test(text), 'checksum');
  requireChecksum(sums, expectedAsset, hash, 'installer');

  let manifest;
  try { manifest = JSON.parse(requireText(assets, 'release-manifest.json', text => text.length > 0, 'manifest')); }
  catch (error) { throw new Error(`Invalid release manifest: ${error.message}`); }
  if (manifest.status !== 'published' || manifest.version !== metadata.version || manifest.installer !== expectedAsset || manifest.sha256?.toLowerCase() !== hash || manifest.sourceCommit !== metadata.sourceCommit || manifest.sourceHasUncommittedChanges !== false) {
    throw new Error('Release manifest version, source revision, clean-source status or installer hash does not match.');
  }
  const versionParts = metadata.version.split('.').map(Number);
  const requiresMcpGateway = versionParts[0] > 1 || (versionParts[0] === 1 && versionParts[1] >= 3);
  const hasMcpGateway = manifest.mcpGatewayAsset !== undefined || manifest.mcpGatewaySha256 !== undefined;
  if (requiresMcpGateway && !hasMcpGateway) throw new Error('This release requires a versioned MCP gateway asset and SHA-256 in its manifest.');
  const gatewayName = `RhinoInside-Mcp-v${metadata.version}.zip`;
  let gatewayHash;
  if (hasMcpGateway) {
    if (manifest.mcpGatewayAsset !== gatewayName || !HASH.test(manifest.mcpGatewaySha256 ?? '') || !uploaded.has(gatewayName)) {
      throw new Error('MCP gateway asset metadata is incomplete or does not match its version.');
    }
    const gateway = assets.get(gatewayName);
    if (gateway === undefined) throw new Error('MCP gateway bytes were not downloaded.');
    gatewayHash = createHash('sha256').update(bytesOf(gateway)).digest('hex');
    if (gatewayHash !== manifest.mcpGatewaySha256.toLowerCase()) throw new Error('MCP gateway SHA-256 does not match the release manifest.');
    requireChecksum(sums, gatewayName, gatewayHash, 'MCP gateway');
  }

  requireText(assets, 'LICENSE', text => /^MIT License\s/m.test(text) && /THE SOFTWARE IS PROVIDED "AS IS"/i.test(text), 'license');
  requireText(assets, 'ACKNOWLEDGEMENTS.md', text => /^# Acknowledgements\s/m.test(text) && /ArcRhino/i.test(text), 'acknowledgements');
  requireText(assets, 'THIRD_PARTY_NOTICES.md', text => /^# Third-party notices\s/m.test(text) && /McNeel|Rhino\.Inside/i.test(text) && /Newtonsoft/i.test(text), 'third-party notices');

  const validation = requireText(assets, 'VALIDATION.md', text => /^# Validation record\s/m.test(text) && !UNSANITIZED.test(text), 'sanitized validation record');
  const escaped = value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const record = {
    version: uniqueField(validation, /^[-*]\s*Version:\s*`?([0-9]+\.[0-9]+\.[0-9]+)`?\s*$/gmi),
    sourceCommit: uniqueField(validation, /^[-*]\s*Source commit:\s*`?([a-f0-9]{40,64})`?\s*$/gmi),
    installer: uniqueField(validation, new RegExp(`^[-*]\\s*Installer:\\s*\\x60?(${escaped(expectedAsset)})\\x60?\\s*$`, 'gmi')),
    installerSha256: uniqueField(validation, /^[-*]\s*Installer SHA-256:\s*`?([a-f0-9]{64})`?\s*$/gmi),
    gateway: uniqueField(validation, new RegExp(`^[-*]\\s*MCP gateway:\\s*\\x60?(${escaped(gatewayName)})\\x60?\\s*$`, 'gmi')),
    gatewaySha256: uniqueField(validation, /^[-*]\s*MCP gateway SHA-256:\s*`?([a-f0-9]{64})`?\s*$/gmi),
    compile: uniqueField(validation, /^[-*]\s*Compile build:\s*(PASS)\s*$/gmi),
    core: uniqueField(validation, /^[-*]\s*Core tests:\s*(PASS)\s*$/gmi),
    hostRun1: uniqueField(validation, /^[-*]\s*Embedded host run 1:\s*(PASS) \(fresh host and clean project\)\s*$/gmi),
    hostRun2: uniqueField(validation, /^[-*]\s*Embedded host run 2:\s*(PASS) \(fresh host and clean project\)\s*$/gmi),
    mcp: uniqueField(validation, /^[-*]\s*MCP coexistence:\s*(PASS)\s*$/gmi),
    visual: uniqueField(validation, /^[-*]\s*Visual acceptance:\s*(PASS)\s*$/gmi),
    package: uniqueField(validation, /^[-*]\s*Package audit:\s*(PASS)\s*$/gmi),
    sanitized: uniqueField(validation, /^[-*]\s*Sanitized record:\s*(yes)\s*$/gmi)
  };
  const requiredRecordLines = hasMcpGateway ? 15 : 13;
  const nonemptyRecordLines = validation.split(/\r?\n/).map(line => line.trim()).filter(Boolean);
  if (nonemptyRecordLines.length !== requiredRecordLines || nonemptyRecordLines[0] !== '# Validation record' || record.version !== metadata.version || record.sourceCommit !== metadata.sourceCommit || record.installer !== expectedAsset || record.installerSha256?.toLowerCase() !== hash ||
      !record.compile || !record.core || !record.hostRun1 || !record.hostRun2 || !record.mcp || !record.visual || !record.package || record.sanitized !== 'yes' ||
      (hasMcpGateway && (record.gateway !== gatewayName || record.gatewaySha256?.toLowerCase() !== gatewayHash))) {
    throw new Error('VALIDATION.md must bind both artifacts to this source and record completed compile, core, two clean host, MCP coexistence, visual and package checks.');
  }
  return { hash, gatewayHash, sourceCommit: metadata.sourceCommit, repository: target };
}

async function resolveTagCommit(api, tag, headers) {
  let response = await fetch(`${api}/git/ref/tags/${encodeURIComponent(tag)}`, { headers });
  if (!response.ok) throw new Error(`Release tag lookup failed: ${response.status}`);
  let object = (await response.json()).object;
  for (let depth = 0; depth < 5; depth += 1) {
    if (object?.type === 'commit' && COMMIT.test(object.sha ?? '')) return object.sha;
    if (object?.type !== 'tag' || !COMMIT.test(object.sha ?? '')) throw new Error('Release tag does not resolve to a source commit.');
    response = await fetch(`${api}/git/tags/${object.sha}`, { headers });
    if (!response.ok) throw new Error(`Annotated tag lookup failed: ${response.status}`);
    object = (await response.json()).object;
  }
  throw new Error('Release tag has too many nested tag objects.');
}
async function fetchAsset(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`Public download failed (${response.status}): ${url}`);
  return Buffer.from(await response.arrayBuffer());
}

export async function main() {
  const metadataUrl = new URL('../site/release.json', import.meta.url);
  const metadata = JSON.parse(await readFile(metadataUrl, 'utf8'));
  if (!/^[0-9]+\.[0-9]+\.[0-9]+$/.test(metadata.version ?? '') || !['candidate', 'published'].includes(metadata.status)) {
    throw new Error('Invalid release version or status in site/release.json.');
  }
  const repository = resolveSiteRepository(metadata.repository, process.env.SITE_REPOSITORY);
  if (metadata.status === 'candidate') {
    console.log(`Candidate v${metadata.version}: Pages may deploy with an explicit pending-validation label; no release download is enabled.`);
    return;
  }
  if (metadata.repository !== repository) throw new Error('Published site metadata must pin the configured public repository.');
  const api = `https://api.github.com/repos/${repository}`;
  const headers = { Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' };
  if (process.env.GH_TOKEN) headers.Authorization = `Bearer ${process.env.GH_TOKEN}`;
  const response = await fetch(`${api}/releases/tags/v${metadata.version}`, { headers });
  if (!response.ok) throw new Error(`Release lookup failed: ${response.status}`);
  const release = await response.json();
  if (COMMIT.test(release.target_commitish ?? '') && release.target_commitish !== metadata.sourceCommit) {
    throw new Error('Release target commit does not match configured source metadata.');
  }
  const tagCommit = await resolveTagCommit(api, `v${metadata.version}`, headers);
  const gatewayName = `RhinoInside-Mcp-v${metadata.version}.zip`;
  const names = ['SHA256SUMS.txt', 'release-manifest.json', 'LICENSE', 'ACKNOWLEDGEMENTS.md', 'THIRD_PARTY_NOTICES.md', 'VALIDATION.md', metadata.asset, gatewayName];
  const assetByName = new Map((release.assets ?? []).map(item => [item.name, item]));
  const contents = new Map();
  for (const name of names) {
    const asset = assetByName.get(name);
    if (!asset) continue;
    contents.set(name, await fetchAsset(asset.browser_download_url));
  }
  const result = validatePublishedRelease({ metadata, repository, release, assets: contents, tagCommit });
  console.log(`Verified public release v${metadata.version}: installer ${result.hash}, MCP gateway ${result.gatewayHash}, source ${result.sourceCommit}`);
}

if (process.argv[1] && pathToFileURL(path.resolve(process.argv[1])).href === import.meta.url) await main();
