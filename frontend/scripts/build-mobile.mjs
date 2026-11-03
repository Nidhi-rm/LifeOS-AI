import { cp, mkdir, readdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const frontendRoot = fileURLToPath(new URL('../', import.meta.url));
const outputRoot = path.join(frontendRoot, 'mobile-web');
const apiBaseUrl = process.env.LIFEOS_API_BASE_URL?.trim().replace(/\/+$/, '');

if (!apiBaseUrl) {
  throw new Error('Set LIFEOS_API_BASE_URL to the HTTPS address of your deployed LifeOS API before building mobile assets.');
}

let parsedApiUrl;
try {
  parsedApiUrl = new URL(apiBaseUrl);
} catch {
  throw new Error('LIFEOS_API_BASE_URL must be a valid absolute URL.');
}
if (parsedApiUrl.protocol !== 'https:') {
  throw new Error('LIFEOS_API_BASE_URL must use HTTPS so sign-in tokens are protected in transit.');
}

await mkdir(outputRoot, { recursive: true });
for (const fileName of ['index.html', 'app.js', 'favicon.svg']) {
  await cp(path.join(frontendRoot, fileName), path.join(outputRoot, fileName));
}
for (const fileName of await readdir(frontendRoot)) {
  if (fileName.endsWith('.css')) {
    await cp(path.join(frontendRoot, fileName), path.join(outputRoot, fileName));
  }
}

const indexPath = path.join(outputRoot, 'index.html');
const indexHtml = await readFile(indexPath, 'utf8');
const marker = '<meta name="lifeos-api-base" content="">';
if (!indexHtml.includes(marker)) {
  throw new Error('Could not find the LifeOS API configuration marker in frontend/index.html.');
}
await writeFile(indexPath, indexHtml.replace(marker,
  `<meta name="lifeos-api-base" content="${apiBaseUrl}">`));

console.log(`Mobile web assets prepared in ${outputRoot}`);
