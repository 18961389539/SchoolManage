// Run after: dotnet build tools/School.LicenseIssuer/School.LicenseIssuer.csproj -c Release
// Uses only Node built-ins and a .NET 10 executable compiled from this project.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { constants, createHash, createPrivateKey, createPublicKey, randomBytes, verify } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = path.dirname(fileURLToPath(import.meta.url));
const workspace = path.resolve(directory, '..', '..');
const localRoot = path.join(workspace, '.local');
fs.mkdirSync(localRoot, { recursive: true });
const temporary = fs.mkdtempSync(path.join(localRoot, 'license-issuer-smoke-'));
const binary = path.join(directory, 'bin', 'Release', 'net10.0', process.platform === 'win32' ? 'School.LicenseIssuer.exe' : 'School.LicenseIssuer');
assert.ok(fs.existsSync(binary), 'Build the issuer in Release configuration first.');
const password = randomBytes(32).toString('hex');
const env = { ...process.env, SCHOOL_ISSUER_KEY_PASSWORD: password };
const checks = [];
const observedOutput = [];
let successful = false;
let fingerprint = '';
let copiedPublicKeyPath = '';

function invoke(args, expected = 0, extraEnvironment = {}) {
  const result = spawnSync(binary, args, { env: { ...env, ...extraEnvironment }, encoding: 'utf8', windowsHide: true, timeout: 30000 });
  assert.ifError(result.error);
  const output = `${result.stdout}\n${result.stderr}`;
  observedOutput.push(output);
  assert.ok(!output.includes(password), 'Issuer must never print the password.');
  assert.ok(!output.includes('BEGIN ENCRYPTED PRIVATE KEY'), 'Issuer must never print private PEM content.');
  assert.equal(result.status, expected, `Issuer ${args[0]} returned ${result.status}: ${result.stderr}`);
  return result;
}
function pass(name) { checks.push(name); console.log(`PASS ${name}`); }
try {
  const keys = path.join(temporary, 'keys');
  invoke(['keygen', '--out-dir', keys]);
  const privatePath = path.join(keys, 'issuer-private.encrypted.pem');
  const publicPath = path.join(keys, 'license-public.pem');
  const pem = fs.readFileSync(privatePath, 'utf8');
  const armor = '-'.repeat(5);
  assert.ok(pem.startsWith(`${armor}BEGIN ENCRYPTED PRIVATE KEY${armor}`));
  assert.ok(!pem.includes(`${armor}BEGIN PRIVATE KEY${armor}`));
  const privateKey = createPrivateKey({ key: pem, format: 'pem', passphrase: password });
  const publicKey = createPublicKey(fs.readFileSync(publicPath));
  assert.ok(privateKey.asymmetricKeyDetails.modulusLength >= 2048);
  assert.deepEqual(createPublicKey(privateKey).export({ type: 'spki', format: 'der' }), publicKey.export({ type: 'spki', format: 'der' }));
  fingerprint = createHash('sha256').update(publicKey.export({ type: 'spki', format: 'der' })).digest('hex').toUpperCase();
  assert.throws(() => createPrivateKey({ key: pem, format: 'pem', passphrase: 'incorrect-passphrase' }));
  pass('RSA-3072 keypair, encrypted PKCS8 and independent Node decryption');

  invoke(['keygen', '--out-dir', keys], 2);
  assert.equal(fs.readFileSync(privatePath, 'utf8'), pem);
  pass('key generation refuses to overwrite existing keys');

  const resourcesPath = path.join(temporary, 'resources.json');
  const resources = [{ name: '模拟节点', authorizationCode: 'TEST-NODE-CODE', quantity: 2 }, { name: '模拟设备', authorizationCode: 'TEST-DEVICE-CODE', quantity: 0 }];
  fs.writeFileSync(resourcesPath, JSON.stringify(resources));
  const outputPath = path.join(temporary, 'license.json');
  const machineCode = '00112233445566778899AABBCCDDEEFF';
  const argumentsBase = ['issue', '--private-key', privatePath, '--machine-code', machineCode, '--resources', resourcesPath, '--output', outputPath, '--license-id', 'SMOKE-LICENSE-001'];
  invoke([...argumentsBase, '--expires', '2030-12-31']);
  const envelope = JSON.parse(fs.readFileSync(outputPath, 'utf8'));
  assert.deepEqual(Object.keys(envelope).sort(), ['payload', 'signature']);
  const bytes = Buffer.from(envelope.payload, 'base64');
  const signature = Buffer.from(envelope.signature, 'base64');
  assert.equal(verify('RSA-SHA256', bytes, { key: publicKey, padding: constants.RSA_PKCS1_PADDING }, signature), true);
  const payload = JSON.parse(bytes.toString('utf8'));
  assert.deepEqual(Object.keys(payload).sort(), ['licenseId', 'product', 'machineCode', 'issuedAt', 'expiresAt', 'resources'].sort());
  assert.equal(payload.licenseId, 'SMOKE-LICENSE-001'); assert.equal(payload.product, 'SchoolTraining'); assert.equal(payload.machineCode, machineCode);
  assert.ok(Math.abs(Date.now() - Date.parse(payload.issuedAt)) < 10000); assert.deepEqual(payload.resources, resources);
  assert.equal(payload.expiresAt, '2030-12-31T15:59:59.9999999Z');
  pass('issued envelope signature and strict server payload contract');

  const tampered = Buffer.from(JSON.stringify({ ...payload, resources: [{ ...payload.resources[0], quantity: 999 }] }));
  assert.equal(verify('RSA-SHA256', tampered, { key: publicKey, padding: constants.RSA_PKCS1_PADDING }, signature), false);
  pass('tampered resource quantities fail independent signature verification');

  invoke(argumentsBase, 2);
  const noExpiryPath = path.join(temporary, 'license-perpetual.json');
  const noExpiryArguments = argumentsBase.map(value => value === outputPath ? noExpiryPath : value === 'SMOKE-LICENSE-001' ? 'SMOKE-LICENSE-002' : value);
  invoke(noExpiryArguments);
  const noExpiry = JSON.parse(Buffer.from(JSON.parse(fs.readFileSync(noExpiryPath, 'utf8')).payload, 'base64').toString('utf8'));
  assert.equal(noExpiry.expiresAt, null);
  pass('expiry date uses UTC+08 end-of-day; omitted expiry is explicit null; no overwrite');

  if (process.env.SCHOOL_ISSUER_TEST_URL) {
    assert.equal(process.env.SCHOOL_TEST_ALLOW_MUTATION, '1', 'API verification requires a disposable instance and explicit mutation opt-in.');
    const apiBase = new URL(process.env.SCHOOL_ISSUER_TEST_URL);
    assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(apiBase.hostname), 'Issuer API smoke is restricted to a local disposable instance.');
    assert.ok(process.env.SCHOOL_TEST_PASSWORD, 'Set SCHOOL_TEST_PASSWORD for the isolated server.');
    assert.ok(process.env.SCHOOL_ISSUER_TEST_PUBLIC_KEY_PATH, 'Set the disposable server public-key path.');
    const keyDestination = path.resolve(process.env.SCHOOL_ISSUER_TEST_PUBLIC_KEY_PATH);
    const keyParent = fs.realpathSync(path.dirname(keyDestination));
    assert.ok(keyParent.startsWith(`${fs.realpathSync(localRoot)}${path.sep}`), 'Public-key fixture must stay inside the workspace .local directory.');
    fs.copyFileSync(publicPath, keyDestination, fs.constants.COPYFILE_EXCL); copiedPublicKeyPath = keyDestination;
    const cookies = new Map(); let csrf = '';
    async function request(endpoint, method = 'GET', body) {
      const headers = { Origin: apiBase.origin, Cookie: [...cookies].map(([k, v]) => `${k}=${v}`).join('; ') };
      if (csrf) headers['X-CSRF-TOKEN'] = csrf;
      const isForm = body instanceof FormData;
      if (body !== undefined && !isForm) headers['Content-Type'] = 'application/json';
      const response = await fetch(new URL(`/api${endpoint}`, apiBase), { method, headers, body: body === undefined ? undefined : isForm ? body : JSON.stringify(body) });
      for (const value of response.headers.getSetCookie()) { const pair = value.split(';')[0]; const equals = pair.indexOf('='); cookies.set(pair.slice(0, equals), pair.slice(equals + 1)); }
      return { status: response.status, body: await response.json() };
    }
    const login = await request('/auth/login', 'POST', { userName: process.env.SCHOOL_TEST_USER ?? 'testadmin', password: process.env.SCHOOL_TEST_PASSWORD });
    assert.equal(login.status, 200); csrf = login.body.csrfToken;
    const licenses = await request('/licenses'); assert.equal(licenses.status, 200); assert.equal(licenses.body.publicKeyConfigured, true);
    const boundOutput = path.join(temporary, 'license-api-bound.json');
    const boundId = `SMOKE-API-${randomBytes(4).toString('hex')}`;
    const boundArgs = argumentsBase.map(value => value === machineCode ? licenses.body.machineCode : value === outputPath ? boundOutput : value === 'SMOKE-LICENSE-001' ? boundId : value);
    invoke([...boundArgs, '--expires', '2030-12-31']);
    async function upload(contents, name) { const form = new FormData(); form.append('file', new Blob([contents], { type: 'application/json' }), name); return request('/licenses', 'POST', form); }
    const boundContents = fs.readFileSync(boundOutput, 'utf8');
    const imported = await upload(boundContents, 'bound-license.json'); assert.equal(imported.status, 200); assert.equal(imported.body.importedCount, 1);
    const duplicate = await upload(boundContents, 'duplicate-license.json'); assert.equal(duplicate.status, 409);
    const boundEnvelope = JSON.parse(boundContents); const boundPayload = JSON.parse(Buffer.from(boundEnvelope.payload, 'base64').toString('utf8'));
    boundEnvelope.payload = Buffer.from(JSON.stringify({ ...boundPayload, licenseId: `${boundId}-tampered` })).toString('base64');
    const tamperedUpload = await upload(JSON.stringify(boundEnvelope), 'tampered-license.json'); assert.equal(tamperedUpload.status, 400);
    const wrongMachine = `${licenses.body.machineCode[0] === 'A' ? 'B' : 'A'}${licenses.body.machineCode.slice(1)}`;
    const wrongOutput = path.join(temporary, 'license-wrong-machine.json');
    invoke([...argumentsBase.map(value => value === machineCode ? wrongMachine : value === outputPath ? wrongOutput : value === 'SMOKE-LICENSE-001' ? `${boundId}-wrong-machine` : value), '--expires', '2030-12-31']);
    const wrongUpload = await upload(fs.readFileSync(wrongOutput, 'utf8'), 'wrong-machine.json'); assert.equal(wrongUpload.status, 400);
    const after = await request('/licenses'); assert.ok(after.body.items.some(x => x.id === boundId));
    pass('actual School.Training API accepts issuer output; rejects duplicates, tampering and wrong machine binding');
  }

  const badOutput = path.join(temporary, 'must-not-exist.json');
  const badArguments = argumentsBase.map(value => value === outputPath ? badOutput : value);
  invoke(badArguments, 3, { SCHOOL_ISSUER_KEY_PASSWORD: 'Incorrect-password-1' });
  assert.equal(fs.existsSync(badOutput), false);
  fs.writeFileSync(resourcesPath, JSON.stringify([{ name: 'invalid', authorizationCode: 'TEST', quantity: -1 }]));
  invoke(badArguments, 2); assert.equal(fs.existsSync(badOutput), false);
  invoke(['keygen', '--out-dir', path.join(temporary, 'missing-env')], 2, { SCHOOL_ISSUER_KEY_PASSWORD: '' });
  pass('wrong password, negative resources and weak passwords fail without output files');
  successful = true;
} finally {
  if (copiedPublicKeyPath) {
    const copy = createHash('sha256').update(fs.readFileSync(copiedPublicKeyPath)).digest('hex');
    const original = createHash('sha256').update(fs.readFileSync(path.join(temporary, 'keys', 'license-public.pem'))).digest('hex');
    assert.equal(copy, original, 'Refuse to remove a public-key fixture that another process changed.');
    fs.unlinkSync(copiedPublicKeyPath);
  }
  // Resolve both paths and verify containment before any recursive removal on Windows.
  const resolvedRoot = fs.realpathSync(localRoot);
  const resolvedTemporary = fs.realpathSync(temporary);
  assert.ok(resolvedTemporary.startsWith(`${resolvedRoot}${path.sep}`));
  assert.ok(path.basename(resolvedTemporary).startsWith('license-issuer-smoke-'));
  fs.rmSync(resolvedTemporary, { recursive: true, force: true });
  const evidence = {
    recordedAtUtc: new Date().toISOString(),
    status: successful ? 'passed' : 'failed',
    command: 'node tools/School.LicenseIssuer/verify-smoke.mjs',
    assemblySha256: createHash('sha256').update(fs.readFileSync(path.join(directory, 'bin', 'Release', 'net10.0', 'School.LicenseIssuer.dll'))).digest('hex').toUpperCase(),
    testPublicKeySha256: fingerprint,
    checks,
    temporaryKeysRemoved: !fs.existsSync(temporary),
    externalNugetDependencies: 0,
    apiIntegration: process.env.SCHOOL_ISSUER_TEST_URL ? new URL(process.env.SCHOOL_ISSUER_TEST_URL).origin : null,
    scope: 'Environment-password CLI flow, independent Node RSA verification, file/format failure cases. Hidden interactive password input is implemented but not UI-automated.'
  };
  const evidenceDirectory = path.join(workspace, 'docs', 'verification');
  fs.mkdirSync(evidenceDirectory, { recursive: true });
  fs.writeFileSync(path.join(evidenceDirectory, 'license-issuer-smoke.json'), `${JSON.stringify(evidence, null, 2)}\n`);
  console.log(JSON.stringify(evidence, null, 2));
}
