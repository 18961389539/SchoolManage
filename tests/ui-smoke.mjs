// Optional UI regression for personnel, simulated resources, host inventory, licensing and service recovery.
// Not part of the release gate: needs a Chromium/Edge binary and playwright-core. Usage:
//   node tests/ui-smoke.mjs
// Environment: SCHOOL_APP_DIR (app output folder), CHROME_PATH (browser binary), E2E_KEEP=1 (keep the temp workspace).
// E2E_REPORT_PATH optionally writes a separate report; E2E_SCREENSHOTS=1 saves screenshots.
// E2E_SCREENSHOT_PREFIX keeps screenshots from different runs separate.
// E2E_LEGACY_UI=1 skips assertions for features added after 0.1.0-p4.
// E2E_CABINET_WORKFLOWS=1 includes cabinet and device page CRUD, with a separate E2E_REPORT_PATH.
// E2E_NODE_ACCOUNT_WORKFLOWS=1 includes node account assignment and removal across browser pages.
// E2E_NAVIGATION_ISOLATION=1 checks that late responses do not reopen dialogs after changing pages.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createHash, generateKeyPairSync, sign} from 'node:crypto';
import {once} from 'node:events';
import {existsSync} from 'node:fs';
import {mkdir, readFile, readdir, rm, writeFile} from 'node:fs/promises';
import {createRequire} from 'node:module';
import net from 'node:net';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {runCabinetUiChecks} from './cabinet-ui-checks.mjs';
import {runNodeAccountUiChecks} from './node-account-ui-checks.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const appDirectory = path.resolve(process.env.SCHOOL_APP_DIR ?? path.join(root, 'src/School.Training/bin/Debug/net10.0'));
const appExecutable = path.join(appDirectory, process.platform === 'win32' ? 'School.Training.exe' : 'School.Training');
const appDll = path.join(appDirectory, 'School.Training.dll');
const runDirectory = path.join(root, '.local', `ui-test-${Date.now()}`);
const cabinetWorkflows = process.env.E2E_CABINET_WORKFLOWS === '1';
const nodeAccountWorkflows = process.env.E2E_NODE_ACCOUNT_WORKFLOWS === '1';
const defaultReportName = nodeAccountWorkflows
  ? 'ui-smoke-node-accounts.json'
  : cabinetWorkflows ? 'ui-smoke-cabinets.json' : 'ui-smoke.json';
const reportPath = path.resolve(process.env.E2E_REPORT_PATH ?? path.join(root, 'docs', 'verification', defaultReportName));
const screenshotDirectory = path.join(path.dirname(reportPath), 'screenshots');
const legacyUi = process.env.E2E_LEGACY_UI === '1';
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
const {privateKey, publicKey} = generateKeyPairSync('rsa', {modulusLength: 2048});
const publicKeyPath = path.join(runDirectory, 'license-public.pem');
await writeFile(publicKeyPath, publicKey.export({type: 'spki', format: 'pem'}));
const base = `http://127.0.0.1:${await freePort()}`;
const report = {status: 'running', base, workspace: runDirectory};
async function captureScreenshot(target, filename, {fullPage = true, mask = [], style = '', preserveMessages = false} = {}) {
  if (process.env.E2E_SCREENSHOTS !== '1') return;
  const page = typeof target.waitForFunction === 'function' ? target : target.page();
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await page.waitForFunction(() => {
    const loading = [...document.querySelectorAll('.el-loading-mask')].some(mask =>
      mask.getClientRects().length > 0 && getComputedStyle(mask).opacity !== '0'
    );
    const transitioning = document.getAnimations().some(animation => {
      const effect = animation.effect;
      return effect instanceof KeyframeEffect &&
        effect.target instanceof Element &&
        effect.target.closest('.el-overlay, .el-drawer, .el-dialog, .el-loading-mask') &&
        Number.isFinite(effect.getComputedTiming().endTime) &&
        (animation.pending || animation.playState === 'running');
    });
    return !loading && !transitioning;
  }, undefined, {timeout: 10000});
  await mkdir(screenshotDirectory, {recursive: true});
  const output = path.join(screenshotDirectory, (process.env.E2E_SCREENSHOT_PREFIX ?? '') + filename);
  await target.screenshot({
    path: output, ...(fullPage ? {fullPage: true} : {}), mask, maskColor: '#41526e',
    style: style + (preserveMessages ? '' : '\n.el-message { visibility: hidden !important; }'),
    animations: 'disabled', type: 'jpeg', quality: 83
  });
  (report.screenshots ??= []).push(path.relative(root, output).replaceAll('\\', '/'));
}
const capture = nodeAccountWorkflows && !cabinetWorkflows ? async () => {} : captureScreenshot;

function entityUi(page) {
  // Sidebar groups start collapsed for every group except the current page, so open
  // them before asking Playwright to click an item that may not be visible yet.
  const expandNavigation = async () => {
    const collapsedGroups = page.locator('.nav-group-toggle[aria-expanded="false"]');
    while (await collapsedGroups.count()) await collapsedGroups.first().click();
  };
  const openPage = async name => {
    await expandNavigation();
    await page.getByRole('button', {name, exact: true}).click();
    // Wait for the lazy page to mount before inspecting its toolbar; a few pages title their
    // heading differently from the menu entry, hence the silent fallback.
    await page.getByRole('heading', {name, exact: true}).first().waitFor({timeout: 5000}).catch(() => {});
    // Lists remember their filters between visits; start each page from the unfiltered set so
    // a keyword left over from an earlier check cannot hide the row a later check looks for.
    const clearFilters = page.getByRole('button', {name: '清除筛选', exact: true});
    if (await clearFilters.count()) {
      await clearFilters.click();
      await page.waitForFunction(() => ![...document.querySelectorAll('.el-loading-mask')].some(
        mask => mask.getClientRects().length > 0 && getComputedStyle(mask).opacity !== '0'
      ));
    }
  };
  const rowNamed = name => page.getByRole('row').filter({
    has: page.getByRole('cell', {name, exact: true})
      .or(page.getByRole('cell', {name: `${name} 模拟`, exact: true}))
  });
  const formField = (dialog, label) => dialog.locator('.el-form-item')
    .filter({has: page.getByText(label, {exact: true})});
  async function selectFormOption(dialog, label, option) {
    await formField(dialog, label).getByRole('combobox').click();
    await page.getByRole('option', {name: option, exact: true}).click();
  }
  async function saveForm(dialog, singular) {
    await dialog.getByRole('button', {name: `保存${singular}`}).click();
    await dialog.waitFor({state: 'hidden'});
  }
  async function deleteRow(row) {
    await row.getByRole('button', {name: '删除', exact: true}).click();
    await page.getByRole('dialog', {name: '删除确认'}).getByRole('button', {name: '确认删除'}).click();
    await row.waitFor({state: 'hidden'});
  }
  async function listResponse(endpoint, params, trigger) {
    const observed = [];
    const [response] = await Promise.all([
      page.waitForResponse(response => {
        const url = new URL(response.url());
        if (url.pathname !== `/api${endpoint}` || response.request().method() !== 'GET') return false;
        observed.push(url.search);
        return Object.entries(params).every(([key, value]) => (url.searchParams.get(key) ?? '') === value);
      }, {timeout: 40_000}),
      trigger()
    ]).catch(error => {
      throw new Error(`GET /api${endpoint} expecting ${JSON.stringify(params)} (observed: ${observed.join(' ') || 'none'}): ${error.message}`, {cause: error});
    });
    assert.ok(response.ok());
    return await response.json();
  }
  async function searchList(title, endpoint, query, params = {}) {
    const field = page.getByRole('textbox', {name: `搜索${title}`});
    await field.fill(query);
    return listResponse(endpoint, {search: query, ...params}, () => field.press('Enter'));
  }
  async function filterList(endpoint, label, option, params) {
    const control = page.getByRole('combobox', {name: `按${label}筛选`});
    return listResponse(endpoint, params, async () => {
      if (option) {
        await control.click();
        await page.getByRole('option', {name: option, exact: true}).click();
        return;
      }
      const filter = page.locator('.table-filter').filter({has: control});
      await filter.hover();
      await filter.locator('.el-select__clear').click();
    });
  }
  return {openPage, rowNamed, formField, selectFormOption, saveForm, deleteRow, listResponse, searchList, filterList};
}

