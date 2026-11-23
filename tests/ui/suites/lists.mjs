import { openPage, visit } from '../harness.mjs'

export const name = 'lists'
export const title = '列表状态：分页与搜索跨页面保留，且按列表隔离'

export async function run({ base, browser, admin, check }) {
  const { context, page, consoleErrors } = await openPage(browser, base, admin.jar)
  const searchBox = page.getByPlaceholder('输入关键词搜索')
  const activePage = () => page.locator('.el-pager li.is-active').innerText()

  await visit(page, base, '#/students', { wait: 500 })
  const pages = await page.locator('.el-pager li.number').count()
  check('学生列表有多页数据', pages >= 2, `pager=${pages}`)

  // 点第 2 页后立刻离开：落盘靠卸载时的 flush。
  await page.locator('.el-pager li.number', { hasText: '2' }).click()
  await page.waitForTimeout(120)
  await page.evaluate(() => {
    location.hash = 'classes'
  })
  await page.waitForTimeout(500)
  await page.evaluate(() => {
    location.hash = 'students'
  })
  await page.waitForTimeout(700)
  check('离开时保存的分页回来仍停在第 2 页', (await activePage()) === '2', await activePage())

  // 已提交的搜索词：提交后立刻离开，回来时输入框应该还在。
  await searchBox.fill('回归学生1')
  await page.getByRole('button', { name: '搜索', exact: true }).first().click()
  await page.evaluate(() => {
    location.hash = 'classes'
  })
  await page.waitForTimeout(500)
  await page.evaluate(() => {
    location.hash = 'students'
  })
  await page.waitForTimeout(700)
  check('离开时保存的已提交搜索词回来仍在', (await searchBox.inputValue()) === '回归学生1', await searchBox.inputValue())

  // 页面隔离：课程页的筛选不能串到学生页。
  await visit(page, base, '#/courses', { wait: 500 })
  await page.getByPlaceholder('搜索课程名称或简介').fill('网络基础')
  await page.waitForTimeout(500)
  await page.evaluate(() => {
    location.hash = 'students'
  })
  await page.waitForTimeout(600)
  check('课程页的搜索不会串到学生页', (await searchBox.inputValue()) === '回归学生1', await searchBox.inputValue())
  await page.evaluate(() => {
    location.hash = 'courses'
  })
  await page.waitForTimeout(700)
  check(
    '课程页自己的搜索仍在',
    (await page.getByPlaceholder('搜索课程名称或简介').inputValue()) === '网络基础'
  )

  check('无控制台错误', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '))
  await context.close()
}
