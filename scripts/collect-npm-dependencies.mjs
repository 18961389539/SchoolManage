import fs from 'node:fs';
import path from 'node:path';

const webRoot = path.resolve(process.argv[2]);
const lock = JSON.parse(fs.readFileSync(path.join(webRoot, 'package-lock.json'), 'utf8'));
const components = [];
for (const [relative, entry] of Object.entries(lock.packages ?? {})) {
  if (!relative || entry.dev) continue;
  const folder = path.resolve(webRoot, relative);
  if (!folder.startsWith(webRoot + path.sep)) throw new Error('Invalid package location');
  const metadata = JSON.parse(fs.readFileSync(path.join(folder, 'package.json'), 'utf8'));
  components.push({ folder, name: metadata.name ?? '', version: metadata.version ?? '', license: typeof metadata.license === 'string' ? metadata.license : JSON.stringify(metadata.license ?? ''), homepage: metadata.homepage ?? '' });
}
process.stdout.write(JSON.stringify(components));
