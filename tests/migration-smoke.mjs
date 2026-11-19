// Versioned schema migration regression. Uses an isolated data directory and the
// built application; requires Node.js 22.5+ for node:sqlite.
import assert from 'node:assert/strict';
import {execFileSync, spawn} from 'node:child_process';
import {createHash, randomBytes} from 'node:crypto';
import {existsSync, readFileSync} from 'node:fs';
import {mkdir, writeFile, readdir} from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import {DatabaseSync} from 'node:sqlite';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const appDirectory = path.resolve(process.env.SCHOOL_APP_DIR ?? path.join(root, 'src/School.Training/bin/Debug/net10.0'));
const appExecutable = path.join(appDirectory, process.platform === 'win32' ? 'School.Training.exe' : 'School.Training');
const appDll = path.join(appDirectory, 'School.Training.dll');
const runApp = args => existsSync(appExecutable)
  ? execFileSync(appExecutable, args, {windowsHide:true, stdio:'pipe'})
  : execFileSync('dotnet', [appDll, ...args], {windowsHide:true, stdio:'pipe'});
const runDirectory = path.join(root, '.local', `migration-test-${Date.now()}`);
const data = path.join(runDirectory, 'data');
const password = `MigrationQA${randomBytes(12).toString('hex')}`;
const processes = [];
const checks = [];
await mkdir(runDirectory, {recursive:true});
const pass = name => { checks.push(name); console.log(`PASS ${name}`); };

