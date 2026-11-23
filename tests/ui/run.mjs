/**
 * 界面回归入口：起临时服务 → 建管理员/学生会话 → 按顺序跑各套件 → 汇总退出码。
 *
 *   npm --prefix web run test:ui
 *   npm --prefix web run test:ui -- --only=lists
 *   CHROME_PATH=... npm --prefix web run test:ui
 *
 * 前置：dotnet build src/School.Training && npm --prefix web run build
 */
import { createChecker, ensureStudents, openBrowser, signInAdmin, signInStudent, startServer } from './harness.mjs'
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
if (failure) {
  console.error(`\n运行失败：${failure.message || failure}`)
  process.exit(1)
}
console.log(`\n${failed ? 'FAILED' : 'OK'}  ${total - failed}/${total} 项通过`)
if (failed) {
  console.log(`未通过：\n${labels.map(label => `  - ${label}`).join('\n')}`)
  process.exit(1)
}
