/**
 * 下载场景输入资产（SPZ/PLY + GLB）到本地目录。
 *
 * 用法：
 *   node Tools/scene-download.mjs --sceneId=33 --splatUrl=... --colliderUrl=... --outputDir=/path/to/dir [--panoUrl=...]
 *
 * Azure 私有容器 URL 用 AZURE_STORAGE_CONNECTION_STRING 直连下载；
 * 其他公共 CDN URL 用 fetch 下载。
 *
 * 输出（stdout）：JSON { "splat": "/path/room.spz", "collider": "/path/collision.glb", "pano": "/path/pano" | null }
 */
import { createRequire } from 'module';
import { readFileSync, mkdirSync, writeFileSync } from 'fs';
import { join } from 'path';

const UGC_ROOT = '/Users/jammie/Code/ugcplatform';
const require = createRequire(`${UGC_ROOT}/noop.js`);
const { BlobServiceClient, BlobSASPermissions } = require('@azure/storage-blob');

function parseArgs() {
  const args = process.argv.slice(2);
  const result = {};
  for (const arg of args) {
    if (!arg.startsWith('--')) continue;
    const eq = arg.indexOf('=');
    if (eq < 0) continue;
    result[arg.slice(2, eq)] = arg.slice(eq + 1);
  }
  return result;
}

function getConnectionString(envPath) {
  // 优先环境变量，其次从 .env.local 解析 AZURE_STORAGE_CONNECTION_STRING
  if (process.env.AZURE_STORAGE_CONNECTION_STRING) return process.env.AZURE_STORAGE_CONNECTION_STRING;
  try {
    const text = readFileSync(envPath, 'utf8');
    for (const line of text.split('\n')) {
      const t = line.trim();
      if (!t || t.startsWith('#')) continue;
      const eq = t.indexOf('=');
      if (eq < 0) continue;
      const key = t.slice(0, eq).trim();
      let val = t.slice(eq + 1).trim();
      if ((val.startsWith('"') && val.endsWith('"')) || (val.startsWith("'") && val.endsWith("'"))) val = val.slice(1, -1);
      if (key === 'AZURE_STORAGE_CONNECTION_STRING') return val;
    }
  } catch (e) { /* ignore */ }
  return null;
}

function parseAzureUrl(url) {
  // https://<account>.blob.core.windows.net/<container>/<blob...>
  try {
    const u = new URL(url);
    if (!u.hostname.endsWith('.blob.core.windows.net')) return null;
    const parts = u.pathname.split('/').filter(Boolean);
    if (parts.length < 2) return null;
    return { container: parts[0], blobPath: parts.slice(1).join('/') };
  } catch { return null; }
}

async function downloadToFile(url, destPath, connectionString) {
  let downloadUrl = url;
  const azure = parseAzureUrl(url);
  if (azure && connectionString) {
    // 私有 Azure blob：签 5 分钟只读 SAS，再用 fetch 下载（避免 SDK downloadToBuffer 超时问题）。
    const service = BlobServiceClient.fromConnectionString(connectionString);
    const container = service.getContainerClient(azure.container);
    const blob = container.getBlobClient(azure.blobPath);
    const expiresOn = new Date(Date.now() + 5 * 60 * 1000);
    downloadUrl = await blob.generateSasUrl({
      permissions: BlobSASPermissions.parse('r'),
      expiresOn,
    });
  }
  const res = await fetch(downloadUrl);
  if (!res.ok) throw new Error(`下载失败 ${url}: HTTP ${res.status}`);
  const buffer = Buffer.from(await res.arrayBuffer());
  writeFileSync(destPath, buffer);
  return buffer.length;
}

const args = parseArgs();
const envPath = `${UGC_ROOT}/.env.local`;
const connectionString = getConnectionString(envPath);
const outputDir = args.outputDir;
if (!outputDir) { console.error('缺少 --outputDir'); process.exit(1); }

mkdirSync(outputDir, { recursive: true });

try {
  const result = {};
  if (args.splatUrl) {
    const dest = join(outputDir, 'room.spz');
    const size = await downloadToFile(args.splatUrl, dest, connectionString);
    result.splat = dest;
    result.splatBytes = size;
  }
  if (args.colliderUrl) {
    const dest = join(outputDir, 'collision.glb');
    const size = await downloadToFile(args.colliderUrl, dest, connectionString);
    result.collider = dest;
    result.colliderBytes = size;
  }
  if (args.panoUrl) {
    const dest = join(outputDir, 'pano');
    const size = await downloadToFile(args.panoUrl, dest, connectionString);
    result.pano = dest;
    result.panoBytes = size;
  }
  process.stdout.write(JSON.stringify(result));
} catch (e) {
  console.error(e.message);
  process.exit(1);
}
