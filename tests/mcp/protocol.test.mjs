import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { handleRequest, pipeForPid, tools } from '../../tools/mcp/protocol.mjs';

const rpc = (method, params = {}) => ({ jsonrpc: '2.0', id: 0, method, params });
const modernMeta = {
  'io.modelcontextprotocol/protocolVersion': '2026-07-28',
  'io.modelcontextprotocol/clientInfo': { name: 'offline-test', version: '1.0.0' },
  'io.modelcontextprotocol/clientCapabilities': {}
};
const modernRpc = (method, params = {}) => rpc(method, { ...params, _meta: modernMeta });

test('initialize and tool discovery work without contacting an ArcGIS host', async () => {
  const forbidden = () => { throw new Error('Host must not be contacted.'); };
  const init = await handleRequest(rpc('initialize', { protocolVersion: '2025-11-25' }), forbidden);
  assert.equal(init.id, 0);
  assert.equal(init.result.protocolVersion, '2025-11-25');
  assert.equal((await handleRequest(rpc('tools/list'), forbidden)).result.tools.length, 8);
  assert.equal(await handleRequest({ jsonrpc: '2.0', method: 'notifications/initialized' }, forbidden), null);
});

test('modern discovery advertises only the implemented stateless version', async () => {
  const discovered = await handleRequest(modernRpc('server/discover'), () => {
    throw new Error('Discovery must not contact the host.');
  });
  assert.deepEqual(discovered.result.supportedVersions, ['2026-07-28']);
  assert.equal(discovered.result.resultType, 'complete');
  assert.deepEqual(discovered.result.capabilities, { tools: {} });
  assert.deepEqual(discovered.result._meta['io.modelcontextprotocol/serverInfo'], {
    name: 'rhino-inside-arcgis', version: '1.3.0'
  });
});

test('modern requests validate per-request metadata and version without prior initialization', async () => {
  let calls = 0;
  const reply = await handleRequest(modernRpc('tools/call', { name: 'rhino_status', arguments: {} }), async () => {
    calls++;
    return { ok: true, result: { started: false } };
  });
  assert.equal(reply.result.resultType, 'complete');
  assert.deepEqual(reply.result.structuredContent, { started: false });
  assert.deepEqual(reply.result._meta['io.modelcontextprotocol/serverInfo'], {
    name: 'rhino-inside-arcgis', version: '1.3.0'
  });
  assert.equal(calls, 1);

  const missingCapabilities = await handleRequest(rpc('tools/list', { _meta: {
    'io.modelcontextprotocol/protocolVersion': '2026-07-28'
  } }), async () => { throw new Error('No host invocation expected.'); });
  assert.equal(missingCapabilities.error.code, -32602);

  const unsupported = await handleRequest(rpc('tools/list', { _meta: {
    ...modernMeta, 'io.modelcontextprotocol/protocolVersion': '2027-01-01'
  } }), async () => { throw new Error('No host invocation expected.'); });
  assert.equal(unsupported.error.code, -32022);
  assert.deepEqual(unsupported.error.data, { supported: ['2026-07-28'], requested: '2027-01-01' });
});
test('mutation calls refuse missing source identity, enum errors and unexpected script arguments before transport', async () => {
  let calls = 0;
  const invoke = () => { calls++; return { ok: true, result: {} }; };
  for (const args of [{ arcgisLayer: 'A', rhinoLayer: 'R' }, { arcgisLayer: 'A', rhinoLayer: 'R', expectedSource: 'gdb/a', conflicts: 'arbitrary' }, { arcgisLayer: 'A', rhinoLayer: 'R', expectedSource: 'gdb/a', script: '_Delete' }]) {
    assert.equal((await handleRequest(rpc('tools/call', { name: 'rhino_apply', arguments: args }), invoke)).error.code, -32602);
  }
  assert.equal(calls, 0);
});
test('host rejections and ambiguous transport failures return MCP tool errors', async () => {
  const req = rpc('tools/call', { name: 'rhino_status' });
  assert.equal((await handleRequest(req, async () => ({ ok: false, error: 'Source changed' }))).result.isError, true);
  const error = await handleRequest(req, async () => { throw new Error('Inspect state before retrying'); });
  assert.match(error.result.content[0].text, /Inspect state/);
});
test('successful structured reports and arguments survive the protocol boundary', async () => {
  const args = { arcgisLayer: 'Buildings', rhinoLayer: 'GIS::Buildings', expectedSource: 'gdb/Buildings', conflicts: 'Manual' };
  const reply = await handleRequest(rpc('tools/call', { name: 'rhino_apply', arguments: args }), async request => {
    assert.deepEqual(request.arguments, args);
    return { ok: true, result: { held: 2, warnings: 1 } };
  });
  assert.deepEqual(reply.result.structuredContent, { held: 2, warnings: 1 });
});
test('PID routing is explicit and rejects invalid pipe selectors', () => {
  for (const pid of [undefined, '', '0', '-1', '1\\other', 'abc']) assert.throws(() => pipeForPid(pid));
  assert.equal(pipeForPid('1234'), '\\\\.\\pipe\\RhinoInside.ArcGIS.Mcp.v1.1234');
  assert.equal(tools.filter(t => t.annotations.readOnlyHint).length, 4);
});

