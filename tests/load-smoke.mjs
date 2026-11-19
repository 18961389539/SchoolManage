// Local load regression baseline. Node built-ins only; never targets an existing service.
// Build the main project first. This script copies its output and starts its own loopback instance.
// Optional environment:
// SCHOOL_LOAD_SOURCE_DIR, SCHOOL_LOAD_READ_COUNT>=100, SCHOOL_LOAD_READ_CONCURRENCY>=20,
// SCHOOL_LOAD_WRITE_COUNT>=40, SCHOOL_LOAD_WRITE_CONCURRENCY>=8,
// SCHOOL_LOAD_IMPORT_COUNT=100..10000 (use 1000/10000 for import scale evidence).
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { performance } from 'node:perf_hooks';
import { fileURLToPath } from 'node:url';

const workspace = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const source = path.resolve(process.env.SCHOOL_LOAD_SOURCE_DIR ?? path.join(workspace, 'src', 'School.Training', 'bin', 'Debug', 'net10.0'));
const localRoot = path.join(workspace, '.local');
const evidenceDirectory = path.join(workspace, 'docs', 'verification');
const evidencePath = path.join(evidenceDirectory, 'load-smoke.json');
fs.mkdirSync(localRoot, { recursive: true });
fs.mkdirSync(evidenceDirectory, { recursive: true });
const runDirectory = fs.mkdtempSync(path.join(localRoot, 'load-smoke-'));
const runtimeDirectory = path.join(runDirectory, 'program');
const dataDirectory = path.join(runDirectory, 'data');
const serviceLog = path.join(runDirectory, 'service-output.log');
const relative = value => path.relative(workspace, value).split(path.sep).join('/');
const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const round = n => Math.round(n * 100) / 100;
function integerEnvironment(name, fallback, minimum, maximum) {
  const raw = process.env[name];
  const number = raw === undefined ? fallback : Number(raw);
  assert.ok(Number.isInteger(number) && number >= minimum && number <= maximum, `${name} must be an integer in ${minimum}..${maximum}.`);
  return number;
}
const readCount = integerEnvironment('SCHOOL_LOAD_READ_COUNT', 100, 100, 10000);
const readConcurrency = integerEnvironment('SCHOOL_LOAD_READ_CONCURRENCY', 20, 20, 100);
const writeCount = integerEnvironment('SCHOOL_LOAD_WRITE_COUNT', 40, 40, 1000);
const writeConcurrency = integerEnvironment('SCHOOL_LOAD_WRITE_CONCURRENCY', 8, 8, 32);
const importStudentCount = integerEnvironment('SCHOOL_LOAD_IMPORT_COUNT', 100, 100, 10000);
const cpuList = os.cpus();
const evidence = {
  recordedAtUtc: new Date().toISOString(), status: 'running',
  command: 'node tests/load-smoke.mjs',
  sourceDirectory: relative(source), runtimeDirectory: relative(runtimeDirectory), dataDirectory: relative(dataDirectory), serviceLog: relative(serviceLog),
  machine: { platform: process.platform, architecture: process.arch, osRelease: os.release(), logicalCpuCount: cpuList.length, availableParallelism: os.availableParallelism(), cpuModel: cpuList[0]?.model ?? null, totalMemoryBytes: os.totalmem(), nodeVersion: process.version },
  parameters: { readCount, readConcurrency, writeCount, writeConcurrency, importStudentCount, importProbeWriteCount: 8, importProbeWriteConcurrency: 4, warmupReadCount: 5, regressionP95ThresholdMs: 5000, regressionMaxThresholdMs: 15000 },
  stages: {}, integrity: {}, processStopped: false,
  limitations: [
    'A single local Windows development-host run against a single service instance and SQLite database; not a production capacity guarantee.',
    'HTTP loopback with one administrator session, a fresh small database, and no browser rendering, TLS/network delay, physical equipment, or actual hypervisor workload.',
    'Other development processes may share this machine. No claim is made about concurrent real users, sustained workloads, or other hardware.',
    'Import progress is observed through the persisted batch-status endpoint. The initial password stays in process memory and is never persisted. Password hashing finishes before the SQLite write transaction starts.'
  ]
};

