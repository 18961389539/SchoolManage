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

async function api(path, method = 'GET', data, expected = 200, session) {
  const sessionCookies = session?.cookies ?? cookies;
  const csrfToken = session?.csrf ?? csrf;
  const headers = { Cookie: [...sessionCookies].map(([k, v]) => `${k}=${v}`).join('; '), Origin: new URL(base).origin };
  if (data !== undefined) headers['Content-Type'] = 'application/json';
  if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;
  const response = await fetch(`${base}/api${path}`, { method, headers, body: data === undefined ? undefined : JSON.stringify(data) });
  for (const value of response.headers.getSetCookie()) {
    const pair = value.split(';')[0]; const index = pair.indexOf('=');
    sessionCookies.set(pair.slice(0, index), pair.slice(index + 1));
  }
  const responseText = await response.text();
  assert.ok(responseText.length > 0, `${method} ${path}: server returned an empty body (HTTP ${response.status})`);
  const body = JSON.parse(responseText);
  assert.equal(response.status, expected, `${method} ${path}: ${body.message ?? response.statusText}`);
  if (session && body.csrfToken) session.csrf = body.csrfToken;
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
async function terminal(deviceId, expectedStatus = 101, origin = new URL(base).origin, sessionCookies = cookies) {
  const url = new URL(`/api/terminal/${deviceId}`, base); const key = randomBytes(16).toString('base64');
  return new Promise((resolve, reject) => {
    const request = (url.protocol === 'https:' ? https : http).request(url, { headers: { Connection: 'Upgrade', Upgrade: 'websocket', 'Sec-WebSocket-Version': '13', 'Sec-WebSocket-Key': key, Origin: origin, Cookie: [...sessionCookies].map(([k, v]) => `${k}=${v}`).join('; ') } });
    request.on('upgrade', (response, socket, head) => {
      assert.equal(expectedStatus, 101, `Unexpected WS upgrade; expected ${expectedStatus}.`);
      assert.equal(response.headers['sec-websocket-accept'], createHash('sha1').update(`${key}258EAFA5-E914-47DA-95CA-C5AB0DC85B11`).digest('base64'));
      const result = new Terminal(socket); if (head.length) result.feed(head); resolve(result);
    });
    request.on('response', response => {
      let body = '';
      response.setEncoding('utf8');
      response.on('data', chunk => { body += chunk; });
      response.on('end', () => {
        try { assert.equal(response.statusCode, expectedStatus, body); resolve(body); }
        catch (error) { reject(error); }
      });
    });
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
  const emptyScopedList = await api(`/devices?cabinetId=${cabinet.id}&search=no-match-${suffix}`);
  assert.deepEqual(emptyScopedList, { total: 0, cabinet: { id: cabinet.id, name: cabinet.name }, items: [] });
  assert.deepEqual(await api('/devices?cabinetId=missing-cabinet'), { total: 0, cabinet: null, items: [] });
  mark('scoped device lists identify their cabinet even when no device matches');

  await api(`/device-types/${type.id}`, 'DELETE', undefined, 409);
  await api(`/cabinets/${cabinet.id}`, 'DELETE', undefined, 409); mark('reference-safe deletes');
  assert.equal((await api(`/devices/${device.id}/connect-test`, 'POST', {})).isSimulated, true);
  const probes = await api('/cabinets/test-connections', 'POST', { ids: [cabinet.id] }); assert.equal(probes.items[0].success, true); assert.equal(probes.items[0].isSimulated, true); mark('connection probes identify simulated transport');

  // Outbound policy: an administrator account must not be able to use connect probes as a
  // loopback port scanner for the management host.
  const blockedCabinet = await api('/cabinets', 'POST', { name: `回环机柜-${suffix}`, host: '127.0.0.1', port: 5080, jumpPort: 22, isSimulated: false });
  cleanup.push(() => api(`/cabinets/${blockedCabinet.id}`, 'DELETE'));
  const batchProbes = (await api('/cabinets/test-connections', 'POST', { ids: [blockedCabinet.id, cabinet.id] })).items;
  const simulatedProbe = batchProbes.find(row => row.id === cabinet.id);
  const blockedProbe = batchProbes.find(row => row.id === blockedCabinet.id);
  assert.equal(batchProbes.length, 2);
  assert.equal(simulatedProbe.name, cabinet.name);
  assert.equal(simulatedProbe.success, true);
  assert.equal(blockedProbe.name, blockedCabinet.name);
  assert.equal(blockedProbe.success, false);
  assert.match(blockedProbe.message, /不允许连接/);
  assert.ok(!JSON.stringify(batchProbes).includes('NeverReturnThisSecret'));
  mark('each result in a mixed cabinet batch includes the corresponding name without credentials');
  const blockedDevice = await api('/devices', 'POST', { name: `回环设备-${suffix}`, cabinetId: blockedCabinet.id, deviceTypeId: type.id, port: 5080, webPort: 80, webProtocol: 'http', serialPortNumber: 1, connectionType: 'tcp', host: '127.0.0.1' });
  cleanup.push(() => api(`/devices/${blockedDevice.id}`, 'DELETE'));
  const blockedDeviceProbe = await api(`/devices/${blockedDevice.id}/connect-test`, 'POST', {});
  assert.equal(blockedDeviceProbe.success, false);
  assert.match(blockedDeviceProbe.message, /不允许连接/);
  const probeAudit = await api('/audits?search=测试');
  assert.ok(probeAudit.items.some(x => x.action === '测试机柜连接' && x.target.includes(blockedCabinet.name)), 'cabinet probe was not audited');
  assert.ok(probeAudit.items.some(x => x.action === '测试设备连接' && x.target === blockedDevice.name), 'device probe was not audited');
  mark('loopback and link-local probes refused and audited');

  await terminal(device.id, 403, 'https://foreign.example');
  const cli = await terminal(device.id); await cli.waitFor('SIMULATED DEVICE');
  await terminal(device.id, 409);
  cli.send('configure terminal\rhostname TESTLAB\rinterface ethernet0/0\rshutdown\rdescription test-interface\rend\rwrite memory\rshow interfaces\r');
  await cli.waitFor('admin-down'); await cli.waitFor('test-interface');
  assert.equal((await api(`/devices?search=${encodeURIComponent(suffix)}`)).items.find(x => x.id === device.id).sessionActive, true);
  const webPath = `/devices/${device.id}/web-console`;
  const busyWeb = await api(webPath);
  assert.equal(busyWeb.busy, true);
  assert.equal(busyWeb.hostname, 'TESTLAB');
  assert.equal(busyWeb.interfaces.find(item => item.name === 'ethernet0/0').enabled, false);
  await api(webPath, 'PUT', {version:busyWeb.version, hostname:'WEBTEST', interfaces:busyWeb.interfaces}, 409);
  await api(`${webPath}/startup`, 'PUT', {version:busyWeb.version}, 409);
  await api(`/devices/${device.id}`, 'DELETE', undefined, 409); mark('terminal same-origin guard, exclusive lease and functional CLI');
  const reclaimed = await api(`/cabinets/${cabinet.id}/reclaim`, 'POST', {}); assert.equal(reclaimed.releasedSessions, 1); await cli.waitClosed();
  const fresh = await terminal(device.id); await fresh.waitFor('TESTLAB#');
  fresh.send('show running-config\r'); await fresh.waitFor('test-interface'); fresh.close(); await fresh.waitClosed(); mark('reclaim releases sessions; simulation configuration persists');

  const web = await api(webPath);
  assert.equal(web.busy, false);
  assert.equal(web.hasStartupConfiguration, true);
  assert.equal(web.startupHostname, 'TESTLAB');
  assert.equal(web.unsavedChanges, false);
  assert.equal(web.isSimulated, true);
  assert.equal(Object.hasOwn(web, 'password'), false);
  await api(`/devices/${blockedDevice.id}/web-console`, 'GET', undefined, 404);
  await api(webPath, 'PUT', {version:web.version, hostname:'INVALID HOST', interfaces:web.interfaces}, 400);
  const duplicateInterfaces = web.interfaces.map(item => ({...item, name:web.interfaces[0].name}));
  await api(webPath, 'PUT', {version:web.version, hostname:'WEBTEST', interfaces:duplicateInterfaces}, 400);
  assert.equal((await api(webPath)).version, web.version);
  const browserInterfaces = web.interfaces.map(item => item.name === 'ethernet0/0' ? {...item, enabled:true, description:'web-interface'} : item);
  const appliedWeb = await api(webPath, 'PUT', {version:web.version, hostname:'WEBTEST', interfaces:browserInterfaces});
  assert.equal(appliedWeb.unsavedChanges, true);
  assert.equal(appliedWeb.enabledInterfaceCount, web.enabledInterfaceCount + 1);
  await api(webPath, 'PUT', {version:web.version, hostname:'STALE', interfaces:web.interfaces}, 409);
  await api(`${webPath}/startup`, 'PUT', {version:web.version}, 409);
  const savedWeb = await api(`${webPath}/startup`, 'PUT', {version:appliedWeb.version});
  assert.equal(savedWeb.unsavedChanges, false);
  assert.equal(savedWeb.startupHostname, 'WEBTEST');
  await api(webPath, 'PUT', {version:savedWeb.version, hostname:'RUNTIME', interfaces:browserInterfaces});
  const webCli = await terminal(device.id); await webCli.waitFor('RUNTIME#');
  webCli.send('reload\rshow interfaces\r'); await webCli.waitFor('WEBTEST#'); await webCli.waitFor('web-interface');
  webCli.send('configure terminal\rhostname TESTLAB\rend\rwrite memory\r'); await webCli.waitFor('模拟启动配置已保存');
  webCli.close(); await webCli.waitClosed();
  assert.equal((await api(webPath)).startupHostname, 'TESTLAB');
  mark('simulated Web and CLI share running and startup configuration, reject stale writes and share an exclusive lease');

  const node = await api('/nodes', 'POST', { name: `测试节点-${suffix}`, provider: 'simulated', totalCpu: 4, totalMemoryMb: 4096, totalDiskGb: 20 });
  cleanup.push(() => api(`/nodes/${node.id}`, 'DELETE'));
  const nodeList = `/nodes?search=${encodeURIComponent(node.name)}&provider=simulated&pageSize=1`;
  const readListedNode = async () => {
    const {items, total} = await api(nodeList);
    assert.equal(total, 1);
    assert.equal(items.length, 1);
    assert.equal(items[0].id, node.id);
    return items[0];
  };
  const metricValues = item => [item.healthStatus, item.cpuUsagePercent, item.memoryUsagePercent, item.diskUsagePercent];
  assert.deepEqual(metricValues(await readListedNode()), ['healthy', 0, 0, 0]);
  assert.deepEqual(await api(`${nodeList}&page=2`), {items: [], total: 1});
  await api(`/nodes/${node.id}/configuration`, 'PUT', { ipAddress: '192.0.2.20', subnetMask: '255.0.255.0', gateway: '192.0.2.1' }, 400);
  await api(`/nodes/${node.id}/configuration`, 'PUT', { ipAddress: '192.0.2.20', subnetMask: '255.255.255.0', gateway: '198.51.100.1' }, 400);
  const config = await api(`/nodes/${node.id}/configuration`, 'PUT', { ipAddress: '192.0.2.20', subnetMask: '255.255.255.0', gateway: '192.0.2.1', systemTime: '2030-01-01T12:00:00+08:00' });
  assert.equal(config.isSimulated, true); assert.equal(config.ipAddress, '192.0.2.20'); assert.ok(Math.abs(Date.parse(config.systemTime) - Date.parse('2030-01-01T04:00:00Z')) < 2000); mark('simulated clock/network persistence and subnet validation');
  const vm = await api('/virtual-machines', 'POST', { name: `测试虚拟机-${suffix}`, nodeId: node.id, cpuCount: 2, memoryMb: 1024, diskGb: 5 });
  cleanup.push(async () => { await api(`/virtual-machines/${vm.id}/stop`, 'POST', {}); await api(`/virtual-machines/${vm.id}`, 'DELETE'); });
  assert.equal((await api(`/virtual-machines/${vm.id}/start`, 'POST', {})).status, 'running');
  const nodeStatus = await api(`/nodes/${node.id}/status`); assert.equal(nodeStatus.runningCount, 1); assert.equal(nodeStatus.isSimulated, true); assert.equal(nodeStatus.cpuUsagePercent, 50);
  const runningNode = await readListedNode();
  assert.deepEqual(metricValues(runningNode), ['healthy', 50, 25, 25]);
  assert.deepEqual(metricValues(runningNode), metricValues(nodeStatus));
  assert.equal(runningNode.virtualMachineCount, 1);
  await api(`/virtual-machines/${vm.id}`, 'DELETE', undefined, 409);
  await api(`/nodes/${node.id}`, 'DELETE', undefined, 409);
  await api(`/virtual-machines/${vm.id}/stop`, 'POST', {}); await api(`/nodes/${node.id}/disable`, 'POST', {});
  const disabledNode = await readListedNode();
  assert.equal(disabledNode.enabled, false);
  assert.deepEqual(metricValues(disabledNode), ['healthy', 0, 0, 25]);
  await api(`/virtual-machines/${vm.id}/start`, 'POST', {}, 409); await api(`/nodes/${node.id}/enable`, 'POST', {});
  assert.equal((await api(`/virtual-machines/${vm.id}/start`, 'POST', {})).status, 'running'); mark('VM lifecycle, node scheduling and resource accounting');
  const externalNode = await api('/nodes', 'POST', {name: `未接入节点-${suffix}`, provider: 'hyperv', host: 'unavailable.invalid'});
  cleanup.push(() => api(`/nodes/${externalNode.id}`, 'DELETE'));
  const externalList = await api(`/nodes?search=${encodeURIComponent(externalNode.name)}`);
  assert.equal(externalList.total, 1);
  assert.equal(externalList.items[0].id, externalNode.id);
  assert.deepEqual(metricValues(externalList.items[0]), [null, null, null, null]);
  await api(`/nodes/${externalNode.id}/status`, 'GET', undefined, 501);
  mark('paginated cluster list reports the same metrics as node details and never fabricates external metrics');

  const deviceInput = { name: device.name, cabinetId: cabinet.id, deviceTypeId: type.id, connectionType: 'simulated', nodeId: node.id };
  await api(`/devices/${device.id}`, 'PUT', {...deviceInput, connectionType:'tcp', host:'192.0.2.30'}, 400);
  await api(`/devices/${device.id}`, 'PUT', {...deviceInput, nodeId:'missing-node'}, 400);
  const linked = await api(`/devices/${device.id}`, 'PUT', deviceInput);
  assert.equal(linked.nodeId, node.id);
  assert.equal(linked.nodeName, node.name);
  const unlinked = await api('/devices', 'POST', {...deviceInput, name:`未分配设备-${suffix}`, nodeId:null});
  cleanup.push(() => api(`/devices/${unlinked.id}`, 'DELETE'));
  assert.equal(unlinked.nodeId, null);
  assert.equal((await api('/nodes?provider=simulated')).items.some(item => item.id === node.id), true);

  const anonymous = {cookies:new Map(),csrf:''};
  await api('/my-resources', 'GET', undefined, 401, anonymous);
  const makeLearner = async (kind, nodeId) => {
    const userName = `${kind}${suffix}`;
    const initialPassword = `InitialPassword-${suffix}1`;
    const learner = await api('/people', 'POST', {kind, userName, name:`实训账号-${kind}-${suffix}`, number:`QA${suffix}${kind}`, password:initialPassword});
    cleanup.push(() => api(`/people/${learner.id}`, 'DELETE'));
    const candidates = await api(`/nodes/${nodeId}/account-candidates?search=${encodeURIComponent(userName)}`);
    const userId = candidates.items.find(item => item.userName === userName).userId;
    const session = {cookies:new Map(),csrf:''};
    await api('/auth/login', 'POST', {userName, password:initialPassword}, 200, session);
    await api('/my-resources', 'GET', undefined, 428, session);
    await api('/auth/password', 'POST', {currentPassword:initialPassword, newPassword:`ChangedPassword-${suffix}2`}, 200, session);
    assert.deepEqual(await api('/my-resources', 'GET', undefined, 200, session), {nodes:[],devices:[],virtualMachines:[]});
    await api(`/nodes/${nodeId}/accounts`, 'POST', {userIds:[userId]});
    return {session,userId};
  };
  const student = await makeLearner('student', node.id);
  const teacher = await makeLearner('teacher', 'simulation-node');
  const myStudent = await api('/my-resources', 'GET', undefined, 200, student.session);
  assert.deepEqual(myStudent.nodes.map(item => item.id), [node.id]);
  assert.deepEqual(myStudent.devices.map(item => item.id), [device.id]);
  assert.equal(myStudent.devices[0].canAccess, true);
  assert.deepEqual(Object.keys(myStudent.devices[0]).sort(), ['cabinetName','canAccess','id','isSimulated','name','nodeId','nodeName','sessionActive']);
  assert.deepEqual(myStudent.virtualMachines.map(item => item.id), [vm.id]);
  const myTeacher = await api('/my-resources', 'GET', undefined, 200, teacher.session);
  assert.deepEqual(myTeacher.nodes.map(item => item.id), ['simulation-node']);
  assert.deepEqual(myTeacher.devices.map(item => item.id).sort(), ['simulation-router','simulation-switch']);
  await api('/devices', 'GET', undefined, 403, student.session);
  await api(`/virtual-machines/${vm.id}/stop`, 'POST', {}, 403, student.session);
  const learnerWeb = await api(webPath, 'GET', undefined, 200, student.session);
  const learnerInterfaces = learnerWeb.interfaces.map(item => item.name === 'ethernet0/1' ? {...item, description:'learner-web'} : item);
  const learnerApplied = await api(webPath, 'PUT', {version:learnerWeb.version, hostname:learnerWeb.hostname, interfaces:learnerInterfaces}, 200, student.session);
  assert.equal(learnerApplied.interfaces.find(item => item.name === 'ethernet0/1').description, 'learner-web');
  const absentWeb = await api('/devices/absent-device/web-console', 'GET', undefined, 404, student.session);
  assert.deepEqual(await api(`/devices/${unlinked.id}/web-console`, 'GET', undefined, 404, student.session), absentWeb);
  assert.deepEqual(await api('/devices/simulation-switch/web-console', 'GET', undefined, 404, student.session), absentWeb);
  assert.deepEqual(await api(`/devices/${blockedDevice.id}/web-console`, 'GET', undefined, 404, student.session), absentWeb);
  await api(webPath, 'GET', undefined, 404, teacher.session);
  await api('/devices/simulation-switch/web-console', 'GET', undefined, 200, teacher.session);
  const origin = new URL(base).origin;
  const absent = await terminal('absent-device', 404, origin, student.session.cookies);
  assert.equal(await terminal(unlinked.id, 404, origin, student.session.cookies), absent);
  assert.equal(await terminal('simulation-switch', 404, origin, student.session.cookies), absent);
  assert.equal(await terminal(blockedDevice.id, 404, origin, student.session.cookies), absent);
  await terminal(device.id, 403, 'https://foreign.example', student.session.cookies);
  mark('learner inventory is isolated by explicit simulated node assignment without leaking device credentials or identity');

  const studentTerminal = await terminal(device.id, 101, origin, student.session.cookies);
  const teacherTerminal = await terminal('simulation-switch', 101, origin, teacher.session.cookies);
  await studentTerminal.waitFor('TESTLAB#');
  await teacherTerminal.waitFor('SIMULATED DEVICE');
  studentTerminal.send('configure terminal\rhostname STUDENTLAB\rend\rwrite memory\r');
  await studentTerminal.waitFor('模拟启动配置已保存');
  await api(`/nodes/${node.id}/disable`, 'POST', {});
  await studentTerminal.waitClosed(3000);
  assert.deepEqual(await api(webPath, 'PUT', {version:learnerApplied.version, hostname:'DISABLED', interfaces:learnerInterfaces}, 404, student.session), absentWeb);
  assert.equal((await api('/my-resources', 'GET', undefined, 200, student.session)).devices[0].canAccess, false);
  assert.equal(await terminal(device.id, 404, origin, student.session.cookies), absent);
  assert.equal(teacherTerminal.closed, false);
  teacherTerminal.send('show version\r');
  await teacherTerminal.waitFor('School Training Simulator 1.0');
  await api(`/nodes/${node.id}/enable`, 'POST', {});
  const reassigned = await terminal(device.id, 101, origin, student.session.cookies);
  await reassigned.waitFor('STUDENTLAB#');
  await api(`/nodes/${node.id}/accounts/${student.userId}`, 'DELETE');
  await reassigned.waitClosed(3000);
  assert.deepEqual(await api('/my-resources', 'GET', undefined, 200, student.session), {nodes:[],devices:[],virtualMachines:[]});
  assert.deepEqual(await api(webPath, 'GET', undefined, 404, student.session), absentWeb);
  assert.equal(await terminal(device.id, 404, origin, student.session.cookies), absent);
  await api(`/nodes/simulation-node/accounts/${teacher.userId}`, 'DELETE');
  await teacherTerminal.waitClosed(3000);
  mark('authorized learners can use simulation; disabling nodes and removing assignments revoke only affected sessions');

  const temporaryNode = await api('/nodes', 'POST', {name:`待删除设备关联-${suffix}`, provider:'simulated'});
  await api(`/devices/${unlinked.id}`, 'PUT', {...deviceInput, name:unlinked.name, nodeId:temporaryNode.id});
  await api(`/nodes/${temporaryNode.id}`, 'DELETE');
  const kept = (await api(`/devices?search=${encodeURIComponent(unlinked.name)}`)).items.find(item => item.id === unlinked.id);
  assert.equal(kept.nodeId, null);
  mark('deleting an otherwise unused node leaves its device available to administrators');

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
