import { readFile, writeFile, mkdir, copyFile, readdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.dirname(fileURLToPath(import.meta.url));
const release = JSON.parse(await readFile(path.join(root, 'release.json'), 'utf8'));
const repository = process.env.SITE_REPOSITORY || process.env.GITHUB_REPOSITORY || release.repository || '';
const ownerPattern = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$/;
const namePattern = /^[A-Za-z0-9._-]{1,100}$/;
function checkRepository(value, required) {
  if (!value && !required) return '';
  const parts = value.split('/');
  if (parts.length !== 2 || !ownerPattern.test(parts[0]) || !namePattern.test(parts[1]) || parts[1] === '.' || parts[1] === '..' || parts[1].endsWith('.git')) {
    throw new Error('Repository must be a valid GitHub OWNER/NAME path.');
  }
  return value;
}
if (!/^\d+\.\d+\.\d+$/.test(release.version) || !['candidate', 'published'].includes(release.status) ||
    release.asset !== `RhinoInside.ArcGISPro-v${release.version}.esriAddinX`) {
  throw new Error('Invalid pinned release metadata');
}
const targetRepository = checkRepository(repository, release.status === 'published');
if (release.status === 'published' && (targetRepository !== release.repository || !/^[a-f0-9]{40,64}$/i.test(release.sourceCommit ?? '') || !/^[a-f0-9]{64}$/i.test(release.sha256 ?? ''))) {
  throw new Error('Published release metadata requires a matching repository, source commit and installer SHA-256.');
}
const repo = targetRepository ? `https://github.com/${targetRepository}` : '';
const tag = `v${release.version}`;
const releaseUrl = repo ? `${repo}/releases/tag/${tag}` : '';
const downloadUrl = repo ? `${repo}/releases/download/${tag}/${release.asset}` : '';
const gatewayAsset = `RhinoInside-Mcp-v${release.version}.zip`;
const gatewayDownloadUrl = repo ? `${repo}/releases/download/${tag}/${gatewayAsset}` : '';
const published = release.status === 'published';
const releasePanel = published
  ? `<div class="release-panel"><h2>Download ${tag}</h2><p>ArcGIS Pro 3.7 · Rhino 8 · Windows 11 x64</p><a class="button" href="${downloadUrl}">Download installer</a><p class="small"><a href="${releaseUrl}">Release notes</a> · <a href="${repo}/releases/download/${tag}/SHA256SUMS.txt">Checksums</a></p><p class="small">Optional MCP gateway for Node.js 22+: <a href="${gatewayDownloadUrl}">Download gateway ZIP</a>.</p><p class="small">Unsigned add-in. Your organization's add-in policy applies.</p><details><summary>Verify the download</summary><code class="hash">${release.sha256}</code></details></div>`
  : `<div class="release-panel"><h2>Version ${release.version} — candidate</h2><p>This is a release candidate. Runtime validation is pending; this page does not certify ArcGIS Pro and embedded Rhino compatibility.</p><p class="small">No stable installer or gateway download is available. See Releases for the experimental preview. Requires ArcGIS Pro 3.7, Rhino 8 and Windows 11 x64.</p></div>`;
const pages = [
  ['index.html', 'Overview', 'Rhino.Inside for ArcGIS Pro'],
  ['install.html', 'Install', 'Install Rhino.Inside-ArcGIS'],
  ['guide.html', 'User guide', 'User guide — Rhino.Inside-ArcGIS'],
  ['releases.html', 'Releases', 'Releases — Rhino.Inside-ArcGIS'],
  ['about.html', 'About', 'About — Rhino.Inside-ArcGIS']
];
await mkdir(path.join(root, 'dist'), { recursive: true });
const documents = new Map();
for (const [file, label, title] of pages) {
  let body = await readFile(path.join(root, 'pages', file), 'utf8');
  body = body.replaceAll('{{release-panel}}', releasePanel).replaceAll('{{version}}', release.version).replaceAll('{{repo-url}}', repo);
  const nav = pages.map(([url, text]) => `<a href="${url}"${url === file ? ' aria-current="page"' : ''}>${text}</a>`).join('');
  const github = repo ? `<a href="${repo}">GitHub</a>` : '';
  const html = `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>${title}</title><meta name="description" content="Rhino inside ArcGIS Pro, with layer synchronization and change tracking between the applications. Installation, user guide and versioned releases."><meta name="theme-color" content="#ffffff"><link rel="stylesheet" href="style.css"></head>
<body><a class="skip" href="#main">Skip to content</a><header><div class="header-inner"><a class="brand" href="index.html">Rhino.Inside for ArcGIS Pro</a><nav aria-label="Main navigation">${nav}${github}</nav></div></header><main id="main">${body}</main><footer><p>${repo ? `<a href="${repo}/blob/main/LICENSE">MIT license</a> · <a href="${repo}/blob/main/ACKNOWLEDGEMENTS.md">Acknowledgements</a>` : 'MIT license · Acknowledgements'}<br>Independent community project. Rhino and ArcGIS Pro require separate licenses.</p></footer></body></html>`;
  if (/{{.+?}}/.test(html)) throw new Error(`Unresolved template in ${file}`);
  documents.set(file, html);
  await writeFile(path.join(root, 'dist', file), html);
}
await copyFile(path.join(root, 'style.css'), path.join(root, 'dist', 'style.css'));
await writeFile(path.join(root, 'dist', '.nojekyll'), '');
await copyFile(path.join(root, 'preview.json'), path.join(root, 'dist', 'preview.json'));
// Stable availability stays separate from the explicitly experimental preview.
await writeFile(path.join(root, 'dist', 'install.json'), JSON.stringify({
  name: 'Rhino.Inside-ArcGIS', version: release.version, status: release.status,
  available: published, repository: repo, experimentalPreview: 'preview.json',
  requirements: { os: 'Windows 11 x64 build 22621+', arcgisPro: '3.7', rhino: '8', node: '22+ for optional MCP only', licensedApplicationsRequired: true },
  installScript: published ? `https://raw.githubusercontent.com/${targetRepository}/${tag}/tools/Install-RhinoInsideArcGIS.ps1` : null,
  installer: published ? { url: downloadUrl, sha256: release.sha256 } : null,
  mcpGateway: published ? gatewayDownloadUrl : null,
  sourceCommit: published ? release.sourceCommit : null,
  instructions: `${repo}/blob/main/docs/AGENT_INSTALL.md`,
  requiresProClosedForInstall: true, signed: false, writesRequireLocalReview: true
}, null, 2) + '\n');
await writeFile(path.join(root, 'dist', 'llms.txt'), `# Rhino.Inside-ArcGIS\n\nRhino 8 inside ArcGIS Pro 3.7 on Windows 11 x64.\n\n- Status: ${release.status} ${tag}\n- [Machine-readable install metadata](install.json)\n- [Installation for users and agents](install.html)\n- [Agent installation guide](${repo}/blob/main/docs/AGENT_INSTALL.md)\n- [Experimental preview metadata](preview.json)\n- [Experimental preview installation and known issues](${repo}/blob/main/docs/PREVIEW_INSTALL.md)\n- [MCP tools and local-review policy](${repo}/blob/main/docs/MCP_CONTROL.md)\n- [About](about.html)\n\nChoose a named stable release when available. The experimental preview requires explicit opt-in and has known shutdown issues. Verify hashes and close Pro before installation. Both applications need licenses. The add-in is unsigned. Node.js 22+ is required only for optional MCP. Read-only inspection is available; agents cannot approve their own GIS writes.\n`);
// A project-site deployment runs below the repository path. Relative URLs must resolve there.
for (const [file, html] of documents) {
  for (const match of html.matchAll(/(?:href|src)="([^"]+)"/g)) {
    const url = match[1];
    if (/^(https?:|mailto:)/.test(url)) continue;
    if (url.startsWith('/')) throw new Error(`Root-relative asset/link in ${file}: ${url}`);
    const [target, fragment] = url.split('#');
    const targetFile = target || file;
    if (!documents.has(targetFile) && !(await readdir(path.join(root, 'dist'))).includes(targetFile)) {
      throw new Error(`Missing local target in ${file}: ${url}`);
    }
    if (fragment && !documents.get(targetFile)?.includes(`id="${fragment}"`)) {
      throw new Error(`Missing anchor in ${file}: ${url}`);
    }
  }
}
console.log(`Built and checked ${pages.length} pages; installer ${tag} is ${release.status}${targetRepository ? ` for ${targetRepository}` : ''}.`);
