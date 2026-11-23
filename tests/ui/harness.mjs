/**
 * 界面回归的公共设施：起一个临时数据目录的服务、登录不同角色、开浏览器、记断言。
 *
 * 设计约束：
 *  - 每个套件拿到的都是同一个服务实例，数据互不影响由套件自己保证（各自开 context）；
 *  - 服务与浏览器都由 run.mjs 负责回收，套件里不要 close；
 *  - 不写仓库内文件，数据目录建在系统临时目录里。
 */
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
import { cp, mkdir, mkdtemp, readdir, rm } from 'node:fs/promises'
import { existsSync } from 'node:fs'
import net from 'node:net'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const require = createRequire(import.meta.url)

export const ADMIN = { userName: 'admin', password: 'UiAdmin2026x', schoolName: '界面回归学校', displayName: '管理员' }
export const STUDENT = { userName: 'ui-student', password: 'UiStudent2026x', initial: 'UiStudent2026a', name: '回归学生' }

export function loadPlaywright() {
  for (const candidate of ['playwright-core', path.join(root, '.local/e2e/node_modules/playwright-core')]) {
    try {
      return require(candidate)
    } catch {
      /* try next */
    }
  }
  throw new Error('未找到 playwright-core，可在项目根目录执行：npm install --no-save playwright-core')
}

export async function findBrowser() {
  if (process.env.CHROME_PATH) return process.env.CHROME_PATH
  const playwrightRoot = path.join(process.env.LOCALAPPDATA ?? '', 'ms-playwright')
  try {
    for (const entry of await readdir(playwrightRoot)) {
      if (!entry.startsWith('chromium-')) continue
      const candidate = path.join(playwrightRoot, entry, 'chrome-win64', 'chrome.exe')
      if (existsSync(candidate)) return candidate
    }
  } catch {
    /* fall through to Edge */
  }
  const edge = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'
  if (existsSync(edge)) return edge
  throw new Error('未找到 Chromium/Edge，请设置 CHROME_PATH')
}

async function freePort() {
  const server = net.createServer()
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
  const port = server.address().port
  await new Promise(resolve => server.close(resolve))
  return port
}

export async function api(base, endpoint, { method = 'GET', body, jar = '', csrf = '' } = {}) {
  const headers = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (jar) headers.cookie = jar
  if (csrf) headers['X-CSRF-TOKEN'] = csrf
  const response = await fetch(`${base}/api${endpoint}`, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body)
  })
  const text = await response.text()
  let data = null
  try {
    data = text ? JSON.parse(text) : null
  } catch {
    data = text
  }
  return { status: response.status, data, jar: response.headers.getSetCookie().map(item => item.split(';')[0]).join('; ') }
}

export async function startServer() {
  const appDir = path.join(root, 'src/School.Training')
  const exe = path.join(appDir, 'bin/Debug/net10.0/School.Training.exe')
  if (!existsSync(exe)) throw new Error(`未找到服务可执行文件 ${exe}，请先 dotnet build src/School.Training`)
  const dist = path.join(root, 'web/dist/index.html')
  if (!existsSync(dist)) throw new Error('未找到 web/dist，请先 npm --prefix web run build')

  const wwwroot = path.join(appDir, 'wwwroot')
  await mkdir(wwwroot, { recursive: true })
  await cp(path.join(root, 'web/dist'), wwwroot, { recursive: true, force: true })

  const dataDir = await mkdtemp(path.join(os.tmpdir(), 'school-ui-'))
  const port = await freePort()
  const base = `http://127.0.0.1:${port}`
  const child = spawn(exe, ['--urls', base], {
    cwd: appDir,
    env: { ...process.env, SCHOOL_DATA_DIR: dataDir },
    stdio: ['ignore', 'pipe', 'pipe']
  })
  const logs = []
  child.stdout.on('data', chunk => logs.push(String(chunk)))
  child.stderr.on('data', chunk => logs.push(String(chunk)))

  const deadline = Date.now() + 60_000
  let ready = false
  while (Date.now() < deadline) {
    try {
      if ((await fetch(`${base}/api/status`)).ok) {
        ready = true
        break
      }
    } catch {
      /* not listening yet */
    }
    await new Promise(resolve => setTimeout(resolve, 250))
  }
  if (!ready) {
    child.kill()
    throw new Error(`服务在 60 秒内未就绪：\n${logs.join('').slice(-2000)}`)
  }

  return {
    base,
    async stop() {
      child.kill()
      await new Promise(resolve => {
        const timer = setTimeout(resolve, 5000)
        child.once('exit', () => {
          clearTimeout(timer)
          resolve()
        })
      })
      await rm(dataDir, { recursive: true, force: true })
    }
  }
}

export async function openBrowser() {
  const { chromium } = loadPlaywright()
  return chromium.launch({ executablePath: await findBrowser(), headless: true })
}

