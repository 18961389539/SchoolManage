// Real Excel/CSV HTTP integration. Copies the built app into a fresh OS-temp workspace.
// No installed service, existing school database, frontend or source file is changed.
import assert from 'node:assert/strict';
import { spawn, execFileSync } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { cp, mkdir, mkdtemp, readFile, readdir, realpath, rm, stat, writeFile } from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { performance } from 'node:perf_hooks';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const outputFile = path.join(root, 'docs', 'verification', 'excel-smoke.json');
const sourceApp = process.env.SCHOOL_EXCEL_TEST_APP
  ? path.resolve(process.env.SCHOOL_EXCEL_TEST_APP)
  : path.join(root, 'src', 'School.Training', 'bin', 'Debug', 'net10.0');
const tempParent = await realpath(os.tmpdir());
const workspace = await mkdtemp(path.join(tempParent, 'SchoolTraining-Excel-QA-'));
const runtime = path.join(workspace, 'app');
const data = path.join(workspace, 'data');
const fixtureDirectory = path.join(workspace, 'fixtures');
const password = `ExcelQA${randomBytes(12).toString('hex')}`;
const report = {
  suite: 'excel-csv-real-http-boundaries',
  startedAt: new Date().toISOString(),
  sourceApp,
  isolatedWorkspace: workspace,
  databaseInitiallyEmpty: false,
  backendSha256: '',
  fixtures: [],
  checks: [],
  processStopped: false,
  temporaryWorkspaceRemoved: false,
  passed: false
};
let child;
let serviceOutput = '';
let failure;

