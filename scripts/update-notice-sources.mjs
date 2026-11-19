// Build-maintenance utility. Downloads upstream notice text into tracked sources;
// normal release builds use those local sources and do not need these requests.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const project = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const inventory = JSON.parse((await fs.readFile(path.resolve(process.argv[2]), 'utf8')).replace(/^\uFEFF/, ''));
const candidates = ['LICENSE', 'LICENSE.TXT', 'LICENSE.txt', 'LICENSE.md', 'license.txt', 'license.md', 'License.txt', 'COPYING'];
const cache = new Map();
let downloaded = 0;
for (const component of inventory.components.filter(value => value.noticeFiles.length === 0)) {
  const repository = component.repositoryUrl || component.projectUrl || '';
  const match = repository.match(/^https:\/\/github.com\/([^/]+)\/([^/#]+?)(?:\.git)?\/?$/i);
  if (!match) { console.log(`Review required: ${component.name} (upstream repository unavailable)`); continue; }
  const refs = component.repositoryCommit ? [component.repositoryCommit] : [`v${component.version}`, component.version, 'master', 'main'];
  const cacheKey = `${match[1]}/${match[2]}@${refs.join(',')}`;
  let result = cache.get(cacheKey);
  if (result === undefined) {
    result = null;
    for (const ref of refs) {
      const responses = await Promise.all(candidates.map(async file => {
        const url = `https://raw.githubusercontent.com/${match[1]}/${match[2]}/${ref}/${file}`;
        try {
          const response = await fetch(url, { signal: AbortSignal.timeout(15000) });
          if (!response.ok) return null;
          const text = await response.text();
          if (text.length < 50 || !/license|permission|redistribut/i.test(text)) return null;
          return { url, file, text };
        } catch { return null; }
      }));
      result = responses.find(Boolean) ?? null;
      if (result) break;
    }
    cache.set(cacheKey, result);
  }
  if (!result) { console.log(`Review required: ${component.name} (upstream notice not found)`); continue; }
  const target = path.join(project, 'docs', 'third-party-sources', `${component.name}-${component.version}`);
  await fs.mkdir(target, { recursive: true });
  await fs.writeFile(path.join(target, result.file), result.text, 'utf8');
  await fs.writeFile(path.join(target, 'SOURCE.json'), JSON.stringify({ component: component.name, version: component.version, source: result.url, retrievedAtUtc: new Date().toISOString() }, null, 2), 'utf8');
  downloaded++;
  console.log(`Collected: ${component.name}@${component.version}`);
}
console.log(`Collected ${downloaded} component notice sources.`);
