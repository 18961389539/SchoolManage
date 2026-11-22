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
const deviceColumns = db => db.prepare('PRAGMA table_info("Devices")').all().map(column => column.name);
const currentVersion = 16;

function removeDeviceNodeAssociation(db) {
  const columns = deviceColumns(db).filter(column => column !== 'NodeId').map(name => `"${name}"`).join(', ');
  const schema = db.prepare('SELECT sql FROM sqlite_schema WHERE type = ? AND name = ?').get('table', 'Devices').sql;
  const legacySchema = schema.replace('CREATE TABLE "Devices"', 'CREATE TABLE "Devices_v4"')
    .split('\n').filter(line => !line.includes('"NodeId"')).join('\n').replace(/,\s*\)$/, '\n)');
  const indexes = db.prepare('SELECT sql FROM sqlite_schema WHERE type = ? AND tbl_name = ? AND sql IS NOT NULL')
    .all('index', 'Devices').map(row => row.sql).filter(sql => !sql.includes('"NodeId"'));
  db.exec(legacySchema);
  db.exec(`INSERT INTO "Devices_v4" (${columns}) SELECT ${columns} FROM "Devices"`);
  db.exec('DROP TABLE "Devices"; ALTER TABLE "Devices_v4" RENAME TO "Devices"');
  for (const sql of indexes) db.exec(sql);
  assert.equal(deviceColumns(db).includes('NodeId'), false);
}

