// Integration smoke for an isolated, disposable School.Training instance.
// SCHOOL_TEST_URL=http://127.0.0.1:5088 SCHOOL_TEST_PASSWORD=<test password>
// SCHOOL_TEST_ALLOW_MUTATION=1 node tests/infrastructure-smoke.mjs
// Only Node built-ins are used. Never point this at a school's production instance.
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import { randomBytes, createHash } from 'node:crypto';
import { EventEmitter } from 'node:events';

const base = process.env.SCHOOL_TEST_URL ?? 'http://127.0.0.1:5088';
const password = process.env.SCHOOL_TEST_PASSWORD;
const userName = process.env.SCHOOL_TEST_USER ?? 'testadmin';
assert.equal(process.env.SCHOOL_TEST_ALLOW_MUTATION, '1', 'Set SCHOOL_TEST_ALLOW_MUTATION=1 for a disposable test instance.');
assert.ok(password?.length >= 12, 'Supply SCHOOL_TEST_PASSWORD in the environment.');
const cookies = new Map();
let csrf = '';
const passed = [];
const cleanup = [];

async function api(path, method = 'GET', data, expected = 200) {
  const headers = { Cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join('; '), Origin: new URL(base).origin };
  if (data !== undefined) headers['Content-Type'] = 'application/json';
  if (csrf) headers['X-CSRF-TOKEN'] = csrf;
  const response = await fetch(`${base}/api${path}`, { method, headers, body: data === undefined ? undefined : JSON.stringify(data) });
  for (const value of response.headers.getSetCookie()) {
    const pair = value.split(';')[0]; const index = pair.indexOf('=');
    cookies.set(pair.slice(0, index), pair.slice(index + 1));
  }
  const responseText = await response.text();
  assert.ok(responseText.length > 0, `${method} ${path}: server returned an empty body (HTTP ${response.status})`);
  const body = JSON.parse(responseText);
  assert.equal(response.status, expected, `${method} ${path}: ${body.message ?? response.statusText}`);
  return body;
}
function mark(name) { passed.push(name); console.log(`PASS ${name}`); }