async function verifyNavigationIsolation(page, ui, adminApi) {
  const {openPage, rowNamed} = ui;
  async function holdNextResponse(pathname, method, start, whileHeld, expectedStatus = 200) {
    const matches = url => url.pathname === pathname;
    let signalReady;
    let release;
    const ready = new Promise(resolve => { signalReady = resolve; });
    const held = new Promise(resolve => { release = resolve; });
    const routeHandler = async route => {
      const upstream = await route.fetch();
      signalReady(upstream.status());
      await held;
      await route.fulfill({response: upstream});
    };
    await page.route(matches, routeHandler, {times: 1});
    try {
      await start();
      const status = await Promise.race([
        ready,
        new Promise(resolve => {
          const timer = setTimeout(() => resolve(0), 15000);
          timer.unref();
        })
      ]);
      assert.equal(status, expectedStatus, `Did not observe an isolated ${method} ${pathname} response`);
      await whileHeld();
      const delivered = page.waitForResponse(response => matches(new URL(response.url())) && response.request().method() === method);
      release();
      await (await delivered).finished();
    } finally {
      release();
      await page.unroute(matches, routeHandler);
    }
  }

  await openPage('实训设备');
  await holdNextResponse('/api/nodes', 'GET',
    () => page.getByRole('button', {name: '新增设备', exact: true}).click(),
    async () => {
      await openPage('学生管理');
      await page.getByRole('heading', {name: '学生管理', exact: true}).waitFor();
    }
  );
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await page.getByRole('dialog', {name: /新增设备|新增学生/}).count(), 0);

  const importButton = page.getByRole('button', {name: '批量导入', exact: true});
  const studentImport = page.getByRole('dialog', {name: '批量导入学生'});
  const teacherImport = page.getByRole('dialog', {name: '批量导入教师'});
  await importButton.click();
  await studentImport.waitFor();
  await holdNextResponse('/api/imports/preview', 'POST',
    () => studentImport.getByLabel('选择导入文件').setInputFiles({
      name: 'stale-student-preview.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from('姓名,账号,学号\r\n应忽略的学生预览,ui_delayed_student,UI-DELAYED-01\r\n')
    }),
    async () => {
      await page.evaluate(() => { location.hash = '#teachers'; });
      await page.getByRole('heading', {name: '教师管理', exact: true}).waitFor();
      await studentImport.waitFor({state: 'hidden'});
      await importButton.click();
      await teacherImport.waitFor();
    }
  );
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await teacherImport.getByText(/stale-student-preview|应忽略的学生预览/).count(), 0);
  await teacherImport.getByText('上传师生档案文件', {exact: true}).waitFor();
  await teacherImport.getByRole('button', {name: '关闭', exact: true}).click();

  const fixtureType = await adminApi('/device-types', 'POST', {name: '跨页删除验证类型'});
  await openPage('设备类型');
  await rowNamed(fixtureType.name).getByRole('button', {name: '删除', exact: true}).click();
  const removal = page.getByRole('dialog', {name: '删除确认'});
  await removal.waitFor();
  const deletionRequests = [];
  const recordDeletion = request => {
    if (request.method() === 'DELETE') deletionRequests.push(new URL(request.url()).pathname);
  };
  page.on('request', recordDeletion);
  try {
    await page.evaluate(() => { location.hash = '#professions'; });
    await page.getByRole('heading', {name: '专业管理', exact: true}).waitFor();
    const closedOnNavigation = await removal.waitFor({state: 'hidden', timeout: 2000}).then(() => true, () => false);
    if (!closedOnNavigation) {
      await removal.getByRole('button', {name: '确认删除', exact: true}).click({timeout: 2000});
      await removal.waitFor({state: 'hidden'});
    }
    const result = await adminApi('/device-types?search=' + encodeURIComponent(fixtureType.name));
    assert.deepEqual(deletionRequests, []);
    assert.equal(result.total, 1);
  } finally {
    page.off('request', recordDeletion);
    await adminApi(`/device-types/${fixtureType.id}`, 'DELETE');
  }

  await openPage('学生管理');
  await importButton.click();
  const [previewResponse] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === '/api/imports/preview'),
    studentImport.getByLabel('选择导入文件').setInputFiles({
      name: 'commit-wait.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from('姓名,账号,学号\r\n提交等待测试,ui_commit_wait,UI-COMMIT-01\r\n')
    })
  ]);
  const preview = await previewResponse.json();
  await studentImport.locator('input[type="password"]').fill(password);
  await studentImport.getByRole('button', {name: '导入 1 条有效记录'}).click();
  const confirmImport = page.getByRole('dialog', {name: '确认导入'});
  await holdNextResponse(`/api/imports/${preview.batchId}/commit`, 'POST',
    () => confirmImport.getByRole('button', {name: '开始导入'}).click(),
    async () => {
      assert.equal(await studentImport.getByRole('button', {name: '取消导入', exact: true}).count(), 0);
    }, 202
  );
  await studentImport.waitFor({state: 'hidden'});
  const imported = await adminApi('/people?kind=student&search=ui_commit_wait');
  assert.equal(imported.total, 1);
  await adminApi(`/people/${imported.items[0].id}`, 'DELETE');
}

