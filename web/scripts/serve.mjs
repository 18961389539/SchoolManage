import { createReadStream, existsSync, statSync } from 'node:fs'
import { createServer } from 'node:http'
import { basename, extname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const dist = fileURLToPath(new URL('../dist/', import.meta.url))
const indexFile = join(dist, 'index.html')
const assetsDir = join(dist, 'assets')
const port = Number(process.env.PORT) || 8080
const assetName = /^[A-Za-z0-9._-]+$/

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
  if (url.pathname.startsWith('/assets/')) {
    const name = basename(url.pathname)
    const requested = url.pathname.slice('/assets/'.length)
    const file = join(assetsDir, name)
    if (name !== requested || !assetName.test(name) || !existsSync(file) || !statSync(file).isFile()) {
      res.writeHead(404)
      res.end()
      return
    }
    send(res, file)
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
