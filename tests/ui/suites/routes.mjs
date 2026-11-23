import { openPage, visit } from '../harness.mjs'

export const name = 'routes'
export const title = '路由语义：404 与 403 必须说出来'

export async function run({ base, browser, admin, check }) {
  const { context, page, consoleErrors } = await openPage(browser, base, admin.jar)
  const notice = page.locator('.route-notice')

  await visit(page, base, '#/dashboard')
  check('控制台可打开', await page.locator('.app-layout').isVisible())

  await visit(page, base, '#/my-exams')
  const forbidden = await notice.innerText()
  check('学生页面被管理员访问 -> 403 提示', forbidden.includes('无权访问'), forbidden.replace(/\n+/g, ' / '))
  check('403 带上页面名称', forbidden.includes('我的考试'))
  check('403 不再静默跳首页', page.url().endsWith('#/my-exams'), page.url())

  await visit(page, base, '#/no-such-page')
  const missing = (await notice.innerText()).replace(/\n+/g, ' / ')
  check('未知地址 -> 404 提示', missing.includes('页面不存在'), missing)
  check('404 显示请求地址', missing.includes('#/no-such-page'))
  check('404 面包屑跟随', (await page.locator('.breadcrumb').innerText()).includes('页面不存在'))

  await page.locator('.route-notice-actions button').first().click()
  await page.waitForTimeout(500)
  check(
    '404 的返回首页可用',
    /#\/?dashboard$/.test(page.url()) && (await page.locator('.app-layout').isVisible()),
    page.url()
  )

  for (const [hash, heading] of [
    ['#/virtual-machines', '虚拟机管理'],
    ['#/students', '学生管理'],
    ['#/server', '服务器信息']
  ]) {
    await visit(page, base, hash)
    const actual = await page.locator('h1').first().innerText()
    check(`${heading} 仍可达`, (await notice.count()) === 0 && actual.includes(heading), actual)
  }

  check('无控制台错误', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '))
  await context.close()
}