try {
  const useExe = existsSync(appExecutable);
  const contentRoot = existsSync(path.join(appDirectory, 'wwwroot', 'index.html')) ? appDirectory : path.join(root, 'src/School.Training');
  child = spawn(useExe ? appExecutable : 'dotnet', useExe ? ['--urls', base] : [appDll, '--urls', base], {
    cwd: contentRoot,
    env: {...process.env, SCHOOL_DATA_DIR: path.join(runDirectory, 'data'), SCHOOL_LICENSE_PUBLIC_KEY: publicKeyPath, ASPNETCORE_ENVIRONMENT: 'Production'},
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '';
  child.stdout.on('data', chunk => { output = (output + chunk).slice(-40000); });
  child.stderr.on('data', chunk => { output = (output + chunk).slice(-40000); });
  let ready = false;
  for (let attempt = 0; attempt < 150; attempt++) {
    if (child.exitCode !== null) throw new Error(`service exited: ${output}`);
    try {
      const status = await fetch(`${base}/api/status`);
      if (status.ok) {
        report.productVersion = (await status.json()).version;
        ready = true;
        break;
      }
    } catch { /* keep waiting */ }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (!ready) throw new Error(`service did not start: ${output}`);

  const {chromium} = loadPlaywright();
  browser = await chromium.launch({executablePath: await findBrowser(), headless: true});
  report.browserVersion = browser.version();
  const page = await browser.newPage({viewport: {width: 1440, height: 1000}});
  const ui = entityUi(page);
  const {openPage, rowNamed, formField, selectFormOption, saveForm, deleteRow, searchList, filterList} = ui;
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

  const adminSession = await page.request.get(`${base}/api/auth/me`);
  const {csrfToken} = await adminSession.json();
  assert.ok(csrfToken, '管理员会话缺少安全令牌');
  const adminHeaders = {Origin: base, 'X-CSRF-TOKEN': csrfToken};
  async function adminApi(endpoint, method = 'GET', data) {
    const response = await page.request.fetch(`${base}/api${endpoint}`, {method, data, headers: adminHeaders});
    assert.ok(response.ok(), `${method} ${endpoint}: ${response.status()} ${await response.text()}`);
    return await response.json();
  }
  await adminApi('/simulation/bootstrap', 'POST', {});
  const unassignedNode = await adminApi('/nodes', 'POST', {name: 'UI 未分配节点', provider: 'simulated'});
  if (process.env.E2E_NAVIGATION_ISOLATION === '1') {
    await verifyNavigationIsolation(page, ui, adminApi);
    mark('切换页面时忽略旧表单与导入预览、作废旧删除确认，待导入提交确认后才显示取消选项');
  }
  await openPage('实训设备');
  const routerRow = page.getByRole('row').filter({hasText: '模拟路由器 RT-01'}).first();
  await routerRow.getByRole('button', {name: '编辑', exact: true}).click();
  const deviceEdit = page.getByRole('dialog', {name: '编辑设备'});
  await deviceEdit.locator('.el-form-item').filter({has: page.getByText('实训节点', {exact: true})}).getByRole('combobox').click();
  await page.getByRole('option', {name: 'UI 未分配节点', exact: true}).click();
  await deviceEdit.getByRole('button', {name: '保存设备'}).click();
  await deviceEdit.waitFor({state: 'hidden'});
  const devices = await adminApi('/devices');
  assert.equal(devices.items.find(device => device.id === 'simulation-router').nodeId, unassignedNode.id);
  mark('管理员可通过设备表单关联模拟节点');

  const learnerName = 'ui_learner_01';
  await adminApi('/people', 'POST', {kind: 'student', name: '实训学生', userName: learnerName, number: 'UI-001', password, role: 'Student'});
  const candidates = await adminApi(`/nodes/simulation-node/account-candidates?search=${learnerName}`);
  assert.equal(candidates.items.length, 1);
  const learnerId = candidates.items[0].userId;
  await adminApi('/nodes/simulation-node/accounts', 'POST', {userIds: [learnerId]});
  const learnerPage = await browser.newPage({viewport: {width: 1440, height: 1000}});
  try {
    await learnerPage.goto(base, {waitUntil: 'domcontentloaded'});
    await learnerPage.locator('input[autocomplete="username"]').fill(learnerName);
    await learnerPage.locator('input[autocomplete="current-password"]').fill(password);
    await learnerPage.getByRole('button', {name: '登录平台'}).click();
    const changePassword = learnerPage.getByRole('dialog', {name: '首次登录，请修改密码'});
    await changePassword.waitFor();
    assert.equal(await learnerPage.getByRole('heading', {name: '我的实训资源', exact: true}).count(), 0);
    await changePassword.locator('input[autocomplete="current-password"]').fill(password);
    for (const input of await changePassword.locator('input[autocomplete="new-password"]').all()) await input.fill('NewPassw0rd2026');
    await changePassword.getByRole('button', {name: '保存新密码'}).click();
    await changePassword.waitFor({state: 'hidden'});
    if (!legacyUi) await learnerPage.getByText('我的实训资源', {exact: true}).click();
    await learnerPage.getByRole('heading', {name: '我的实训资源', exact: true}).waitFor();
    await learnerPage.getByText('模拟计算节点 NODE-01', {exact: true}).first().waitFor();
    await learnerPage.getByText('模拟 Linux 实训环境', {exact: true}).waitFor();
    assert.equal(await learnerPage.getByText('UI 未分配节点', {exact: true}).count(), 0);
    assert.equal(await learnerPage.getByText('模拟路由器 RT-01', {exact: true}).count(), 0);
    assert.equal(await learnerPage.getByRole('button', {name: /启动虚拟机|停止虚拟机|新增节点/}).count(), 0);
    await capture(learnerPage, 'learner-resources.jpg');
    mark('师生改密后只看见自己获分配的模拟资源，虚拟机状态只读');

    await learnerPage.getByRole('button', {name: '打开模拟交换机 SW-01Web 管理'}).click();
    const webConsole = learnerPage.getByRole('dialog', {name: '模拟交换机 SW-01 · 模拟 Web 管理'});
    const runHostname = webConsole.getByRole('textbox', {name: '运行主机名'});
    await runHostname.fill('SW-LEARNER-UI');
    await webConsole.getByRole('button', {name: '应用运行配置'}).click();
    await webConsole.getByText('运行配置尚未保存到启动配置', {exact: true}).waitFor();
    await webConsole.getByRole('button', {name: '保存为启动配置'}).click();
    await webConsole.getByText('运行与启动配置一致', {exact: true}).waitFor();
    await capture(learnerPage, 'simulator-web.jpg');
    await webConsole.getByRole('button', {name: '关闭', exact: true}).click();
    await webConsole.waitFor({state: 'hidden'});
    mark('师生可在 Web 管理里更新运行配置并保存为启动配置');

    await learnerPage.getByRole('button', {name: '打开模拟交换机 SW-01终端'}).click();
    const terminal = learnerPage.locator('.terminal-dialog');
    await terminal.getByText('已连接', {exact: false}).first().waitFor();
    await terminal.locator('.xterm-helper-textarea').focus();
    await learnerPage.keyboard.type('show version');
    await learnerPage.keyboard.press('Enter');
    await terminal.getByText('School Training Simulator 1.0', {exact: false}).first().waitFor();
    await terminal.getByText('主机名：SW-LEARNER-UI', {exact: false}).first().waitFor();
    await adminApi(`/nodes/simulation-node/accounts/${learnerId}`, 'DELETE');
    await terminal.getByText('连接已关闭', {exact: false}).first().waitFor();
    await terminal.getByRole('button', {name: '断开并关闭'}).click();
    await learnerPage.getByText('暂未分配实训资源', {exact: true}).waitFor();
    mark('师生终端首次打开即连接，读取 Web 写入的配置，撤销分配自动关闭会话');
  } finally {
    await learnerPage.close();
  }

  if (cabinetWorkflows) {
    report.cabinetObservations = {};
    await runCabinetUiChecks({
      page, ui, adminApi, capture: captureScreenshot, mark,
      productVersion: report.productVersion, observations: report.cabinetObservations
    });
  }

  if (nodeAccountWorkflows) {
    report.nodeAccountObservations = await runNodeAccountUiChecks({
      page, ui, adminApi, mark, capture: captureScreenshot, password
    });
  }

  const licenseStatus = await adminApi('/licenses');
  assert.equal(licenseStatus.publicKeyConfigured, true);
  assert.match(licenseStatus.machineCode, /^[A-F0-9]{32}$/);
  const fixtures = [
    {fileName: 'ui-license-base.lic', id: 'UI-LICENSE-BASE', quantity: 3},
    {fileName: 'ui-license-renewal.lic', id: 'UI-LICENSE-RENEWAL', quantity: 7}
  ];
  for (const fixture of fixtures) {
    const payload = Buffer.from(JSON.stringify({
      licenseId: fixture.id, product: 'SchoolTraining', machineCode: licenseStatus.machineCode,
      issuedAt: new Date(Date.now() - 1000).toISOString(), expiresAt: null,
      resources: [{name: '实训设备', authorizationCode: 'TRAINING.DEVICES', quantity: fixture.quantity}]
    }));
    const envelope = {payload: payload.toString('base64'), signature: sign('RSA-SHA256', payload, privateKey).toString('base64')};
    await writeFile(path.join(runDirectory, fixture.fileName), JSON.stringify(envelope));
  }
  await page.getByRole('button', {name: '许可证管理', exact: true}).click();
  const quotas = page.locator('section').filter({has: page.getByRole('heading', {name: '资源使用与许可额度', exact: true})});
  const deviceQuotaRow = quotas.getByRole('row').filter({hasText: 'TRAINING.DEVICES'});
  await deviceQuotaRow.getByRole('cell', {name: '未启用限制', exact: true}).waitFor();
  const [chooser] = await Promise.all([
    page.waitForEvent('filechooser'),
    page.getByRole('button', {name: '上传许可证', exact: true}).click()
  ]);
  await chooser.setFiles(fixtures.map(fixture => path.join(runDirectory, fixture.fileName)));
  const uploadResults = page.locator('section').filter({has: page.getByRole('heading', {name: '上传结果', exact: true})});
  for (const fixture of fixtures) {
    const row = uploadResults.getByRole('row').filter({hasText: fixture.fileName});
    await row.getByRole('cell', {name: '成功', exact: true}).waitFor();
    await row.getByRole('cell', {name: /共 2 项/}).waitFor();
    await page.locator('.license-surface').getByText(fixture.id, {exact: true}).first().waitFor();
  }
  await deviceQuotaRow.getByRole('cell', {name: '2', exact: true}).waitFor();
  await deviceQuotaRow.getByRole('cell', {name: '7', exact: true}).waitFor();
  report.deviceLicenseQuota = {used: 2, limit: 7, imported: 2};
  // Capture only the quotas, excluding the installation fingerprint in the neighboring section.
  await capture(quotas, 'license-quotas.jpg', {fullPage: false});
  mark('许可证双文件批量导入，设备已用数为 2，重叠授权取较高额度 7');
  const [guideDownload] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('link', {name: '使用说明', exact: true}).click()
  ]);
  assert.equal(guideDownload.suggestedFilename(), 'license-guide.md');
  const guide = await readFile(await guideDownload.path(), 'utf8');
  assert.match(guide, /许可证使用说明/);
  assert.match(guide, /设备特征码/);
  mark('许可证使用说明可离线下载，文件含设备绑定与授权使用说明');

  await openPage('专业管理');
  const professionName = '界面验收专业';
  const otherProfessionName = '界面验收备用专业';
  for (const [name, code] of [[professionName, 'UI-QA'], [otherProfessionName, 'UI-ALT']]) {
    await page.getByRole('button', {name: '新增专业'}).click();
    const dialog = page.getByRole('dialog', {name: '新增专业'});
    await formField(dialog, '专业名称').getByRole('textbox').fill(name);
    await formField(dialog, '专业代码').getByRole('textbox').fill(code);
    await saveForm(dialog, '专业');
    await rowNamed(name).waitFor();
  }
  const initialProfession = await searchList('专业管理', '/professions', 'UI-QA');
  assert.equal(initialProfession.total, 1);
  const [profession] = initialProfession.items;
  const {items: [otherProfession]} = await adminApi('/professions?search=UI-ALT');
  await rowNamed(professionName).getByRole('button', {name: '编辑', exact: true}).click();
  const professionEdit = page.getByRole('dialog', {name: '编辑专业'});
  await formField(professionEdit, '备注').getByRole('textbox').fill('专业备注已更新');
  await saveForm(professionEdit, '专业');
  await rowNamed(professionName).getByRole('button', {name: '查看', exact: true}).click();
  const professionDetails = page.getByRole('dialog', {name: '专业详情'});
  await professionDetails.getByText('专业备注已更新', {exact: true}).waitFor();
  await professionDetails.getByRole('button', {name: /close|关闭/i}).click();

  await openPage('班级管理');
  const className = '界面验收一班';
  const otherClassName = '界面验收二班';
  for (const [name, department] of [[className, professionName], [otherClassName, otherProfessionName]]) {
    await page.getByRole('button', {name: '新增班级'}).click();
    const dialog = page.getByRole('dialog', {name: '新增班级'});
    await formField(dialog, '班级名称').getByRole('textbox').fill(name);
    await selectFormOption(dialog, '所属专业', department);
    await saveForm(dialog, '班级');
    await rowNamed(name).waitFor();
  }
  const {items: [schoolClass]} = await adminApi(`/classes?search=${encodeURIComponent(className)}`);
  const {items: [otherClass]} = await adminApi(`/classes?search=${encodeURIComponent(otherClassName)}`);
  if (!legacyUi) {
    const filtered = await filterList('/classes', '所属专业', professionName, {professionId: profession.id});
    assert.deepEqual(filtered.items.map(row => row.name), [className]);
    await rowNamed(otherClassName).waitFor({state: 'hidden'});
    const noMatch = await searchList('班级管理', '/classes', otherClassName, {professionId: profession.id});
    assert.equal(noMatch.total, 0);
    await page.getByText('没有符合条件的记录', {exact: true}).waitFor();
    const cleared = await filterList('/classes', '所属专业', null, {professionId: '', search: otherClassName});
    assert.deepEqual(cleared.items.map(row => row.name), [otherClassName]);
    await rowNamed(otherClassName).waitFor();
    mark('班级按专业筛选，可与关键词联合并清除专业条件');
  }

  for (const {kind, person, numberLabel, role} of [
    {kind: 'student', person: '学生', numberLabel: '学号', role: 'Student'},
    {kind: 'teacher', person: '教师', numberLabel: '教师工号', role: 'Teacher'}
  ]) {
    const title = `${person}管理`;
    const fixtures = ['01', '02', '03'].map(suffix => ({
      name: `验收${person}${suffix}`,
      userName: `ui_${kind}_${suffix}`,
      number: `UI-${kind}-${suffix}`
    }));
    await openPage(title);

    for (const fixture of fixtures) {
      await page.getByRole('button', {name: `新增${person}`, exact: true}).click();
      const dialog = page.getByRole('dialog', {name: `新增${person}`});
      await formField(dialog, '姓名').getByRole('textbox').fill(fixture.name);
      await formField(dialog, '平台账号').getByRole('textbox').fill(fixture.userName);
      await formField(dialog, numberLabel).getByRole('textbox').fill(fixture.number);
      await selectFormOption(dialog, '所属班级', schoolClass.name);
      if (!legacyUi && kind === 'teacher' && fixture === fixtures[2]) {
        await selectFormOption(dialog, '所属角色', '管理员');
      }
      await dialog.locator('input[autocomplete="new-password"]').fill(password);
      await saveForm(dialog, person);
      await rowNamed(fixture.name).waitFor();
    }

    const [first, second, third] = fixtures;
    const edit = {
      ...first, name: `已编辑验收${person}`, number: `UI-${kind}-edited`,
      phone: '13800000001', email: `${kind}@example.test`
    };
    await rowNamed(first.name).getByRole('button', {name: '编辑', exact: true}).click();
    const editDialog = page.getByRole('dialog', {name: `编辑${person}`});
    await formField(editDialog, '姓名').getByRole('textbox').fill(edit.name);
    await formField(editDialog, numberLabel).getByRole('textbox').fill(edit.number);
    await selectFormOption(editDialog, '所属班级', otherClass.name);
    await selectFormOption(editDialog, '性别', '女');
    await formField(editDialog, '手机号码').getByRole('textbox').fill(edit.phone);
    await formField(editDialog, '电子邮箱').getByRole('textbox').fill(edit.email);
    await saveForm(editDialog, person);
    await rowNamed(edit.name).waitFor();
    for (const query of [edit.name, edit.number, edit.userName]) {
      const filtered = await searchList(title, '/people', query);
      assert.equal(filtered.total, 1);
      const [found] = filtered.items;
      assert.equal(found.name, edit.name);
      assert.equal(found.classId, otherClass.id);
      assert.equal(found.phone, edit.phone);
      assert.equal(found.email, edit.email);
      assert.equal(found.gender, '女');
      assert.equal(found.role, role);
      await rowNamed(edit.name).waitFor();
      await rowNamed(second.name).waitFor({state: 'hidden'});
    }
    await rowNamed(edit.name).getByRole('button', {name: '查看', exact: true}).click();
    const details = page.getByRole('dialog', {name: `${person}详情`});
    for (const value of [edit.name, edit.userName, edit.number, otherClass.name, '女', edit.phone, edit.email]) {
      await details.getByText(value, {exact: true}).first().waitFor();
    }
    await capture(page, `${kind}-details.jpg`);
    await details.getByRole('button', {name: /close|关闭/i}).click();
    await details.waitFor({state: 'hidden'});
    mark(`${person}新增、编辑、查看，按姓名、账号与${numberLabel}筛选`);

    const personSession = await browser.newContext();
    const newPassword = 'ResetPassw0rd2026';
    const login = pass => personSession.request.post(`${base}/api/auth/login`, {
      headers: {Origin: base}, data: {userName: edit.userName, password: pass}
    });
    try {
      assert.equal((await login(password)).status(), 200);
      assert.equal((await personSession.request.get(`${base}/api/auth/me`)).status(), 200);
      await rowNamed(edit.name).getByRole('button', {name: '重置密码'}).click();
      const resetDialog = page.getByRole('dialog', {name: '重置账号密码'});
      await resetDialog.locator('input[type="password"]').fill(newPassword);
      await resetDialog.getByRole('button', {name: '确认重置'}).click();
      await resetDialog.waitFor({state: 'hidden'});
      assert.equal((await personSession.request.get(`${base}/api/auth/me`)).status(), 401);
      assert.equal((await login(password)).status(), 401);
      const newLogin = await login(newPassword);
      assert.equal(newLogin.status(), 200);
      assert.equal((await newLogin.json()).user.mustChangePassword, true);
      mark(`${person}页面重置密码，旧口令及旧会话失效，新口令登录须改密`);
    } finally {
      await personSession.close();
    }

    await searchList(title, '/people', '');
    await rowNamed(second.name).waitFor();
    await capture(page, `${kind}-management.jpg`);
    if (!legacyUi) {
      const initialFilter = await filterList('/people', '所属班级', `${className} · ${professionName}`, {
        classId: schoolClass.id
      });
      assert.deepEqual(initialFilter.items.map(row => row.name).sort(), [second.name, third.name].sort());
      await rowNamed(second.name).locator('label.el-checkbox').click();
      await page.getByRole('button', {name: '删除 1 项'}).waitFor();
      const otherClassFilter = await filterList('/people', '所属班级', `${otherClassName} · ${otherProfessionName}`, {
        classId: otherClass.id
      });
      assert.deepEqual(otherClassFilter.items.map(row => row.name), [edit.name]);
      await page.getByRole('button', {name: '删除 1 项'}).waitFor({state: 'hidden'});
      const combined = await searchList(title, '/people', second.userName, {classId: otherClass.id});
      assert.equal(combined.total, 0);
      await page.getByText('没有符合条件的记录', {exact: true}).waitFor();
      const clearClassFilter = await filterList('/people', '所属班级', null, {classId: '', search: second.userName});
      assert.deepEqual(clearClassFilter.items.map(row => row.name), [second.name]);
      await rowNamed(second.name).waitFor();
      await searchList(title, '/people', '');
      mark(`${person}按班级与关键词联合筛选，可清除筛选且切换条件清空已勾选项`);

      if (kind === 'teacher') {
        await filterList('/people', '所属班级', `${className} · ${professionName}`, {classId: schoolClass.id});
        const teachers = await filterList('/people', '所属角色', '教师', {classId: schoolClass.id, role});
        assert.deepEqual(teachers.items.map(row => row.name), [second.name]);
        await rowNamed(second.name).getByRole('cell', {name: '教师', exact: true}).waitFor();
        const admins = await filterList('/people', '所属角色', '管理员', {classId: schoolClass.id, role: 'Admin'});
        assert.deepEqual(admins.items.map(row => row.name), [third.name]);
        await rowNamed(third.name).getByRole('cell', {name: '管理员', exact: true}).waitFor();
        await capture(page, 'teacher-role-class-filter.jpg', {fullPage: false});
        const clearRoleFilter = await filterList('/people', '所属角色', null, {classId: schoolClass.id, role: ''});
        assert.equal(clearRoleFilter.total, 2);
        await filterList('/people', '所属班级', null, {classId: '', role: ''});
        mark('教师班级和角色可组合筛选，教师与管理员显示不同记录，可独立清除角色条件');
      }
    }

    const importedName = `导入验收${person}`;
    const importedUser = `ui_import_${kind}`;
    const rejectedName = `重复账号验收${person}`;
    const importCsv = [
      `姓名,账号,${kind === 'teacher' ? '工号' : '学号'},班级,性别,手机号,电子邮箱`,
      `${importedName},${importedUser},UI-IMPORT-${kind},${className},女,,`,
      `${rejectedName},${second.userName},UI-DUPLICATE-${kind},${className},,,wrong-email`
    ].join('\r\n');
    await page.getByRole('button', {name: '批量导入', exact: true}).click();
    const importDialog = page.getByRole('dialog', {name: `批量导入${person}`});
    const [template] = await Promise.all([
      page.waitForEvent('download'), importDialog.getByRole('link', {name: '下载模板'}).click()
    ]);
    assert.match(template.suggestedFilename(), /\.(csv|xlsx)$/i);
    assert.equal(await template.failure(), null);
    await importDialog.locator('input[type="file"]').setInputFiles({
      name: `ui-${kind}-import.csv`, mimeType: 'text/csv', buffer: Buffer.from('\uFEFF' + importCsv)
    });
    const invalidImportRow = importDialog.getByRole('row').filter({hasText: rejectedName});
    await invalidImportRow.getByRole('cell', {name: '3', exact: true}).waitFor();
    assert.match(await invalidImportRow.innerText(), /账号[：:]/);
    assert.match(await invalidImportRow.innerText(), /邮箱[：:]/);
    await capture(page, `${kind}-import-validation.jpg`, {fullPage: false});
    await importDialog.getByRole('button', {name: '移除问题行'}).click();
    await invalidImportRow.waitFor({state: 'hidden'});
    await importDialog.locator('input[type="password"]').fill(password);
    await importDialog.getByRole('button', {name: '导入 1 条有效记录'}).click();
    await page.getByRole('dialog', {name: '确认导入'}).getByRole('button', {name: '开始导入'}).click();
    await importDialog.waitFor({state: 'hidden'});
    await rowNamed(importedName).waitFor();
    await rowNamed(second.name).waitFor();
    assert.equal((await adminApi(`/people?kind=${kind}&search=${importedUser}`)).total, 1);
    assert.equal((await adminApi(`/people?kind=${kind}&search=${encodeURIComponent(rejectedName)}`)).total, 0);
    await deleteRow(rowNamed(importedName));
    mark(`${person}模板可下载，导入可定位出错行和列、移除问题行并保存有效档案，已有账号保留`);

    await deleteRow(rowNamed(edit.name));
    await rowNamed(second.name).locator('label.el-checkbox').click();
    await rowNamed(third.name).locator('label.el-checkbox').click();
    await page.getByRole('button', {name: '删除 2 项'}).click();
    await page.getByRole('dialog', {name: '批量删除'}).getByRole('button', {name: '确认删除'}).click();
    await rowNamed(second.name).waitFor({state: 'hidden'});
    await rowNamed(third.name).waitFor({state: 'hidden'});
    assert.equal((await adminApi(`/people?kind=${kind}&search=ui_${kind}_`)).total, 0);
    mark(`${person}单条删除及勾选两条批量删除`);
  }

  await openPage('班级管理');
  // Filters are remembered per list now, so target the class explicitly instead of assuming
  // the list still shows everything from the earlier filter checks.
  await searchList('班级管理', '/classes', className);
  await rowNamed(className).getByRole('button', {name: '编辑', exact: true}).click();
  const classEdit = page.getByRole('dialog', {name: '编辑班级'});
  await selectFormOption(classEdit, '所属专业', otherProfessionName);
  await formField(classEdit, '备注').getByRole('textbox').fill('班级备注已更新');
  await saveForm(classEdit, '班级');
  const classSearch = await searchList('班级管理', '/classes', className);
  assert.equal(classSearch.total, 1);
  assert.equal(classSearch.items[0].professionId, otherProfession.id);
  await rowNamed(className).getByRole('button', {name: '查看', exact: true}).click();
  const classDetails = page.getByRole('dialog', {name: '班级详情'});
  await classDetails.getByText('班级备注已更新', {exact: true}).waitFor();
  await classDetails.getByText(otherProfessionName, {exact: true}).waitFor();
  await classDetails.getByRole('button', {name: /close|关闭/i}).click();
  await deleteRow(rowNamed(className));
  await searchList('班级管理', '/classes', '');
  await deleteRow(rowNamed(otherClassName));
  mark('班级新增、按名称搜索、查看、修改所属专业与备注以及删除');

  await openPage('专业管理');
  await deleteRow(rowNamed(professionName));
  await deleteRow(rowNamed(otherProfessionName));
  mark('专业新增、按代码搜索、编辑备注、查看以及删除');

  await openPage('设备类型');
  const typeName = 'UI 验收串口类型';
  await page.getByRole('button', {name: '新增设备类型'}).click();
  const typeCreate = page.getByRole('dialog', {name: '新增设备类型'});
  await formField(typeCreate, '类型名称').getByRole('textbox').fill(typeName);
  await saveForm(typeCreate, '设备类型');
  const typeRow = rowNamed(typeName);
  await typeRow.getByRole('cell', {name: 'CRLF', exact: true}).waitFor();
  await typeRow.getByRole('cell', {name: 'DEL', exact: true}).waitFor();
  const typeSearch = await searchList('设备类型', '/device-types', typeName);
  assert.equal(typeSearch.total, 1);
  for (const [newline, backspace, value] of [['CR', 'BS (0x08)', 'BS'], ['LF', 'DEL (0x7F)', 'DEL']]) {
    await typeRow.getByRole('button', {name: '编辑', exact: true}).click();
    const dialog = page.getByRole('dialog', {name: '编辑设备类型'});
    await selectFormOption(dialog, '换行符', newline);
    await selectFormOption(dialog, '回退键', backspace);
    await saveForm(dialog, '设备类型');
    await typeRow.getByRole('cell', {name: newline, exact: true}).waitFor();
    await typeRow.getByRole('cell', {name: value, exact: true}).waitFor();
  }
  await deleteRow(typeRow);
  mark('设备类型新增、搜索、删除及 CRLF / CR / LF、DEL / BS 参数编辑');

  await openPage('服务器信息');
  const host = page.locator('.server-page');
  const inventory = await adminApi('/server/info');
  assert.equal(inventory.isSimulated, false);
  assert.ok(inventory.cpu.logicalCoreCount > 0);
  assert.ok(inventory.networks.length > 0);
  for (const field of ['主机名称', 'CPU 核心', '内存', '处理器', '网络接口']) {
    await host.getByText(field, {exact: true}).first().waitFor();
  }
  const coreSummary = host.locator('.summary-grid > div').filter({hasText: 'CPU 核心'}).locator('strong');
  await coreSummary.waitFor();
  assert.equal(
    (await coreSummary.innerText()).replace(/\s+/g, ' ').trim(),
    `${inventory.cpu.physicalCoreCount ?? '—'} 物理 / ${inventory.cpu.logicalCoreCount ?? '—'} 逻辑`
  );
  const blockFor = heading => host.locator('.server-block').filter({
    has: page.getByRole('heading', {name: heading, exact: true})
  });
  const cpuBlock = blockFor('处理器');
  const processors = inventory.cpu.processors || [];
  if (processors.length) {
    const firstProcessor = cpuBlock.locator('.el-table__body tr').first();
    await firstProcessor.waitFor();
    const cells = firstProcessor.locator('td');
    assert.ok((await cells.nth(0).innerText()).trim().length > 0, '处理器型号为空');
    assert.equal((await cells.nth(1).innerText()).trim(), String(processors[0].coreCount ?? '—'));
    assert.equal((await cells.nth(2).innerText()).trim(), String(processors[0].logicalCoreCount ?? '—'));
  }
  const memoryBlock = blockFor('内存');
  for (const field of ['插槽', '容量', '类型', '主频']) {
    await memoryBlock.getByRole('columnheader', {name: field, exact: true}).waitFor();
  }
  const networkSection = host.locator('.server-network');
  for (const field of ['接口', 'MAC 地址', 'IPv4', '速率']) {
    await networkSection.getByRole('columnheader', {name: field, exact: true}).waitFor();
  }
  // The default view only lists physical adapters that are up; switch to the full list so the
  // capture covers every adapter the collector returned, including virtual ones.
  await networkSection.getByText('全部', {exact: true}).click();
  await networkSection.locator('.el-table__body tr.el-table__row').first().waitFor();
  const hostnameCell = host.locator('.summary-grid > div').filter({hasText: '主机名称'}).locator('strong');
  const adapterCells = networkSection.locator(
    '.el-table__body tr td:nth-child(3), .el-table__body tr td:nth-child(5), .el-table__body tr td:nth-child(6)'
  );
  const hostMasks = [hostnameCell, adapterCells];
  await capture(page, 'server-information-redacted.jpg', {fullPage: false, mask: hostMasks});
  await capture(memoryBlock, 'server-memory.jpg', {
    fullPage: false, style: '.topbar { visibility: hidden; }'
  });
  await capture(networkSection, 'server-network-redacted.jpg', {
    fullPage: false, mask: [adapterCells], style: '.topbar { visibility: hidden; }'
  });
  report.hostScreenshotRedactions = ['主机名称', '网卡说明', 'MAC 地址', 'IPv4 地址'];
  mark('服务器信息页面显示真实只读 CPU、内存、网卡，截图隐去主机和网络标识');

  const nodesTitle = '集群节点';
  await openPage(nodesTitle);
  const referenceName = '模拟计算节点 NODE-01';
  const referenceNode = rowNamed(referenceName);
  if (!legacyUi) {
    const {items: [sampleVm]} = await adminApi('/virtual-machines?nodeId=simulation-node');
    assert.equal(sampleVm.nodeId, 'simulation-node');
    await adminApi(`/virtual-machines/${sampleVm.id}/start`, 'POST', {});
    await adminApi('/nodes/simulation-node/accounts', 'POST', {userIds: [learnerId]});
  }
  const nodeStatus = await adminApi('/nodes/simulation-node/status');
  assert.equal(nodeStatus.healthStatus, 'healthy');
  assert.equal(nodeStatus.isSimulated, true);
  const nodeFields = [
    '主机地址', '允许资源分配', '健康状态', 'CPU 使用率 (%)', '内存使用率 (%)',
    '磁盘使用率 (%)', '虚拟机数量', '虚拟机配额', '账号数量', '账号配额'
  ];
  const matchingNodes = await searchList(nodesTitle, '/nodes', referenceName);
  assert.deepEqual(matchingNodes.items.map(node => node.id), ['simulation-node']);
  await page.locator('.el-message').first().waitFor({state: 'hidden'});
  if (!legacyUi) {
    await page.setViewportSize({width: 2240, height: 1000});
    const [listed] = matchingNodes.items;
    for (const [key, header] of [
      ['cpuUsagePercent', 'CPU'], ['memoryUsagePercent', '内存'],
      ['diskUsagePercent', '磁盘']
    ]) {
      assert.ok(listed[key] > 0, `${header}没有反映运行资源`);
      assert.equal(listed[key], nodeStatus[key]);
      await page.getByRole('columnheader', {name: header, exact: true}).waitFor();
      await referenceNode.getByRole('cell', {name: `${listed[key]}%`, exact: true}).first().waitFor();
    }
    await referenceNode.getByRole('cell', {name: '正常', exact: true}).waitFor();
    await referenceNode.getByRole('cell', {name: `1 / ${nodeStatus.accountQuota}`, exact: true}).waitFor();
    await referenceNode.getByRole('cell', {
      name: `${nodeStatus.virtualMachineCount} / ${nodeStatus.virtualMachineQuota}`, exact: true
    }).waitFor();
    await capture(page, 'cluster-list-information.jpg', {fullPage: false});
    await page.setViewportSize({width: 1440, height: 1000});
    mark('集群列表展示健康、CPU/内存/磁盘使用率及数量配额，与节点详情数据一致');
  }
  await referenceNode.getByRole('button', {name: '状态', exact: true}).click();
  const statusDrawer = page.getByRole('dialog', {name: `${referenceName} · 节点状态`});
  for (const field of nodeFields) await statusDrawer.getByText(field, {exact: true}).waitFor();
  await statusDrawer.getByText('模拟资源分配统计', {exact: true}).waitFor();
  await page.setViewportSize({width: 1440, height: 1350});
  await capture(page, 'cluster-information.jpg', {fullPage: false});
  await page.setViewportSize({width: 1440, height: 1000});
  await statusDrawer.getByRole('button', {name: /close|关闭/i}).click();
  await statusDrawer.waitFor({state: 'hidden'});
  mark('集群节点可搜索并查看地址、启用及健康、三项使用率、虚拟机和账号数量配额');

  await searchList(nodesTitle, '/nodes', '');
  const nodeName = 'UI 集群配置节点';
  const nodeHost = 'node-ui.example.test';
  await page.getByRole('button', {name: '新增节点', exact: true}).click();
  const nodeCreate = page.getByRole('dialog', {name: '新增节点'});
  await formField(nodeCreate, '节点名称').getByRole('textbox').fill(nodeName);
  await formField(nodeCreate, '节点地址').getByRole('textbox').fill(nodeHost);
  await nodeCreate.getByRole('button', {name: '保存节点'}).click();
  await nodeCreate.waitFor({state: 'hidden'});
  const nodeRow = rowNamed(nodeName);
  await nodeRow.getByRole('button', {name: '编辑', exact: true}).click();
  const nodeEdit = page.getByRole('dialog', {name: '编辑节点'});
  const configuredHost = 'edited-ui.example.test';
  await formField(nodeEdit, '节点地址').getByRole('textbox').fill(configuredHost);
  await formField(nodeEdit, '节点内存 (MB)').getByRole('spinbutton').fill('8192');
  await formField(nodeEdit, '账号配额').getByRole('spinbutton').fill('12');
  await formField(nodeEdit, '虚拟机配额').getByRole('spinbutton').fill('5');
  await page.setViewportSize({width: 1440, height: 1150});
  await capture(page, 'cluster-configuration.jpg', {fullPage: false});
  await page.setViewportSize({width: 1440, height: 1000});
  await nodeEdit.getByRole('button', {name: '保存节点'}).click();
  await nodeEdit.waitFor({state: 'hidden'});
  const editedNodes = await searchList(nodesTitle, '/nodes', configuredHost);
  assert.equal(editedNodes.total, 1);
  const [editedNode] = editedNodes.items;
  assert.equal(editedNode.totalMemoryMb, 8192);
  assert.equal(editedNode.accountQuota, 12);
  assert.equal(editedNode.virtualMachineQuota, 5);
  await nodeRow.getByRole('cell', {name: '0 / 12', exact: true}).waitFor();
  await nodeRow.getByRole('cell', {name: '0 / 5', exact: true}).waitFor();
  await nodeRow.getByRole('button', {name: '停用', exact: true}).click();
  await page.getByRole('dialog', {name: '停用节点'}).getByRole('button', {name: '确认停用'}).click();
  await nodeRow.getByRole('cell', {name: '已停用', exact: true}).waitFor();
  await nodeRow.getByRole('button', {name: '启用', exact: true}).click();
  await nodeRow.getByRole('cell', {name: '允许分配', exact: true}).waitFor();

  const configurationUrl = `/nodes/${editedNode.id}/configuration`;
  await nodeRow.getByRole('button', {name: '系统配置', exact: true}).click();
  const configuration = page.getByRole('dialog', {name: `${nodeName} · 系统配置`});
  const ip = '192.0.2.20';
  const subnetMask = '255.255.255.0';
  const gateway = '192.0.2.1';
  await formField(configuration, 'IPv4 地址').getByRole('textbox').fill(ip);
  await formField(configuration, '子网掩码').getByRole('textbox').fill(subnetMask);
  await formField(configuration, '默认网关').getByRole('textbox').fill(gateway);
  await configuration.locator('.el-switch__core').click();
  await configuration.locator('input[type="datetime-local"]').fill('2030-01-01T12:00');
  await capture(page, 'simulated-node-network-time.jpg', {fullPage: false});
  await configuration.getByRole('button', {name: '保存模拟配置'}).click();
  await configuration.waitFor({state: 'hidden'});
  const configuredNode = await adminApi(configurationUrl);
  assert.equal(configuredNode.ipAddress, ip);
  assert.equal(configuredNode.subnetMask, subnetMask);
  assert.equal(configuredNode.gateway, gateway);
  assert.ok(Math.abs(Date.parse(configuredNode.systemTime) - Date.parse('2030-01-01T12:00:00+08:00')) < 10000);
  mark('通过系统配置页面保存模拟节点 IPv4、掩码、网关和北京时间');

  await deleteRow(nodeRow);
  mark('集群节点新增、编辑地址/内存/两种配额、停启和删除');

  await page.locator('.nav-group button', {hasText: '学生管理'}).click();
  await page.waitForSelector('h1:has-text("学生")', {timeout: 30000});
  await page.getByText('实训学生', {exact: true}).first().waitFor();
  await capture(page, 'student-import-entry.jpg');
  await page.click('button:has-text("批量导入")');
  await page.waitForSelector('.import-dialog', {timeout: 30000});
  const started = Date.now();
  await page.setInputFiles('input[type="file"]', path.join(runDirectory, 'students-10000.csv'));
  await page.waitForSelector('.import-pager', {timeout: 180000});
  await page.waitForTimeout(600);
  report.previewMilliseconds = Date.now() - started;
  report.summary = (await page.locator('.import-summary').innerText()).replace(/\s+/g, ' ').trim();
  report.pager = (await page.locator('.import-pager').innerText()).replace(/\s+/g, ' ').trim();
  const rows = page.locator('.import-dialog .el-table__body tbody tr');
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
  assert.ok(await page.locator('.import-dialog .el-table__body tbody tr .invalid-cell').count() > 0, '问题单元格未标红');
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
  try {
    if (child?.pid && child.exitCode === null && child.signalCode === null) {
      const exited = once(child, 'exit');
      child.kill();
      await exited;
    }
    if (!process.env.E2E_KEEP) {
      await rm(runDirectory, {recursive: true, force: true, maxRetries: 5, retryDelay: 100});
    }
  } catch (error) {
    report.cleanupError = error instanceof Error ? error.message : String(error);
    report.status = 'failed';
    process.exitCode = 1;
  }
  report.passed = passed.length;
  report.checks = passed;
  report.completedAt = new Date().toISOString();
  try { report.testedAssemblySha256 = createHash('sha256').update(await readFile(appDll)).digest('hex'); } catch { /* executable-only deployments */ }
  try {
    report.testedIndexSha256 = createHash('sha256').update(await readFile(path.join(appDirectory, 'wwwroot', 'index.html'))).digest('hex');
  } catch { /* development builds may serve the index from the source tree */ }
  await mkdir(path.dirname(reportPath), {recursive: true});
  await writeFile(reportPath, JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
}
