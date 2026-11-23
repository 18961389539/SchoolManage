import { openPage, visit } from '../harness.mjs'

export const name = 'dialogs'
export const title = '放弃确认：干净时不打扰，改动后 X 与 Esc 都要确认'

export async function run({ base, browser, admin, check }) {
  const { context, page, consoleErrors } = await openPage(browser, base, admin.jar)
  const dialog = page.locator('.el-dialog:visible')
  const messageBox = page.locator('.el-message-box')

  await visit(page, base, '#/courses', { wait: 500 })

  // 干净表单：取消直接关，不弹确认。
  await page.getByRole('button', { name: '新建课程' }).click()
  await dialog.waitFor()
  await dialog.getByRole('button', { name: '取消' }).click()
  await page.waitForTimeout(700)
  check('未修改时取消不打扰', (await page.locator('.el-message-box').count()) === 0 && (await dialog.count()) === 0)

  // 改了内容：点 X 应该拦一次。
  await page.getByRole('button', { name: '新建课程' }).click()
  await dialog.waitFor()
  await page.getByPlaceholder('例如：计算机网络基础').fill('临时课程')
  await dialog.locator('.el-dialog__headerbtn').click()
  await messageBox.waitFor()
  check('修改后关闭会确认', (await messageBox.innerText()).includes('放弃未保存的修改'))
  await messageBox.getByRole('button', { name: '继续编辑' }).click()
  await page.waitForTimeout(300)
  check(
    '选择继续编辑保留输入',
    (await dialog.isVisible()) && (await page.getByPlaceholder('例如：计算机网络基础').inputValue()) === '临时课程'
  )

  await dialog.locator('.el-dialog__headerbtn').click()
  await messageBox.waitFor()
  await messageBox.getByRole('button', { name: '放弃修改' }).click()
  await page.waitForTimeout(500)
  check('确认放弃后关闭', (await dialog.count()) === 0)

  // 实体表单：Esc 关闭同样要确认。
  await visit(page, base, '#/students', { wait: 600 })
  await page.getByRole('button', { name: '新增学生' }).click()
  await dialog.waitFor()
  await page.getByPlaceholder('请输入姓名').fill('临时学生')
  await page.keyboard.press('Escape')
  await messageBox.waitFor()
  check('实体表单 Esc 也走确认', (await messageBox.innerText()).includes('放弃未保存的修改'))
  await messageBox.getByRole('button', { name: '放弃修改' }).click()
  await page.waitForTimeout(500)
  check('实体表单确认后关闭', (await dialog.count()) === 0)

  check('无控制台错误', consoleErrors.length === 0, consoleErrors.slice(0, 3).join(' | '))
  await context.close()
}
