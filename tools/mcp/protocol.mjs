import { createConnection } from 'node:net';

const text = { type: 'string', minLength: 1, maxLength: 4096 };
const MODERN_VERSION = '2026-07-28';
const LEGACY_VERSIONS = ['2025-11-25', '2025-06-18', '2025-03-26', '2024-11-05'];
const SUPPORTED_MODERN_VERSIONS = [MODERN_VERSION];
const META = {
  protocolVersion: 'io.modelcontextprotocol/protocolVersion',
  clientInfo: 'io.modelcontextprotocol/clientInfo',
  clientCapabilities: 'io.modelcontextprotocol/clientCapabilities',
  serverInfo: 'io.modelcontextprotocol/serverInfo'
};
const SERVER_INFO = { name: 'rhino-inside-arcgis', version: '1.3.0' };
const link = { arcgisLayer: text, rhinoLayer: text, expectedSource: text };
const schema = (properties = {}, required = []) => ({ type: 'object', properties, required, additionalProperties: false });
const linked = Object.keys(link);
export const tools = [
  ['rhino_status', 'Inspect the embedded Rhino host and busy state.', schema(), true],
  ['rhino_links', 'List saved links including the exact source identity required for later calls.', schema(), true],
  ['rhino_launch', 'Launch embedded Rhino after local confirmation in ArcGIS Pro.', schema(), false],
  ['rhino_profile', 'Inspect a saved link profile and current schema.', schema(link, linked), true],
  ['rhino_preview', 'Read-only synchronization preview. First initialize through an approved Pull in the dockpane.', schema(link, linked), true],
  ['rhino_pull', 'Pull the saved link after local confirmation. Initializes georeferencing if necessary.', schema({ ...link, selectedOnly: { type: 'boolean' } }, linked), false],
  ['rhino_apply', 'Recompute preview, show local review, then apply under the shared synchronization gate.', schema({ ...link, conflicts: { type: 'string', enum: ['Manual', 'PreferRhino', 'PreferArcGis'] } }, linked), false],
  ['rhino_save', 'Save the active Rhino document after local confirmation; unsaved files use the host Save dialog.', schema(), false]
].map(([name, description, inputSchema, readOnly]) => ({ name, description, inputSchema, annotations: { readOnlyHint: readOnly, destructiveHint: !readOnly, openWorldHint: false } }));

export function validateArguments(name, args) {
  const tool = tools.find(t => t.name === name);
  if (!tool) throw new Error(`Unknown tool: ${name}`);
  if (!args || typeof args !== 'object' || Array.isArray(args)) throw new Error('Arguments must be an object.');
  for (const key of Object.keys(args)) {
    const spec = tool.inputSchema.properties[key];
    if (!spec) throw new Error(`Unexpected argument: ${key}`);
    if (typeof args[key] !== spec.type) throw new Error(`Invalid argument type: ${key}`);
    if (spec.type === 'string' && (!args[key].trim() || args[key].length > spec.maxLength)) throw new Error(`Invalid argument length: ${key}`);
    if (spec.enum && !spec.enum.includes(args[key])) throw new Error(`Invalid argument value: ${key}`);
  }
  for (const key of tool.inputSchema.required) if (!(key in args)) throw new Error(`Missing argument: ${key}`);
  return tool;
}

export function pipeForPid(pid) {
  if (!/^[1-9]\d{0,9}$/.test(String(pid ?? ''))) throw new Error('Set RHINOINSIDE_MCP_HOST_PID to the target ArcGIS Pro process ID. No automatic host selection is performed.');
  return `\\\\.\\pipe\\RhinoInside.ArcGIS.Mcp.v1.${pid}`;
}

const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const hasOwn = (value, key) => Object.prototype.hasOwnProperty.call(value ?? {}, key);

function requestMetadata(request) {
  const metadata = request?.params?._meta;
  if (!object(metadata)) return null;
  // Legacy _meta may carry progressToken, so only protocol-family fields select stateless mode.
  const modern = request.method === 'server/discover' || hasOwn(metadata, META.protocolVersion) ||
    hasOwn(metadata, META.clientInfo) || hasOwn(metadata, META.clientCapabilities);
  return modern ? metadata : null;
}

function modernError(id, code, message, data) {
  return { jsonrpc: '2.0', id, error: { code, message, ...(data === undefined ? {} : { data }) } };
}

function modernResult(id, result) {
  return { jsonrpc: '2.0', id, result: { resultType: 'complete', ...result,
    _meta: { [META.serverInfo]: SERVER_INFO } } };
}

