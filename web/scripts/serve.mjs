import { createReadStream, existsSync, statSync } from 'node:fs'
import { createServer } from 'node:http'
import { extname, join, normalize, sep } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../dist/', import.meta.url))
const indexFile = join(root, 'index.html')
const port = Number(process.env.PORT) || 8080

const types = {
  '.css': 'text/css; charset=utf-8',
  '.html': 'text/html; charset=utf-8',
  '.ico': 'image/x-icon',
  '.jpeg': 'image/jpeg',
  '.jpg': 'image/jpeg',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json',
  '.map': 'application/json',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.txt': 'text/plain; charset=utf-8',
  '.webp': 'image/webp',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2'
}

if (!existsSync(indexFile)) {
  console.error('dist/index.html 不存在。请先在 web 目录执行 npm run build。')
  process.exit(1)
}

function insideRoot(file) {
  return file === root || file.startsWith(root.endsWith(sep) ? root : root + sep)
}

function resolveFile(pathname) {
  let decoded = '/'
  try {
    decoded = decodeURIComponent(pathname)
  } catch {
    return null
  }
  if (decoded.includes('\0')) return null
  const relative = normalize(decoded).replace(/^(\.\.(\/|\\|$))+/, '').replace(/^[/\\]+/, '')
  const file = join(root, relative)
  return insideRoot(file) ? file : null
}

function send(res, file) {
  const stream = createReadStream(file)
  stream.on('error', () => {
    if (!res.headersSent) res.writeHead(404)
    res.end()
  })
  res.writeHead(200, {
    'content-type': types[extname(file)] || 'application/octet-stream',
    'cache-control': extname(file) === '.html' ? 'no-cache' : 'public, max-age=31536000, immutable'
  })
  stream.pipe(res)
}

createServer((req, res) => {
  const url = new URL(req.url ?? '/', 'http://127.0.0.1')
  if (url.pathname === '/api' || url.pathname.startsWith('/api/')) {
    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' })
    res.end('not found')
    return
  }
  const file = resolveFile(url.pathname)
  if (!file) {
    res.writeHead(400)
    res.end()
    return
  }
  const candidate = existsSync(file) && statSync(file).isDirectory() ? join(file, 'index.html') : file
  if (existsSync(candidate) && statSync(candidate).isFile()) {
    send(res, candidate)
    return
  }
  if (extname(url.pathname)) {
    res.writeHead(404)
    res.end()
    return
  }
  send(res, indexFile)
}).listen(port, '0.0.0.0', () => {
  console.log(`listening on 0.0.0.0:${port}`)
})