const xml = value => String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;');
const cell = (reference, value) => `<c r="${reference}" t="inlineStr"><is><t xml:space="preserve">${xml(value)}</t></is></c>`;
function columnName(index) {
  let result = '';
  while (index > 0) { index--; result = String.fromCharCode(65 + index % 26) + result; index = Math.floor(index / 26); }
  return result;
}
function normalRows(rows) {
  return rows.map((values, index) => `<row r="${index + 1}">${values.map((value, col) => cell(`${columnName(col + 1)}${index + 1}`, value)).join('')}</row>`).join('');
}
function workbookEntries(sheetRows) {
  return [
    { name: '[Content_Types].xml', content: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>' },
    { name: '_rels/.rels', content: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>' },
    { name: 'xl/workbook.xml', content: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><bookViews><workbookView/></bookViews><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>' },
    { name: 'xl/_rels/workbook.xml.rels', content: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>' },
    { name: 'xl/styles.xml', content: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>' },
    { name: 'xl/worksheets/sheet1.xml', content: `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>${sheetRows}</sheetData></worksheet>` }
  ];
}

const compressionScript = String.raw`
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$spec = Get-Content -LiteralPath $env:SCHOOL_EXCEL_ZIP_SPEC -Raw -Encoding UTF8 | ConvertFrom-Json
$archive = [IO.Compression.ZipFile]::Open($env:SCHOOL_EXCEL_ZIP_OUTPUT, [IO.Compression.ZipArchiveMode]::Create)
$utf8 = New-Object Text.UTF8Encoding($false)
try {
  foreach ($item in $spec.entries) {
    $entry = $archive.CreateEntry([string]$item.name, [IO.Compression.CompressionLevel]::Optimal)
    $writer = New-Object IO.StreamWriter($entry.Open(), $utf8)
    try {
      if ($item.PSObject.Properties['repeatLength']) {
        [long]$remaining = $item.repeatLength
        $chunk = 'A' * 65536
        while ($remaining -gt 0) { $length = [int][Math]::Min(65536, $remaining); $writer.Write($chunk.Substring(0, $length)); $remaining -= $length }
      } else { $writer.Write([string]$item.content) }
    } finally { $writer.Dispose() }
  }
} finally { $archive.Dispose() }
`;

async function makeXlsx(name, entries) {
  const spec = path.join(fixtureDirectory, `${name}.json`);
  const output = path.join(fixtureDirectory, `${name}.xlsx`);
  await writeFile(spec, JSON.stringify({ entries }));
  execFileSync(path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'), [
    '-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(compressionScript, 'utf16le').toString('base64')
  ], { windowsHide: true, timeout: 30000, env: { ...process.env, SCHOOL_EXCEL_ZIP_SPEC: spec, SCHOOL_EXCEL_ZIP_OUTPUT: output }, stdio: 'pipe' });
  const info = await stat(output);
  const expandedBytes = entries.reduce((sum, item) => sum + (item.repeatLength || Buffer.byteLength(item.content || '')), 0);
  report.fixtures.push({ name: path.basename(output), bytes: info.size, zipEntries: entries.length, expandedBytes });
  return output;
}

async function freePort() {
  const server = net.createServer();
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}
async function start() {
  const base = `http://127.0.0.1:${await freePort()}`;
  child = spawn(path.join(runtime, 'School.Training.exe'), ['--urls', base], {
    cwd: runtime,
    env: { ...process.env, SCHOOL_DATA_DIR: data, ASPNETCORE_ENVIRONMENT: 'Production' },
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  child.stdout.on('data', buffer => { serviceOutput = (serviceOutput + buffer).slice(-60000); });
  child.stderr.on('data', buffer => { serviceOutput = (serviceOutput + buffer).slice(-60000); });
  let spawnError;
  child.once('error', error => { spawnError = error; });
  for (let attempt = 0; attempt < 150; attempt++) {
    if (spawnError) throw spawnError;
    if (child.exitCode !== null) throw Error(`Isolated server exited: ${serviceOutput}`);
    try { const result = await fetch(`${base}/api/status`, { signal: AbortSignal.timeout(2000) }); if (result.ok) return base; } catch { }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw Error(`Isolated server failed to become ready: ${serviceOutput}`);
}
class Client {
  constructor(base) { this.base = base; this.cookies = new Map(); this.csrf = ''; }
  async request(route, method = 'GET', input, expected = 200) {
    const headers = { Cookie: [...this.cookies].map(([key, value]) => `${key}=${value}`).join('; ') };
    if (this.csrf) headers['X-CSRF-TOKEN'] = this.csrf;
    let body;
    if (input instanceof FormData) body = input;
    else if (input !== undefined) { headers['Content-Type'] = 'application/json'; body = JSON.stringify(input); }
    const started = performance.now();
    const response = await fetch(`${this.base}/api${route}`, { method, headers, body, signal: AbortSignal.timeout(20000) });
    for (const cookie of response.headers.getSetCookie()) { const first = cookie.split(';')[0]; const split = first.indexOf('='); this.cookies.set(first.slice(0, split), first.slice(split + 1)); }
    const type = response.headers.get('content-type') || '';
    assert.ok(type.includes('json'), `${method} ${route} must return JSON, got ${type}`);
    const result = await response.json();
    assert.equal(response.status, expected, `${method} ${route}: ${JSON.stringify(result).slice(0, 500)}`);
    if (result.csrfToken) this.csrf = result.csrfToken;
    return { body: result, status: response.status, durationMs: Math.round(performance.now() - started) };
  }
}
async function uploadFile(client, file, kind = 'student', expected = 200) {
  const form = new FormData(); form.append('file', new Blob([await readFile(file)]), path.basename(file));
  return client.request(`/imports/preview?kind=${kind}`, 'POST', form, expected);
}
async function waitForImport(client, batchId) {
  const started = Date.now(); const observed = []; let last;
  while (Date.now() - started < 120000) {
    last = (await client.request(`/imports/${batchId}`)).body;
    const sample = { status: last.status, processedCount: last.processedCount, totalCount: last.totalCount };
    if (!observed.length || JSON.stringify(observed.at(-1)) !== JSON.stringify(sample)) observed.push(sample);
    if (last.status === 'completed') return { ...last, observedStates: observed };
    if (last.status === 'failed' || last.status === 'cancelled') throw Error(last.failureMessage || `Import ended as ${last.status}`);
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw Error(`Import ${batchId} did not finish within 120 seconds: ${JSON.stringify(last)}`);
}
async function check(name, action) {
  const started = performance.now();
  try {
    const evidence = await action();
    report.checks.push({ name, passed: true, durationMs: Math.round(performance.now() - started), ...evidence });
    console.log(`PASS ${name}`);
  } catch (error) {
    report.checks.push({ name, passed: false, durationMs: Math.round(performance.now() - started), error: error.message });
    throw error;
  }
}
async function fastReject(client, file, messagePattern) {
  const result = await uploadFile(client, file, 'student', 400);
  assert.match(result.body.message, messagePattern);
  assert.ok(result.durationMs < 5000, `Boundary rejection took ${result.durationMs} ms`);
  assert.equal((await client.request('/status')).body.initialized, true);
  return { responseStatus: result.status, rejectionMs: result.durationMs, message: result.body.message, maxAllowedMs: 5000 };
}

try {
  await mkdir(fixtureDirectory);
  await mkdir(data);
  assert.equal((await readdir(data)).length, 0);
  report.databaseInitiallyEmpty = true;
  assert.ok((await stat(path.join(sourceApp, 'School.Training.exe'))).isFile(), 'Build the backend first');
  await cp(sourceApp, runtime, { recursive: true });
  report.backendSha256 = createHash('sha256').update(await readFile(path.join(runtime, 'School.Training.dll'))).digest('hex');

  const studentHeader = ['姓名', '账号', '学号', '班级', '性别', '手机号', '电子邮箱'];
  const teacherHeader = ['姓名', '账号', '工号', '班级', '性别', '手机号', '电子邮箱'];
  const studentFile = await makeXlsx('valid-students', workbookEntries(normalRows([
    studentHeader,
    ['张明', 'excel_student_01', '2026S001', 'Excel验收班', '男', '13800000001', 'student01@example.test'],
    ['李华', 'excel_student_02', '2026S002', 'Excel验收班', '女', '', 'student02@example.test']
  ])));
  const teacherFile = await makeXlsx('valid-teachers', workbookEntries(normalRows([
    teacherHeader, ['陈老师', 'excel_teacher_01', 'T2026001', 'Excel验收班', '女', '13800000002', 'teacher01@example.test']
  ])));
  const sparseRowFile = await makeXlsx('sparse-last-row', workbookEntries(`<row r="1048576">${cell('A1048576', '姓名')}</row>`));
  const sparseColumnFile = await makeXlsx('sparse-last-column', workbookEntries(`<row r="1">${cell('XFD1', '姓名')}</row>`));
  const entryCountFile = await makeXlsx('too-many-zip-entries', [...workbookEntries(normalRows([studentHeader])), ...Array.from({ length: 1995 }, (_, index) => ({ name: `extra/${index}.txt`, content: '' }))]);
  const expandedFile = await makeXlsx('expanded-over-64mb', [...workbookEntries(normalRows([studentHeader])), { name: 'extra/repeated.txt', repeatLength: 65 * 1024 * 1024 }]);
  const corruptFile = path.join(fixtureDirectory, 'corrupt.xlsx'); await writeFile(corruptFile, Buffer.from('PK\x03\x04not-a-valid-zip'));
  const csvFile = path.join(fixtureDirectory, 'quoted-students.csv');
  const csvName = '带"昵称",学生\r\n第二行';
  const csv = '\uFEFF姓名,账号,学号,班级,性别,手机号,电子邮箱\r\n' + `"${csvName.replaceAll('"', '""')}",csv_quoted_01,CSV2026001,Excel验收班,女,,quoted@example.test\r\n`;
  await writeFile(csvFile, csv);

  const client = new Client(await start());
  assert.equal((await client.request('/status')).body.initialized, false);
  await client.request('/setup', 'POST', { schoolName: 'Excel独立验收学校', userName: 'exceladmin', displayName: 'Excel测试管理员', password });
  assert.ok(client.csrf, 'Setup must return a CSRF token');
  const profession = (await client.request('/professions', 'POST', { name: 'Excel测试专业', code: 'EXCEL-QA', remark: '' })).body;
  await client.request('/classes', 'POST', { name: 'Excel验收班', professionId: profession.id, remark: '' });

  await check('真实 .xlsx 学生预览、字段解析、提交及账号创建', async () => {
    const preview = (await uploadFile(client, studentFile)).body;
    assert.equal(preview.validCount, 2); assert.equal(preview.errorCount, 0);
    assert.equal(preview.rows[0].rowNumber, 2); assert.equal(preview.rows[0].data.name, '张明');
    assert.equal(preview.rows[0].data.number, '2026S001'); assert.equal(preview.rows[0].data.className, 'Excel验收班');
    const queued = await client.request(`/imports/${preview.batchId}/commit`, 'POST', { removeInvalidRows: false, initialPassword: password }, 202);
    assert.equal(queued.body.status, 'queued');
    const commit = await waitForImport(client, preview.batchId);
    assert.equal(commit.importedCount, 2);
    const people = (await client.request('/people?kind=student')).body;
    assert.equal(people.total, 2); assert.ok(people.items.every(person => person.role === 'Student' && person.className === 'Excel验收班'));
    const student = new Client(client.base);
    const session = (await student.request('/auth/login', 'POST', { userName: 'excel_student_01', password })).body;
    assert.equal(session.user.role, 'Student'); assert.equal(session.user.mustChangePassword, true);
    return { validRows: 2, importedCount: commit.importedCount, firstRowNumber: preview.rows[0].rowNumber, accountRole: session.user.role, mustChangePassword: session.user.mustChangePassword, observedImportStates: commit.observedStates };
  });
  await check('真实 .xlsx 教师预览、工号关联、提交及账号创建', async () => {
    const preview = (await uploadFile(client, teacherFile, 'teacher')).body;
    assert.equal(preview.validCount, 1); assert.equal(preview.errorCount, 0); assert.equal(preview.rows[0].data.number, 'T2026001');
    await client.request(`/imports/${preview.batchId}/commit`, 'POST', { removeInvalidRows: false, initialPassword: password }, 202);
    const commit = await waitForImport(client, preview.batchId);
    assert.equal(commit.importedCount, 1);
    const people = (await client.request('/people?kind=teacher')).body;
    assert.equal(people.total, 1); assert.equal(people.items[0].number, 'T2026001');
    const teacher = new Client(client.base);
    const session = (await teacher.request('/auth/login', 'POST', { userName: 'excel_teacher_01', password })).body;
    assert.equal(session.user.role, 'Teacher'); assert.equal(session.user.mustChangePassword, true);
    return { validRows: 1, importedCount: commit.importedCount, teacherNumber: people.items[0].number, accountRole: session.user.role };
  });
  await check('稀疏工作表第1048576行快速拒绝且服务继续可用', () => fastReject(client, sparseRowFile, /前10001行.*30列/));
  await check('稀疏工作表第16384列快速拒绝且服务继续可用', () => fastReject(client, sparseColumnFile, /前10001行.*30列/));
  await check('损坏 .xlsx 返回可读400错误', () => fastReject(client, corruptFile, /无法读取文件/));
  await check('超过2000个ZIP条目的 .xlsx 在解析前拒绝', () => fastReject(client, entryCountFile, /64MB|结构过于复杂/));
  await check('解压超过64MB的压缩 .xlsx 在解析前拒绝', () => fastReject(client, expandedFile, /64MB|结构过于复杂/));
  await check('CSV引号、逗号、双引号转义与单元格内换行保真', async () => {
    const preview = (await uploadFile(client, csvFile)).body;
    assert.equal(preview.validCount, 1); assert.equal(preview.errorCount, 0);
    assert.equal(preview.rows[0].data.name, csvName); assert.equal(preview.rows[0].rowNumber, 2);
    await client.request(`/imports/${preview.batchId}/commit`, 'POST', { removeInvalidRows: false, initialPassword: password }, 202);
    const commit = await waitForImport(client, preview.batchId);
    assert.equal(commit.importedCount, 1);
    const people = (await client.request('/people?kind=student&search=csv_quoted_01')).body;
    assert.equal(people.total, 1); assert.equal(people.items[0].name, csvName);
    return { importedCount: commit.importedCount, embeddedComma: true, escapedQuote: true, embeddedCrLf: true, originalRowNumber: preview.rows[0].rowNumber };
  });
  report.passed = true;
} catch (error) {
  failure = error;
  report.error = error.message;
  process.exitCode = 1;
} finally {
  if (child && child.exitCode === null) {
    child.kill();
    await Promise.race([new Promise(resolve => child.once('exit', resolve)), new Promise(resolve => setTimeout(resolve, 5000))]);
  }
  report.processStopped = !child || child.exitCode !== null || child.signalCode !== null;
  if (!report.processStopped) { report.passed = false; report.error = `${report.error || ''} Isolated server did not stop.`.trim(); process.exitCode = 1; }
  if (report.processStopped) {
    const resolved = await realpath(workspace);
    assert.equal(path.dirname(resolved).toLowerCase(), tempParent.toLowerCase(), 'Refusing unsafe temp cleanup');
    assert.ok(path.basename(resolved).startsWith('SchoolTraining-Excel-QA-'), 'Refusing non-test directory cleanup');
    await rm(resolved, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 });
    report.temporaryWorkspaceRemoved = true;
  }
  report.completedAt = new Date().toISOString();
  report.passedChecks = report.checks.filter(item => item.passed).length;
  await mkdir(path.dirname(outputFile), { recursive: true });
  await writeFile(outputFile, JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify({ passed: report.passed, passedChecks: report.passedChecks, processStopped: report.processStopped, evidence: outputFile }, null, 2));
}
if (failure) console.error(failure.stack);
