// Runs the classroom work lifecycle against a fresh, isolated application database.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import {mkdtemp} from 'node:fs/promises';
import net from 'node:net';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {DatabaseSync} from 'node:sqlite';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';

const root = fileURLToPath(new URL('..', import.meta.url));
const app = path.resolve(process.env.SCHOOL_APP_DIR ?? path.join(root, 'src/School.Training/bin/Debug/net10.0'));
const data = await mkdtemp(path.join(tmpdir(), 'school-classroom-case-'));
const password = `CaseQA${randomBytes(12).toString('hex')}`;
const changedPassword = `Changed${password}`;
let child;
let base;

async function start() {
  const server = net.createServer();
  await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
  base = `http://127.0.0.1:${server.address().port}`;
  await new Promise(resolve => server.close(resolve));
  child = spawn('dotnet', [path.join(app, 'School.Training.dll'), '--urls', base], {
    cwd: app, env: {...process.env, SCHOOL_DATA_DIR: data, ASPNETCORE_ENVIRONMENT: 'Production'},
    windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '';
  for (const stream of [child.stdout, child.stderr]) stream.on('data', chunk => { output = (output + chunk).slice(-12000); });
  for (let attempt = 0; attempt < 150 && child.exitCode === null; attempt++) {
    try { if ((await fetch(`${base}/api/status`)).ok) return; } catch {}
    await delay(100);
  }
  throw new Error(`Isolated service failed to start: ${output}`);
}

async function stop() {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const exited = new Promise(resolve => child.once('exit', resolve));
  child.kill();
  await Promise.race([exited, delay(5000, undefined, {ref: false}).then(() => {
    throw new Error('Isolated service did not stop');
  })]);
}

function client() {
  const cookies = new Map();
  let token;
  return async (route, method = 'GET', payload, expected = 200, rawResponse = false) => {
    const headers = {Cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join('; ')};
    if (token) headers['X-CSRF-TOKEN'] = token;
    const formData = payload instanceof FormData;
    if (payload !== undefined && !formData) headers['Content-Type'] = 'application/json';
    const response = await fetch(`${base}/api${route}`, {
      method, headers, body: payload === undefined || formData ? payload : JSON.stringify(payload)
    });
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(';')[0];
      const index = pair.indexOf('=');
      cookies.set(pair.slice(0, index), pair.slice(index + 1));
    }
    const raw = await response.text();
    assert.equal(response.status, expected, `${method} ${route}: ${raw.slice(0, 700)}`);
    if (rawResponse) return {raw, headers: response.headers};
    const result = raw ? JSON.parse(raw) : null;
    if (result?.csrfToken) token = result.csrfToken;
    return result;
  };
}

const v = ({workId, revision}) => ({workId, revision});
const admin = client();
const student = client();
const peer = client();
const outsider = client();
const teacher = client();
const command = (account, route, name, snapshot, expected) =>
  account(`${route}/commands`, 'POST', {command: name, connectionVersion: snapshot.connectionVersion}, expected);

try {
  await start();
  await admin('/setup', 'POST', {schoolName: '实操测试学校', userName: 'caseadmin', displayName: '管理员', password});
  const profession = await admin('/professions', 'POST', {name: '实训', code: 'CASE'});
  const schoolClass = await admin('/classes', 'POST', {name: '实训班', professionId: profession.id});
  const studentIds = [];
  for (const [index, account] of [student, peer, outsider].entries()) {
    const userName = `caselearner${index}`;
    await admin('/people', 'POST', {
      kind: 'student', userName, password, name: `学生 ${index}`, number: `CASE${index}`,
      ...(index < 2 ? {classId: schoolClass.id} : {})
    });
    studentIds.push((await account('/auth/login', 'POST', {userName, password})).user.id);
    await account('/auth/password', 'POST', {currentPassword: password, newPassword: changedPassword});
  }
  await admin('/people', 'POST', {kind: 'teacher', userName: 'caseteacher', password, name:'课堂教师', number:'TCASE'});
  const teacherSession = await teacher('/auth/login', 'POST', {userName:'caseteacher', password});
  await teacher('/auth/password', 'POST', {currentPassword:password, newPassword:changedPassword});
  await admin('/simulation/bootstrap', 'POST', {});
  const managedVm = await admin('/virtual-machines', 'POST', {
    name:'控制台测试虚拟机', nodeId:'simulation-node', cpuCount:2, memoryMb:1024, diskGb:10
  });
  assert.match(managedVm.uuid, /^[0-9a-f-]{36}$/);
  assert.match(managedVm.ipAddress, /^10(\.\d{1,3}){3}$/);
  const managed = `/virtual-machines/${managedVm.id}`;
  const managedConsole = `/vm-console/machine/${managedVm.id}`;
  await admin(managedConsole, 'GET', undefined, 409);
  assert.equal((await admin(`${managed}/start`, 'POST', {})).detectionStatus, 'reachable');
  const reachable = (await admin(`/virtual-machines?search=${managedVm.uuid}`)).items[0];
  assert.equal(reachable.ipAddress, managedVm.ipAddress);
  assert.equal((await admin(`/virtual-machines?search=${managedVm.ipAddress}`)).total, 1);
  const runningConsole = await admin(managedConsole);
  assert.equal((await command(admin, managedConsole, 'link down', runningConsole)).linkUp, false);
  await outsider(managedConsole, 'GET', undefined, 404);
  await admin(`${managed}/restart`, 'POST', {});
  await command(admin, managedConsole, 'link up', runningConsole, 409);
  const rebooted = await admin(managedConsole);
  assert.equal(rebooted.linkUp, true);
  assert.equal(rebooted.lines.length, 0);
  await admin(`${managed}/destroy`, 'POST', {});
  await admin(managedConsole, 'GET', undefined, 404);
  assert.equal((await admin(`/virtual-machines?search=${managedVm.uuid}`)).total, 0);
  const [device, otherDevice] = await admin('/nodes/simulation-node/case-devices');
  const series = await admin('/course-series', 'POST', {name: '网络专业核心课', description: '配置与排错'});
  const course = await admin('/courses', 'POST', {name: '网络配置实操', type: 'core', seriesId: series.id});
  const chapter = await admin(`/courses/${course.id}/chapters`, 'POST', {name: '准备'});
  const section = await admin(`/chapters/${chapter.id}/sections`, 'POST', {name: '基础配置'});
  const blueprint = {deviceIds:[device.id, otherDevice.id], links:[{fromDeviceId:device.id, fromPort:device.interfaces[0],
    toDeviceId:otherDevice.id, toPort:otherDevice.interfaces[0], network:'实训网段'}]};
  const templateInput = {name:'课堂模拟拓扑', nodeId:'simulation-node', blueprint};
  await admin('/case-topology-templates', 'POST', { ...templateInput, blueprint: {...blueprint,
    links:[{ ...blueprint.links[0], toPort:'nonexistent-port' }]} }, 400);
  const template = await admin('/case-topology-templates', 'POST', templateInput);
  assert.deepEqual((await admin('/case-topology-templates?nodeId=simulation-node'))[0].blueprint, blueprint);
  const resourceInput = {name: '设备配置', kind: 'casePractice', startupNodeId: 'simulation-node',
    topologyBlueprint:template.blueprint, caseRubric: [
    {name: '主机名', deviceId: device.id, field: 'hostname', interfaceName: null, expected: 'CASE-OK', score: 70},
    {name: '停用端口', deviceId: device.id, field: 'enabled', interfaceName: device.interfaces[0],
      expected: 'false', score: 30, guidance: '检查接口启用状态。'}
  ]};
  const resource = await admin(`/sections/${section.id}/resources`, 'POST', resourceInput);
  await admin(`/case-topology-templates/${template.id}`, 'DELETE', undefined, 204);
  const lessonChapter = await admin(`/courses/${course.id}/chapters`, 'POST', {name: '理论学习'});
  const lessonSection = await admin(`/chapters/${lessonChapter.id}/sections`, 'POST', {name: '入门'});
  const lecture = await admin(`/sections/${lessonSection.id}/resources`, 'POST', {
    name: '课堂讲解资料', kind: 'theory', content: '网络配置流程'
  });
  const practice = await admin(`/sections/${lessonSection.id}/resources`, 'POST', {
    name: '理论测验', kind: 'theoryPractice',
    questions: [{type: 'single', stem: '选择正确结果', options: ['正确', '错误'], answers: [0], score: 10}]
  });
  const upload = new FormData();
  upload.set('file', new Blob(['分屏内容验收']), 'lesson.txt');
  const attachment = await admin(`/resources/${lecture.id}/files`, 'POST', upload);
  assert.deepEqual(await student('/course-series'), []);
  assert.equal((await admin(`/courses/${course.id}/publish?activate=false`, 'POST', {})).isActive, false);
  assert.equal((await student('/course-series'))[0].courseCount, 1);
  assert.equal((await student(`/courses?seriesId=${series.id}`)).items[0].id, course.id);
  assert.equal((await student('/teaching/availability')).classrooms, false);
  assert.equal((await teacher(`/teaching/students?classId=${schoolClass.id}&professionId=${profession.id}`)).total, 2);
  await student('/teaching/students', 'GET', undefined, 403);
  const roomInput = {
    name: '实操课堂', courseId: course.id, teacherUserId: teacherSession.user.id, studentUserIds: [studentIds[0]],
    openAt: new Date().toISOString(), closeAt: new Date(Date.now() + 3600000).toISOString()
  };
  await admin('/classrooms', 'POST', roomInput, 400);
  await admin(`/courses/${course.id}/activation`, 'PUT', {active: true});
  assert.equal((await student('/teaching/availability')).classrooms, true);
  const room = await admin('/classrooms', 'POST', {...roomInput, openAt:new Date(Date.now()+300000).toISOString()});
  const roomUrl = `/classrooms/${room.id}`;
  assert.equal((await teacher(`${roomUrl}/students`)).items[0].professionName, profession.name);
  await teacher(`${roomUrl}/students`, 'POST', {studentUserIds: [studentIds[1]]});
  await admin(`${roomUrl}/open`, 'POST', {});
  assert.equal((await student(`${roomUrl}/content`)).canLearn, false);
  await student(`${roomUrl}/resources/${lecture.id}/start`, 'POST', {}, 409);
  await teacher(roomUrl, 'PUT', roomInput);
  await admin(`/courses/${course.id}/activation`, 'PUT', {active: false}, 409);
  assert.equal((await student('/classrooms')).items[0].resources.theoryPracticeCount, 1);
  const attachmentUrl = `${roomUrl}/files/${attachment.id}?preview=true`;
  const inline = await student(attachmentUrl, 'GET', undefined, 200, true);
  assert.equal(inline.raw, '分屏内容验收');
  assert.equal(inline.headers.get('x-frame-options'), 'SAMEORIGIN');
  await outsider(attachmentUrl, 'GET', undefined, 404);
  const lessonContent = await student(`${roomUrl}/content`);
  const lessonResources = lessonContent.tree.find(item => item.id === lessonChapter.id).sections[0].resources;
  assert.equal(lessonResources.find(item => item.id === lecture.id).files[0].id, attachment.id);
  const question = lessonResources.find(item => item.id === practice.id).questions[0];
  const theoryUrl = `${roomUrl}/resources/${practice.id}`;
  await student(`${theoryUrl}/start`, 'POST', {});
  const firstPractice = await student(`${theoryUrl}/submit`, 'POST', {answers: [{questionId: question.id, selected: [1]}]});
  assert.equal(firstPractice.score, 0);
  assert.ok(firstPractice.startedAt <= firstPractice.submittedAt);
  await student(`${theoryUrl}/start`, 'POST', {});
  const secondPractice = await student(`${theoryUrl}/submit`, 'POST', {answers: [{questionId: question.id, selected: [0]}]});
  assert.equal(secondPractice.attemptCount, 2);
  assert.equal(secondPractice.bestScore, 10);
  assert.equal(secondPractice.averageScore, 5);
  assert.ok(secondPractice.startedAt >= firstPractice.submittedAt);
  const stats = await teacher(`${roomUrl}/statistics?chapterId=${lessonChapter.id}`);
  assert.equal(stats.students.find(item => item.userId === studentIds[0]).overall.learningRate, 50);
  assert.equal(stats.students.find(item => item.userId === studentIds[0]).casePractice.total, 0);
  const csv = await teacher(`${roomUrl}/statistics/exports/students?chapterId=${lessonChapter.id}&search=CASE0`, 'GET', undefined, 200, true);
  assert.match(csv.raw, /理论练习学习率/);
  assert.match(csv.raw, /CASE0/);
  assert.doesNotMatch(csv.raw, /CASE1/);
  const [cabinet] = (await admin('/cabinets')).items;
  const group = await teacher(`${roomUrl}/groups`, 'POST', {name:'一组', userIds:[studentIds[0]]});
  await teacher(`${roomUrl}/cabinet-assignments`, 'POST', {cabinetIds:[cabinet.id], groupId:group.id, resourceId:resource.id});
  const groupDevices = `${roomUrl}/resources/${resource.id}/cabinet-devices`;
  const [cabinetDevice] = await student(groupDevices);
  assert.ok(cabinetDevice);
  const cabinetWeb = `/devices/${cabinetDevice.id}/web-console`;
  await student(cabinetWeb);
  await admin('/nodes/simulation-node/accounts', 'POST', {userIds:[studentIds[1]]});
  assert.deepEqual(await peer(groupDevices), []);
  await peer(cabinetWeb, 'GET', undefined, 404);
  assert.ok((await student('/my-resources')).devices.some(item => item.id === cabinetDevice.id && item.canAccess));
  const [assignedCabinet] = (await teacher(`${roomUrl}/cabinet-assignments`)).items;
  await teacher(`${roomUrl}/cabinet-assignments/cancel`, 'POST', {ids:[assignedCabinet.id]});
  assert.deepEqual(await student(groupDevices), []);
  await student(cabinetWeb, 'GET', undefined, 404);
  await peer(cabinetWeb);
  console.log('PASS course activation, core series, individual enrollment, scoped attachment preview, practice summaries, chapter export and group cabinet access');
  const resourceUrl = `${roomUrl}/resources/${resource.id}`;
  const workUrl = `${resourceUrl}/case-work`;
  const record = await student(`${resourceUrl}/start`, 'POST', {});
  await peer(`${resourceUrl}/start`, 'POST', {});
  let before = await student(workUrl);
  assert.deepEqual(before.topology, blueprint);
  assert.deepEqual(new Set(before.devices.map(item => item.id)), new Set(blueprint.deviceIds));
  const live = `/classrooms/case-sessions/${before.sessionId}/live`;
  const assist = `/classrooms/case-sessions/${before.sessionId}/work/devices/${device.id}`;
  const helperConfig = {...before.devices.find(item => item.id === device.id).configuration, hostname:'ASSIST-OK'};
  await teacher(assist, 'PUT', {...v(before), configuration:helperConfig}, 409);
  await peer(live, 'POST', {allowAssist:true}, 404);
  const {token} = await student(live, 'POST', {allowAssist:true});
  await student(`${live}/frame`, 'POST', {token, image:'ZmFrZQ=='}, 400);
  const sample = Buffer.from([0xff, 0xd8, 0xff, 0xd9]).toString('base64');
  await student(`${live}/frame`, 'POST', {token, image:sample}, 204);
  assert.deepEqual(await teacher(live), {sharing:true, allowAssist:true, image:sample});
  await outsider(live, 'GET', undefined, 403);
  await teacher(assist, 'PUT', {...v(before), configuration:helperConfig});
  before = await student(workUrl);
  assert.equal(before.devices.find(item => item.id === device.id).configuration.hostname, 'ASSIST-OK');
  await student(`${live}/stop`, 'POST', {token}, 204);
  await teacher(assist, 'PUT', {...v(before), configuration:helperConfig}, 409);
  assert.equal((await teacher(live)).image, null);
  const peerState = await peer(workUrl);
  const peerShare = `/classrooms/case-sessions/${peerState.sessionId}/live`;
  const oldShare = await peer(peerShare, 'POST', {allowAssist:true});
  await peer(`${peerShare}/frame`, 'POST', {token:oldShare.token, image:sample}, 204);
  await peer(`${workUrl}/save`, 'POST', v(peerState));
  await peer(`${resourceUrl}/start`, 'POST', {});
  assert.equal((await teacher(peerShare)).sharing, false);
  await peer(`${peerShare}/frame`, 'POST', {token:oldShare.token, image:sample}, 404);
  const classroomConsole = `/vm-console/classroom/${before.sessionId}`;
  const studentConsole = await student(classroomConsole);
  assert.equal(studentConsole.id, before.virtualMachine.id);
  await peer(classroomConsole, 'GET', undefined, 404);
  await outsider(classroomConsole, 'GET', undefined, 404);
  await command(student, classroomConsole, 'link down', studentConsole);
  const teacherConsole = await teacher(classroomConsole);
  assert.equal(teacherConsole.lines.at(-1).command, 'link down');
  assert.equal(teacherConsole.linkUp, false);
  const adminReserved = await admin(`/vm-console/machine/${studentConsole.id}`);
  assert.equal(adminReserved.canWrite, false);
  await command(admin, `/vm-console/machine/${studentConsole.id}`, 'link up', adminReserved, 403);
  await admin(`/virtual-machines/${studentConsole.id}/destroy`, 'POST', {}, 409);
  await teacher(`/classrooms/case-sessions/${before.sessionId}/restart`, 'POST', {});
  assert.equal((await student(classroomConsole)).linkUp, true);
  await outsider(workUrl, 'GET', undefined, 403);
  await student(`${resourceUrl}/complete`, 'POST', {}, 409);
  assert.ok(!JSON.stringify(before).includes('CASE-OK'), 'Unscored API exposed the rubric target');

  const config = structuredClone(before.devices.find(item => item.id === device.id).configuration);
  config.hostname = 'CASE-OK';
  const update = await student(`${workUrl}/devices/${device.id}`, 'PUT', {...v(before), configuration: config});
  await student(`${workUrl}/devices/${device.id}`, 'PUT', {...v(before), configuration: config}, 409);
  const revision = {...v(before), revision: update.revision};
  const trial = await student(`${workUrl}/trial`, 'POST', revision);
  assert.equal(trial.score, 70);
  assert.equal(trial.details[1].guidance, '检查接口启用状态。');
  assert.notEqual((await peer(workUrl)).devices.find(item => item.id === device.id).configuration.hostname, 'CASE-OK');
  await student(`${workUrl}/save`, 'POST', revision);
  assert.equal((await student(workUrl)).status, 'saved');
  await student(classroomConsole, 'GET', undefined, 404);
  await student(`${workUrl}/finish`, 'POST', revision, 409);

  await stop();
  await start();
  await student(`${resourceUrl}/start`, 'POST', {});
  const resumed = await student(workUrl);
  assert.equal(resumed.startedAt, before.startedAt);
  assert.equal(resumed.devices.find(item => item.id === device.id).configuration.hostname, 'CASE-OK');
  const final = await student(`${workUrl}/finish`, 'POST', v(resumed));
  assert.equal(final.score, 70);
  assert.equal(final.passed, true);
  assert.equal((await student(`${workUrl}/finish`, 'POST', v(resumed))).id, final.id);
  assert.deepEqual((await student(workUrl)).devices, []);

  await student(`${resourceUrl}/start`, 'POST', {});
  const fresh = await student(workUrl);
  assert.notEqual(fresh.workId, resumed.workId);
  assert.notEqual(fresh.devices.find(item => item.id === device.id).configuration.hostname, 'CASE-OK');
  const second = await student(`${workUrl}/finish`, 'POST', v(fresh));
  assert.equal(second.score, 0);
  const history = await admin(`${roomUrl}/statistics/case-records/${record.id}`);
  assert.equal(history.attemptCount, 2);
  assert.deepEqual(history.grades.map(item => item.score), [0, 70, 70]);
  await outsider(`${roomUrl}/statistics/case-records/${record.id}`, 'GET', undefined, 403);
  const position = await admin('/camp/positions', 'POST', {
    name:'控制台测评', description:null, durationMinutes:5, startupNodeId:'simulation-node',
    skills:[{name:'基础能力'}],
    questions:[
      {type:'single', stem:'选择正确答案', options:['对','错'], answers:[0], skillIndex:0},
      {type:'practical', stem:'保存指定设备配置', options:[], answers:[], skillIndex:0, score:10,
        practicalRubric:[{name:'检查主机名', deviceId:device.id, field:'hostname', interfaceName:null, expected:'CAMP-OK', score:10}]},
      {type:'practical', stem:'另一个独立项目', options:[], answers:[], skillIndex:0, score:10,
        practicalRubric:[{name:'另一项', deviceId:device.id, field:'hostname', interfaceName:null, expected:'SECOND', score:10}]}
    ]
  });
  const camp = await outsider(`/camp/positions/${position.id}/start`, 'POST', {});
  const campConsole = `/vm-console/camp/${camp.attemptId}`;
  const campVm = await outsider(campConsole);
  assert.notEqual(campVm.id, (await peer(workUrl)).virtualMachine.id, 'An active classroom VM was reused for a camp attempt');
  assert.ok((await outsider(`/camp/positions/${position.id}/environment`)).virtualMachines
    .some(vm => vm.selected && vm.id === campVm.id));
  await student(campConsole, 'GET', undefined, 404);
  await command(outsider, campConsole, 'link down', campVm);
  assert.equal((await teacher(campConsole)).linkUp, false);
  await teacher(`/camp/monitor/${camp.attemptId}/restart`, 'POST', {});
  assert.equal((await outsider(campConsole)).linkUp, true);
  const campUrl = `/camp/positions/${position.id}`;
  const [objective, firstProject, secondProject] = (await outsider(campUrl)).questions;
  assert.equal(firstProject.automaticPractical, true);
  assert.ok(!JSON.stringify(firstProject).includes('CAMP-OK'));
  const automaticUrl = `${campUrl}/practical/${firstProject.id}`;
  await peer(automaticUrl, 'GET', undefined, 404);
  const project = await outsider(automaticUrl);
  const projectConfig = structuredClone(project.devices[0].configuration);
  projectConfig.hostname = 'CAMP-OK';
  const savedProject = await outsider(`${automaticUrl}/devices/${device.id}`, 'PUT', {revision: project.revision, configuration: projectConfig});
  assert.equal(savedProject.hasWork, true);
  await outsider(`${automaticUrl}/devices/${device.id}`, 'PUT', {revision: project.revision, configuration: projectConfig}, 409);
  assert.notEqual((await outsider(`${campUrl}/practical/${secondProject.id}`)).devices[0].configuration.hostname, 'CAMP-OK');
  await outsider(`${campUrl}/answers`, 'POST', {answers: [{questionId:objective.id, selected:[0]},
    {questionId:secondProject.id, selected:[], text:'SECOND'}]});
  const db = new DatabaseSync(path.join(data, 'school.db'));
  db.exec('PRAGMA busy_timeout=5000');
  try {
    const deadline = new Date(Date.now() + 300).toISOString().replace('T', ' ').replace('Z', '');
    db.prepare('UPDATE "JobAttempts" SET "Deadline" = ? WHERE "Id" = ?').run(deadline, camp.attemptId);
    const status = db.prepare('SELECT "Status" FROM "JobAttempts" WHERE "Id" = ?');
    for (let retry = 0; retry < 60 && status.get(camp.attemptId).Status !== 'graded'; retry++) await delay(150);
    assert.equal(status.get(camp.attemptId).Status, 'graded');
  } finally { db.close(); }
  const gradedCamp = await outsider(`/camp/attempts/${camp.attemptId}/record`);
  assert.equal(gradedCamp.attempt.autoSubmitted, true);
  assert.equal(gradedCamp.attempt.practicalScore, 10);
  assert.equal(gradedCamp.attempt.objectiveScore, 10);
  assert.equal(gradedCamp.questions.find(item => item.questionId === firstProject.id).practicalDetails[0].matched, true);
  assert.equal(gradedCamp.questions.find(item => item.questionId === secondProject.id).answered, false);
  await teacher(roomUrl, 'PUT', {...roomInput, openAt:new Date(Date.now()-1800000).toISOString(),
    closeAt:new Date(Date.now()-1000).toISOString()});
  assert.equal((await student(`${roomUrl}/content`)).canLearn, false);
  await student(`${theoryUrl}/submit`, 'POST', {answers:[{questionId:question.id, selected:[0]}]}, 409);
  await teacher(`/classrooms/case-sessions/${(await peer(workUrl)).sessionId}/restart`, 'POST', {}, 409);
  for (let attempt = 0; attempt < 25 && (await peer(workUrl)).status !== 'saved'; attempt++) await delay(180);
  assert.equal((await peer(workUrl)).status, 'saved');
  console.log('PASS classroom window enforcement, topology deployment, consent, scoped frames, assistance and automatic session release');
  console.log('PASS camp project isolation, device-based scoring, score details and deadline submission without learner requests');
  console.log('PASS virtual machine search, console controls, stale restart commands, destroy guard and live classroom/camp isolation');
  console.log('PASS classroom grading, guidance, isolation, save and process restart, stale writes, repeat grading and staff history');
} finally {
  await stop();
}