class Terminal extends EventEmitter {
  constructor(socket) { super(); this.socket = socket; this.buffer = Buffer.alloc(0); this.output = ''; this.closed = false; socket.on('data', b => this.feed(b)); socket.on('close', () => { this.closed = true; this.emit('closed'); }); socket.on('error', e => this.emit('terminal-error', e)); }
  feed(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 2) {
      const opcode = this.buffer[0] & 15;
      let length = this.buffer[1] & 127; let offset = 2;
      if (length === 126) { if (this.buffer.length < 4) return; length = this.buffer.readUInt16BE(2); offset = 4; }
      if (length === 127) { if (this.buffer.length < 10) return; const n = this.buffer.readBigUInt64BE(2); assert.ok(n < 1048576n); length = Number(n); offset = 10; }
      const masked = !!(this.buffer[1] & 128); if (masked) offset += 4;
      if (this.buffer.length < offset + length) return;
      let payload = this.buffer.subarray(offset, offset + length);
      if (masked) { const mask = this.buffer.subarray(offset - 4, offset); payload = Buffer.from(payload.map((b, i) => b ^ mask[i % 4])); }
      this.buffer = this.buffer.subarray(offset + length);
      if (opcode === 1 || opcode === 0) { this.output += payload.toString('utf8'); this.emit('output'); }
      if (opcode === 8) { this.closed = true; this.frame(Buffer.alloc(0), 8); this.socket.end(); this.emit('closed'); }
      if (opcode === 9) this.frame(payload, 10);
    }
  }
  frame(payload, opcode = 1) {
    if (this.socket.destroyed) return;
    const mask = randomBytes(4); const ext = payload.length >= 126 ? 2 : 0;
    const header = Buffer.alloc(2 + ext + 4); header[0] = 0x80 | opcode;
    header[1] = 0x80 | (ext ? 126 : payload.length); if (ext) header.writeUInt16BE(payload.length, 2);
    mask.copy(header, 2 + ext); const encoded = Buffer.from(payload.map((b, i) => b ^ mask[i % 4]));
    this.socket.write(Buffer.concat([header, encoded]));
  }
  send(text) { this.frame(Buffer.from(text)); }
  async waitFor(text, timeout = 5000) {
    if (this.output.includes(text)) return;
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => { clear(); reject(new Error(`Terminal did not produce expected output: ${text}`)); }, timeout);
      const check = () => { if (this.output.includes(text)) { clear(); resolve(); } };
      const closed = () => { clear(); reject(new Error(`Terminal closed before: ${text}`)); };
      const clear = () => { clearTimeout(timer); this.off('output', check); this.off('closed', closed); };
      this.on('output', check); this.on('closed', closed); check();
    });
  }
  async waitClosed(timeout = 5000) {
    if (this.closed) return;
    await new Promise((resolve, reject) => { const timer = setTimeout(() => reject(new Error('Terminal was not reclaimed.')), timeout); this.once('closed', () => { clearTimeout(timer); resolve(); }); });
  }
  close() { this.frame(Buffer.alloc(0), 8); }
}
async function terminal(deviceId, expectedStatus = 101, origin = new URL(base).origin) {
  const url = new URL(`/api/terminal/${deviceId}`, base); const key = randomBytes(16).toString('base64');
  return new Promise((resolve, reject) => {
    const request = (url.protocol === 'https:' ? https : http).request(url, { headers: { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': key, Origin: origin, Cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join('; ') } });
    request.on('upgrade', (response, socket, head) => {
      assert.equal(expectedStatus, 101, `Unexpected WS upgrade; expected ${expectedStatus}.`);
      assert.equal(response.headers['sec-websocket-accept'], createHash('sha1').update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64'));
      const result = new Terminal(socket); if (head.length) result.feed(head); resolve(result);
    });
    request.on('response', response => { response.resume(); try { assert.equal(response.statusCode, expectedStatus); resolve(null); } catch (error) { reject(error); } });
    request.on('error', reject); request.setTimeout(10000, () => request.destroy(new Error('WebSocket handshake timed out.'))); request.end();
  });
}

try {
  const status = await api('/status');
  if (!status.initialized) await api('/setup', 'POST', { schoolName: '隔离集成测试', userName, displayName: '测试管理员', password });
  else await api('/auth/login', 'POST', { userName, password });
  csrf = (await api('/auth/me')).csrfToken;
  assert.ok(csrf); mark('admin session and antiforgery token');

  const first = await api('/simulation/bootstrap', 'POST', {});
  const second = await api('/simulation/bootstrap', 'POST', {});
  assert.equal(second.created, 0); assert.equal(first.isSimulated, true); mark('simulation bootstrap is explicit and idempotent');

  const suffix = randomBytes(4).toString('hex');
  const type = await api('/device-types', 'POST', { name: `测试类型-${suffix}`, newline: 'CRLF', backspace: 'DEL' });
  cleanup.push(() => api(`/device-types/${type.id}`, 'DELETE'));
  const cabinet = await api('/cabinets', 'POST', { name: `测试机柜-${suffix}`, host: 'simulator.local', port: 23, jumpPort: 22, isSimulated: true, jumpPassword: 'NeverReturnThisSecret-123' });
  assert.equal(cabinet.hasJumpPassword, true); assert.ok(!JSON.stringify(cabinet).includes('NeverReturnThisSecret'));
  cleanup.push(() => api(`/cabinets/${cabinet.id}`, 'DELETE'));
  const device = await api('/devices', 'POST', { name: `测试交换机-${suffix}`, cabinetId: cabinet.id, deviceTypeId: type.id, port: 23, webPort: 80, webProtocol: 'http', serialPortNumber: 1, connectionType: 'simulated', password: 'NeverReturnDeviceSecret-456' });
  cleanup.push(() => api(`/devices/${device.id}`, 'DELETE'));
  assert.equal(device.hasPassword, true); assert.ok(!JSON.stringify(device).includes('NeverReturnDeviceSecret'));
  const list = await api(`/devices?search=${encodeURIComponent(suffix)}`); assert.equal(list.total, 1);
  assert.ok(!JSON.stringify(list).includes('NeverReturnDeviceSecret')); mark('CRUD, filtering, write-only credential responses');

  await api(`/device-types/${type.id}`, 'DELETE', undefined, 409);
  await api(`/cabinets/${cabinet.id}`, 'DELETE', undefined, 409); mark('reference-safe deletes');
  assert.equal((await api(`/devices/${device.id}/connect-test`, 'POST', {})).isSimulated, true);
  const probes = await api('/cabinets/test-connections', 'POST', { ids: [cabinet.id] }); assert.equal(probes.items[0].success, true); assert.equal(probes.items[0].isSimulated, true); mark('connection probes identify simulated transport');

  await terminal(device.id, 403, 'https://foreign.example');
  const cli = await terminal(device.id); await cli.waitFor('SIMULATED DEVICE');
  await terminal(device.id, 409);
  cli.send('configure terminal\rhostname TESTLAB\rinterface ethernet0/0\rshutdown\rdescription test-interface\rend\rwrite memory\rshow interfaces\r');
  await cli.waitFor('admin-down'); await cli.waitFor('test-interface');
  assert.equal((await api(`/devices?search=${encodeURIComponent(suffix)}`)).items[0].sessionActive, true);
  await api(`/devices/${device.id}`, 'DELETE', undefined, 409); mark('terminal same-origin guard, exclusive lease and functional CLI');
  const reclaimed = await api(`/cabinets/${cabinet.id}/reclaim`, 'POST', {}); assert.equal(reclaimed.releasedSessions, 1); await cli.waitClosed();
  const fresh = await terminal(device.id); await fresh.waitFor('TESTLAB#');
  fresh.send('show running-config\r'); await fresh.waitFor('test-interface'); fresh.close(); await fresh.waitClosed(); mark('reclaim releases sessions; simulation configuration persists');

  const node = await api('/nodes', 'POST', { name: `测试节点-${suffix}`, provider: 'simulated', totalCpu: 4, totalMemoryMb: 4096, totalDiskGb: 20 });
  cleanup.push(() => api(`/nodes/${node.id}`, 'DELETE'));
  await api(`/nodes/${node.id}/configuration`, 'PUT', { ipAddress: '192.0.2.20', subnetMask: '255.0.255.0', gateway: '192.0.2.1' }, 400);
  await api(`/nodes/${node.id}/configuration`, 'PUT', { ipAddress: '192.0.2.20', subnetMask: '255.255.255.0', gateway: '198.51.100.1' }, 400);
  const config = await api(`/nodes/${node.id}/configuration`, 'PUT', { ipAddress: '192.0.2.20', subnetMask: '255.255.255.0', gateway: '192.0.2.1', systemTime: '2030-01-01T12:00:00+08:00' });
  assert.equal(config.isSimulated, true); assert.equal(config.ipAddress, '192.0.2.20'); assert.ok(Math.abs(Date.parse(config.systemTime) - Date.parse('2030-01-01T04:00:00Z')) < 2000); mark('simulated clock/network persistence and subnet validation');
  const vm = await api('/virtual-machines', 'POST', { name: `测试虚拟机-${suffix}`, nodeId: node.id, cpuCount: 2, memoryMb: 1024, diskGb: 5 });
  cleanup.push(async () => { await api(`/virtual-machines/${vm.id}/stop`, 'POST', {}); await api(`/virtual-machines/${vm.id}`, 'DELETE'); });
  assert.equal((await api(`/virtual-machines/${vm.id}/start`, 'POST', {})).status, 'running');
  const nodeStatus = await api(`/nodes/${node.id}/status`); assert.equal(nodeStatus.runningCount, 1); assert.equal(nodeStatus.isSimulated, true); assert.equal(nodeStatus.cpuUsagePercent, 50);
  await api(`/virtual-machines/${vm.id}`, 'DELETE', undefined, 409);
  await api(`/nodes/${node.id}`, 'DELETE', undefined, 409);
  await api(`/virtual-machines/${vm.id}/stop`, 'POST', {}); await api(`/nodes/${node.id}/disable`, 'POST', {});
  await api(`/virtual-machines/${vm.id}/start`, 'POST', {}, 409); await api(`/nodes/${node.id}/enable`, 'POST', {});
  assert.equal((await api(`/virtual-machines/${vm.id}/start`, 'POST', {})).status, 'running'); mark('VM lifecycle, node scheduling and resource accounting');

  const server = await api('/server/info'); assert.equal(server.isSimulated, false); assert.ok(server.hostName); assert.ok(server.cpu.logicalCoreCount > 0); assert.ok(Array.isArray(server.networks)); mark('management host inventory remains real and read-only');

  const logoutTerminal = await terminal(device.id); await logoutTerminal.waitFor('SIMULATED DEVICE');
  await api('/auth/logout', 'POST', {}); await logoutTerminal.waitClosed(3000);
  await api('/auth/login', 'POST', { userName, password }); csrf = (await api('/auth/me')).csrfToken;
  mark('logout revokes an already-open terminal lease');

  const passwordTerminal = await terminal(device.id); await passwordTerminal.waitFor('SIMULATED DEVICE');
  const temporaryPassword = `Revocation-${randomBytes(12).toString('hex')}1a`;
  const changed = await api('/auth/password', 'POST', { currentPassword: password, newPassword: temporaryPassword });
  csrf = changed.csrfToken; await passwordTerminal.waitClosed(3000);
  const restored = await api('/auth/password', 'POST', { currentPassword: temporaryPassword, newPassword: password }); csrf = restored.csrfToken;
  mark('password change invalidates an already-open terminal and renews CSRF');
} finally {
  for (const operation of cleanup.reverse()) { try { await operation(); } catch (error) { console.error(`CLEANUP ${error.message}`); } }
}
console.log(JSON.stringify({ status: 'passed', assertions: passed.length, checks: passed }, null, 2));