test('stdio framing routes to a synthetic named pipe and reports denial and disconnect', {
  skip: process.platform !== 'win32', timeout: 15000
}, async t => {
  // This pipe name uses a synthetic PID and is owned only by this test process. It never probes
  // for, selects, or communicates with an ArcGIS Pro process.
  const pid = '99999999';
  const pipe = pipeForPid(pid);
  const requests = [];
  const fakeHost = createServer(socket => {
    let text = '';
    socket.on('data', chunk => {
      text += chunk.toString('utf8');
      const newline = text.indexOf('\n');
      if (newline < 0) return;
      const request = JSON.parse(text.slice(0, newline));
      requests.push(request);
      if (request.operation === 'rhino_links') return socket.end();
      const reply = request.operation === 'rhino_pull'
        ? { ok: false, error: 'Local review denied the operation.' }
        : { ok: true, result: { pid: Number(pid), operation: request.operation } };
      socket.end(JSON.stringify(reply) + '\n');
    });
  });
  fakeHost.listen(pipe);
  await once(fakeHost, 'listening');

  const serverPath = fileURLToPath(new URL('../../tools/mcp/server.mjs', import.meta.url));
  const child = spawn(process.execPath, [serverPath], {
    env: { ...process.env, RHINOINSIDE_MCP_HOST_PID: pid },
    stdio: ['pipe', 'pipe', 'pipe']
  });
  t.after(async () => {
    child.stdin.destroy();
    child.kill();
    await new Promise(resolve => fakeHost.close(resolve));
  });

  let output = '';
  const queued = [];
  const waiters = [];
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', chunk => {
    output += chunk;
    let newline;
    while ((newline = output.indexOf('\n')) >= 0) {
      const line = output.slice(0, newline);
      output = output.slice(newline + 1);
      const waiter = waiters.shift();
      if (waiter) waiter(JSON.parse(line));
      else queued.push(JSON.parse(line));
    }
  });
  const nextResponse = () => queued.length
    ? Promise.resolve(queued.shift())
    : new Promise(resolve => waiters.push(resolve));
  const send = async request => {
    child.stdin.write(JSON.stringify(request) + '\n');
    return nextResponse();
  };

  const gated = await send(rpc('tools/list'));
  assert.equal(gated.error.code, -32000);
  const initialized = await send(rpc('initialize', { protocolVersion: '2025-11-25' }));
  assert.equal(initialized.result.protocolVersion, '2025-11-25');
  assert.equal((await send(rpc('tools/list'))).error.code, -32000);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method: 'notifications/initialized' }) + '\n');
  const legacyTools = await send(rpc('tools/list'));
  assert.equal(legacyTools.result.tools.length, 8);

  const discovered = await send(modernRpc('server/discover'));
  assert.deepEqual(discovered.result.supportedVersions, ['2026-07-28']);
  const status = await send(modernRpc('tools/call', { name: 'rhino_status', arguments: {} }));
  assert.equal(status.result.structuredContent.operation, 'rhino_status');
  assert.deepEqual(requests[0], { operation: 'rhino_status', arguments: {} });

  const denied = await send(modernRpc('tools/call', {
    name: 'rhino_pull', arguments: { arcgisLayer: 'A', rhinoLayer: 'R', expectedSource: 'synthetic.gdb/A' }
  }));
  assert.equal(denied.result.isError, true);
  assert.match(denied.result.content[0].text, /denied/);

  const disconnected = await send(modernRpc('tools/call', { name: 'rhino_links', arguments: {} }));
  assert.equal(disconnected.result.isError, true);
  assert.match(disconnected.result.content[0].text, /response|retry/i);
});
