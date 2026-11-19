// Starts an isolated copy of the built product, runs the existing infrastructure
// integration suite against it, and records the evidence report.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash, randomBytes} from 'node:crypto';
import {existsSync, readFileSync} from 'node:fs';
import {mkdir, writeFile} from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const appDirectory = path.resolve(process.env.SCHOOL_APP_DIR ?? path.join(root, 'src/School.Training/bin/Debug/net10.0'));
const appExecutable = path.join(appDirectory, process.platform === 'win32' ? 'School.Training.exe' : 'School.Training');
const appDll = path.join(appDirectory, 'School.Training.dll');
const runDirectory = path.join(root, '.local', `infrastructure-selfhost-${Date.now()}`);
const data = path.join(runDirectory, 'data');
const password = `InfraQA${randomBytes(12).toString('hex')}`;
const userName = 'infraadmin';
let service; let failure; let parsed; let baseUrl = '';
await mkdir(runDirectory, {recursive:true});
const sha = file => createHash('sha256').update(readFileSync(file)).digest('hex').toUpperCase();
async function freePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = server.address().port; await new Promise(resolve => server.close(resolve)); return port;
}
try {
  const base = baseUrl = `http://127.0.0.1:${await freePort()}`;
  const useExe = existsSync(appExecutable);
  service = spawn(useExe ? appExecutable : 'dotnet', useExe ? ['--urls', base] : [appDll, '--urls', base], {
    cwd: appDirectory,
    env: {...process.env, SCHOOL_DATA_DIR:data, ASPNETCORE_ENVIRONMENT:'Production'},
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '';
  service.stdout.on('data', chunk => { output = (output + chunk).slice(-80000); });
  service.stderr.on('data', chunk => { output = (output + chunk).slice(-80000); });
  for (let attempt = 0; attempt < 200; attempt++) {
    if (service.exitCode !== null) throw new Error(`Service exited: ${output}`);
    try { const response = await fetch(`${base}/api/status`); if (response.ok) break; } catch {}
    await new Promise(resolve => setTimeout(resolve, 100));
    if (attempt === 199) throw new Error(`Service did not start: ${output}`);
  }
  const setup = await fetch(`${base}/api/setup`, {
    method: 'POST', headers: {'Content-Type':'application/json'},
    body: JSON.stringify({schoolName:'基础设施自托管测试', userName, displayName:'基础设施测试管理员', password})
  });
  assert.equal(setup.status, 200, await setup.text());

  const child = spawn(process.execPath, [path.join(root, 'tests/infrastructure-smoke.mjs')], {
    cwd: root,
    env: {...process.env, SCHOOL_TEST_URL:base, SCHOOL_TEST_PASSWORD:password, SCHOOL_TEST_USER:userName, SCHOOL_TEST_ALLOW_MUTATION:'1'},
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  let testOutput = '', testError = '';
  child.stdout.on('data', chunk => { testOutput = (testOutput + chunk).slice(-80000); });
  child.stderr.on('data', chunk => { testError = (testError + chunk).slice(-80000); });
  const exitCode = await new Promise(resolve => child.once('exit', resolve));
  assert.equal(exitCode, 0, `Infrastructure suite failed: ${testError || testOutput}`);
  const jsonStart = testOutput.lastIndexOf('\n{');
  parsed = JSON.parse(jsonStart >= 0 ? testOutput.slice(jsonStart + 1) : testOutput);
  assert.equal(parsed.status, 'passed');
} catch (error) { failure = error; }
finally {
  if (service && service.exitCode === null) {
    service.kill();
    await Promise.race([new Promise(resolve => service.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 5000))]);
  }
  const processStopped = !service || service.exitCode !== null || service.signalCode !== null;
  if (!processStopped) { failure ??= new Error('Owned infrastructure service did not stop.'); service?.kill('SIGKILL'); }
  const report = {
    recordedAtUtc: new Date().toISOString(),
    status: failure ? 'failed' : 'passed',
    testCommand: 'node tests/infrastructure-smoke.mjs',
    isolatedUrl: baseUrl,
    dataDirectory: path.relative(root, data).split(path.sep).join('/'),
    testedAssembly: path.relative(root, appDll).split(path.sep).join('/'),
    testedAssemblySha256: sha(appDll),
    testScriptSha256: sha(path.join(root, 'tests/infrastructure-smoke.mjs')),
    passedGroups: parsed?.checks ?? [],
    processStopped,
    verificationScope: 'Isolated development assembly; simulated equipment plus actual Win11 management-host inventory. No physical devices or actual hypervisor were tested.',
    error: failure?.message
  };
  await mkdir(path.join(root, 'docs/verification'), {recursive:true});
  await writeFile(path.join(root, 'docs/verification/infrastructure-smoke.json'), `${JSON.stringify(report, null, 2)}\n`);
  console.log(JSON.stringify({status:report.status, assertions:parsed?.assertions ?? 0, passedGroups:report.passedGroups.length, processStopped}, null, 2));
}
if (failure) { console.error(failure.stack); process.exitCode = 1; }
