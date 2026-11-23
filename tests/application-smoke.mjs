// Real HTTP integration against a fresh local database. No school or installed service is touched.
// Requires Node.js 22.5+ for the built-in node:sqlite module used by the database-guard regression.
import assert from 'node:assert/strict';
import {spawn, execFileSync} from 'node:child_process';
import {generateKeyPairSync, sign, randomBytes, createHash} from 'node:crypto';
import {existsSync, readFileSync} from 'node:fs';
import {mkdir, writeFile, readFile, readdir, utimes} from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import http from 'node:http';
import {DatabaseSync} from 'node:sqlite';
import {fileURLToPath} from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const appDirectory = path.resolve(process.env.SCHOOL_APP_DIR ?? path.join(root, 'src/School.Training/bin/Debug/net10.0'));
const appExecutable = path.join(appDirectory, process.platform === 'win32' ? 'School.Training.exe' : 'School.Training');
const appDll = path.join(appDirectory, 'School.Training.dll');
const runApp = args => existsSync(appExecutable)
  ? execFileSync(appExecutable, args, {windowsHide:true, stdio:'pipe'})
  : execFileSync('dotnet', [appDll, ...args], {windowsHide:true, stdio:'pipe'});
const dir = path.join(root, '.local', `application-test-${Date.now()}`);
await mkdir(dir, {recursive:true});
const password = `Qa${randomBytes(12).toString('hex')}`;
const replacement = `New${randomBytes(12).toString('hex')}`;
const {publicKey,privateKey} = generateKeyPairSync('rsa',{modulusLength:2048,publicKeyEncoding:{type:'spki',format:'pem'},privateKeyEncoding:{type:'pkcs8',format:'pem'}});
const keyFile = path.join(dir,'license-public.pem'); await writeFile(keyFile,publicKey);
const processes=[]; const passed=[];
const mark = name => {passed.push(name); console.log(`PASS ${name}`);};
async function port(){const s=net.createServer();await new Promise(r=>s.listen(0,'127.0.0.1',r));const p=s.address().port;await new Promise(r=>s.close(r));return p;}
async function start(data){
  const base=`http://127.0.0.1:${await port()}`;
  const useExe=existsSync(appExecutable);
  const child=spawn(useExe?appExecutable:'dotnet',useExe?['--urls',base]:[appDll,'--urls',base],{cwd:appDirectory,env:{...process.env,SCHOOL_DATA_DIR:data,SCHOOL_LICENSE_PUBLIC_KEY:keyFile,ASPNETCORE_ENVIRONMENT:'Production','School__Backups__RetentionCount':'2','School__Backups__MinimumFreeBytes':'0','School__Logs__RetentionDays':'1','School__Database__BusyTimeoutSeconds':'2'},windowsHide:true,stdio:['ignore','pipe','pipe']});
  processes.push(child); let output='';child.stdout.on('data',b=>{output=(output+b).slice(-80000)});child.stderr.on('data',b=>{output=(output+b).slice(-80000)});
  // A cold self-contained start can take well over 10 seconds on a busy or AV-scanned machine.
  for(let i=0;i<300;i++){if(child.exitCode!==null)throw Error(`Service exited: ${output}`);try{const r=await fetch(`${base}/api/status`);if(r.ok)return base;}catch{}await new Promise(r=>setTimeout(r,100));}
  throw Error(`Service did not start: ${output}`);
}
class Client {
  constructor(base){this.base=base;this.cookies=new Map();this.csrf='';}
  async attempt(route,method='GET',data,options={}){
    const headers={Cookie:[...this.cookies].map(([k,v])=>`${k}=${v}`).join('; '),...options.headers};
    let body;
    if(data instanceof FormData)body=data;
    else if(data!==undefined){headers['Content-Type']='application/json';body=JSON.stringify(data);}
    if(this.csrf&&!options.noCsrf)headers['X-CSRF-TOKEN']=this.csrf;
    const r=await fetch(this.base+'/api'+route,{method,headers,body});
    for(const cookie of r.headers.getSetCookie()){const pair=cookie.split(';')[0],i=pair.indexOf('=');this.cookies.set(pair.slice(0,i),pair.slice(i+1));}
    const type=r.headers.get('content-type')??'';
    const result=type.includes('json')?await r.json():Buffer.from(await r.arrayBuffer());
    if(result?.csrfToken)this.csrf=result.csrfToken;
    return {status:r.status,result,headers:r.headers};
  }
  async request(route,method='GET',data,expected=200,options={}){
    const response=await this.attempt(route,method,data,options);
    assert.equal(response.status,expected,`${method} ${route}: ${JSON.stringify(response.result).slice(0,600)}`);
    return response.result;
  }
}
function upload(name,content){const form=new FormData();form.append('file',new Blob([content]),name);return form;}
async function waitForImport(api,batchId,timeoutMs=120000){
  const started=Date.now();let last;
  while(Date.now()-started<timeoutMs){
    last=await api.request(`/imports/${batchId}`);
    if(last.status==='completed')return last;
    if(last.status==='failed'||last.status==='cancelled')throw Error(last.failureMessage||`Import ended as ${last.status}`);
    await new Promise(resolve=>setTimeout(resolve,100));
  }
  throw Error(`Import ${batchId} did not finish: ${JSON.stringify(last)}`);
}
function assertKeys(value,keys,label){
  assert.ok(value&&typeof value==='object'&&!Array.isArray(value),`${label} should be an object`);
  for(const key of keys)assert.ok(Object.prototype.hasOwnProperty.call(value,key),`${label} is missing ${key}`);
}
function assertPagedShape(response,itemKeys,label){
  assertKeys(response,['total','items'],label);
  assert.ok(Array.isArray(response.items),`${label}.items should be an array`);
  for(const item of response.items.slice(0,3))assertKeys(item,itemKeys,`${label}.items[]`);
}
try{
  const data=path.join(dir,'data');
  await mkdir(path.join(data,'logs'),{recursive:true});
  await writeFile(path.join(data,'.schooltraining-data.json'),JSON.stringify({product:'SchoolTraining',schemaVersion:1}));
  const staleLog=path.join(data,'logs','service-20200101.log');await writeFile(staleLog,'stale log');
  const oldTime=new Date(Date.now()-10*24*60*60*1000);await utimes(staleLog,oldTime,oldTime);
  const api=new Client(await start(data));
  assert.equal(existsSync(staleLog),false);
  assert.ok((await readdir(path.join(data,'logs'))).some(name=>/^service-\d{8}\.log$/.test(name)));
  mark('过期日志按保留天数清理，当前日志继续写入');
  assert.equal((await api.request('/status')).initialized,false);
  const unexpectedHostStatus=await new Promise((resolve,reject)=>{const request=http.get(api.base+'/api/status',{headers:{Host:'untrusted.invalid'}},response=>{response.resume();response.once('end',()=>resolve(response.statusCode))});request.once('error',reject)});assert.equal(unexpectedHostStatus,400);
  await api.request('/professions','GET',undefined,401);
  await api.request('/setup','POST',{schoolName:'验收测试学校',userName:'testadmin',displayName:'测试管理员',password:'weak'},400);
  await api.request('/setup','POST',{schoolName:'验收测试学校',userName:'testadmin',displayName:'测试管理员',password});
  await api.request('/setup','POST',{schoolName:'重复',userName:'otheradmin',displayName:'重复管理员',password},409);
  const me=await api.request('/auth/me');assert.equal(me.user.role,'Admin');assert.ok(me.csrfToken);
  const unknownStarted=Date.now();const unknownLogin=await api.attempt('/auth/login','POST',{userName:'nobody-here',password});const unknownElapsed=Date.now()-unknownStarted;
  const wrongStarted=Date.now();const wrongLogin=await api.attempt('/auth/login','POST',{userName:'testadmin',password:`Wrong-${password}`});const wrongElapsed=Date.now()-wrongStarted;
  assert.equal(unknownLogin.status,401);assert.equal(wrongLogin.status,401);
  assert.equal(unknownLogin.result.message,wrongLogin.result.message);
  assert.ok(unknownElapsed>=wrongElapsed*0.4,`Unknown-account login took ${unknownElapsed}ms against ${wrongElapsed}ms for a wrong password; the password hash was likely skipped`);
  mark('未知账号与密码错误执行同样校验并返回相同401');
  assert.equal((await api.request('/missing-interface','GET',undefined,404)).message,'接口不存在');
  mark('首次本机初始化、登录会话、重复初始化保护');
  await api.request('/professions','POST',{name:'软件技术',code:'510203'},400,{noCsrf:true});
  await api.request('/professions','POST',{name:'软件技术',code:'510203'},403,{headers:{Origin:'https://untrusted.invalid'}});
  await api.request('/professions','POST',{name:null,code:'bad'},400);
  const profession=await api.request('/professions','POST',{name:'软件技术',code:'510203',remark:'测试'});
  await api.request('/professions','POST',{name:'同代码',code:'510203'},409);
  const cls=await api.request('/classes','POST',{name:'软件一班',professionId:profession.id});
  const raceOne=new Client(api.base),raceTwo=new Client(api.base);
  raceOne.cookies=new Map(api.cookies);raceOne.csrf=api.csrf;raceTwo.cookies=new Map(api.cookies);raceTwo.csrf=api.csrf;
  const classRace=await Promise.all([
    raceOne.attempt('/classes','POST',{name:'并发同名班级',professionId:profession.id}),
    raceTwo.attempt('/classes','POST',{name:'并发同名班级',professionId:profession.id})
  ]);
  assert.deepEqual(classRace.map(x=>x.status).sort((a,b)=>a-b),[200,409]);
  const racedClass=(await api.request('/classes?search=并发同名班级')).items[0];await api.request(`/classes/${racedClass.id}`,'DELETE');
  await api.request(`/professions/${profession.id}`,'DELETE',undefined,409);
  const student=await api.request('/people','POST',{kind:'student',name:'测试学生',userName:'student001',number:'2026001',classId:cls.id,password,role:'Admin'});
  const students=await api.request('/people?kind=student');assert.equal(students.items[0].role,'Student');
  await api.request('/people','POST',{kind:'teacher',name:'重复账号',userName:'STUDENT001',number:'T001',password},400);
  await api.request(`/classes/${cls.id}`,'DELETE',undefined,409);
  const temporary=await api.request('/professions','POST',{name:'临时专业',code:'TEMP'});
  await api.request(`/professions/${temporary.id}`,'PUT',{name:'已编辑专业',code:'TEMP2',remark:'编辑验证'});
  assert.equal((await api.request('/professions?search=TEMP2')).total,1);
  const temporaryClass=await api.request('/classes','POST',{name:'临时班级',professionId:temporary.id});
  await api.request(`/classes/${temporaryClass.id}`,'PUT',{name:'已编辑班级',professionId:temporary.id,remark:'编辑验证'});
  assert.equal((await api.request('/classes?search=已编辑班级')).total,1);
  assert.deepEqual((await api.request(`/classes?professionId=${temporary.id}`)).items.map(row=>row.id),[temporaryClass.id]);
  assert.equal((await api.request(`/classes?professionId=${profession.id}&search=已编辑班级`)).total,0);
  assert.deepEqual((await api.request(`/people?kind=student&classId=${cls.id}`)).items.map(row=>row.id),[student.id]);
  assert.equal((await api.request(`/people?kind=student&classId=${temporaryClass.id}`)).total,0);
  const teacher=await api.request('/people','POST',{kind:'teacher',name:'临时教师',userName:'teacher001',number:'T001',classId:cls.id,password});
  const administrator=await api.request('/people','POST',{kind:'teacher',name:'临时管理员',userName:'teacheradmin001',number:'T003',classId:temporaryClass.id,password,role:'Admin'});
  await api.request(`/people/${teacher.id}`,'PUT',{kind:'teacher',name:'已编辑教师',userName:'teacher001',number:'T002',classId:cls.id,gender:'女',email:'teacher@example.test',role:'Teacher'});
  const teacherRow=(await api.request('/people?kind=teacher&search=T002')).items[0];assert.equal(teacherRow.name,'已编辑教师');assert.equal(teacherRow.email,'teacher@example.test');
  assert.deepEqual((await api.request('/people?kind=teacher&role=Admin')).items.map(row=>row.id),[administrator.id]);
  assert.deepEqual((await api.request(`/people?kind=teacher&classId=${cls.id}&role=Teacher&search=T002`)).items.map(row=>row.id),[teacher.id]);
  assert.equal((await api.request(`/people?kind=teacher&classId=${temporaryClass.id}&role=Teacher`)).total,0);
  await api.request('/people/batch-delete','POST',{ids:[teacher.id,administrator.id]});assert.equal((await api.request('/people?kind=teacher')).total,0);
  await api.request(`/classes/${temporaryClass.id}`,'DELETE');await api.request(`/professions/${temporary.id}`,'DELETE');
  mark('CSRF和跨站拒绝、输入校验、专业班级关联、师生账号唯一性');
  mark('班级按专业、师生按班级、教师按角色筛选，可与关键词组合');
  mark('并发同名班级仅一个成功，数据库唯一约束与409映射生效');
  const dashboard=await api.request('/dashboard');
  assertKeys(dashboard,['schoolName','counts','recentAudits','server','version'],'dashboard');
  assertKeys(dashboard.counts,['professions','classes','teachers','students','cabinets','devices','nodes'],'dashboard.counts');
  assertKeys(dashboard.server,['machineName','os','framework'],'dashboard.server');
  assertPagedShape(await api.request('/professions'),['id','name','code','remark'],'professions');
  assertPagedShape(await api.request('/classes'),['id','name','professionId','professionName','remark'],'classes');
  assertPagedShape(await api.request('/people?kind=student'),['id','kind','name','userName','number','classId','className','gender','phone','email','role'],'people');
  assertPagedShape(await api.request('/courses'),['id','name','description','type','status','remark','createdAt','publishedAt','isActive','seriesId','version'],'courses');
  assertPagedShape(await api.request('/exam-questions'),['id','bankId','bankName','type','stem','optionsJson','answerJson','analysis','score','createdAt','updatedAt','practicalNodeId'],'exam-questions');
  assertPagedShape(await api.request('/exam-papers'),['id','name','description','remark','createdAt','updatedAt','questionCount','totalScore','typeCounts','examCount'],'exam-papers');
  assertPagedShape(await api.request('/cabinets'),['id','name','host','port','jumpHost','jumpPort','jumpUserName','hasJumpPassword','remark','isSimulated','deviceCount','activeSessionCount'],'cabinets');
  assertPagedShape(await api.request('/nodes'),['id','name','host','provider','port','userName','hasPassword','enabled','remark','totalCpu','totalMemoryMb','totalDiskGb','isSimulated','healthStatus','cpuUsagePercent','memoryUsagePercent','diskUsagePercent','virtualMachineCount','virtualMachineQuota','accountCount','accountQuota'],'nodes');
  assertPagedShape(await api.request('/audits'),['id','at','userName','action','target','detail'],'audits');
  const backups=await api.request('/backups');
  assertKeys(backups,['items','automatic'],'backups');
  assert.ok(Array.isArray(backups.items),'backups.items should be an array');
  assertKeys(backups.automatic,['enabled','timeOfDay','lastAttemptAt','lastSuccessAt','lastMessage'],'backups.automatic');
  const licenses=await api.request('/licenses');
  assertKeys(licenses,['machineCode','publicKeyConfigured','mode','quotas','items'],'licenses');
  assertKeys(await api.request('/server/info'),['hostName','osDescription','operatingSystem','architecture','isSimulated','collectedAt','cpu','memory','networks','bootTime','uptimeSeconds','processUptimeSeconds','warnings'],'server/info');
  mark('高频接口响应形状稳定，前后端契约未漂移');
  const csv='姓名,账号,学号,班级,性别,手机号,电子邮箱\r\n导入学生,import001,2026002,软件一班,女,13800000001,import@example.test\r\n重复账号,testadmin,2026003,软件一班,男,,\r\n错误班级,import003,2026004,不存在班级,,,\r\n错误邮箱,import004,2026005,软件一班,,,bad-email\r\n';
  const preview=await api.request('/imports/preview?kind=student','POST',upload('students.csv',csv));
  assert.equal(preview.validCount,1);assert.equal(preview.errorCount,3);assert.ok(preview.rows[1].errors[0].column);assert.equal(preview.rows[1].rowNumber,3);
  await api.request(`/imports/${preview.batchId}/commit`,'POST',{removeInvalidRows:false,initialPassword:password},400);
  const queueStatus=await api.request(`/imports/${preview.batchId}/commit`,'POST',{removeInvalidRows:true,initialPassword:password},202);
  assert.equal(queueStatus.status,'queued');assert.equal(queueStatus.totalCount,1);assert.equal(queueStatus.removedCount,3);
  const commit=await waitForImport(api,preview.batchId);
  assert.equal(commit.importedCount,1);assert.equal(commit.removedCount,3);
  await api.request(`/imports/${preview.batchId}/commit`,'POST',{removeInvalidRows:true,initialPassword:password},409);
  assert.equal((await api.request('/people?kind=student')).total,2);
  mark('CSV预览定位错误行列、后台导入任务可查询、仅移除待导入问题行、原子提交与防重复');
  const cancelRows=Array.from({length:600},(_,index)=>`取消学生${index},cancel${String(index).padStart(3,'0')},CANCEL-${String(index).padStart(3,'0')},软件一班,,,`).join('\r\n');
  const cancelPreview=await api.request('/imports/preview?kind=student','POST',upload('cancel-students.csv',`姓名,账号,学号,班级,性别,手机号,电子邮箱\r\n${cancelRows}\r\n`));
  assert.equal(cancelPreview.validCount,600);
  await api.request(`/imports/${cancelPreview.batchId}/commit`,'POST',{removeInvalidRows:false,initialPassword:password},202);
  let running=await api.request(`/imports/${cancelPreview.batchId}`);
  const startedAt=Date.now();
  while(running.status!=='running'&&Date.now()-startedAt<10000) {
    await new Promise(resolve=>setTimeout(resolve,50));
    running=await api.request(`/imports/${cancelPreview.batchId}`);
  }
  assert.equal(running.status,'running',`Import never reached running state: ${JSON.stringify(running)}`);
  await api.request(`/imports/${cancelPreview.batchId}/cancel`,'POST');
  let cancelled=await api.request(`/imports/${cancelPreview.batchId}`);
  const cancelDeadline=Date.now()+10000;
  while(['running','queued','cancelling'].includes(cancelled.status)&&Date.now()<cancelDeadline) {
    await new Promise(resolve=>setTimeout(resolve,50));
    cancelled=await api.request(`/imports/${cancelPreview.batchId}`);
  }
  assert.equal(cancelled.status,'cancelled',`Import was not cancelled: ${JSON.stringify(cancelled)}`);
  assert.ok(cancelled.processedCount<600);
  assert.equal((await api.request('/people?kind=student')).total,2);
  mark('运行中导入可协作取消，取消后不写入任何账号');
  const studentApi=new Client(api.base);const login=await studentApi.request('/auth/login','POST',{userName:'student001',password});assert.equal(login.user.mustChangePassword,true);
  await studentApi.request('/dashboard','GET',undefined,428);
  const changed=await studentApi.request('/auth/password','POST',{currentPassword:password,newPassword:replacement});assert.equal(changed.user.mustChangePassword,false);
  await studentApi.request('/people?kind=student','GET',undefined,403);
  await studentApi.request('/dashboard');
  await api.request(`/people/${student.id}/reset-password`,'POST',{newPassword:password});
  await studentApi.request('/auth/me','GET',undefined,401);
  mark('学生不能提升权限、首次改密、管理员重置后旧会话撤销');
  const lic=await api.request('/licenses');assert.equal(lic.publicKeyConfigured,true);assert.equal(lic.machineCode.length,32);
  await api.request('/licenses','POST',upload('null.license.json','null'),400);
  const nullPayload=Buffer.from('null');await api.request('/licenses','POST',upload('null-payload.license.json',JSON.stringify({payload:nullPayload.toString('base64'),signature:sign('RSA-SHA256',nullPayload,privateKey).toString('base64')})),400);
  const payload=Buffer.from(JSON.stringify({licenseId:'QA-LICENSE-001',product:'SchoolTraining',machineCode:lic.machineCode,issuedAt:new Date().toISOString(),expiresAt:null,resources:[{name:'模拟节点',authorizationCode:'QA-RESOURCE',quantity:5}]}));
  const envelope={payload:payload.toString('base64'),signature:sign('RSA-SHA256',payload,privateKey).toString('base64')};
  await api.request('/licenses','POST',upload('valid.license.json',JSON.stringify(envelope)));
  await api.request('/licenses','POST',upload('duplicate.license.json',JSON.stringify(envelope)),409);
  envelope.signature=Buffer.alloc(256).toString('base64');await api.request('/licenses','POST',upload('tampered.license.json',JSON.stringify(envelope)),400);
  assert.equal((await api.request('/licenses')).items.length,1);
  mark('许可证RSA签名、设备绑定、重复导入和篡改拒绝');
  const quotaClient = new Client(await start(path.join(dir, 'quota-data')));
  await quotaClient.request('/setup', 'POST', {schoolName: '许可证额度验收', userName: 'quotaaudit', displayName: '测试管理员', password});
  const quotaCode = (await quotaClient.request('/licenses')).machineCode;
  const resource = (code, quantity) => ({ name: code, authorizationCode: `TRAINING.${code}`, quantity });
  const signedLicense = (licenseId, resources, expiresAt = null) => {
    const signedBytes = Buffer.from(JSON.stringify({ licenseId, product: 'SchoolTraining', machineCode: quotaCode,
      issuedAt: new Date(Date.now() - 1000).toISOString(), expiresAt, resources }));
    return JSON.stringify({ payload: signedBytes.toString('base64'), signature: sign('RSA-SHA256', signedBytes, privateKey).toString('base64') });
  };
  const quotaRow = async (code) => (await quotaClient.request('/licenses')).quotas.find(row => row.code === `TRAINING.${code}`);
  assert.equal((await quotaRow('CABINETS')).limit, null);
  const limits = [resource('CABINETS', 1), resource('DEVICES', 2), resource('NODES', 1), resource('VMS', 1), resource('ASSIGNMENTS', 1)];
  const invalidBatch = upload('quota.lic', signedLicense('quota', limits));
  invalidBatch.append('file', new Blob(['invalid']), 'bad.lic');
  await quotaClient.request('/licenses', 'POST', invalidBatch, 400);
  assert.equal((await quotaClient.request('/licenses')).items.length, 0, 'Batch must not partially import');
  const validBatch = upload('quota.lic', signedLicense('quota', limits));
  validBatch.append('file', new Blob([signedLicense('renewal', [resource('CABINETS', 1)])]), 'renewal.lic');
  assert.equal((await quotaClient.request('/licenses', 'POST', validBatch)).importedCount, 2);
  assert.equal((await quotaRow('CABINETS')).limit, 1, 'Overlapping licenses must not double the limit');
  assert.match((await quotaClient.request('/licenses/guide')).toString('utf8'), /TRAINING\.ASSIGNMENTS/);
  const racingCabinets = await Promise.all(Array.from({length: 6}, (_, i) => quotaClient.attempt('/cabinets', 'POST', {name: `许可机柜 ${i}`, isSimulated: true})));
  assert.equal(racingCabinets.filter(row => row.status === 200).length, 1);
  assert.ok(racingCabinets.filter(row => row.status !== 200).every(row => row.status === 409 && row.result.code === 'TRAINING.CABINETS'));
  const winner = racingCabinets.find(row => row.status === 200).result;
  await quotaClient.request('/simulation/bootstrap', 'POST', undefined, 409);
  assert.equal((await quotaClient.request('/device-types')).total, 0, 'Rejected bootstrap must leave no partial resources');
  await quotaClient.request(`/cabinets/${winner.id}`, 'DELETE');
  const bootstrap = await quotaClient.request('/simulation/bootstrap', 'POST');
  assert.equal(bootstrap.skippedVirtualMachines, 1);
  assert.equal((await quotaRow('DEVICES')).used, 2);
  assert.equal((await quotaRow('VMS')).used, 1);
  const type = (await quotaClient.request('/device-types')).items[0];
  assert.equal((await quotaClient.request('/devices', 'POST', {name:'超额设备',cabinetId:'simulation-cabinet',deviceTypeId:type.id},409)).code, 'TRAINING.DEVICES');
  assert.equal((await quotaClient.request('/nodes', 'POST', {name:'超额节点'},409)).code, 'TRAINING.NODES');
  assert.equal((await quotaClient.request('/virtual-machines','POST',{name:'超额虚拟机',nodeId:'simulation-node'},409)).code, 'TRAINING.VMS');
  const studentIds = [];
  for (const n of [1, 2]) {
    await quotaClient.request('/people', 'POST', {kind:'student',name:`额度学生${n}`,userName:`quota-student-${n}`,number:`QUOTA${n}`,password});
    const candidates = await quotaClient.request(`/nodes/simulation-node/account-candidates?search=quota-student-${n}`);
    studentIds.push(candidates.items[0].userId);
  }
  const assignmentUrl = '/nodes/simulation-node/accounts';
  assert.equal((await quotaClient.request(assignmentUrl, 'POST', {userIds:studentIds}, 409)).code, 'TRAINING.ASSIGNMENTS');
  assert.equal((await quotaRow('ASSIGNMENTS')).used, 0);
  await quotaClient.request(assignmentUrl, 'POST', {userIds:[studentIds[0]]});
  await quotaClient.request(`${assignmentUrl}/${studentIds[0]}`, 'DELETE');
  await quotaClient.request(assignmentUrl, 'POST', {userIds:[studentIds[1]]});
  assert.equal((await quotaRow('ASSIGNMENTS')).used, 1);
  mark('批量许可证原子上传、五类授权额度、并发防超额、初始化与分配均计入额度');

  const expiring = new Client(await start(path.join(dir, 'quota-expiry-data')));
  await expiring.request('/setup', 'POST', {schoolName: '授权到期验收', userName: 'expiryaudit', displayName: '测试管理员', password});
  const expiresAt = new Date(Date.now() + 2800);
  await expiring.request('/licenses', 'POST', upload('expiry.lic', signedLicense('expiry', [resource('CABINETS', 1)], expiresAt.toISOString())));
  const beforeExpiry = await expiring.request('/cabinets', 'POST', {name:'已有机柜',isSimulated:true});
  await new Promise(resolve => setTimeout(resolve, Math.max(0, +expiresAt - Date.now()) + 50));
  const expired = await expiring.request('/cabinets', 'POST', {name:'到期后新增',isSimulated:true}, 409);
  assert.equal(expired.limit, 0);
  await expiring.request(`/cabinets/${beforeExpiry.id}`,'DELETE');
  await expiring.request('/licenses','POST',upload('renewed.lic',signedLicense('renewed',[resource('CABINETS',1)])));
  const expiresDb = new DatabaseSync(path.join(dir, 'quota-expiry-data', 'school.db'));
  try {
    const saved = expiresDb.prepare('SELECT PayloadJson FROM Licenses WHERE Id=?').get('renewed');
    const writePayload = expiresDb.prepare('UPDATE Licenses SET PayloadJson=? WHERE Id=?');
    writePayload.run('{corrupted', 'renewed');
    await expiring.request('/cabinets','POST',{name:'损坏许可证无法新增',isSimulated:true},409);
    writePayload.run(saved.PayloadJson, 'renewed');
  } finally { expiresDb.close(); }
  await expiring.request('/cabinets','POST',{name:'续期后新增',isSimulated:true});
  mark('许可到期或文件损坏时禁止新增、仍可释放资源及续期恢复');
  await api.request('/settings','PUT',{schoolName:'更新后的学校'});
  const cabinet=await api.request('/cabinets','POST',{name:'备份凭据验证机柜',isSimulated:true,jumpPassword:'BackupSecret2026!'});assert.equal(cabinet.hasJumpPassword,true);assert.equal(cabinet.jumpPassword,undefined);
  const counts=(await api.request('/dashboard')).counts;assert.equal(counts.students,2);assert.equal(counts.professions,1);
  const backup=await api.request('/backups','POST');const zip=await api.request('/backups/'+backup.fileName);
  const archive=path.join(dir,backup.fileName);await writeFile(archive,zip);
  const restored=path.join(dir,'restored');await mkdir(restored);
  execFileSync('powershell.exe',['-NoProfile','-NonInteractive','-Command','Expand-Archive -LiteralPath $env:SCHOOL_TEST_ARCHIVE -DestinationPath $env:SCHOOL_TEST_RESTORED'],{windowsHide:true,env:{...process.env,SCHOOL_TEST_ARCHIVE:archive,SCHOOL_TEST_RESTORED:restored},stdio:'pipe'});
  const restoredManifest=JSON.parse(await readFile(path.join(restored,'manifest.json'),'utf8'));
  const schemaVersion = (await api.request('/status')).schemaVersion;
  assert.equal(restoredManifest.schemaVersion,1);assert.equal(restoredManifest.databaseSchemaVersion,schemaVersion);
  assert.ok((await readdir(path.join(restored,'keys'))).some(f=>f.endsWith('.xml')));
  const restoredApi=new Client(await start(restored));restoredApi.cookies=new Map(api.cookies);restoredApi.csrf=api.csrf;
  assert.equal((await restoredApi.request('/auth/me')).user.userName,'testadmin');
  assert.equal((await restoredApi.request('/people?kind=student')).total,2);
  assert.equal((await restoredApi.request('/settings')).schoolName,'更新后的学校');
  const keys=await readdir(path.join(restored,'keys'));assert.ok((await readFile(path.join(restored,'keys',keys.find(f=>f.endsWith('.xml'))),'utf8')).includes('encryptedSecret'));
  runApp(['--validate-database',path.join(restored,'school.db')]);
  runApp(['--validate-backup',path.join(restored,'school.db'),path.join(restored,'keys')]);
  const wrongKeyData=path.join(dir,'wrong-key-data');const wrongKeyApi=new Client(await start(wrongKeyData));await wrongKeyApi.request('/setup','POST',{schoolName:'独立密钥验证',userName:'differentadmin',displayName:'测试管理员',password});
  let wrongKeyRejected=false;try{runApp(['--validate-backup',path.join(restored,'school.db'),path.join(wrongKeyData,'keys')])}catch(error){wrongKeyRejected=error.status===1}assert.equal(wrongKeyRejected,true);
  const wrongSchema=await readFile(path.join(restored,'school.db'));wrongSchema.writeUInt32BE(schemaVersion+1,60);const badDatabase=path.join(dir,'wrong-schema.db');await writeFile(badDatabase,wrongSchema);
  let rejected=false;try{runApp(['--validate-database',badDatabase])}catch(error){rejected=error.status===1}assert.equal(rejected,true);
  mark('SQLite在线一致性备份、密钥打包、独立恢复实例的数据与会话验证');
  mark('DPAPI密钥加密、只读数据库校验、未知结构版本拒绝');
  assert.ok((await api.request('/audits?search=提交导入')).total>=1);
  await api.request('/people/batch-delete','POST',{ids:[student.id,'missing-record']},404);
  assert.equal((await api.request('/people?kind=student')).total,2);
  mark('审计可查询、批量删除失败不部分删除');
  await api.request('/backups','POST');await api.request('/backups','POST');
  assert.equal((await api.request('/backups')).items.length,2);
  mark('备份保留数量上限生效，旧备份被有界清理');
  const lockDb=new DatabaseSync(path.join(data,'school.db'));
  try{
    lockDb.exec('PRAGMA busy_timeout=5000');lockDb.exec('BEGIN IMMEDIATE');
    const busy=await api.attempt('/professions','POST',{name:'锁等待测试',code:'LOCK-'+Date.now()});
    assert.equal(busy.status,503);assert.equal(busy.headers.get('retry-after'),'5');assert.match(busy.result.message,/数据库正忙/);
  }finally{try{lockDb.exec('ROLLBACK')}catch{}lockDb.close();}
  mark('SQLite写锁等待返回503与Retry-After，不再误报409');

  const adminA=await api.request('/people','POST',{kind:'teacher',name:'并发管理员A',userName:'admina',number:'ADM001',password,role:'Admin'});
  const adminB=await api.request('/people','POST',{kind:'teacher',name:'并发管理员B',userName:'adminb',number:'ADM002',password,role:'Admin'});
  const adminAClient=new Client(api.base),adminBClient=new Client(api.base);
  await adminAClient.request('/auth/login','POST',{userName:'admina',password});
  await adminAClient.request('/auth/password','POST',{currentPassword:password,newPassword:replacement+'A'});
  await adminBClient.request('/auth/login','POST',{userName:'adminb',password});
  await adminBClient.request('/auth/password','POST',{currentPassword:password,newPassword:replacement+'B'});
  const sqlite=new DatabaseSync(path.join(data,'school.db'));
  try{
    sqlite.exec('PRAGMA busy_timeout=5000');
    sqlite.prepare('DELETE FROM Users WHERE Id = ?').run(me.user.id);
    assert.equal(sqlite.prepare("SELECT COUNT(*) AS count FROM Users WHERE Role='Admin'").get().count,2);
    const [demoteA,demoteB]=await Promise.all([
      adminAClient.attempt(`/people/${adminA.id}`,'PUT',{kind:'teacher',name:'并发管理员A',userName:'admina',number:'ADM001',role:'Teacher'}),
      adminBClient.attempt(`/people/${adminB.id}`,'PUT',{kind:'teacher',name:'并发管理员B',userName:'adminb',number:'ADM002',role:'Teacher'})
    ]);
    assert.deepEqual([demoteA.status,demoteB.status].sort((a,b)=>a-b),[200,409]);
    assert.equal(sqlite.prepare("SELECT COUNT(*) AS count FROM Users WHERE Role='Admin'").get().count,1);
    let triggerBlocked=false;
    try{
      sqlite.exec('BEGIN IMMEDIATE');
      try{sqlite.exec("DELETE FROM Users WHERE Role='Admin'");}
      finally{try{sqlite.exec('ROLLBACK')}catch{}}
    }catch(error){triggerBlocked=String(error.message).includes('must_keep_at_least_one_admin');}
    assert.equal(triggerBlocked,true);
    assert.equal(sqlite.prepare("SELECT COUNT(*) AS count FROM Users WHERE Role='Admin'").get().count,1);
  }finally{sqlite.close();}
  mark('管理员并发降级仅成功一个，数据库触发器阻止删除最后一名管理员');

  const report={schemaVersion,testedAssembly:appDll,testedAssemblySha256:createHash('sha256').update(readFileSync(appDll)).digest('hex').toUpperCase(),passed:passed.length,checks:passed,completedAt:new Date().toISOString(),workspace:dir};
  await writeFile(path.join(root,'.local/application-test-results.json'),JSON.stringify(report,null,2));
  await mkdir(path.join(root,'docs/verification'),{recursive:true});await writeFile(path.join(root,'docs/verification/application-smoke.json'),JSON.stringify(report,null,2));
  console.log(JSON.stringify(report,null,2));
}finally{
  for(const child of processes){if(child.exitCode===null){child.kill();await Promise.race([new Promise(r=>child.once('exit',r)),new Promise(r=>setTimeout(r,1500))]);}}
}
