import assert from 'node:assert/strict';

export async function runNodeAccountUiChecks({page, ui, adminApi, mark, capture, password}) {
  const quota = 11;
  const fixtureCount = 12;
  const nodeName = '账号跨页验收节点';
  const keyword = 'p8_node_ui_';
  const node = await adminApi('/nodes', 'POST', {name: nodeName, provider: 'simulated', accountQuota: quota});
  const people = [];
  for (let index = 1; index <= fixtureCount; index++) {
    const sequence = String(index).padStart(2, '0');
    const kind = index % 2 === 0 ? 'teacher' : 'student';
    people.push(await adminApi('/people', 'POST', {
      name: `跨页验收${sequence}`, userName: keyword + sequence, number: `QA-NODE-${sequence}`,
      kind, role: kind === 'teacher' ? 'Teacher' : 'Student', password
    }));
  }
  const endpoint = `/nodes/${node.id}/accounts`;
  const candidateEndpoint = `/nodes/${node.id}/account-candidates`;
  const {openPage, rowNamed, listResponse} = ui;
  const button = (scope, name) => scope.getByRole('button', {name, exact: true});
  const row = (panel, account) => panel.getByRole('row').filter({
    has: page.getByText(account.userName, {exact: true})
  });
  const accountCheckbox = (panel, account) => row(panel, account).locator('label.el-checkbox');
  const screenshot = name => capture(page, `node-accounts-${name}.jpg`, {fullPage: false});
  const pageButton = (panel, direction) => panel.locator(`.node-account-pagination .btn-${direction}`);
  const activePage = panel => panel.locator('.el-pager .is-active');

  await openPage('实训节点');
  const nodeRow = rowNamed(nodeName);
  await nodeRow.getByRole('cell', {name: '0 / 11', exact: true}).waitFor();
  await button(nodeRow, '分配账号').click();
  const dialog = page.getByRole('dialog', {name: `${nodeName} · 分配账号`});
  const assignedTab = count => dialog.getByRole('tab', {name: `已分配账号（${count}）`, exact: true});
  const candidatesTab = dialog.getByRole('tab', {name: '添加师生账号', exact: true});
  const assignedPanel = dialog.getByRole('tabpanel', {name: /已分配账号/});
  const candidatesPanel = dialog.getByRole('tabpanel', {name: '添加师生账号', exact: true});
  const selection = count => candidatesPanel.getByText(`已选 ${count} 项`, {exact: true});
  const assignButton = count => button(candidatesPanel, `分配选中账号（${count}）`);
  const candidatePage = (number, direction) => listResponse(
    candidateEndpoint, {search: keyword, page: String(number), pageSize: '10'},
    () => pageButton(candidatesPanel, direction).click()
  );
  const summaryValue = (label, value) => dialog.locator('.node-account-summary span')
    .filter({hasText: new RegExp(`^${label}\\s*${value}$`)});

  await assignedTab(0).waitFor();
  await summaryValue('账号配额', quota).waitFor();
  await candidatesTab.click();
  const search = candidatesPanel.getByRole('textbox', {name: '搜索可分配的师生账号'});
  await search.fill(keyword);
  const first = await listResponse(candidateEndpoint, {search: keyword, page: '1', pageSize: '10'},
    () => search.press('Enter'));
  assert.equal(first.total, fixtureCount);
  assert.equal(first.items.length, 10);
  await row(candidatesPanel, first.items[0]).waitFor();
  await candidatesPanel.locator('.el-table__header label.el-checkbox').click();
  await selection(10).waitFor();
  const second = await candidatePage(2, 'next');
  assert.equal(second.items.length, 2);
  const [crossPageAccount, unselectedAccount] = second.items;
  await accountCheckbox(candidatesPanel, crossPageAccount).click();
  await selection(11).waitFor();
  await accountCheckbox(candidatesPanel, unselectedAccount).click();
  await selection(12).waitFor();
  await candidatesPanel.getByText('已选数量超过剩余 11 个名额', {exact: true}).waitFor();
  assert.equal(await assignButton(12).isDisabled(), true);
  await accountCheckbox(candidatesPanel, unselectedAccount).click();
  await selection(11).waitFor();

  await candidatePage(1, 'prev');
  await row(candidatesPanel, first.items[0]).waitFor();
  for (const account of first.items) {
    assert.equal(await accountCheckbox(candidatesPanel, account).locator('input').isChecked(), true);
  }
  await candidatePage(2, 'next');
  await row(candidatesPanel, crossPageAccount).waitFor();
  assert.equal(await accountCheckbox(candidatesPanel, crossPageAccount).locator('input').isChecked(), true);
  assert.equal(await accountCheckbox(candidatesPanel, unselectedAccount).locator('input').isChecked(), false);
  await selection(11).waitFor();
  await screenshot('cross-page-selection');
  mark('节点账号跨页勾选后返回页面仍保留复选，超出节点配额时禁用提交');

  const [assignedResponse] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === `/api${endpoint}` &&
      response.request().method() === 'POST'),
    assignButton(11).click()
  ]);
  assert.equal(assignedResponse.status(), 200);
  const expectedAccounts = [...first.items, crossPageAccount];
  const submitted = assignedResponse.request().postDataJSON().userIds;
  assert.deepEqual(submitted.sort(), expectedAccounts.map(account => account.userId).sort());
  const assignment = await assignedResponse.json();
  assert.equal(assignment.assignedCount, 11);
  assert.equal(assignment.accountCount, 11);
  await summaryValue('已分配', 11).waitFor();
  await summaryValue('剩余名额', 0).waitFor();
  await selection(0).waitFor();
  await row(candidatesPanel, unselectedAccount).waitFor();
  await assignedTab(11).click();
  const assignedLast = await listResponse(endpoint, {search: '', page: '2', pageSize: '10'},
    () => pageButton(assignedPanel, 'next').click());
  assert.equal(assignedLast.accountCount, quota);
  assert.equal(assignedLast.total, quota);
  assert.deepEqual(assignedLast.items.map(account => account.userId), [crossPageAccount.userId]);
  await row(assignedPanel, crossPageAccount).waitFor();
  await screenshot('assigned-page-two-full');
  mark('节点页面一次提交两页所选的 11 个账号，已分配数量与剩余额度同步，已分配列表可翻页');

  await button(row(assignedPanel, crossPageAccount), '移除').click();
  const confirmation = page.getByRole('dialog', {name: '移除节点账号分配'});
  const recovered = await listResponse(endpoint, {search: '', page: '1', pageSize: '10'},
    () => button(confirmation, '移除分配').click());
  assert.equal(recovered.total, 10);
  assert.equal(recovered.accountCount, 10);
  await assignedTab(10).waitFor();
  await summaryValue('已分配', 10).waitFor();
  await summaryValue('剩余名额', 1).waitFor();
  await row(assignedPanel, first.items[0]).waitFor();
  assert.equal(await activePage(assignedPanel).innerText(), '1');
  await candidatesTab.click();
  await row(candidatesPanel, crossPageAccount).waitFor();
  await row(candidatesPanel, unselectedAccount).waitFor();
  const preserved = await adminApi(`/people?kind=${crossPageAccount.kind}&search=${crossPageAccount.userName}`);
  assert.equal(preserved.total, 1);
  assert.equal(preserved.items[0].id, crossPageAccount.personId);
  await screenshot('released-account-candidate');
  await button(dialog, '关闭').click();
  await dialog.waitFor({state: 'hidden'});
  await nodeRow.getByRole('cell', {name: '10 / 11', exact: true}).waitFor();
  mark('移除已分配末页的账号后回到有效页，配额释放且账号重新出现在候选列表，原档案保留');

  for (const account of first.items) await adminApi(`${endpoint}/${account.userId}`, 'DELETE');
  await adminApi('/people/batch-delete', 'POST', {ids: people.map(person => person.id)});
  await adminApi(`/nodes/${node.id}`, 'DELETE');
  return {candidateCount: fixtureCount, firstPageSelected: 10, secondPageSelected: 1, quota, assigned: 11, afterRemoval: 10};
}
