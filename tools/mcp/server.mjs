import { handleRequest, invokePipe } from './protocol.mjs';

// Stdout is reserved for newline-delimited JSON-RPC. No dependency installation is needed.
process.stdin.setEncoding('utf8');
let pending = '';
let queue = Promise.resolve();
let legacyInitialized = false;
let legacyInitializeSeen = false;
function send(value) { if (value) process.stdout.write(JSON.stringify(value) + '\n'); }

function isModernRequest(request) {
  const metadata = request?.params?._meta;
  return request?.method === 'server/discover' || (metadata && typeof metadata === 'object' &&
    ['io.modelcontextprotocol/protocolVersion', 'io.modelcontextprotocol/clientInfo',
      'io.modelcontextprotocol/clientCapabilities'].some(key => Object.hasOwn(metadata, key)));
}

process.stdin.on('data', chunk => {
  pending += chunk;
  if (pending.length > 1024 * 1024) { process.stderr.write('Input exceeded the limit.\n'); process.exitCode = 1; process.stdin.destroy(); return; }
  let end;
  while ((end = pending.indexOf('\n')) >= 0) {
    const line = pending.slice(0, end); pending = pending.slice(end + 1);
    if (!line.trim()) continue;
    queue = queue.then(async () => {
      let request;
      try { request = JSON.parse(line); } catch { return send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error.' } }); }
      if (legacyInitializeSeen && request?.jsonrpc === '2.0' &&
          request?.method === 'notifications/initialized' && !Object.hasOwn(request ?? {}, 'id'))
        legacyInitialized = true;
      if (Object.hasOwn(request ?? {}, 'id') && !isModernRequest(request) &&
          !legacyInitialized && !['initialize', 'ping'].includes(request.method)) {
        return send({ jsonrpc: '2.0', id: request.id, error: { code: -32000, message: 'Initialize the MCP session first.' } });
      }
      const response = await handleRequest(request, r => invokePipe(process.env.RHINOINSIDE_MCP_HOST_PID, r));
      if (request?.method === 'initialize' && response?.result?.protocolVersion)
        legacyInitializeSeen = true;
      send(response);
    }).catch(error => process.stderr.write(error.message + '\n'));
  }
});
process.stdin.on('end', () => { if (pending.trim()) send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Incomplete JSON-RPC line.' } }); });
