// Build the server first, then run: node tests/node-accounts-smoke.mjs
// Only Node built-ins; owns a fresh .local program copy, database and loopback service.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.resolve(process.env.SCHOOL_NODE_ACCOUNT_SOURCE_DIR ?? path.join(workspace, 'src/School.Training/bin/Debug/net10.0'));
const localRoot = path.join(workspace, '.local'); fs.mkdirSync(localRoot, { recursive: true });
const runDirectory = fs.mkdtempSync(path.join(localRoot, 'node-accounts-smoke-'));
const program = path.join(runDirectory, 'program'); const data = path.join(runDirectory, 'data');
const executableName = process.platform === 'win32' ? 'School.Training.exe' : 'School.Training';
const executable = path.join(program, executableName); const log = path.join(runDirectory, 'service.log');
const relative = value => path.relative(workspace, value).split(path.sep).join('/');
const sha = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const report = { recordedAtUtc: new Date().toISOString(), status: 'running', command: 'node tests/node-accounts-smoke.mjs', dataDirectory: relative(data), runtimeDirectory: relative(program), newBaselineSchemaVersion: 4, checks: [], processStopped: false, scope: 'Single-instance simulated node platform-account associations and quotas. Does not create OS accounts, change physical hypervisors, migrate prior development databases or establish production capacity.' };
let service; let exited = false; let exitStatus = null; let serviceError = null; let url = ''; let failure;
function pass(name) { report.checks.push(name); console.log(`PASS ${name}`); }
async function freePort() { const server = net.createServer(); await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); }); const number = server.address().port; await new Promise(resolve => server.close(resolve)); return number; }
function session() {
  const cookies = new Map(); let csrf = '';
  return { async call(endpoint, method = 'GET', body, expected = 200) {
    const headers = { Origin: url, Cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join('; ') };
    if (csrf) headers['X-CSRF-TOKEN'] = csrf;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(`${url}/api${endpoint}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
    for (const value of response.headers.getSetCookie()) { const pair = value.split(';')[0]; const equals = pair.indexOf('='); cookies.set(pair.slice(0, equals), pair.slice(equals + 1)); }
    const text = await response.text(); let result;
    try { result = JSON.parse(text); } catch { throw new Error(`${method} ${endpoint}: non-JSON HTTP ${response.status}`); }
    const accepted = Array.isArray(expected) ? expected : [expected];
    assert.ok(accepted.includes(response.status), `${method} ${endpoint}: HTTP ${response.status}: ${result.message ?? 'unexpected response'}`);
    if (result.csrfToken) csrf = result.csrfToken;
    return { ...result, httpStatus: response.status };
  } };
}
const admin = session();
const nodeInput = (node, changes = {}) => ({ name: node.name, host: node.host, provider: node.provider, port: node.port, userName: node.userName, enabled: node.enabled, remark: node.remark, totalCpu: node.totalCpu, totalMemoryMb: node.totalMemoryMb, totalDiskGb: node.totalDiskGb, accountQuota: node.accountQuota, virtualMachineQuota: node.virtualMachineQuota, ...changes });
async function stop() {
  if (!service) { report.processStopped = true; return; }
  if (!exited) service.kill('SIGTERM');
  const deadline = Date.now() + 5000; while (!exited && Date.now() < deadline) await pause(50);
  if (!exited) { service.kill('SIGKILL'); const forcedDeadline = Date.now() + 5000; while (!exited && Date.now() < forcedDeadline) await pause(50); }
  report.processStopped = exited; report.serviceExit = exitStatus;
  if (url && exited) report.portReleased = await new Promise(resolve => { const socket = net.connect({ host: '127.0.0.1', port: Number(new URL(url).port) }); socket.once('connect', () => { socket.destroy(); resolve(false); }); socket.once('error', () => { socket.destroy(); resolve(true); }); socket.setTimeout(1000, () => { socket.destroy(); resolve(null); }); });
}

try {
  const sourceAssembly = path.join(source, 'School.Training.dll');
  assert.ok(fs.existsSync(sourceAssembly), 'Build the current v1 server model first.');
  report.sourceAssemblySha256 = sha(sourceAssembly);
  fs.cpSync(source, program, { recursive: true, force: false, errorOnExist: true });
  report.testedAssemblySha256 = sha(path.join(program, 'School.Training.dll')); assert.equal(report.testedAssemblySha256, report.sourceAssemblySha256);
  assert.equal(fs.existsSync(data), false);
  url = `http://127.0.0.1:${await freePort()}`; report.isolatedUrl = url;
  service = spawn(executable, ['--urls', url], { cwd: program, windowsHide: true, env: { ...process.env, SCHOOL_DATA_DIR: data, ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production' }, stdio: ['ignore', 'pipe', 'pipe'] });
  service.stdout.on('data', chunk => fs.appendFileSync(log, chunk)); service.stderr.on('data', chunk => fs.appendFileSync(log, chunk));
  service.on('error', error => { serviceError = error; }); service.on('exit', (code, signal) => { exited = true; exitStatus = { code, signal }; });
  const deadline = Date.now() + 30000;
  while (true) { if (serviceError) throw serviceError; if (exited) throw new Error('Fresh v1 service failed startup; inspect isolated service.log.'); try { const result = await fetch(`${url}/api/status`, { signal: AbortSignal.timeout(1000) }); if (result.ok) break; } catch { } if (Date.now() > deadline) throw new Error('Startup timed out.'); await pause(100); }
  const password = `Node-${randomBytes(18).toString('hex')}Aa1!`;
  const setup = await admin.call('/setup', 'POST', { schoolName: '节点配额隔离测试', userName: 'nodeadmin', displayName: '节点测试管理员', password });
  const initialAdminId = setup.user.id;
  const prefix = `n${randomBytes(4).toString('hex')}`;
  const people = [];
  for (let i = 0; i < 8; i++) {
    const kind = i === 7 ? 'teacher' : 'student';
    const userName = `${prefix}.${kind}.${i}`;
    const person = await admin.call('/people', 'POST', { kind, name: `配额测试${i}`, userName, number: `${prefix}-${i}`, password, role: kind === 'teacher' ? 'Teacher' : 'Student' });
    people.push({ personId: person.id, userName, kind });
  }
  const defaults = await admin.call('/nodes', 'POST', { name: `${prefix}默认配额节点`, provider: 'simulated' });
  assert.equal(defaults.accountQuota, 200); assert.equal(defaults.virtualMachineQuota, 50);
  const candidates = await admin.call(`/nodes/${defaults.id}/account-candidates?pageSize=100`);
  assert.equal(candidates.total, 8); assert.ok(candidates.items.some(x => x.kind === 'teacher')); assert.ok(candidates.items.some(x => x.kind === 'student'));
  assert.ok(!candidates.items.some(x => x.userId === initialAdminId));
  for (const person of people) person.userId = candidates.items.find(x => x.personId === person.personId).userId;
  await admin.call(`/nodes/${defaults.id}/accounts`, 'POST', { userIds: [initialAdminId] }, 400);
  await admin.call(`/nodes/${defaults.id}/accounts`, 'POST', { userIds: [people[0].userId, people[0].userId] }, 400);
  pass('default quotas; eligible teacher/student candidates exclude initial administrator and duplicate input');

  const nodeA = await admin.call('/nodes', 'POST', { name: `${prefix}节点A`, accountQuota: 2, virtualMachineQuota: 1 });
  const nodeB = await admin.call('/nodes', 'POST', { name: `${prefix}节点B`, accountQuota: 2, virtualMachineQuota: 1 });
  await admin.call(`/nodes/${nodeA.id}/accounts`, 'POST', { userIds: [people[0].userId] });
  await admin.call(`/nodes/${nodeB.id}/accounts`, 'POST', { userIds: [people[0].userId] });
  await admin.call(`/nodes/${nodeA.id}/accounts`, 'POST', { userIds: [people[0].userId] }, 409);
  const statusA = await admin.call(`/nodes/${nodeA.id}/status`); assert.equal(statusA.accountCount, 1); assert.equal(statusA.accountQuota, 2); assert.equal(statusA.virtualMachineQuota, 1);
  const nodeList = await admin.call(`/nodes?search=${prefix}&pageSize=100`); assert.equal(nodeList.items.find(x => x.id === nodeA.id).accountCount, 1);
  pass('same account can span nodes; node-local uniqueness and actual status/list counts');

  await admin.call(`/nodes/${nodeA.id}/accounts`, 'POST', { userIds: [people[1].userId, people[2].userId] }, 409);
  assert.equal((await admin.call(`/nodes/${nodeA.id}/accounts`)).accountCount, 1);
  await admin.call(`/nodes/${nodeA.id}/accounts`, 'POST', { userIds: [people[1].userId] });
  await admin.call(`/nodes/${nodeA.id}`, 'PUT', nodeInput(nodeA, { accountQuota: 1 }), 409);
  const disabled = await admin.call(`/nodes/${nodeA.id}/disable`, 'POST', {}); assert.equal(disabled.accountCount, 2);
  await admin.call(`/nodes/${nodeA.id}/accounts`, 'POST', { userIds: [people[2].userId] }, 409);
  await admin.call(`/nodes/${nodeA.id}/accounts/${people[1].userId}`, 'DELETE');
  assert.equal((await admin.call(`/nodes/${nodeA.id}/accounts`)).accountCount, 1);
  await admin.call(`/nodes/${nodeA.id}/enable`, 'POST', {});
  pass('atomic quota rejection, shrink protection and removal while disabled');

  const raceNode = await admin.call('/nodes', 'POST', { name: `${prefix}并发节点`, accountQuota: 3, virtualMachineQuota: 2 });
  const race = await Promise.all(people.map(person => admin.call(`/nodes/${raceNode.id}/accounts`, 'POST', { userIds: [person.userId] }, [200, 409])));
  assert.equal(race.filter(x => x.httpStatus === 200).length, 3); assert.equal(race.filter(x => x.httpStatus === 409).length, 5);
  const racedAccounts = await admin.call(`/nodes/${raceNode.id}/accounts?pageSize=100`); assert.equal(racedAccounts.total, 3); assert.equal(racedAccounts.accountCount, 3); assert.equal(new Set(racedAccounts.items.map(x => x.userId)).size, 3);
  const duplicateNode = await admin.call('/nodes', 'POST', { name: `${prefix}重复竞争节点`, accountQuota: 2 });
  const duplicateRace = await Promise.all([0, 1].map(() => admin.call(`/nodes/${duplicateNode.id}/accounts`, 'POST', { userIds: [people[6].userId] }, [200, 409])));
  assert.equal(duplicateRace.filter(x => x.httpStatus === 200).length, 1); assert.equal(duplicateRace.filter(x => x.httpStatus === 409).length, 1);
  report.concurrentAllocation = { simultaneousAccountRequests: 8, quota: 3, successes: 3, quotaRejections: 5, duplicateRaceSuccesses: 1 };
  pass('parallel account allocation never exceeds quota; simultaneous duplicate assignment is unique');

  await admin.call(`/nodes/${nodeA.id}`, 'DELETE', undefined, 409); await admin.call(`/nodes/${nodeB.id}`, 'DELETE', undefined, 409);
  await admin.call(`/people/${people[0].personId}`, 'DELETE');
  assert.equal((await admin.call(`/nodes/${nodeA.id}/accounts`)).accountCount, 0);
  assert.equal((await admin.call(`/nodes/${nodeB.id}/accounts`)).accountCount, 0);
  assert.ok(!(await admin.call(`/nodes/${nodeA.id}/account-candidates?pageSize=100`)).items.some(x => x.userId === people[0].userId));
  await admin.call(`/nodes/${nodeA.id}`, 'DELETE'); await admin.call(`/nodes/${nodeB.id}`, 'DELETE');
  await admin.call(`/nodes/${duplicateNode.id}/accounts/${people[6].userId}`, 'DELETE');
  assert.ok((await admin.call(`/people?kind=student&search=${people[6].userName}`)).items.some(x => x.id === people[6].personId));
  await admin.call(`/nodes/${duplicateNode.id}`, 'DELETE');
  pass('assigned nodes cannot be deleted; user deletion cascades across nodes; explicit unassignment keeps user');

  const vmNode = await admin.call('/nodes', 'POST', { name: `${prefix}虚拟机配额节点`, virtualMachineQuota: 1 });
  const vmRace = await Promise.all([0, 1].map(i => admin.call('/virtual-machines', 'POST', { name: `${prefix}配额虚拟机${i}`, nodeId: vmNode.id, cpuCount: 1, memoryMb: 512, diskGb: 1 }, [200, 409])));
  assert.equal(vmRace.filter(x => x.httpStatus === 200).length, 1); assert.equal(vmRace.filter(x => x.httpStatus === 409).length, 1);
  const admittedVm = vmRace.find(x => x.httpStatus === 200);
  assert.equal((await admin.call(`/nodes/${vmNode.id}/status`)).virtualMachineCount, 1);
  await admin.call(`/nodes/${vmNode.id}`, 'PUT', nodeInput(vmNode, { virtualMachineQuota: 0 }), 409);
  const destination = await admin.call('/nodes', 'POST', { name: `${prefix}迁入目标节点`, virtualMachineQuota: 0 });
  await admin.call(`/virtual-machines/${admittedVm.id}`, 'PUT', { name: admittedVm.name, nodeId: destination.id, cpuCount: 1, memoryMb: 512, diskGb: 1 }, 409);
  assert.equal((await admin.call(`/virtual-machines?nodeId=${vmNode.id}`)).total, 1);
  await admin.call(`/virtual-machines/${admittedVm.id}`, 'DELETE');
  await admin.call(`/nodes/${vmNode.id}`, 'PUT', nodeInput(vmNode, { virtualMachineQuota: 0 }));
  await admin.call('/virtual-machines', 'POST', { name: '应拒绝', nodeId: vmNode.id, cpuCount: 1, memoryMb: 512, diskGb: 1 }, 409);
  report.concurrentVirtualMachines = { simultaneousCreateRequests: 2, quota: 1, successes: 1, quotaRejections: 1 };
  pass('parallel VM creation, migration admission and shrinking respect VM quotas including stopped VMs');

  await admin.call('/simulation/bootstrap', 'POST', {});
  const simulated = (await admin.call('/nodes?search=NODE-01&pageSize=100')).items.find(x => x.id === 'simulation-node');
  for (const vm of (await admin.call('/virtual-machines?nodeId=simulation-node&pageSize=100')).items) await admin.call(`/virtual-machines/${vm.id}`, 'DELETE');
  await admin.call('/nodes/simulation-node', 'PUT', nodeInput(simulated, { virtualMachineQuota: 0 }));
  const repeated = await admin.call('/simulation/bootstrap', 'POST', {}); assert.equal(repeated.skippedVirtualMachines, 2);
  assert.equal((await admin.call('/virtual-machines?nodeId=simulation-node')).total, 0);
  pass('simulation bootstrap cannot bypass a configured zero VM quota');

  const teacher = people.find(x => x.kind === 'teacher'); const teacherSession = session();
  await teacherSession.call('/auth/login', 'POST', { userName: teacher.userName, password });
  await teacherSession.call('/auth/password', 'POST', { currentPassword: password, newPassword: `Changed-${randomBytes(12).toString('hex')}Aa1!` });
  await teacherSession.call(`/nodes/${defaults.id}/accounts`, 'GET', undefined, 403);
  await teacherSession.call(`/nodes/${defaults.id}/account-candidates`, 'GET', undefined, 403);
  await teacherSession.call(`/nodes/${defaults.id}/accounts`, 'POST', { userIds: [people[1].userId] }, 403);
  pass('node account lists, candidates and assignment remain administrator-only');

  await stop(); assert.equal(report.processStopped, true); assert.equal(report.portReleased, true);
  const validation = spawnSync(executable, ['--validate-database', path.join(data, 'school.db')], { cwd: program, windowsHide: true, encoding: 'utf8', timeout: 30000 });
  assert.ifError(validation.error); assert.equal(validation.status, 0, `DatabaseSafety rejected the fresh v2 account-assignment schema: ${validation.stderr}`);
  report.databaseSafetyValidation = { exitCode: validation.status, passed: true };
  pass('fresh v3 database with quota fields and assignment FKs passes DatabaseSafety');
  report.status = 'passed';
} catch (error) { failure = error; report.status = 'failed'; report.error = error.message; }
finally {
  await stop(); if (!report.processStopped || (url && report.portReleased !== true)) { report.status = 'failed'; failure ??= new Error('Owned test service did not stop.'); }
  report.recordedAtUtc = new Date().toISOString();
  fs.mkdirSync(path.join(workspace, 'docs/verification'), { recursive: true });
  fs.writeFileSync(path.join(workspace, 'docs/verification/node-accounts-smoke.json'), `${JSON.stringify(report, null, 2)}\n`);
  console.log(JSON.stringify(report, null, 2));
}
if (failure) { console.error(failure.message); process.exitCode = 1; }