async function freePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = server.address().port; await new Promise(resolve => server.close(resolve)); return port;
}
async function startService() {
  const base = `http://127.0.0.1:${await freePort()}`;
  const useExe = existsSync(appExecutable);
  const child = spawn(useExe ? appExecutable : 'dotnet', useExe ? ['--urls', base] : [appDll, '--urls', base], {
    cwd: appDirectory,
    env: {...process.env, SCHOOL_DATA_DIR:data, ASPNETCORE_ENVIRONMENT:'Production'},
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  processes.push(child);
  let output = '';
  child.stdout.on('data', chunk => { output = (output + chunk).slice(-80000); });
  child.stderr.on('data', chunk => { output = (output + chunk).slice(-80000); });
  for (let attempt = 0; attempt < 200; attempt++) {
    if (child.exitCode !== null) throw new Error(`Service exited: ${output}`);
    try { const response = await fetch(`${base}/api/status`); if (response.ok) return {base, child, output:() => output}; } catch {}
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Service did not start: ${output}`);
}
async function stopService(service) {
  if (!service || service.child.exitCode !== null) return;
  service.child.kill();
  await Promise.race([new Promise(resolve => service.child.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 5000))]);
  assert.ok(service.child.exitCode !== null || service.child.signalCode !== null, 'Owned service did not stop.');
}
class Client {
  constructor(base) { this.base = base; this.cookies = new Map(); this.csrf = ''; }
  async request(route, method = 'GET', data, expected = 200) {
    const headers = {Cookie:[...this.cookies].map(([key, value]) => `${key}=${value}`).join('; ')};
    if (data !== undefined) headers['Content-Type'] = 'application/json';
    if (this.csrf) headers['X-CSRF-TOKEN'] = this.csrf;
    const response = await fetch(`${this.base}/api${route}`, {method, headers, body:data === undefined ? undefined : JSON.stringify(data)});
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(';')[0]; const index = pair.indexOf('=');
      this.cookies.set(pair.slice(0, index), pair.slice(index + 1));
    }
    const result = await response.json();
    assert.equal(response.status, expected, `${method} ${route}: ${JSON.stringify(result).slice(0, 600)}`);
    if (result?.csrfToken) this.csrf = result.csrfToken;
    return result;
  }
}
function openDb() {
  const db = new DatabaseSync(path.join(data, 'school.db'));
  db.exec('PRAGMA busy_timeout=5000');
  return db;
}
const versionOf = db => Number(db.prepare('PRAGMA user_version').get().user_version);
const classIndex = db => db.prepare('PRAGMA index_list("Classes")').all().find(index => index.name === 'IX_Classes_ProfessionId_Name');
const auditIndex = db => db.prepare('PRAGMA index_list("Audits")').all().find(index => index.name === 'IX_Audits_At_Id');
const importColumns = db => db.prepare('PRAGMA table_info("ImportBatches")').all().map(column => column.name);

let service; let failure;
try {
  service = await startService();
  const client = new Client(service.base);
  await client.request('/setup', 'POST', {schoolName:'迁移测试学校', userName:'migrationadmin', displayName:'迁移管理员', password});
  const profession = await client.request('/professions', 'POST', {name:'迁移专业', code:'MIG001'});
  const schoolClass = await client.request('/classes', 'POST', {name:'迁移一班', professionId:profession.id});
  let db = openDb();
  assert.equal(versionOf(db), 4);
  assert.equal(classIndex(db)?.unique, 1);
  assert.equal(auditIndex(db)?.unique, 0);
  assert.ok(importColumns(db).includes('Status'));
  assert.ok(importColumns(db).includes('ProcessedCount'));
  db.close();
  await stopService(service); service = null;
  pass('fresh database is created at schema version 4 with import job state columns and an audit time index');

  db = openDb();
  db.exec('DROP INDEX "IX_Classes_ProfessionId_Name"');
  db.exec('PRAGMA user_version=1');
  assert.equal(versionOf(db), 1);
  assert.equal(classIndex(db), undefined);
  assert.equal(Number(db.prepare('SELECT COUNT(*) AS count FROM "Classes"').get().count), 1);
  db.close();
  let currentSchemaRejected=false;
  try { runApp(['--validate-database', path.join(data, 'school.db')]); } catch (error) { currentSchemaRejected=error.status===1; }
  assert.equal(currentSchemaRejected,true);
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);

  service = await startService();
  const restartedClient = new Client(service.base);
  const status = await restartedClient.request('/status');
  assert.equal(status.schemaVersion, 4);
  assert.equal(status.initialized, true);
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), 4);
  assert.equal(classIndex(db)?.unique, 1);
  assert.equal(auditIndex(db)?.unique, 0);
  assert.ok(importColumns(db).includes('Status'));
  assert.ok(importColumns(db).includes('CompletedAt'));
  const classes = db.prepare('SELECT "Name" FROM "Classes"').all();
  assert.deepEqual(classes.map(row => row.Name), [schoolClass.name]);
  db.close();
  const backups = (await readdir(path.join(data, 'backups'))).filter(name => /^pre-migration-v1-.*\.db$/.test(name));
  assert.ok(backups.length >= 1, 'Pre-migration snapshot was not created.');
  const snapshot = new DatabaseSync(path.join(data, 'backups', backups.sort().at(-1)));
  assert.equal(snapshot.prepare('PRAGMA quick_check').get().quick_check, 'ok');
  assert.equal(Number(snapshot.prepare('PRAGMA user_version').get().user_version), 1);
  snapshot.close();
  pass('v1 database upgrades to v4, preserves data, creates unique and audit indexes, import job columns and a v1 pre-migration snapshot; old backups are restore-validated but current-schema validation rejects them');

  db = openDb();
  for (const column of ['Status','RemoveInvalidRows','TotalCount','ProcessedCount','ImportedCount','RemovedCount','FailureMessage','QueuedAt','StartedAt','CompletedAt'])
    db.exec(`ALTER TABLE "ImportBatches" DROP COLUMN "${column}"`);
  db.prepare('INSERT INTO "ImportBatches" ("Id","UserId","Kind","RowsJson","CreatedAt","Committed") VALUES (?,?,?,?,?,?)')
    .run('legacy-preview', 'migration-admin', 'student', '[]', new Date().toISOString(), 0);
  db.prepare('INSERT INTO "ImportBatches" ("Id","UserId","Kind","RowsJson","CreatedAt","Committed") VALUES (?,?,?,?,?,?)')
    .run('legacy-completed', 'migration-admin', 'teacher', '[]', new Date().toISOString(), 1);
  db.exec('PRAGMA user_version=2');
  assert.equal(importColumns(db).includes('Status'), false);
  db.close();
  service = await startService();
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), 4);
  assert.ok(importColumns(db).includes('Status'));
  assert.equal(auditIndex(db)?.unique, 0);
  const legacyStatuses = db.prepare('SELECT "Id","Status" FROM "ImportBatches" WHERE "Id" LIKE ? ORDER BY "Id"').all('legacy-%');
  assert.deepEqual(legacyStatuses.map(row => [row.Id, row.Status]), [['legacy-completed','Completed'],['legacy-preview','Preview']]);
  db.close();
  pass('v2 database adds import job columns, preserves legacy batches and maps Committed to terminal status');

  db = openDb();
  db.exec('DROP INDEX "IX_Audits_At_Id"');
  db.exec('PRAGMA user_version=3');
  assert.equal(auditIndex(db), undefined);
  db.close();
  service = await startService();
  const v3Status = await new Client(service.base).request('/status');
  assert.equal(v3Status.schemaVersion, 4);
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), 4);
  assert.equal(auditIndex(db)?.unique, 0);
  assert.equal(Number(db.prepare('SELECT COUNT(*) AS count FROM "Classes"').get().count), 1);
  db.close();
  pass('v3 database adds the audit time index without changing business data');

  db = openDb();
  db.exec('DROP INDEX "IX_Classes_ProfessionId_Name"');
  db.exec('PRAGMA user_version=1');
  db.prepare('INSERT INTO "Classes" ("Id","Name","ProfessionId","Remark") VALUES (?,?,?,?)')
    .run(randomBytes(16).toString('hex'), schoolClass.name, profession.id, '重复迁移阻断');
  db.close();
  let rejected = false; let output = '';
  try { service = await startService(); } catch (error) { rejected = true; output = error.message; }
  assert.equal(rejected, true);
  assert.match(output, /同专业同名班级|InvalidOperationException/);
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), 1);
  assert.equal(classIndex(db), undefined);
  db.close();
  pass('duplicate class names block v1→v4 migration without advancing the schema version');
} catch (error) { failure = error; }
finally {
  try { await stopService(service); } catch (error) { failure ??= error; }
  const report = {
    schemaVersion: 4,
    testedAssembly: appDll,
    testedAssemblySha256: createHash('sha256').update(readFileSync(appDll)).digest('hex').toUpperCase(),
    status: failure ? 'failed' : 'passed',
    checks,
    completedAt: new Date().toISOString(),
    workspace: runDirectory,
    error: failure?.message
  };
  await mkdir(path.join(root, 'docs/verification'), {recursive:true});
  await writeFile(path.join(root, 'docs/verification/migration-smoke.json'), `${JSON.stringify(report, null, 2)}\n`);
  console.log(JSON.stringify({status:report.status, checks, workspace:runDirectory}, null, 2));
}
if (failure) { console.error(failure.stack); process.exitCode = 1; }
