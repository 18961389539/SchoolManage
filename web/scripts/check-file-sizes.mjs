/**
 * Enforces the "only shrink" rule for large Vue/TS/CSS files.
 *
 * New files are capped at 800 lines. Files already above the cap are listed in
 * file-size-baseline.json with their current size; they may shrink but never grow.
 * `--update` lowers or removes entries and refuses to raise one. `--init` seeds the
 * baseline once; run it only when introducing the rule to an existing tree.
 */
import { readdir, readFile, writeFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const sourceRoot = path.join(root, 'src')
const baselinePath = path.join(root, 'file-size-baseline.json')
const cap = 800
const init = process.argv.includes('--init')
const update = process.argv.includes('--update')

async function walk(directory) {
  const files = []
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name)
    if (entry.isDirectory()) files.push(...(await walk(full)))
    else if (/\.(vue|ts|css)$/.test(entry.name) && !entry.name.endsWith('.test.ts') && entry.name !== 'components.d.ts' && entry.name !== 'api.generated.ts')
      files.push(full)
  }
  return files
}

const relative = file => path.relative(root, file).split(path.sep).join('/')

async function lineCount(file) {
  const text = await readFile(file, 'utf8')
  if (!text) return 0
  const lines = text.split(/\r?\n/).length
  return text.endsWith('\n') ? lines - 1 : lines
}

const counts = new Map()
for (const file of await walk(sourceRoot)) counts.set(relative(file), await lineCount(file))

let baseline = {}
try {
  baseline = JSON.parse(await readFile(baselinePath, 'utf8'))
} catch (error) {
  if (error.code !== 'ENOENT') throw error
}

if (init) {
  for (const [file, count] of counts) if (count > cap) baseline[file] = count
} else if (update) {
  let refused = false
  for (const [file, allowed] of Object.entries(baseline)) {
    if (!counts.has(file) || counts.get(file) <= cap) delete baseline[file]
    else if (counts.get(file) > allowed) {
      console.error(`拒绝提高基线：${file} ${allowed} -> ${counts.get(file)}`)
      refused = true
    } else baseline[file] = counts.get(file)
  }
  if (refused) process.exit(1)
}

if (init || update) {
  const sorted = Object.fromEntries(Object.entries(baseline).sort(([left], [right]) => left.localeCompare(right)))
  await writeFile(baselinePath, JSON.stringify(sorted, null, 2) + '\n', 'utf8')
  console.log(`已更新 ${path.relative(process.cwd(), baselinePath)}（${Object.keys(sorted).length} 个存量文件）`)
  process.exit(0)
}

const violations = []
for (const [file, count] of counts) {
  const allowed = baseline[file] ?? cap
  if (count > allowed) {
    violations.push(
      baseline[file]
        ? `${file}: ${count} 行，超过存量基线 ${allowed} 行（只允许缩减）`
        : `${file}: ${count} 行，超过新文件上限 ${cap} 行`
    )
  }
}
for (const file of Object.keys(baseline)) if (!counts.has(file)) violations.push(`${file}: 基线存在但文件已不存在，请运行 --update`)

if (violations.length) {
  console.error(`文件体积检查失败：\n${violations.map(item => `  - ${item}`).join('\n')}`)
  process.exit(1)
}
const tracked = Object.keys(baseline).length
console.log(`文件体积检查通过：${counts.size} 个文件，${tracked} 个存量超标文件，新文件上限 ${cap} 行`)