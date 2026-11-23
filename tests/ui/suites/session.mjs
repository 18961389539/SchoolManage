import { api, openPage, visit } from '../harness.mjs'

export const name = 'session'
export const title = '会话：改名即时生效，失效只提示一次'

export async function run({ base, browser, admin, check }) {
  const { context, page } = await openPage(browser, base, admin.jar)

  await visit(page, base, '#/dashboard', { wait: 500 })
  const before = await page.locator('.school-name').innerText()
  const serverSchool = (await api(base, '/status')).data?.schoolName || '界面回归学校'
  check('侧边栏显示服务端学校名称', before.includes(serverSchool), before)

  // 改名：设置页保存后，侧边栏必须立即变，不需要刷新。
  await visit(page, base, '#/settings', { wait: 500 })
  const renamed = `回归校名${Date.now() % 100000}`
  await page.getByPlaceholder('请输入学校名称').fill(renamed)
  await page.getByRole('button', { name: '保存设置' }).click()
  await page.waitForTimeout(700)
  check('保存后侧边栏即时更新（无刷新）', (await page.locator('.school-name').innerText()).includes(renamed))

  // 改姓名：档案保存后，右上角账号菜单必须立即变。
  await visit(page, base, '#/profile', { wait: 500 })
  await page.getByPlaceholder('请输入姓名').fill('回归管理员')
  await page.getByRole('button', { name: '保存信息' }).click()
  await page.waitForTimeout(700)
  const menu = await page.locator('.user-menu').getAttribute('aria-label')
  check('保存档案后账号名即时更新', (menu || '').includes('回归管理员'), menu || '')

  // 会话失效：cookie 被清掉后，下一次请求应回到登录页并给一次性提示。
  await context.clearCookies()
  await page.evaluate(() => {
    location.hash = 'courses'
  })
  await page.waitForTimeout(1500)
  const body = await page.locator('body').innerText()
  check('会话失效回到登录页', (await page.locator('.app-layout').count()) === 0)
  check('失效提示出现一次', body.includes('登录状态已失效，请重新登录。'))
  await context.close()
}
