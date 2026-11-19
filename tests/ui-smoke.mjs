// Optional UI regression: 10k-row import preview paging, invalid-only filter, and the service-error boot screen.
// Not part of the release gate: needs a Chromium/Edge binary and playwright-core. Usage:
//   node tests/ui-smoke.mjs
// Environment: SCHOOL_APP_DIR (app output folder), CHROME_PATH (browser binary), E2E_KEEP=1 (keep the temp workspace).
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash} from 'node:crypto';
import {existsSync} from 'node:fs';
import {mkdir, readFile, readdir, rm, writeFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import net from 'node:net';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const appDirectory = path.resolve(process.env.SCHOOL_APP_DIR ?? path.join(root, 'src/School.Training/bin/Debug/net10.0'));
const appExecutable = path.join(appDirectory, process.platform === 'win32' ? 'School.Training.exe' : 'School.Training');
const appDll = path.join(appDirectory, 'School.Training.dll');
const runDirectory = path.join(root, '.local', `ui-test-${Date.now()}`);
const reportPath = path.join(root, 'docs', 'verification', 'ui-smoke.json');
const password = 'QaPassw0rd2026';

function loadPlaywright() {
  for (const candidate of ['playwright-core', path.join(root, '.local/e2e/node_modules/playwright-core')]) {
    try { return require(candidate); } catch { /* try next */ }
  }
  console.error('未找到 playwright-core。可在项目根目录执行：npm install --no-save playwright-core');
  process.exit(2);
}

async function findBrowser() {
  if (process.env.CHROME_PATH) return process.env.CHROME_PATH;
  const playwrightRoot = path.join(process.env.LOCALAPPDATA ?? '', 'ms-playwright');
  try {
    for (const entry of await readdir(playwrightRoot)) {
      if (!entry.startsWith('chromium-')) continue;
      const candidate = path.join(playwrightRoot, entry, 'chrome-win64', 'chrome.exe');
      if (existsSync(candidate)) return candidate;
    }
  } catch { /* fall through to Edge */ }
  const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
  if (existsSync(edge)) return edge;
  console.error('未找到 Chromium/Edge。请设置 CHROME_PATH 指向 Chrome 或 Edge 可执行文件。');
  process.exit(2);
}

async function freePort() {
  const server = net.createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}

const csv = ['姓名,账号,学号,班级,性别,手机号,电子邮箱'];
for (let index = 1; index <= 10000; index++) csv.push(`学生${index},stu${String(index).padStart(5, '0')},${20260000 + index},,,,${index % 97 === 0 ? 'bad-email' : ''}`);

let child; let browser;
const passed = [];
const mark = name => { passed.push(name); console.log(`PASS ${name}`); };
await mkdir(runDirectory, {recursive: true});
await writeFile(path.join(runDirectory, 'students-10000.csv'), '\uFEFF' + csv.join('\r\n') + '\r\n');
const base = `http://127.0.0.1:${await freePort()}`;
const report = {status: 'running', base, workspace: runDirectory};

try {
  const useExe = existsSync(appExecutable);
  const contentRoot = existsSync(path.join(appDirectory, 'wwwroot', 'index.html')) ? appDirectory : path.join(root, 'src/School.Training');
  child = spawn(useExe ? appExecutable : 'dotnet', useExe ? ['--urls', base] : [appDll, '--urls', base], {
    cwd: contentRoot,
    env: {...process.env, SCHOOL_DATA_DIR: path.join(runDirectory, 'data'), ASPNETCORE_ENVIRONMENT: 'Production'},
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '';
  child.stdout.on('data', chunk => { output = (output + chunk).slice(-40000); });
  child.stderr.on('data', chunk => { output = (output + chunk).slice(-40000); });
  let ready = false;
  for (let attempt = 0; attempt < 150; attempt++) {
    if (child.exitCode !== null) throw new Error(`service exited: ${output}`);
    try { if ((await fetch(`${base}/api/status`)).ok) { ready = true; break; } } catch { /* keep waiting */ }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (!ready) throw new Error(`service did not start: ${output}`);

  const {chromium} = loadPlaywright();
  browser = await chromium.launch({executablePath: await findBrowser(), headless: true});
  const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
  const consoleErrors = [];
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });

  await page.goto(base, {waitUntil: 'domcontentloaded'});
  await page.waitForSelector('.auth-form-wrap', {timeout: 30000});
  await page.fill('input[autocomplete="organization"]', '界面回归学校');
  await page.fill('input[autocomplete="name"]', '界面回归管理员');
  const passwords = page.locator('.auth-form-wrap input[type="password"]');
  await passwords.nth(0).fill(password);
  await passwords.nth(1).fill(password);
  await page.click('button.auth-submit');
  await page.waitForSelector('.app-layout', {timeout: 60000});
  mark('首次初始化并进入工作台');

  await page.locator('.nav-group button', {hasText: '学生管理'}).click();
  await page.waitForSelector('h1:has-text("学生")', {timeout: 30000});
  await page.click('button:has-text("批量导入")');
  await page.waitForSelector('.import-dialog', {timeout: 30000});
  const started = Date.now();
  await page.setInputFiles('input[type="file"]', path.join(runDirectory, 'students-10000.csv'));
  await page.waitForSelector('.import-pager', {timeout: 180000});
  await page.waitForTimeout(600);
  report.previewMilliseconds = Date.now() - started;
  report.summary = (await page.locator('.import-summary').innerText()).replace(/\s+/g, ' ').trim();
  report.pager = (await page.locator('.import-pager').innerText()).replace(/\s+/g, ' ').trim();
  const rows = page.locator('.el-table__body tbody tr');
  assert.equal(await rows.count(), 50, '导入预览首屏行数');
  assert.equal((await rows.first().locator('td').first().innerText()).trim(), '2');
  await page.locator('.import-pager .btn-next').click();
  await page.waitForTimeout(400);
  assert.equal((await rows.first().locator('td').first().innerText()).trim(), '52');
  assert.match(await page.locator('.import-pager').innerText(), /第 51–100 行，共 10000 行/);
  mark('万行预览按页渲染：首屏50行、翻页定位正确');

  await page.locator('.import-pager .btn-prev').click();
  await page.locator('.import-filter').click();
  await page.waitForTimeout(600);
  report.invalidFilterPager = (await page.locator('.import-pager').innerText()).replace(/\s+/g, ' ').trim();
  assert.match(report.invalidFilterPager, /共 103 行/);
  assert.ok(await page.locator('.el-table__body tbody tr .invalid-cell').count() > 0, '问题单元格未标红');
  mark('“仅看问题行”只显示错误行并定位问题单元格');

  await page.route('**/api/status', route => route.abort());
  await page.reload({waitUntil: 'domcontentloaded'});
  await page.waitForSelector('.startup-screen', {timeout: 30000});
  report.errorScreen = (await page.locator('.startup-screen').innerText()).replace(/\s+/g, ' ').trim();
  assert.match(report.errorScreen, /平台服务暂时不可用/);
  assert.match(report.errorScreen, /重新连接/);
  await page.unroute('**/api/status');
  await page.click('button:has-text("重新连接")');
  await page.waitForSelector('.app-layout', {timeout: 60000});
  mark('服务异常显示可重试错误页，重连后恢复工作台');

  report.consoleErrors = consoleErrors;
  report.status = 'passed';
} catch (error) {
  report.status = 'failed';
  report.error = error instanceof Error ? error.message : String(error);
  process.exitCode = 1;
} finally {
  if (browser) await browser.close().catch(() => {});
  if (child && child.exitCode === null) child.kill();
  if (!process.env.E2E_KEEP) await rm(runDirectory, {recursive: true, force: true}).catch(() => {});
  report.passed = passed.length;
  report.checks = passed;
  report.completedAt = new Date().toISOString();
  try { report.testedAssemblySha256 = createHash('sha256').update(await readFile(appDll)).digest('hex'); } catch { /* executable-only deployments */ }
  await mkdir(path.dirname(reportPath), {recursive: true});
  await writeFile(reportPath, JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
}
