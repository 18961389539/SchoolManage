/**
 * Exports the OpenAPI document "contract" and checks web/src/api.generated.ts.
 * Pass --write to regenerate the TypeScript after a named response changes.
 */
import { spawnSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const repoRoot = path.resolve(webRoot, '..')
const target = path.join(webRoot, 'src', 'api.generated.ts')
const write = process.argv.includes('--write')

const directory = mkdtempSync(path.join(tmpdir(), 'school-contract-'))
const documentPath = path.join(directory, 'contract.json')
try {
  const exported = spawnSync(
    'dotnet',
    [
      'run',
      '--no-launch-profile',
      '--project',
      path.join(repoRoot, 'src', 'School.Training', 'School.Training.csproj'),
      '-c',
      'Release',
      '--',
      '--export-openapi',
      documentPath
    ],
    { cwd: repoRoot, stdio: 'inherit' }
  )
  if (exported.status !== 0) process.exit(exported.status ?? 1)
  const document = JSON.parse(readFileSync(documentPath, 'utf8'))
  const generated = render(document)
  let current = ''
  try {
    current = normalize(readFileSync(target, 'utf8'))
  } catch (error) {
    if (error.code !== 'ENOENT') throw error
  }
  if (current === generated) {
    console.log('核心契约与 api.generated.ts 一致')
    process.exit(0)
  }
  if (!write) {
    console.error('api.generated.ts 与后端 contract 文档不一致。请运行 npm run check:api -- --write 后提交生成结果。')
    process.exit(1)
  }
  writeFileSync(target, generated, 'utf8')
  console.log('已更新 src/api.generated.ts')
} finally {
  rmSync(directory, { recursive: true, force: true })
}

function normalize(text) {
  return text.replace(/\r\n/g, '\n').replace(/\s+$/u, '') + '\n'
}

function render(document) {
  const schemas = document.components?.schemas ?? {}
  const names = Object.keys(schemas).sort()
  const operations = []
  for (const [route, item] of Object.entries(document.paths ?? {})) {
    for (const method of Object.keys(item)) {
      if (method === 'parameters' || method.startsWith('x-')) continue
      operations.push(`${method.toUpperCase()} ${route}`)
    }
  }
  operations.sort()
  const lines = [
    '// Generated from the OpenAPI document "contract". Do not edit.',
    '// Regenerate with: npm run check:api -- --write',
    '',
    `export const contractOperations = ${JSON.stringify(operations, null, 2)} as const`,
    ''
  ]
  for (const name of names) lines.push(renderInterface(name, schemas[name]), '')
  return normalize(lines.join('\n'))
}

function renderInterface(name, schema) {
  const required = new Set(schema.required ?? [])
  const properties = Object.keys(schema.properties ?? {}).sort()
  const fields = properties.map(property => {
    const optional = required.has(property) ? '' : '?'
    return `  ${property}${optional}: ${schemaType(schema.properties[property])}`
  })
  return `export interface ${name} {\n${fields.join('\n')}\n}`
}

function schemaType(schema) {
  if (!schema) return 'unknown'
  if (schema.$ref) return schema.$ref.split('/').pop()
  const listed = Array.isArray(schema.type) ? schema.type : schema.type ? [schema.type] : []
  const base = primitiveType(schema, listed.filter(item => item !== 'null'))
  return listed.includes('null') ? `${base} | null` : base
}

function primitiveType(schema, concrete) {
  if (schema.format === 'date-time') return 'string'
  if (concrete.includes('integer') || schema.format === 'int32' || schema.format === 'int64') return 'number'
  if (concrete.includes('boolean')) return 'boolean'
  if (concrete.includes('number')) return 'number'
  if (concrete.includes('array')) return `${schemaType(schema.items)}[]`
  if (concrete.includes('string') || concrete.length === 0) return 'string'
  if (concrete.includes('object')) return 'Record<string, unknown>'
  return 'unknown'
}
