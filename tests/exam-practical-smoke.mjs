// Runs the deployment and grading lifecycle against an isolated local database.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {randomBytes} from 'node:crypto';
import {mkdtemp} from 'node:fs/promises';
import net from 'node:net';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {DatabaseSync} from 'node:sqlite';
import {setTimeout as delay} from 'node:timers/promises';
import {fileURLToPath} from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const app = path.resolve(process.env.SCHOOL_APP_DIR ?? path.join(root, 'src/School.Training/bin/Debug/net10.0'));
const data = await mkdtemp(path.join(tmpdir(), 'school-exam-practical-'));
const server = net.createServer();
await new Promise((resolve, reject) => server.once('error', reject).listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}`;
await new Promise(resolve => server.close(resolve));
const child = spawn('dotnet', [path.join(app, 'School.Training.dll'), '--urls', base], {
  cwd: app, env: {...process.env, SCHOOL_DATA_DIR: data, ASPNETCORE_ENVIRONMENT: 'Production'},
  windowsHide: true, stdio: ['ignore', 'pipe', 'pipe']
});
let output = '';
for (const stream of [child.stdout, child.stderr]) stream.on('data', chunk => { output = (output + chunk).slice(-12000); });
const password = `ExamQA${randomBytes(12).toString('hex')}`;
const changedPassword = `Changed${password}`;

function client() {
  const cookies = new Map();
  let token;
  return async (route, method = 'GET', payload, expected = 200, rawResponse = false) => {
    const headers = {Cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join('; ')};
    if (token) headers['X-CSRF-TOKEN'] = token;
    if (payload !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(`${base}/api${route}`, {
      method, headers, body: payload === undefined ? undefined : JSON.stringify(payload)
    });
    for (const cookie of response.headers.getSetCookie()) {
      const pair = cookie.split(';')[0];
      const index = pair.indexOf('=');
      cookies.set(pair.slice(0, index), pair.slice(index + 1));
    }
    const raw = await response.text();
    assert.equal(response.status, expected, `${method} ${route}: ${raw.slice(0, 1000)}`);
    if (rawResponse) return raw;
    const result = raw ? JSON.parse(raw) : null;
    if (result?.csrfToken) token = result.csrfToken;
    return result;
  };
}

const admin = client();
const alice = client();
const bob = client();

try {
  let ready = false;
  for (let attempt = 0; attempt < 150 && child.exitCode === null; attempt++) {
    try { if ((await fetch(`${base}/api/status`)).ok) { ready = true; break; } } catch {}
    await delay(100);
  }
  assert.ok(ready, output);
  await admin('/setup', 'POST', {schoolName: '实操考试测试学校', userName: 'examadmin', displayName: '管理员', password});
  const profession = await admin('/professions', 'POST', {name: '实训', code: 'EXAM'});
  const schoolClass = await admin('/classes', 'POST', {name: '考试班', professionId: profession.id});
  const studentIds = [];
  for (const [index, account] of [alice, bob].entries()) {
    const userName = `examlearner${index}`;
    await admin('/people', 'POST', {
      kind: 'student', userName, password, name: `学生 ${index}`, number: `EXAM${index}`, classId: schoolClass.id
    });
    const login = await account('/auth/login', 'POST', {userName, password});
    studentIds.push(login.user.id);
    await account('/auth/password', 'POST', {currentPassword: password, newPassword: changedPassword});
  }
  await admin('/people', 'POST', {
    kind:'student', userName:'examabsent', password, name:'缺考学生', number:'EXAMABSENT', classId:schoolClass.id
  });
  await admin('/simulation/bootstrap', 'POST', {});
  const [device] = await admin('/nodes/simulation-node/case-devices');
  const resourceText = '接口参数表\n请使用题目分配的网络。';
  const practicalInput = {
    type: 'practical', stem: '配置网络设备', options: [], answers: [], score: 25,
    practicalNodeId: 'simulation-node', practicalResources: resourceText,
    practicalRubric: [
      {name: '主机名', deviceId: device.id, field: 'hostname', interfaceName: null, expected: 'EXAM-OK', score: 3},
      {name: '关闭端口', deviceId: device.id, field: 'enabled', interfaceName: device.interfaces[0], expected: 'false', score: 1,
        guidance: '请检查端口状态。'}
    ]
  };
  const practical = await admin('/exam-questions', 'POST', practicalInput);
  assert.equal((await admin(`/exam-questions/${practical.id}`)).practicalResources, resourceText);
  const another = await admin('/exam-questions', 'POST', {
    type: 'practical', stem: '为另一台设备配置名称', options: [], answers: [], score: 10,
    practicalNodeId: 'simulation-node',
    practicalRubric: [{name: '另一个项目', deviceId: device.id, field: 'hostname', interfaceName: null, expected: 'SECOND', score: 10}]
  });
  const objective = await admin('/exam-questions', 'POST', {
    type: 'single', stem: '选择正确答案', options: ['对', '错'], answers: [0], score: 5
  });
  const paper = await admin('/exam-papers', 'POST', {name: '实操与理论', description: null, remark: null});
  const draft = await admin('/exam-papers', 'POST', {name:'批量移题验收', description:null, remark:null});
  const draftQuestions = `/exam-papers/${draft.id}/questions`;
  await admin(draftQuestions, 'POST', {items:[{questionId:practical.id}, {questionId:another.id}, {questionId:objective.id}]});
  const initial = (await admin(`/exam-papers/${draft.id}`)).questions;
  await admin(`${draftQuestions}/remove`, 'POST', {paperQuestionIds:[initial[0].id, 'unknown']}, 404);
  await admin(`${draftQuestions}/remove`, 'POST', {paperQuestionIds:[initial[0].id, initial[1].id]}, 204);
  await admin(draftQuestions, 'POST', {items:[{questionId:practical.id}]});
  assert.deepEqual((await admin(`/exam-papers/${draft.id}`)).questions.map(item => item.questionId), [objective.id, practical.id]);
  await admin(`/exam-papers/${paper.id}/questions`, 'POST', {
    items: [{questionId: practical.id, score: 20}, {questionId: another.id}, {questionId: objective.id}]
  });
  await admin(`/exam-questions/${practical.id}`, 'PUT', {...practicalInput, practicalResources: '新版本仅用于今后的试卷'}, 204);
  const exam = await admin('/exams', 'POST', {name: '模拟考试', paperId: paper.id, description: null,
    startAt: new Date(Date.now() + 120000).toISOString(), endAt: new Date(Date.now() + 300000).toISOString(), durationMinutes: 3,
    classIds: [schoolClass.id]
  });
  const examUrl = `/exams/${exam.id}`;
  const hall = `/exam-hall/${exam.id}`;
  const deployment = `${examUrl}/deployment`;
  await admin(`${examUrl}/open`, 'POST', {});
  await alice(`${hall}/start`, 'POST', {}, 409);
  await admin('/nodes/simulation-node/disable', 'POST', {});
  assert.equal((await admin(deployment, 'POST', {studentUserIds: [studentIds[0]]})).failed, 1);
  assert.ok((await admin(`${deployment}/${studentIds[0]}`)).items.every(item => item.status === 'failed' && item.message.includes('停用')));
  await admin('/nodes/simulation-node/enable', 'POST', {});
  assert.equal((await admin(deployment, 'POST', {studentUserIds: [studentIds[0]]})).deployed, 1);
  await bob(`${hall}/start`, 'POST', {}, 409);
  assert.equal((await admin(deployment, 'POST', {})).deployed, 2);
  assert.ok((await admin(`${deployment}/${studentIds[0]}`)).items.every(item => item.status === 'deployed'));
  assert.equal((await alice(hall)).canStart, false);
  await alice(`${hall}/start`, 'POST', {}, 409);
  const timeFixture = new DatabaseSync(path.join(data, 'school.db'));
  timeFixture.exec('PRAGMA busy_timeout=5000');
  timeFixture.prepare('UPDATE "TrainingExams" SET "StartAt" = ? WHERE "Id" = ?').run(
    new Date(Date.now() - 1000).toISOString().replace('T', ' ').replace('Z', ''), exam.id);
  timeFixture.close();
  const aliceAttempt = await alice(`${hall}/start`, 'POST', {});
  const bobAttempt = await bob(`${hall}/start`, 'POST', {});
  const questionList = (await alice(hall)).questions;
  assert.equal(questionList[0].practicalResources, resourceText);
  assert.equal(questionList[2].practicalResources, '');
  const [first, second, theory] = questionList.map(item => item.paperQuestionId);
  const project = `${hall}/practical/${first}`;
  const work = await alice(project);
  const examConsole = `/vm-console/exam/${work.virtualMachine.id}`;
  const consoleState = await alice(examConsole);
  await bob(examConsole, 'GET', undefined, 404);
  assert.notEqual((await bob(project)).virtualMachine.id, work.virtualMachine.id);
  await alice(`${examConsole}/commands`, 'POST', {command:'link down', connectionVersion:consoleState.connectionVersion});
  assert.equal((await admin(examConsole)).linkUp, false);
  const configure = async (account, url, snapshot, hostname) => {
    const configuration = structuredClone(snapshot.devices[0].configuration);
    configuration.hostname = hostname;
    return account(`${url}/devices/${device.id}`, 'PUT', {revision: snapshot.revision, configuration});
  };
  assert.ok(!JSON.stringify(questionList).includes('EXAM-OK'));
  assert.ok(!JSON.stringify(work).includes('EXAM-OK'));
  await alice(`${deployment}/${studentIds[1]}`, 'GET', undefined, 403);
  await configure(alice, project, work, 'EXAM-OK');
  assert.notEqual((await alice(`${hall}/practical/${second}`)).devices[0].configuration.hostname, 'EXAM-OK');
  assert.notEqual((await bob(project)).devices[0].configuration.hostname, 'EXAM-OK');
  assert.equal((await admin(deployment, 'POST', {})).existing, 3);
  assert.equal((await alice(project)).devices[0].configuration.hostname, 'EXAM-OK');
  assert.equal((await admin(`${deployment}/${studentIds[0]}`)).items.length, 2);
  const staffProject = `${deployment}/${studentIds[0]}/${first}`;
  assert.equal((await admin(staffProject)).devices[0].configuration.hostname, 'EXAM-OK');
  const beforeRestart = await alice(project);
  await admin(`${staffProject}/restart`, 'POST', {});
  await alice(`${examConsole}/commands`, 'POST', {command:'status', connectionVersion:consoleState.connectionVersion}, 409);
  assert.equal((await alice(examConsole)).linkUp, true);
  await alice(`${project}/devices/${device.id}`, 'PUT',
    {revision: beforeRestart.revision, configuration: beforeRestart.devices[0].configuration}, 409);
  const restarted = await alice(project);
  assert.equal(restarted.hasWork, false);
  assert.notEqual(restarted.devices[0].configuration.hostname, 'EXAM-OK');
  await configure(alice, project, restarted, 'EXAM-OK');
  await configure(bob, project, await bob(project), 'EXAM-OK');
  const monitor = await admin(`${examUrl}/monitor`);
  assert.equal(monitor.items.find(item => item.studentUserId === studentIds[0]).answeredCount, 1);

  const result = await alice(`${hall}/submit`, 'POST', {answers: [{paperQuestionId: theory, selected: [0]}]});
  assert.equal(result.status, 'graded');
  assert.equal(result.practicalScore, 15);
  assert.equal(result.objectiveScore, 5);
  assert.equal(result.totalScore, 20);
  const liveStatistics = await admin(`${examUrl}/statistics`);
  assert.equal(liveStatistics.expected, 3);
  assert.equal(liveStatistics.started, 2);
  assert.equal(liveStatistics.bands.find(item => item.label.includes('待批阅')).count, 1);
  const staffReview = await admin(`${examUrl}/attempts/${aliceAttempt.attemptId}`);
  assert.equal(staffReview.questions[0].practicalDetails[1].guidance, '请检查端口状态。');
  assert.equal((await alice('/exam-records')).items[0].canReview, false);
  await alice(`/exam-records/${aliceAttempt.attemptId}`, 'GET', undefined, 409);
  await alice(examConsole, 'GET', undefined, 404);
  await admin(`${staffProject}/restart`, 'POST', {}, 409);

  // A real server deadline is scheduled a moment ahead while no student makes requests.
  const db = new DatabaseSync(path.join(data, 'school.db'));
  db.exec('PRAGMA busy_timeout=5000');
  const deadline = new Date(Date.now() + 300).toISOString().replace('T', ' ').replace('Z', '');
  db.prepare('UPDATE "ExamAttempts" SET "Deadline" = ? WHERE "Id" = ?').run(deadline, bobAttempt.attemptId);
  const status = db.prepare('SELECT "Status" FROM "ExamAttempts" WHERE "Id" = ?');
  for (let retry = 0; retry < 60 && status.get(bobAttempt.attemptId).Status !== 'graded'; retry++) await delay(150);
  assert.equal(status.get(bobAttempt.attemptId).Status, 'graded');
  db.close();
  await bob(`/exam-records/${bobAttempt.attemptId}`, 'GET', undefined, 409);
  await admin(`${examUrl}/close`, 'POST', {});
  const expired = await bob(`/exam-records/${bobAttempt.attemptId}`);
  assert.equal(expired.autoSubmitted, true);
  assert.equal(expired.practicalScore, 15);
  assert.equal((await alice('/exam-records')).items[0].canReview, true);
  assert.equal((await alice(`/exam-records/${aliceAttempt.attemptId}`)).questions[0].practicalDetails.length, 2);
  const stats = await admin(`${examUrl}/statistics`);
  assert.deepEqual(stats.bands.map(item => item.count), [1, 2, 0, 0]);
  assert.deepEqual(stats.bands.map(item => item.percent), [33.3, 66.7, 0, 0]);
  assert.equal(stats.typeStats.find(item => item.type === 'single').accuracy, 100);
  assert.equal(stats.typeStats.find(item => item.type === 'practical').correct, 0);
  const statsCsv = await admin(`${examUrl}/statistics/export`, 'GET', undefined, 200, true);
  for (const section of ['数据总览', '分数各段比例', '各题型正确率']) assert.ok(statsCsv.includes(section));
  assert.match(statsCsv, /缺考",1,33\.3%/);
  const theoryAnalysis = (await admin(`${examUrl}/question-analysis?type=single`)).items[0];
  assert.deepEqual([theoryAnalysis.number, theoryAnalysis.answered, theoryAnalysis.unanswered, theoryAnalysis.correctCount], [3, 1, 1, 1]);
  const analysisCsv = await admin(`${examUrl}/question-analysis/export?type=single`, 'GET', undefined, 200, true);
  assert.match(analysisCsv, /3,"单选题","选择正确答案",5,1,1,1,100%/);
  assert.doesNotMatch(analysisCsv, /配置网络设备/);
  const elapsed = (await admin(`${examUrl}/scores?search=EXAM0`)).items[0].elapsedSeconds;
  assert.ok(Number.isInteger(elapsed) && elapsed >= 0);
  console.log('PASS atomic paper question removal, exam bands and export, question statistics and elapsed time');
  console.log('PASS practical exam deployment, project isolation, restart, automatic grades, exam review and background submission');
} finally {
  if (child.exitCode === null && child.signalCode === null) {
    const exited = new Promise(resolve => child.once('exit', resolve));
    child.kill();
    await Promise.race([exited, delay(5000, undefined, {ref: false})]);
  }
}
