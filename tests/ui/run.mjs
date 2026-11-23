/**
 * 界面回归入口：起临时服务 → 建管理员/学生会话 → 按顺序跑各套件 → 汇总退出码。
 *
 *   npm --prefix web run test:ui
 *   npm --prefix web run test:ui -- --only=lists
 *   CHROME_PATH=... npm --prefix web run test:ui
 *
 * 前置：dotnet build src/School.Training && npm --prefix web run build
 */
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { createChecker, ensureStudents, loadAxe, openBrowser, openPage, root, signInAdmin, signInStudent, startServer, visit } from './harness.mjs'
import * as routes from './suites/routes.mjs'
import * as requests from './suites/requests.mjs'
import * as pages from './suites/pages.mjs'
import * as lists from './suites/lists.mjs'
import * as dialogs from './suites/dialogs.mjs'
import * as session from './suites/session.mjs'

// 顺序有讲究：会话套件会改学校名并清 cookie，放最后。
const SUITES = [routes, requests, pages, lists, dialogs, session]

const only = process.argv.find(argument => argument.startsWith('--only='))?.slice('--only='.length)
const selected = only ? SUITES.filter(suite => suite.name === only) : SUITES
if (!selected.length) {
  console.error(`没有匹配的套件：${only}（可选：${SUITES.map(suite => suite.name).join(' / ')}）`)
  process.exit(2)
}

const checker = createChecker()
const accessibility = { axeAvailable: false, pages: [], totalViolations: 0 }
let server
let browser
let failure = null

try {
  server = await startServer()
  browser = await openBrowser()
  console.log(`服务：${server.base}`)
  const admin = await signInAdmin(server.base)
  const student = await signInStudent(server.base, admin)
  await ensureStudents(server.base, admin, 25)

  // Accessibility findings are recorded and printed but never counted as suite failures.
  const axePath = loadAxe()
  if (axePath) {
    accessibility.axeAvailable = true
    // The app sends `script-src 'self'`; page.addScriptTag would be blocked. Playwright's
    // evaluate runs through the debugger protocol, which is not subject to page CSP.
    const axeSource = await readFile(axePath, 'utf8')
    const targets = [
      ['#/dashboard', '控制台'],
      ['#/students', '学生管理'],
      ['#/exams', '考试管理'],
      ['#/audits', '操作日志']
    ]
    let view
    try {
      view = await openPage(browser, server.base, admin.jar)
      for (const [hash, label] of targets) {
        try {
          await visit(view.page, server.base, hash, { wait: 300 })
          await view.page.evaluate(axeSource)
          const result = await view.page.evaluate(async () =>
            await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } })
          )
          const violations = result.violations.map(item => ({
            id: item.id,
            impact: item.impact,
            help: item.help,
            nodes: item.nodes.length
          }))
          const nodes = violations.reduce((sum, item) => sum + item.nodes, 0)
          accessibility.pages.push({ page: hash, label, violations })
          accessibility.totalViolations += nodes
          console.log(`A11Y ${label}: ${violations.length} 类 / ${nodes} 个节点（仅报告）`)
          for (const item of violations.slice(0, 3))
            console.log(`  - ${item.id} (${item.impact || 'n/a'}) × ${item.nodes}: ${item.help}`)
        } catch (error) {
          accessibility.pages.push({ page: hash, label, error: String(error?.message || error) })
          console.log(`A11Y ${label}: 扫描失败（仅报告） ${error?.message || error}`)
        }
      }
    } catch (error) {
      console.log(`A11Y 扫描未执行（仅报告）：${error?.message || error}`)
    } finally {
      await view?.context.close().catch(() => {})
    }
  } else {
    console.log('未找到 axe-core，跳过无障碍扫描（npm install --prefix .local/e2e --no-save axe-core）')
  }

  for (const suite of selected) {
    const started = Date.now()
    console.log(`\n── ${suite.title}`)
    try {
      await suite.run({ base: server.base, browser, admin, student, check: checker.check })
    } catch (error) {
      checker.check(`${suite.title}（套件异常）`, false, String(error?.message || error).split('\n')[0])
    }
    console.log(`   ${suite.name} · ${((Date.now() - started) / 1000).toFixed(1)}s`)
  }
} catch (error) {
  failure = error
} finally {
  await browser?.close().catch(() => {})
  await server?.stop().catch(() => {})
}

const { total, failed, labels } = checker.summary()
const reportPath = process.env.UI_SUITE_REPORT_PATH ?? path.join(root, '.local/verification/ui-suite.json')
await mkdir(path.dirname(reportPath), { recursive: true })
await writeFile(
  reportPath,
  JSON.stringify(
    {
      status: failure || failed ? 'failed' : 'passed',
      suites: selected.map(suite => suite.name),
      total,
      failed,
      labels,
      failure: failure ? String(failure.message || failure) : null,
      accessibility,
      completedAtUtc: new Date().toISOString()
    },
    null,
    2
  ) + '\n',
  'utf8'
)
if (failure) {
  console.error(`\n运行失败：${failure.message || failure}`)
  process.exit(1)
}
console.log(`\n${failed ? 'FAILED' : 'OK'}  ${total - failed}/${total} 项通过`)
if (failed) {
  console.log(`未通过：\n${labels.map(label => `  - ${label}`).join('\n')}`)
  process.exit(1)
}
