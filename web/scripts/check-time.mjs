import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

if (!process.argv.includes('--child')) {
  for (const timeZone of ['UTC', 'Asia/Shanghai', 'America/Los_Angeles']) {
    const result = spawnSync(process.execPath, [fileURLToPath(import.meta.url), '--child'], {
      env: { ...process.env, TZ: timeZone }, encoding: 'utf8'
    })
    assert.equal(result.status, 0, `${timeZone}: ${result.stderr || result.stdout}`)
    process.stdout.write(`${timeZone}: ${result.stdout.trim()}\n`)
  }
} else {
  const source = readFileSync(new URL('../src/time.ts', import.meta.url), 'utf8')
  const result = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext } })
  const { dateTime, shanghaiInputTime, shanghaiInputToIso } = await import(`data:text/javascript;base64,${Buffer.from(result.outputText).toString('base64')}`)
  const utc = '2026-11-19T04:21:00Z'
  assert.match(dateTime(utc), /12:21:00/)
  assert.equal(dateTime('2026-11-19T04:21:00'), dateTime(utc))
  assert.equal(dateTime('2026-11-19 04:21:00'), dateTime(utc))
  assert.equal(dateTime('2026-11-19T12:21:00+08:00'), dateTime(utc))
  assert.equal(shanghaiInputTime(utc), '2026-11-19T12:21:00')
  assert.equal(shanghaiInputTime('2026-11-19T04:21:00'), '2026-11-19T12:21:00')
  assert.equal(shanghaiInputToIso('2026-11-19T12:21'), '2026-11-19T04:21:00.000Z')
  assert.equal(shanghaiInputToIso('2026-11-19T12:21:00'), '2026-11-19T04:21:00.000Z')
  assert.equal(shanghaiInputToIso('invalid'), null)
  assert.equal(dateTime(null), '—')
  console.log(`北京时间转换通过 (${dateTime(utc)})`)
}
