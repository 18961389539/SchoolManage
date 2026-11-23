import { openPage, visit } from '../harness.mjs'

export const name = 'requests'
export const title = '请求生命周期：首屏不被自己取消，切页取消在读请求'

export async function run({ base, browser, admin, check }) {
  const { context, page, consoleErrors, failedRequests } = await openPage(browser, base, admin.jar)

  // 登录后的首屏：hash 落在当前页时不能取消它自己的首次加载。
  await page.goto(`${base}/#dashboard`, { waitUntil: 'domcontentloaded' })
  await page.waitForSelector('.app-layout', { timeout: 15000 })
  await page.waitForTimeout(1200)
  const cards = await page.locator('.main-content .surface, .main-content .stat-card').count()
  check('首屏数据加载完成（未被自身导航取消）', cards > 0, `cards=${cards}`)
  check('首屏无错误提示', (await page.locator('.el-message--error').count()) === 0)

  // 启动读取属于外壳：/status 在途时切页，不能把启动流程取消在骨架屏里。
  let statusStarted = false
  let markStatusStarted = () => {}
  const statusStartedSignal = new Promise(resolve => (markStatusStarted = resolve))
  await page.route('**/api/status', async route => {
    statusStarted = true
    markStatusStarted()
    await new Promise(resolve => setTimeout(resolve, 2000))
    await route.continue().catch(() => {})
  })
  await page.reload({ waitUntil: 'domcontentloaded' })
  await Promise.race([statusStartedSignal, page.waitForTimeout(5000)])
  check('启动状态请求已发出', statusStarted)
  await page.evaluate(() => {
    location.hash = 'students'
  })
  await page.waitForSelector('.app-layout', { timeout: 8000 })
  await page.waitForTimeout(600)
  check('启动请求不被同时的切页取消', (await page.locator('.main-content').innerText()).includes('学生管理'))
  check('启动过程未停留在骨架屏', (await page.locator('.startup-screen').count()) === 0)
  await page.unroute('**/api/status')

  // 读请求在途时切页：新页面正常渲染，且不冒出上一个页面的错误。
  await page.route('**/api/courses?**', async route => {
    await new Promise(resolve => setTimeout(resolve, 2500))
    await route.continue().catch(() => {})
  })
  await page.goto(`${base}/#courses`, { waitUntil: 'domcontentloaded' })
  await page.waitForTimeout(150)
  await page.evaluate(() => {
    location.hash = 'students'
  })
  await page.waitForTimeout(3200)
  check('切页后新页面可用', (await page.locator('.main-content').innerText()).includes('学生管理'))
  const messages = await page.locator('.el-message--error').allInnerTexts()
  check('被取消的读请求不弹错误', messages.length === 0, messages.join(' | '))
  check(
    '在途读请求确实被取消',
    failedRequests.some(item => item.includes('/api/courses') && item.includes('ABORTED')),
    failedRequests.slice(0, 2).join(' | ')
  )
  await page.unroute('**/api/courses?**')

  await visit(page, base, '#/courses', { wait: 800 })
  check('返回课程页可正常加载', (await page.locator('.main-content').innerText()).includes('课程管理'))
  check('无控制台错误', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '))
  await context.close()
}