let service; let failure;
try {
  service = await startService();
  const client = new Client(service.base);
  await client.request('/setup', 'POST', {schoolName:'迁移测试学校', userName:'migrationadmin', displayName:'迁移管理员', password});
  const profession = await client.request('/professions', 'POST', {name:'迁移专业', code:'MIG001'});
  const schoolClass = await client.request('/classes', 'POST', {name:'迁移一班', professionId:profession.id});
  const legacyQuestion = await client.request('/exam-questions', 'POST',
    {type:'single', stem:'已组卷的旧题目', options:['A','B'], answers:[0], score:5});
  const legacyPaper = await client.request('/exam-papers', 'POST', {name:'旧试卷', description:null, remark:null});
  await client.request(`/exam-papers/${legacyPaper.id}/questions`, 'POST', {items:[{questionId:legacyQuestion.id}]});
  await client.request('/simulation/bootstrap', 'POST', {});
  const deviceType = (await client.request('/device-types')).items[0];
  const customDevice = await client.request('/devices', 'POST', {name:'旧库自定义模拟设备', cabinetId:'simulation-cabinet', deviceTypeId:deviceType.id, connectionType:'simulated'});
  let db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.equal(classIndex(db)?.unique, 1);
  assert.equal(auditIndex(db)?.unique, 0);
  assert.ok(importColumns(db).includes('Status'));
  assert.ok(importColumns(db).includes('ProcessedCount'));
  assert.ok(deviceColumns(db).includes('NodeId'));
  db.close();
  await stopService(service); service = null;
  pass(`fresh database is created at schema version ${currentVersion} with device links, import job state and audit index`);

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
  assert.equal(status.schemaVersion, currentVersion);
  assert.equal(status.initialized, true);
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
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
  pass(`v1 database upgrades to v${currentVersion}, preserves data, creates unique and audit indexes, import job columns and a v1 pre-migration snapshot; old backups are restore-validated but current-schema validation rejects them`);

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
  assert.equal(versionOf(db), currentVersion);
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
  assert.equal(v3Status.schemaVersion, currentVersion);
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.equal(auditIndex(db)?.unique, 0);
  assert.equal(Number(db.prepare('SELECT COUNT(*) AS count FROM "Classes"').get().count), 1);
  db.close();
  pass('v3 database adds the audit time index without changing business data');

  db = openDb();
  removeDeviceNodeAssociation(db);
  db.exec('PRAGMA user_version=4');
  db.close();
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);
  service = await startService();
  assert.equal((await new Client(service.base).request('/status')).schemaVersion, currentVersion);
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.deepEqual(db.prepare('SELECT "NodeId" FROM "Devices" WHERE "Id" IN (?, ?) ORDER BY "Id"')
    .all('simulation-switch','simulation-router').map(item => item.NodeId), ['simulation-node','simulation-node']);
  assert.equal(db.prepare('SELECT "NodeId" FROM "Devices" WHERE "Id" = ?').get(customDevice.id).NodeId, null);
  assert.ok(db.prepare('PRAGMA index_list("Devices")').all().some(index => index.name === 'IX_Devices_NodeId'));
  assert.ok(db.prepare('PRAGMA foreign_key_list("Devices")').all().some(key => key.from === 'NodeId' && key.on_delete === 'SET NULL'));
  db.close();
  assert.ok((await readdir(path.join(data, 'backups'))).some(name => /^pre-migration-v4-.*\.db$/.test(name)));
  pass('v4 restore validation and migration preserve devices, link built-in simulated devices only, and preserve an upgrade snapshot');

  db = openDb();
  db.exec(`
    DROP TABLE "ClassroomCaseGrades";
    ALTER TABLE "CourseResources" DROP COLUMN "CaseRubricJson";
    ALTER TABLE "ClassroomCaseSessions" DROP COLUMN "WorkId";
    ALTER TABLE "ClassroomCaseSessions" DROP COLUMN "Revision";
    ALTER TABLE "ClassroomCaseSessions" DROP COLUMN "EnvironmentJson";
    ALTER TABLE "ClassroomCaseSessions" DROP COLUMN "RubricJson";
    PRAGMA user_version=9;
  `);
  db.close();
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);
  service = await startService();
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.ok(db.prepare('PRAGMA table_info("ClassroomCaseSessions")').all().some(column => column.name === 'EnvironmentJson'));
  assert.equal(db.prepare('SELECT COUNT(*) AS count FROM "ClassroomCaseGrades"').get().count, 0);
  assert.equal(db.prepare('SELECT "Name" FROM "Classes" WHERE "Id" = ?').get(schoolClass.id).Name, schoolClass.name);
  db.close();
  pass('v9 backup and startup migration preserve school data and add classroom environment and grade storage');

  db = openDb();
  db.exec(`
    DROP TABLE "ExamPracticalEnvironments";
    ALTER TABLE "ExamQuestions" DROP COLUMN "PracticalNodeId";
    ALTER TABLE "ExamQuestions" DROP COLUMN "PracticalRubricJson";
    ALTER TABLE "ExamPaperQuestions" DROP COLUMN "PracticalNodeId";
    ALTER TABLE "ExamPaperQuestions" DROP COLUMN "PracticalRubricJson";
    ALTER TABLE "ExamAnswers" DROP COLUMN "PracticalDetailsJson";
    PRAGMA user_version=10;
  `);
  db.close();
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);
  service = await startService();
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.ok(db.prepare('PRAGMA table_info("ExamPaperQuestions")').all().some(column => column.name === 'PracticalRubricJson'));
  assert.equal(db.prepare('SELECT COUNT(*) AS count FROM "ExamPracticalEnvironments"').get().count, 0);
  db.close();
  pass('v10 startup migration adds exam templates, student environments and automatic grading details');

  db = openDb();
  db.exec('PRAGMA foreign_keys=OFF');
  const questionSchema = db.prepare("SELECT sql FROM sqlite_schema WHERE type='table' AND name='ExamQuestions'").get().sql;
  const legacyQuestionSchema = questionSchema.replace('CREATE TABLE "ExamQuestions"', 'CREATE TABLE "ExamQuestions_v11"')
    .split('\n').filter(line => !line.includes('"BankId"')).join('\n').replace(/,\s*\)$/, '\n)');
  const questionColumns = db.prepare('PRAGMA table_info("ExamQuestions")').all()
    .filter(column => column.name !== 'BankId').map(column => `"${column.name}"`).join(', ');
  const questionIndexes = db.prepare("SELECT sql FROM sqlite_schema WHERE type='index' AND tbl_name='ExamQuestions' AND sql IS NOT NULL")
    .all().map(index => index.sql).filter(sql => !sql.includes('"BankId"'));
  db.exec(legacyQuestionSchema);
  db.exec(`INSERT INTO "ExamQuestions_v11" (${questionColumns}) SELECT ${questionColumns} FROM "ExamQuestions"`);
  db.exec('DROP TABLE "ExamQuestions"; ALTER TABLE "ExamQuestions_v11" RENAME TO "ExamQuestions"; DROP TABLE "ExamQuestionBanks"');
  for (const sql of questionIndexes) db.exec(sql);
  db.exec('PRAGMA user_version=11');
  db.close();
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);
  service = await startService();
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.equal(db.prepare('SELECT "BankId" FROM "ExamQuestions" WHERE "Id" = ?').get(legacyQuestion.id).BankId, 'default');
  assert.equal(db.prepare('SELECT "QuestionId" FROM "ExamPaperQuestions" WHERE "PaperId" = ?').get(legacyPaper.id).QuestionId, legacyQuestion.id);
  assert.throws(() => db.prepare('UPDATE "ExamQuestions" SET "BankId" = NULL WHERE "Id" = ?').run(legacyQuestion.id));
  assert.equal(db.prepare('PRAGMA foreign_key_check').all().length, 0);
  db.close();
  pass('v11 question-bank migration assigns existing questions to the default bank without changing paper references');

  db = openDb();
  db.exec(`
    DROP INDEX "IX_VirtualMachines_Uuid";
    DROP INDEX "IX_VirtualMachines_IpAddress";
    ALTER TABLE "VirtualMachines" DROP COLUMN "Uuid";
    ALTER TABLE "VirtualMachines" DROP COLUMN "IpAddress";
    PRAGMA user_version=12;
  `);
  db.close();
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);
  service = await startService();
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  const vmIdentities = db.prepare('SELECT Id, Uuid, IpAddress FROM VirtualMachines ORDER BY Id').all();
  assert.ok(vmIdentities.some(vm => vm.Id === 'simulation-vm-linux'));
  assert.ok(vmIdentities.every(vm => /^[0-9a-f-]{36}$/.test(vm.Uuid) && /^10(\.\d{1,3}){3}$/.test(vm.IpAddress)));
  assert.equal(new Set(vmIdentities.map(vm => vm.IpAddress)).size, vmIdentities.length);
  assert.equal(db.prepare('PRAGMA foreign_key_check').all().length, 0);
  db.close();
  pass('v12 migration preserves virtual machine IDs and backfills unique UUIDs and simulation addresses');

  db = openDb();
  db.exec('PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;');
  db.exec(`
    ALTER TABLE "Courses" RENAME TO "Courses_v14";
    CREATE TABLE "Courses" (
      "Id" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "Description" TEXT NOT NULL, "Type" TEXT NOT NULL,
      "Status" TEXT NOT NULL, "Remark" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "PublishedAt" TEXT NULL
    );
    INSERT INTO "Courses" SELECT "Id","Name","Description","Type","Status","Remark","CreatedAt","PublishedAt" FROM "Courses_v14";
    DROP TABLE "Courses_v14";
    CREATE UNIQUE INDEX "IX_Courses_Name" ON "Courses" ("Name");
    CREATE INDEX "IX_Courses_Status_Type" ON "Courses" ("Status","Type");
    DROP TABLE "CoreCourseSeries";
    ALTER TABLE "LearningRecords" DROP COLUMN "PracticeStartedAt";
    ALTER TABLE "PracticeAttempts" DROP COLUMN "StartedAt";
    ALTER TABLE "JobQuestions" DROP COLUMN "PracticalRubricJson";
    ALTER TABLE "JobAttempts" DROP COLUMN "EnvironmentJson";
    ALTER TABLE "JobAnswers" DROP COLUMN "PracticalDetailsJson";
    INSERT INTO "Courses" ("Id","Name","Description","Type","Status","Remark","CreatedAt")
      VALUES ('v13-published','兼容已发布课程','','core','published','','2026-01-01'),
             ('v13-draft','兼容草稿课程','','core','draft','','2026-01-01');
    PRAGMA user_version=13;
  `);
  db.exec('PRAGMA legacy_alter_table=OFF; PRAGMA foreign_keys=ON;');
  db.close();
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);
  service = await startService();
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.ok(db.prepare('PRAGMA table_info("LearningRecords")').all().some(column => column.name === 'PracticeStartedAt' && column.notnull === 0));
  assert.ok(db.prepare('PRAGMA table_info("PracticeAttempts")').all().some(column => column.name === 'StartedAt' && column.notnull === 0));
  assert.equal(db.prepare('SELECT "IsActive" FROM "Courses" WHERE "Id" = ?').get('v13-published').IsActive, 1);
  assert.equal(db.prepare('SELECT "IsActive" FROM "Courses" WHERE "Id" = ?').get('v13-draft').IsActive, 0);
  assert.ok(db.prepare('PRAGMA foreign_key_list("Courses")').all().some(key => key.from === 'SeriesId' && key.on_delete === 'SET NULL'));
  assert.ok(db.prepare('PRAGMA table_info("JobAnswers")').all().some(column => column.name === 'PracticalDetailsJson'));
  db.close();
  pass('v13 migration preserves published course activation and adds series, exercise timestamps and camp grading storage');

  db = openDb();
  db.exec(`
    DROP INDEX "IX_ExamPracticalEnvironments_VmUuid";
    ALTER TABLE "ExamPracticalEnvironments" DROP COLUMN "VmUuid";
    ALTER TABLE "ExamPracticalEnvironments" DROP COLUMN "DeploymentStatus";
    ALTER TABLE "ExamPracticalEnvironments" DROP COLUMN "DeploymentMessage";
    ALTER TABLE "CourseResources" DROP COLUMN "TopologyBlueprintJson";
    ALTER TABLE "ClassroomCaseSessions" DROP COLUMN "TopologyBlueprintJson";
    DROP TABLE "CaseTopologyTemplates";
    PRAGMA user_version=14;
  `);
  db.close();
  runApp(['--validate-backup', path.join(data, 'school.db'), path.join(data, 'keys')]);
  service = await startService();
  await stopService(service); service = null;
  db = openDb();
  assert.equal(versionOf(db), currentVersion);
  assert.ok(db.prepare('PRAGMA table_info("ExamPracticalEnvironments")').all().some(column => column.name === 'DeploymentStatus'));
  assert.ok(db.prepare('PRAGMA index_list("ExamPracticalEnvironments")').all().some(index => index.name === 'IX_ExamPracticalEnvironments_VmUuid' && index.unique === 1));
  assert.ok(db.prepare('PRAGMA table_info("ClassroomCaseSessions")').all().some(column => column.name === 'TopologyBlueprintJson'));
  assert.ok(db.prepare('PRAGMA foreign_key_list("CaseTopologyTemplates")').all().some(key => key.from === 'NodeId' && key.on_delete === 'CASCADE'));
  assert.equal(db.prepare('PRAGMA foreign_key_check').all().length, 0);
  db.close();
  pass('v14 migration adds exercise topology templates and per-project deployment and VM identity fields');

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
  pass('duplicate class names block v1→v5 migration without advancing the schema version');
} catch (error) { failure = error; }
finally {
  try { await stopService(service); } catch (error) { failure ??= error; }
  const report = {
    schemaVersion: currentVersion,
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
