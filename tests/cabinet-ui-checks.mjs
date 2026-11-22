import assert from 'node:assert/strict';

const cabinetTitle = '机柜管理';
const deviceTitle = '实训设备';
const cabinetPrefix = 'QA-R06-Cabinet-';
const devicePrefix = 'QA-R06-Device-';
const jumpPassword = 'QaJumpFixture2026';
const devicePassword = 'QaDeviceFixture2026';

function assertProtected(row, secret, flag) {
  assert.equal(row[flag], true);
  const result = JSON.stringify(row);
  assert.ok(!result.includes(secret));
  assert.ok(!Object.keys(row).some(key => /password/i.test(key) && key !== flag));
}

export async function runCabinetUiChecks({page, ui, adminApi, capture, mark, productVersion, observations}) {
  const {openPage, rowNamed, formField, selectFormOption, saveForm, deleteRow, listResponse, searchList} = ui;
  const screenshot = (name, preserveMessages = false) =>
    capture(page, `r06-${name}.jpg`, {fullPage: false, preserveMessages});
  const exactButton = (scope, name) => scope.getByRole('button', {name, exact: true});
  const resourceTable = page.locator('.resource-table');
  const pagination = page.locator('.el-pagination');
  const numberedNames = prefix => Array.from({length: 11}, (_, index) =>
    `${prefix}${String(index + 1).padStart(2, '0')}`
  );

  async function editInputs(dialog, fields) {
    for (const [label, value] of Object.entries(fields)) {
      await formField(dialog, label).locator('input, textarea').fill(String(value));
    }
  }

  async function saveAndRead(dialog, singular, endpoint, method = 'POST') {
    const [response] = await Promise.all([
      page.waitForResponse(response => new URL(response.url()).pathname === `/api${endpoint}` &&
        response.request().method() === method),
      saveForm(dialog, singular)
    ]);
    assert.equal(response.status(), 200);
    return response.json();
  }

  async function verifyPagination(endpoint, query, expectedNames, filters = {}) {
    const sizeControl = pagination.locator('.el-pagination__sizes');
    const firstPageParams = {search: query, page: '1', pageSize: '10', ...filters};
    const chooseTen = sizeControl.getByText('10条/页', {exact: true});
    if (!(await chooseTen.isVisible())) {
      await sizeControl.locator('.el-select__wrapper').click();
      const first = await listResponse(endpoint, firstPageParams, () =>
        page.getByRole('option', {name: '10条/页', exact: true}).click()
      );
      assert.deepEqual(first.items.map(row => row.name), expectedNames.slice(0, 10));
    }
    const visibleNames = async () => (await resourceTable.locator('.el-table__body .table-name').allTextContents())
      .map(name => name.trim().replace(/\s*模拟$/, '').trim());
    await rowNamed(expectedNames[0]).waitFor();
    await rowNamed(expectedNames[10]).waitFor({state: 'hidden'});
    assert.deepEqual(await visibleNames(), expectedNames.slice(0, 10));

    const next = await listResponse(endpoint, {...firstPageParams, page: '2'}, () => pagination.locator('.btn-next').click());
    assert.equal(next.total, 11);
    assert.deepEqual(next.items.map(row => row.name), expectedNames.slice(10));
    await rowNamed(expectedNames[10]).waitFor();
    await rowNamed(expectedNames[0]).waitFor({state: 'hidden'});
    assert.deepEqual(await visibleNames(), expectedNames.slice(10));
    return firstPageParams;
  }

  async function verifyPageRecovery(endpoint, params, deletedRow, retainedRow) {
    const recovered = await listResponse(endpoint, params, () => deleteRow(deletedRow));
    assert.equal(recovered.total, 10);
    await retainedRow.waitFor();
    assert.equal(await pagination.locator('.el-pager .is-active').innerText(), '1');
  }

  const cabinetNames = numberedNames(cabinetPrefix);
  const cabinetName = cabinetNames.at(-1);
  const cabinetRow = rowNamed(cabinetName);
  await openPage(cabinetTitle);
  await exactButton(page, '新增机柜').click();
  const createCabinet = page.getByRole('dialog', {name: '新增机柜'});
  assert.equal(await createCabinet.getByRole('switch').getAttribute('aria-checked'), 'true');
  await editInputs(createCabinet, {
    机柜名称: cabinetName, 控制器地址: '192.0.2.40', 控制器端口: 2123,
    跳板机地址: '192.0.2.41', 跳板机端口: 2022, 跳板机账号: 'qa_r06_jump', 跳板机密码: jumpPassword
  });
  const cabinet = await saveAndRead(createCabinet, '机柜', '/cabinets');
  assert.equal(cabinet.isSimulated, true);
  assertProtected(cabinet, jumpPassword, 'hasJumpPassword');
  await cabinetRow.waitFor();
  mark('R06 从机柜页面新增模拟机柜，保存控制器和跳板机字段，接口隐藏口令');

  await exactButton(cabinetRow, '编辑').click();
  const editCabinet = page.getByRole('dialog', {name: '编辑机柜'});
  const jumpSecret = formField(editCabinet, '跳板机密码').locator('input');
  assert.equal(await jumpSecret.getAttribute('type'), 'password');
  assert.equal(await jumpSecret.inputValue(), '');
  const cabinetHost = '192.0.2.50';
  const jumpHost = '192.0.2.51';
  const jumpUserName = 'qa_r06_jump_edited';
  await editInputs(editCabinet, {
    控制器地址: cabinetHost, 控制器端口: 2223,
    跳板机地址: jumpHost, 跳板机端口: 3022, 跳板机账号: jumpUserName, 备注: '机柜验收编辑'
  });
  const updatedCabinet = await saveAndRead(editCabinet, '机柜', `/cabinets/${cabinet.id}`, 'PUT');
  assertProtected(updatedCabinet, jumpPassword, 'hasJumpPassword');
  for (const query of [cabinetName, cabinetHost]) {
    const found = await searchList(cabinetTitle, '/cabinets', query);
    assert.equal(found.total, 1);
    assert.equal(found.items[0].id, cabinet.id);
    assertProtected(found.items[0], jumpPassword, 'hasJumpPassword');
    assert.equal(found.items[0].host, cabinetHost);
    assert.equal(found.items[0].port, 2223);
    assert.equal(found.items[0].jumpHost, jumpHost);
    assert.equal(found.items[0].jumpPort, 3022);
    assert.equal(found.items[0].jumpUserName, jumpUserName);
    await cabinetRow.getByRole('cell', {name: cabinetHost, exact: true}).waitFor();
  }
  await exactButton(cabinetRow, '编辑').click();
  assert.equal(await formField(editCabinet, '跳板机地址').locator('input').inputValue(), jumpHost);
  assert.equal(await jumpSecret.inputValue(), '');
  await screenshot('cabinet-configuration');
  await exactButton(editCabinet, '取消').click();
  mark('R06 机柜可按名称和控制器地址检索，编辑后正常回显非口令字段，留空保留凭据');

  const otherCabinets = [];
  for (const name of cabinetNames.slice(0, 10)) {
    otherCabinets.push(await adminApi('/cabinets', 'POST', {name, host: 'simulator.local', isSimulated: true}));
  }
  await searchList(cabinetTitle, '/cabinets', cabinetPrefix);
  const otherCabinetRow = rowNamed(cabinetNames[0]);
  await otherCabinetRow.locator('label.el-checkbox').click();
  await cabinetRow.locator('label.el-checkbox').click();
  const [batchResponse] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === '/api/cabinets/test-connections'),
    exactButton(page, '测试选中机柜').click()
  ]);
  const batch = await batchResponse.json();
  assert.equal(batchResponse.status(), 200);
  assert.deepEqual(batch.items.map(item => item.id).sort(), [cabinet.id, otherCabinets[0].id].sort());
  assert.ok(batch.items.every(item => item.success && item.isSimulated && item.level === 'simulation'));
  const batchDetails = page.getByRole('dialog', {name: '机柜连接测试结果'});
  await batchDetails.getByText('模拟控制器响应正常。', {exact: true}).first().waitFor();
  observations.batchResultsShowCabinetNames =
    await batchDetails.getByText(cabinetName, {exact: true}).count() === 1 &&
    await batchDetails.getByText(cabinetNames[0], {exact: true}).count() === 1;
  await screenshot('cabinet-batch-probe');
  await batchDetails.getByRole('button', {name: /close|关闭/i}).click();
  await batchDetails.waitFor({state: 'hidden'});
  mark('R06 页面勾选两个模拟机柜并批量测试连接，均返回模拟控制器响应正常');

  const scopedDevices = await listResponse('/devices', {cabinetId: cabinet.id}, () => exactButton(cabinetRow, '设备').click());
  assert.equal(scopedDevices.total, 0);
  await page.getByRole('heading', {name: deviceTitle, exact: true}).waitFor();
  observations.devicePageShowsCabinetContext =
    await page.locator('.page-heading, .section-notice').getByText(cabinetName, {exact: false}).count() > 0;
  if (productVersion !== '0.1.0-p6') {
    await page.reload();
    await page.locator('.page-heading').getByText(`所属机柜：${cabinetName}`, {exact: true}).waitFor();
  }
  await exactButton(page, '新增设备').click();
  const createDevice = page.getByRole('dialog', {name: '新增设备'});
  await createDevice.waitFor();
  const cabinetField = formField(createDevice, '所属机柜');
  observations.newDeviceDefaultsToCabinet =
    await cabinetField.getByText(cabinetName, {exact: true}).count() === 1;
  await screenshot('cabinet-scoped-device-form');
  if (!observations.newDeviceDefaultsToCabinet) await selectFormOption(createDevice, '所属机柜', cabinetName);
  const {items: [type]} = await adminApi('/device-types');
  await selectFormOption(createDevice, '设备类型', type.name);
  const deviceNames = numberedNames(devicePrefix);
  const deviceName = deviceNames.at(-1);
  const deviceRow = rowNamed(deviceName);
  await editInputs(createDevice, {
    设备名称: deviceName, 连接地址: '192.0.2.60', 连接端口: 2123, 串口编号: 11,
    'Web 端口': 8443, 连接账号: 'qa_r06_device', 连接密码: devicePassword
  });
  await selectFormOption(createDevice, 'Web 协议', 'HTTPS');
  const device = await saveAndRead(createDevice, '设备', '/devices');
  assert.equal(device.isSimulated, true);
  assert.equal(device.cabinetId, cabinet.id);
  assert.equal(device.deviceTypeId, type.id);
  assert.equal(device.serialPortNumber, 11);
  assert.equal(device.webProtocol, 'https');
  assertProtected(device, devicePassword, 'hasPassword');
  await deviceRow.waitFor();
  mark('R06 从机柜设备列表新增模拟设备，并绑定机柜、设备类型、串口及 Web 参数');

  await exactButton(deviceRow, '编辑').click();
  const editDevice = page.getByRole('dialog', {name: '编辑设备'});
  const deviceSecret = formField(editDevice, '连接密码').locator('input');
  assert.equal(await deviceSecret.getAttribute('type'), 'password');
  assert.equal(await deviceSecret.inputValue(), '');
  const changedDeviceHost = '192.0.2.61';
  const changedDeviceUser = 'qa_r06_device_edited';
  await editInputs(editDevice, {
    连接地址: changedDeviceHost, 连接端口: 2223, 串口编号: 21, 'Web 端口': 8080, 连接账号: changedDeviceUser
  });
  await selectFormOption(editDevice, 'Web 协议', 'HTTP');
  const changedDevice = await saveAndRead(editDevice, '设备', `/devices/${device.id}`, 'PUT');
  assertProtected(changedDevice, devicePassword, 'hasPassword');
  for (const query of [deviceName, changedDeviceHost, changedDeviceUser]) {
    const found = await searchList(deviceTitle, '/devices', query, {cabinetId: cabinet.id});
    assert.equal(found.total, 1);
    const [row] = found.items;
    assert.equal(row.id, device.id);
    assert.equal(row.port, 2223);
    assert.equal(row.serialPortNumber, 21);
    assert.equal(row.webPort, 8080);
    assert.equal(row.webProtocol, 'http');
    assertProtected(row, devicePassword, 'hasPassword');
    await deviceRow.getByRole('cell', {name: changedDeviceHost, exact: true}).waitFor();
  }
  await exactButton(deviceRow, '编辑').click();
  assert.equal(await formField(editDevice, '串口编号').locator('input').inputValue(), '21');
  assert.equal(await formField(editDevice, 'Web 端口').locator('input').inputValue(), '8080');
  assert.equal(await deviceSecret.inputValue(), '');
  await page.setViewportSize({width: 1440, height: 1200});
  await screenshot('device-configuration');
  await page.setViewportSize({width: 1440, height: 1000});
  await exactButton(editDevice, '取消').click();
  mark('R06 模拟设备按名称、地址或连接账号检索，编辑后的连接参数回显而口令始终隐藏');

  await exactButton(deviceRow, '测试').click();
  const deviceProbe = page.getByRole('dialog', {name: `${deviceName} · 连接测试`});
  await deviceProbe.getByText('模拟设备响应正常。', {exact: true}).waitFor();
  await deviceProbe.getByText('模拟器', {exact: true}).first().waitFor();
  await screenshot('device-probe');
  await deviceProbe.getByRole('button', {name: /close|关闭/i}).click();
  mark('R06 从设备列表执行连接检测并显示模拟设备结果');

  const returnToCabinets = exactButton(page, '返回机柜管理');
  if (productVersion !== '0.1.0-p6') {
    await returnToCabinets.click();
    await page.getByRole('heading', {name: cabinetTitle, exact: true}).waitFor();
  } else {
    await openPage(cabinetTitle);
  }
  await searchList(cabinetTitle, '/cabinets', cabinetName);
  await exactButton(cabinetRow, '删除').click();
  const deleteConfirmation = page.getByRole('dialog', {name: '删除确认'});
  const [guardResponse] = await Promise.all([
    page.waitForResponse(response => new URL(response.url()).pathname === `/api/cabinets/${cabinet.id}` &&
      response.request().method() === 'DELETE'),
    exactButton(deleteConfirmation, '确认删除').click()
  ]);
  assert.equal(guardResponse.status(), 409);
  await page.getByText('机柜中仍有设备，请先删除或移走设备。', {exact: true}).waitFor();
  await cabinetRow.waitFor();
  await screenshot('cabinet-delete-guard', true);
  mark('R06 有关联设备时删除机柜返回冲突，界面保留记录并提示如何解除关联');

  const otherDevices = [];
  for (const name of deviceNames.slice(0, 10)) {
    otherDevices.push(await adminApi('/devices', 'POST', {
      name, cabinetId: cabinet.id, deviceTypeId: type.id, connectionType: 'simulated'
    }));
  }
  await exactButton(cabinetRow, '设备').click();
  const allDevices = await searchList(deviceTitle, '/devices', devicePrefix, {cabinetId: cabinet.id});
  assert.equal(allDevices.total, 11);
  const devicePage = await verifyPagination('/devices', devicePrefix, deviceNames, {cabinetId: cabinet.id});
  await screenshot('device-page-two');
  await verifyPageRecovery('/devices', devicePage, deviceRow, rowNamed(deviceNames[0]));
  mark('R06 设备默认按名称排序，切换每页 10 条并翻页，删除末页唯一项自动回到有效页');
  for (const row of otherDevices) await adminApi(`/devices/${row.id}`, 'DELETE');

  await openPage(cabinetTitle);
  const allCabinets = await searchList(cabinetTitle, '/cabinets', cabinetPrefix);
  assert.equal(allCabinets.total, 11);
  const cabinetPage = await verifyPagination('/cabinets', cabinetPrefix, cabinetNames);
  await screenshot('cabinet-page-two');
  await verifyPageRecovery('/cabinets', cabinetPage, cabinetRow, otherCabinetRow);
  mark('R06 解除设备关联后可删除机柜，默认按名称排序且删除末页唯一项自动回到有效页');
  if (productVersion !== '0.1.0-p6') {
    await page.evaluate(id => { location.hash = `devices?cabinetId=${id}`; }, cabinet.id);
    await page.getByText('机柜不存在或已被删除，请返回机柜管理。', {exact: true}).waitFor();
    assert.equal(await exactButton(page, '新增设备').isEnabled(), false);
    await returnToCabinets.click();
    mark('R06 可从机柜设备页返回，重新载入保留机柜范围，已删除机柜链接禁止新增');
  }

  await exactButton(page, '新增机柜').click();
  await editInputs(createCabinet, {机柜名称: cabinetName});
  const recreated = await saveAndRead(createCabinet, '机柜', '/cabinets');
  assert.notEqual(recreated.id, cabinet.id);
  assert.equal(recreated.hasJumpPassword, false);
  assert.equal(recreated.deviceCount, 0);
  assert.equal(recreated.host, 'simulator.local');
  await searchList(cabinetTitle, '/cabinets', cabinetName);
  await deleteRow(cabinetRow);
  for (const row of otherCabinets) await adminApi(`/cabinets/${row.id}`, 'DELETE');
  assert.equal((await adminApi(`/cabinets?search=${cabinetPrefix}`)).total, 0);
  assert.equal((await adminApi(`/devices?search=${devicePrefix}`)).total, 0);
  mark('R06 删除后可重新使用机柜名称，新记录没有旧凭据或设备关联');

  if (productVersion !== '0.1.0-p6') {
    assert.ok(observations.batchResultsShowCabinetNames, '批量检测结果缺少机柜名称');
    assert.ok(observations.devicePageShowsCabinetContext, '机柜的设备页面缺少机柜名称');
    assert.ok(observations.newDeviceDefaultsToCabinet, '从机柜新增设备时未默认使用当前机柜');
    mark('R06 批测结果展示机柜名称，设备页显示当前机柜，新建设备默认使用该机柜');
  }
}
