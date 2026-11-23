import { openPage, visit } from '../harness.mjs'

export const name = 'pages'
export const title = '页面矩阵：每个路由都能渲染，且参数传对了'

const ADMIN_PAGES = [
  ['#/dashboard', ''],
  ['#/courses', '课程管理'],
  ['#/students', '学生管理'],
  ['#/classes', '班级管理'],
  ['#/server', '服务器信息'],
  ['#/audits', '操作日志'],
  ['#/virtual-machines', '虚拟机管理'],
  ['#/exam-questions', '题库管理'],
  ['#/exam-papers', '试卷管理'],
  ['#/exams', '考试管理'],
  ['#/camp-positions', '岗位训练营'],
  ['#/camp-reports', '能力报告'],
  ['#/camp-records', '测评记录'],
  ['#/camp-monitor', '测评监控'],
  ['#/resources-overview', '资源概览'],
  ['#/profile', '个人中心']
]

const STUDENT_PAGES = [
  ['#/student-home', ''],
  ['#/my-exams', '我的考试'],
  ['#/skill-graph', '技能图谱'],
  ['#/resources-overview', '资源概览'],
  ['#/profile', '个人中心']
]

export async function run({ base, browser, admin, student, check }) {
  const adminView = await openPage(browser, base, admin.jar)
  for (const [hash, heading] of ADMIN_PAGES) {
    await visit(adminView.page, base, hash, { wait: 300 })
    const notice = await adminView.page.locator('.route-notice').count()
    const actual = heading ? await adminView.page.locator('h1').first().innerText() : ''
    check(`管理员页面 ${hash.slice(2)}`, notice === 0 && (!heading || actual.includes(heading)), actual || `notice=${notice}`)
  }

  // 同一组件换 key：实体表与运维页必须跟着 key 变标题。
  await visit(adminView.page, base, '#/students')
  await adminView.page.evaluate(() => {
    location.hash = 'classes'
  })
  await adminView.page.waitForTimeout(600)
  check(
    '实体页切换后标题跟随',
    (await adminView.page.locator('h1').first().innerText()).includes('班级管理')
  )
  await visit(adminView.page, base, '#/server')
  await adminView.page.evaluate(() => {
    location.hash = 'audits'
  })
  await adminView.page.waitForTimeout(600)
  check('运维页切换后标题跟随', (await adminView.page.locator('h1').first().innerText()).includes('操作日志'))

  await visit(adminView.page, base, '#/my-resources', { wait: 300 })
  check(
    '管理员访问 my-resources 为 403',
    (await adminView.page.locator('.route-notice').innerText()).includes('无权访问')
  )

  const studentView = await openPage(browser, base, student.jar)
  for (const [hash, heading] of STUDENT_PAGES) {
    await visit(studentView.page, base, hash, { wait: 300 })
    const notice = await studentView.page.locator('.route-notice').count()
    const actual = heading ? await studentView.page.locator('h1').first().innerText() : ''
    check(`学生页面 ${hash.slice(2)}`, notice === 0 && (!heading || actual.includes(heading)), actual || `notice=${notice}`)
  }
  await visit(studentView.page, base, '#/courses', { wait: 300 })
  check(
    '学生访问管理员页仍为 403',
    (await studentView.page.locator('.route-notice').innerText()).includes('无权访问')
  )

  check('管理员视图无控制台错误', adminView.consoleErrors.length === 0, adminView.consoleErrors.slice(0, 3).join(' | '))
  check('学生视图无控制台错误', studentView.consoleErrors.length === 0, studentView.consoleErrors.slice(0, 3).join(' | '))
  await adminView.context.close()
  await studentView.context.close()
}