function cookieList(jar) {
  return jar.split('; ').map(pair => {
    const index = pair.indexOf('=')
    return { name: pair.slice(0, index), value: pair.slice(index + 1), domain: '127.0.0.1', path: '/' }
  })
}

export async function openPage(browser, base, jar, { viewport = { width: 1360, height: 900 }, isMobile = false } = {}) {
  const context = await browser.newContext({ viewport, reducedMotion: 'reduce', isMobile, hasTouch: isMobile })
  if (jar) await context.addCookies(cookieList(jar))
  const page = await context.newPage()
  const consoleErrors = []
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text())
  })
  const failedRequests = []
  page.on('requestfailed', request => failedRequests.push(`${request.url()} ${request.failure()?.errorText || ''}`))
  return { context, page, consoleErrors, failedRequests }
}

export async function visit(page, base, hash, { wait = 400 } = {}) {
  await page.goto(`${base}/${hash}`, { waitUntil: 'networkidle' })
  await page.waitForTimeout(wait)
}

/** 建立管理员会话：未初始化时走 setup，否则走登录。 */
export async function signInAdmin(base) {
  const status = await api(base, '/status')
  const response = status.data?.initialized
    ? await api(base, '/auth/login', { method: 'POST', body: { userName: ADMIN.userName, password: ADMIN.password } })
    : await api(base, '/setup', {
        method: 'POST',
        body: {
          schoolName: ADMIN.schoolName,
          userName: ADMIN.userName,
          displayName: ADMIN.displayName,
          password: ADMIN.password
        }
      })
  if (response.status !== 200) throw new Error(`管理员会话建立失败：${JSON.stringify(response.data)}`)
  return { jar: response.jar, csrf: response.data.csrfToken, user: response.data.user, schoolName: status.data?.schoolName }
}

/** 固定学生账号：首次运行创建并改掉初始密码，之后直接登录。 */
export async function signInStudent(base, admin) {
  const existing = await api(base, '/auth/login', {
    method: 'POST',
    body: { userName: STUDENT.userName, password: STUDENT.password }
  })
  if (existing.status === 200) return { jar: existing.jar, csrf: existing.data.csrfToken, user: existing.data.user }

  const created = await api(base, '/people', {
    method: 'POST',
    jar: admin.jar,
    csrf: admin.csrf,
    body: {
      kind: 'student',
      name: STUDENT.name,
      userName: STUDENT.userName,
      number: 'UI0001',
      role: 'Student',
      password: STUDENT.initial
    }
  })
  if (created.status !== 200 && created.status !== 409) throw new Error(`创建学生失败：${JSON.stringify(created.data)}`)

  const first = await api(base, '/auth/login', {
    method: 'POST',
    body: { userName: STUDENT.userName, password: STUDENT.initial }
  })
  if (first.status !== 200) throw new Error(`学生初始登录失败：${JSON.stringify(first.data)}`)
  if (!first.data.user?.mustChangePassword) return { jar: first.jar, csrf: first.data.csrfToken, user: first.data.user }

  const changed = await api(base, '/auth/password', {
    method: 'POST',
    jar: first.jar,
    csrf: first.data.csrfToken,
    body: { currentPassword: STUDENT.initial, newPassword: STUDENT.password }
  })
  if (changed.status !== 200) throw new Error(`学生改密失败：${JSON.stringify(changed.data)}`)
  const again = await api(base, '/auth/login', {
    method: 'POST',
    body: { userName: STUDENT.userName, password: STUDENT.password }
  })
  if (again.status !== 200) throw new Error(`学生登录失败：${JSON.stringify(again.data)}`)
  return { jar: again.jar, csrf: again.data.csrfToken, user: again.data.user }
}

/** 分页类用例需要多于一页的数据，按需补齐到 count 条。 */
export async function ensureStudents(base, admin, count) {
  const first = await api(base, '/people?kind=student&page=1&pageSize=1', { jar: admin.jar })
  let total = first.data?.total ?? 0
  for (let index = total; index < count; index++) {
    const suffix = String(index + 1).padStart(2, '0')
    const created = await api(base, '/people', {
      method: 'POST',
      jar: admin.jar,
      csrf: admin.csrf,
      body: {
        kind: 'student',
        name: `回归学生${suffix}`,
        userName: `ui-student-${suffix}`,
        number: `UI${String(index + 1).padStart(4, '0')}`,
        role: 'Student',
        password: STUDENT.initial
      }
    })
    if (created.status !== 200 && created.status !== 409) throw new Error(`批量造学生失败：${JSON.stringify(created.data)}`)
    total = index + 1
  }
  return total
}

export function createChecker() {
  const results = []
  return {
    check(label, ok, detail = '') {
      results.push({ label, ok })
      console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? ' :: ' + detail : ''}`)
    },
    get results() {
      return results
    },
    summary() {
      const failed = results.filter(item => !item.ok)
      return { total: results.length, failed: failed.length, labels: failed.map(item => item.label) }
    }
  }
}