let service;
let serviceExit = null;
let serviceError = null;
let serviceOutput = '';
let base = '';
const cookieJar = new Map();
let csrf = '';

function captureOutput(chunk) {
  const text = chunk.toString('utf8');
  serviceOutput += text;
  if (serviceOutput.length > 1024 * 1024) serviceOutput = serviceOutput.slice(-1024 * 1024);
  fs.appendFileSync(serviceLog, text);
}
function startService() {
  service = spawn(path.join(runtimeDirectory, process.platform === 'win32' ? 'School.Training.exe' : 'School.Training'), ['--urls', base], {
    cwd: runtimeDirectory,
    windowsHide: true,
    env: { ...process.env, SCHOOL_DATA_DIR: dataDirectory, ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production' },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  service.stdout.on('data', captureOutput); service.stderr.on('data', captureOutput);
  service.on('error', error => { serviceError = error; });
  service.on('exit', (code, signal) => { serviceExit = { code, signal }; });
}
async function port() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const number = server.address().port;
  await new Promise((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
  return number;
}
async function stopService() {
  if (!service) return;
  if (serviceExit === null) {
    service.kill('SIGTERM');
    const deadline = Date.now() + 5000;
    while (serviceExit === null && Date.now() < deadline) await pause(50);
    if (serviceExit === null) {
      service.kill('SIGKILL');
      const killDeadline = Date.now() + 5000;
      while (serviceExit === null && Date.now() < killDeadline) await pause(50);
    }
  }
  evidence.processStopped = serviceExit !== null || serviceError !== null;
  evidence.serviceExit = serviceExit;
  if (base && evidence.processStopped) {
    const address = new URL(base);
    evidence.portReleased = await new Promise(resolve => {
      const socket = net.connect({ host: '127.0.0.1', port: Number(address.port) });
      socket.once('connect', () => { socket.destroy(); resolve(false); });
      socket.once('error', () => { socket.destroy(); resolve(true); });
      socket.setTimeout(2000, () => { socket.destroy(); resolve(null); });
    });
  }
}
async function waitReady() {
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    if (serviceError) throw serviceError;
    if (serviceExit !== null) throw new Error(`Isolated service exited during startup (${serviceExit.code}). See ${relative(serviceLog)}.`);
    try { const response = await fetch(`${base}/api/status`, { signal: AbortSignal.timeout(1000) }); if (response.ok) return; } catch { }
    await pause(100);
  }
  throw new Error(`Isolated service did not become ready in 30 seconds. See ${relative(serviceLog)}.`);
}

async function request(endpoint, method = 'GET', body, timeoutMs = 30000) {
  const started = performance.now();
  const headers = { Origin: base, Cookie: [...cookieJar].map(([key, value]) => `${key}=${value}`).join('; ') };
  if (csrf) headers['X-CSRF-TOKEN'] = csrf;
  const form = body instanceof FormData;
  if (body !== undefined && !form) headers['Content-Type'] = 'application/json';
  const response = await fetch(`${base}/api${endpoint}`, { method, headers, body: body === undefined ? undefined : form ? body : JSON.stringify(body), signal: AbortSignal.timeout(timeoutMs) });
  for (const value of response.headers.getSetCookie()) { const pair = value.split(';')[0]; const index = pair.indexOf('='); cookieJar.set(pair.slice(0, index), pair.slice(index + 1)); }
  const text = await response.text();
  let result;
  try { result = JSON.parse(text); } catch { throw new Error(`${method} ${endpoint}: non-JSON response (HTTP ${response.status}).`); }
  const latencyMs = performance.now() - started;
  if (response.status !== 200 && response.status !== 202) throw new Error(`${method} ${endpoint}: HTTP ${response.status}, ${result.message ?? 'request rejected'}`);
  return { result, latencyMs, status: response.status };
}
function statistics(samples, elapsedMs, concurrency) {
  const latencies = samples.map(x => x.latencyMs).sort((a, b) => a - b);
  const quantile = fraction => latencies[Math.max(0, Math.ceil(fraction * latencies.length) - 1)] ?? null;
  return {
    requestCount: samples.length, concurrency,
    successCount: samples.filter(x => x.success).length, failureCount: samples.filter(x => !x.success).length,
    elapsedMs: round(elapsedMs), throughputRequestsPerSecond: round(samples.length * 1000 / elapsedMs),
    latencyMs: { p50: quantile(.5) === null ? null : round(quantile(.5)), p95: quantile(.95) === null ? null : round(quantile(.95)), max: latencies.length ? round(latencies.at(-1)) : null },
    quantileMethod: 'nearest-rank',
    errors: samples.filter(x => !x.success).map(x => ({ index: x.index, error: x.error }))
  };
}
async function pool(count, concurrency, operation) {
  let next = 0; const samples = new Array(count); const started = performance.now();
  await Promise.all(Array.from({ length: Math.min(count, concurrency) }, async () => {
    while (true) {
      const index = next++; if (index >= count) return;
      const callStarted = performance.now();
      try { const response = await operation(index); samples[index] = { index, success: true, latencyMs: performance.now() - callStarted, ...response }; }
      catch (error) { samples[index] = { index, success: false, latencyMs: performance.now() - callStarted, error: error.message }; }
    }
  }));
  return { samples, summary: statistics(samples, performance.now() - started, concurrency) };
}

let failure;
try {
  const sourceAssembly = path.join(source, 'School.Training.dll');
  assert.ok(fs.existsSync(sourceAssembly), 'Build src/School.Training first; this test never builds or locks root output.');
  evidence.sourceAssemblySha256 = hash(sourceAssembly);
  fs.cpSync(source, runtimeDirectory, { recursive: true, errorOnExist: true, force: false });
  evidence.testedAssemblySha256 = hash(path.join(runtimeDirectory, 'School.Training.dll'));
  assert.equal(evidence.testedAssemblySha256, evidence.sourceAssemblySha256, 'Source output changed while copying. Run after the active build finishes.');
  assert.equal(fs.existsSync(dataDirectory), false, 'Data directory must not exist before first startup.');
  base = `http://127.0.0.1:${await port()}`; evidence.isolatedUrl = base;
  console.log(`Starting an isolated load instance at ${base}`);
  startService(); await waitReady();
  const password = `Load-${randomBytes(24).toString('hex')}Aa1!`;
  const setup = await request('/setup', 'POST', { schoolName: '隔离负载回归测试', userName: 'loadadmin', displayName: '负载测试管理员', password });
  csrf = setup.result.csrfToken; assert.ok(csrf);
  const prefix = `L${randomBytes(5).toString('hex')}`;
  const seed = (await request('/professions', 'POST', { name: `${prefix}基础专业`, code: `${prefix}-SEED`, remark: '隔离测试数据' })).result;
  const schoolClass = (await request('/classes', 'POST', { name: `${prefix}导入班`, professionId: seed.id, remark: '隔离测试数据' })).result;
  for (let i = 0; i < 5; i++) await request('/professions?pageSize=1000');

  console.log(`Reading ${readCount} requests at concurrency ${readConcurrency}`);
  const reads = await pool(readCount, readConcurrency, async () => {
    const response = await request('/professions?pageSize=1000');
    assert.equal(response.result.total, 1); assert.equal(response.result.items[0].code, `${prefix}-SEED`);
    return { latencyMs: response.latencyMs };
  });
  evidence.stages.concurrentReads = reads.summary;
  assert.equal(reads.summary.failureCount, 0, 'Concurrent read failures were recorded.');

  console.log(`Creating ${writeCount} business records at concurrency ${writeConcurrency}`);
  const writes = await pool(writeCount, writeConcurrency, async index => {
    const code = `${prefix}-W-${String(index).padStart(3, '0')}`;
    const response = await request('/professions', 'POST', { name: `${prefix}并发专业${index}`, code, remark: '并发新增回归' });
    assert.equal(response.result.code, code); assert.ok(response.result.id);
    return { latencyMs: response.latencyMs, id: response.result.id, code };
  });
  evidence.stages.concurrentBusinessWrites = writes.summary;
  assert.equal(writes.summary.failureCount, 0, 'Concurrent write failures were recorded.');
  const storedWrites = (await request(`/professions?search=${encodeURIComponent(`${prefix}-W-`)}&pageSize=1000`)).result;
  assert.equal(storedWrites.total, writeCount); assert.equal(storedWrites.items.length, writeCount);
  assert.equal(new Set(storedWrites.items.map(x => x.id)).size, writeCount);
  assert.equal(new Set(storedWrites.items.map(x => x.code)).size, writeCount);
  assert.deepEqual(storedWrites.items.map(x => x.code).sort(), writes.samples.map(x => x.code).sort());
  evidence.integrity.concurrentWrites = { expectedCount: writeCount, actualCount: storedWrites.total, uniqueIds: writeCount, uniqueCodes: writeCount, matchedAllAcknowledgedWrites: true };

  const digits = String(importStudentCount).length;
  const students = Array.from({ length: importStudentCount }, (_, index) => ({ name: `${prefix}学生${index}`, userName: `${prefix.toLowerCase()}.student.${String(index).padStart(digits, '0')}`, number: `${prefix}-S-${String(index).padStart(digits, '0')}` }));
  const csv = ['name,userName,number,className,gender,phone,email', ...students.map(student => `${student.name},${student.userName},${student.number},${schoolClass.name},,,`)].join('\r\n');
  const form = new FormData(); form.append('file', new Blob([csv], { type: 'text/csv;charset=utf-8' }), 'load-students.csv');
  const preview = await request('/imports/preview?kind=student', 'POST', form);
  assert.equal(preview.result.validCount, importStudentCount); assert.equal(preview.result.errorCount, 0);
  console.log(`Importing ${importStudentCount} students in the background while unrelated CRUD writes run`);
  const importStarted = performance.now(); const importTimeout = Math.max(180000, importStudentCount * 1000); let importSettled = false;
  let importOutcome; const observedImportStates = [];
  const commit = (async () => {
    const queued = await request(`/imports/${preview.result.batchId}/commit`, 'POST', { removeInvalidRows: false, initialPassword: `Import-${randomBytes(16).toString('hex')}Aa1!` }, 180000);
    if (queued.status !== 202) throw new Error(`Import commit returned ${queued.status}, expected 202`);
    while (performance.now() - importStarted < importTimeout) {
      const status = await request(`/imports/${preview.result.batchId}`);
      const sample = { status: status.result.status, processedCount: status.result.processedCount, totalCount: status.result.totalCount, elapsedMs: round(performance.now() - importStarted) };
      if (!observedImportStates.length || observedImportStates.at(-1).status !== sample.status || observedImportStates.at(-1).processedCount !== sample.processedCount) observedImportStates.push(sample);
      if (status.result.status === 'completed') return { success: true, result: status.result, latencyMs: sample.elapsedMs };
      if (status.result.status === 'failed' || status.result.status === 'cancelled') throw new Error(status.result.failureMessage || `Import ended as ${status.result.status}`);
      await pause(100);
    }
    throw new Error(`Import did not finish within ${importTimeout} ms`);
  })().then(value => { importOutcome = value; return value; }, error => { importOutcome = { success: false, error: error.message }; return importOutcome; })
    .finally(() => { importSettled = true; });
  await pause(250);
  const pendingAtProbeDispatch = !importSettled;
  const probes = await pool(8, 4, async index => {
    const startedWhilePending = !importSettled;
    const response = await request('/professions', 'POST', { name: `${prefix}导入期间新增${index}`, code: `${prefix}-I-${index}`, remark: '验证密码哈希不持有长写事务' });
    return { latencyMs: response.latencyMs, id: response.result.id, startedWhileImportPending: startedWhilePending, completedWhileImportPending: !importSettled, completedAfterImportStartMs: round(performance.now() - importStarted) };
  });
  await commit;
  evidence.stages.studentImportWithConcurrentWrites = {
    studentCount: importStudentCount, previewLatencyMs: round(preview.latencyMs),
    commitLatencyMs: importOutcome?.success ? round(importOutcome.latencyMs) : null,
    importResult: importOutcome?.success ? importOutcome.result : { error: importOutcome?.error ?? 'unknown failure' },
    observedImportStates,
    pendingAtProbeDispatch,
    writesStartedWhileImportPending: probes.samples.filter(x => x.startedWhileImportPending).length,
    writesCompletedWhileImportPending: probes.samples.filter(x => x.completedWhileImportPending).length,
    writeProbes: probes.summary,
    writeSamples: probes.samples.map(x => ({ index: x.index, success: x.success, latencyMs: round(x.latencyMs), startedWhileImportPending: x.startedWhileImportPending ?? false, completedWhileImportPending: x.completedWhileImportPending ?? false, completedAfterImportStartMs: x.completedAfterImportStartMs ?? null, error: x.error ?? null })),
    regressionThresholds: { p95MsAtMost: 5000, maxMsAtMost: 15000 }
  };
  assert.equal(importOutcome?.success, true, `Student import failed: ${importOutcome?.error}`);
  assert.equal(importOutcome.result.importedCount, importStudentCount);
  assert.equal(importOutcome.result.processedCount, importStudentCount);
  assert.equal(probes.summary.failureCount, 0, 'CRUD writes failed while importing.');
  assert.equal(pendingAtProbeDispatch, true, 'Import ended before write probes began; overlap was not verified.');
  assert.ok(probes.samples.some(x => x.completedWhileImportPending), 'All unrelated writes waited for import completion; hashing may hold a long SQLite write lock.');
  assert.ok(probes.summary.latencyMs.p95 <= 5000, `Import-overlap CRUD p95 exceeded the local 5-second regression threshold: ${probes.summary.latencyMs.p95}ms.`);
  assert.ok(probes.summary.latencyMs.max <= 15000, `Import-overlap CRUD max exceeded the local 15-second regression threshold: ${probes.summary.latencyMs.max}ms.`);
  const importedHeaders = (await request(`/people?kind=student&search=${encodeURIComponent(prefix)}&pageSize=1000`)).result;
  const importedItems = [...importedHeaders.items];
  for (let pageNumber = 2; importedItems.length < importedHeaders.total; pageNumber++) {
    const next = (await request(`/people?kind=student&search=${encodeURIComponent(prefix)}&pageSize=1000&page=${pageNumber}`)).result;
    assert.ok(next.items.length > 0, `Pagination stopped at ${importedItems.length} of ${importedHeaders.total}`);
    importedItems.push(...next.items);
  }
  assert.equal(importedHeaders.total, importStudentCount); assert.equal(importedItems.length, importStudentCount);
  assert.equal(new Set(importedItems.map(x => x.id)).size, importStudentCount);
  assert.equal(new Set(importedItems.map(x => x.userName)).size, importStudentCount);
  assert.equal(new Set(importedItems.map(x => x.number)).size, importStudentCount);
  assert.deepEqual(importedItems.map(x => x.userName).sort(), students.map(x => x.userName).sort());
  assert.deepEqual(importedItems.map(x => x.number).sort(), students.map(x => x.number).sort());
  assert.ok(importedItems.every(x => x.classId === schoolClass.id));
  const storedProbes = (await request(`/professions?search=${encodeURIComponent(`${prefix}-I-`)}&pageSize=1000`)).result;
  assert.equal(storedProbes.total, 8); assert.equal(new Set(storedProbes.items.map(x => x.code)).size, 8);
  evidence.integrity.studentImport = { expectedCount: importStudentCount, actualCount: importedHeaders.total, uniqueIds: importStudentCount, uniqueAccounts: importStudentCount, uniqueNumbers: importStudentCount, matchedAllInputAccountsAndNumbers: true, correctClassAssociation: true, pagesVerified: Math.ceil(importedItems.length / 1000) };
  evidence.integrity.importOverlapWrites = { expectedCount: 8, actualCount: storedProbes.total, uniqueCodes: 8 };
  evidence.status = 'passed';
} catch (error) {
  failure = error;
  evidence.status = 'failed'; evidence.error = error.message;
} finally {
  try { await stopService(); } catch (error) { evidence.stopError = error.message; evidence.status = 'failed'; failure ??= error; }
  if (!evidence.processStopped || evidence.portReleased !== true) { evidence.status = 'failed'; failure ??= new Error('The owned isolated service did not stop or release its port.'); }
  evidence.recordedAtUtc = new Date().toISOString();
  fs.writeFileSync(evidencePath, `${JSON.stringify(evidence, null, 2)}\n`);
  console.log(JSON.stringify(evidence, null, 2));
}
if (failure) { console.error(failure.message); process.exitCode = 1; }