function validateModernMetadata(request, metadata) {
  const requested = metadata?.[META.protocolVersion];
  if (typeof requested !== 'string' || !object(metadata?.[META.clientCapabilities]))
    return modernError(request.id, -32602, 'Modern requests require protocolVersion and clientCapabilities in params._meta.');
  if (!SUPPORTED_MODERN_VERSIONS.includes(requested))
    return modernError(request.id, -32022, 'Unsupported protocol version.', {
      supported: SUPPORTED_MODERN_VERSIONS, requested
    });
  return null;
}

export function invokePipe(pid, request) {
  return new Promise((resolve, reject) => {
    const socket = createConnection(pipeForPid(pid));
    let data = '';
    const finish = (error, result) => { clearTimeout(timer); socket.destroy(); error ? reject(error) : resolve(result); };
    // A timeout after a write is ambiguous; the client must inspect state before retrying.
    const timer = setTimeout(() => finish(new Error('Host timed out. A submitted mutation may still complete; inspect state before retrying.')), 15 * 60 * 1000);
    socket.setEncoding('utf8');
    socket.on('connect', () => socket.write(JSON.stringify(request) + '\n'));
    socket.on('error', error => finish(error));
    socket.on('end', () => { if (!data.includes('\n')) finish(new Error('Host disconnected without a complete response. Inspect state before retrying mutations.')); });
    socket.on('data', chunk => {
      data += chunk;
      if (data.length > 2 * 1024 * 1024) return finish(new Error('Host response exceeded the limit.'));
      const end = data.indexOf('\n');
      if (end >= 0) {
        try { finish(null, JSON.parse(data.slice(0, end))); } catch (error) { finish(error); }
      }
    });
  });
}

export async function handleRequest(request, invoke) {
  const id = request?.id ?? null;
  const error = (code, message) => ({ jsonrpc: '2.0', id, error: { code, message } });
  if (!request || request.jsonrpc !== '2.0' || typeof request.method !== 'string') return error(-32600, 'Invalid JSON-RPC request.');
  if (!Object.hasOwn(request, 'id')) return null;
  const metadata = requestMetadata(request);
  const isModern = metadata !== null;
  if (isModern) {
    const invalidMetadata = validateModernMetadata(request, metadata);
    if (invalidMetadata) return invalidMetadata;
  }
  let result;
  switch (request.method) {
    case 'initialize': {
      if (isModern) return error(-32601, 'The initialize handshake is only available for legacy protocol versions.');
      const requested = request.params?.protocolVersion;
      result = { protocolVersion: LEGACY_VERSIONS.includes(requested) ? requested : LEGACY_VERSIONS[0], capabilities: { tools: {} }, serverInfo: SERVER_INFO, instructions: 'Local development candidate. Use saved source identities. Every mutation requires review in ArcGIS Pro. Never retry a timed-out mutation without inspecting the host.' };
      break;
    }
    case 'server/discover': {
      if (!isModern) return modernError(id, -32602, 'server/discover requires modern request metadata.');
      result = {
        resultType: 'complete',
        supportedVersions: SUPPORTED_MODERN_VERSIONS,
        capabilities: { tools: {} },
        _meta: { [META.serverInfo]: SERVER_INFO },
        instructions: 'Local development candidate. Use saved source identities. Every mutation requires review in ArcGIS Pro. Never retry a timed-out mutation without inspecting the host.',
        ttlMs: 300000,
        cacheScope: 'public'
      };
      break;
    }
    case 'ping': result = isModern ? { resultType: 'complete' } : {}; break;
    case 'tools/list': result = isModern ? { resultType: 'complete', tools } : { tools }; break;
    case 'tools/call': {
      const name = request.params?.name;
      const args = request.params?.arguments ?? {};
      try { validateArguments(name, args); } catch (e) { return error(-32602, e.message); }
      try {
        const reply = await invoke({ operation: name, arguments: args });
        result = reply.ok
          ? { ...(isModern ? { resultType: 'complete' } : {}), content: [{ type: 'text', text: JSON.stringify(reply.result) }], structuredContent: reply.result, isError: false }
          : { ...(isModern ? { resultType: 'complete' } : {}), content: [{ type: 'text', text: reply.error || 'Host rejected the operation.' }], isError: true };
      } catch (e) { result = { ...(isModern ? { resultType: 'complete' } : {}), content: [{ type: 'text', text: e.message }], isError: true }; }
      break;
    }
    default: return error(-32601, 'Method not found.');
  }
  return isModern ? modernResult(id, result) : { jsonrpc: '2.0', id, result };
}
