/**
 * 上传录制产物到 Azure Blob 私有容器 scene-assets。
 *
 * 用法：
 *   node Tools/scene-upload.mjs --sceneId=33 --folder=/path/to/recording/folder
 *
 * 上传文件清单（相对 folder）：
 *   world_config.json  → scenes/{id}/world_config.json
 *   main.mov           → scenes/{id}/main.mov
 *   right.mov          → scenes/{id}/right.mov
 *   mesh/collision.glb → scenes/{id}/collision.glb
 *   preview.png        → scenes/{id}/preview.png
 *   preview_right.png  → scenes/{id}/preview_right.png
 *
 * 输出（stdout）：JSON { "worldJsonUrl", "previewVideoUrl", "rightVideoUrl", "collisionUrl", "previewUrl", "previewRightUrl" }
 * 每个 URL 是 Azure 稳定地址（不含 SAS）。中间 MP4 不上传（录制收尾已删除）。
 */
import { createRequire } from 'module';
import { readFileSync, readdirSync, statSync } from 'fs';
import { join, extname } from 'path';
import { validate } from './contracts.mjs';

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

function getContainerName(envPath) {
  if (process.env.AZURE_SCENE_CONTAINER) return process.env.AZURE_SCENE_CONTAINER;
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
      if (key === 'AZURE_SCENE_CONTAINER') return val;
    }
  } catch (e) { /* ignore */ }
  return 'scene-assets';
}

const CONTENT_TYPES = {
  '.json': 'application/json',
  '.mp4': 'video/mp4',
  '.mov': 'video/quicktime',
  '.glb': 'model/gltf-binary',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
};

async function uploadFile(blobClient, localPath, contentType) {
  const buffer = readFileSync(localPath);
  // 签 10 分钟写 SAS，用 fetch PUT 上传（避免 SDK uploadData 超时问题）。
  const expiresOn = new Date(Date.now() + 10 * 60 * 1000);
  const sasUrl = await blobClient.generateSasUrl({
    permissions: BlobSASPermissions.parse('cw'),
    expiresOn,
  });
  const res = await fetch(sasUrl, {
    method: 'PUT',
    headers: {
      'x-ms-blob-type': 'BlockBlob',
      'Content-Type': contentType,
      'Content-Length': buffer.length,
    },
    body: buffer,
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(`上传失败 ${localPath}: HTTP ${res.status} ${text}`);
  }
  // 返回稳定地址（不含 SAS）
  return blobClient.url;
}

const args = parseArgs();
const envPath = `${UGC_ROOT}/.env.local`;
const connectionString = getConnectionString(envPath);
const containerName = getContainerName(envPath);
const sceneId = parseInt(args.sceneId, 10);
const folder = args.folder;

if (!sceneId) { console.error('缺少 --sceneId'); process.exit(1); }
if (!folder) { console.error('缺少 --folder'); process.exit(1); }

const service = BlobServiceClient.fromConnectionString(connectionString);
const container = service.getContainerClient(containerName);

// 本地文件相对路径 → Azure blob 后缀。只传交付 MOV（不传中间 mp4）。
const uploadMap = [
  { local: 'world_config.json', blob: 'world_config.json' },
  { local: 'main.mov', blob: 'main.mov' },
  { local: 'right.mov', blob: 'right.mov' },
  { local: join('mesh', 'collision.glb'), blob: 'collision.glb' },
  { local: 'preview.png', blob: 'preview.png' },
  { local: 'preview_right.png', blob: 'preview_right.png' },
];

try {
  const result = {};
  for (const { local, blob } of uploadMap) {
    const localPath = join(folder, local);
    if (!statSync(localPath).isFile()) {
      throw new Error(`缺少录制产物: ${localPath}`);
    }
    const blobPath = `scenes/${sceneId}/${blob}`;
    const blobClient = container.getBlobClient(blobPath);
    const contentType = CONTENT_TYPES[extname(local).toLowerCase()] || 'application/octet-stream';
    const url = await uploadFile(blobClient, localPath, contentType);
    if (blob === 'world_config.json') result.worldJsonUrl = url;
    else if (blob === 'main.mov') result.previewVideoUrl = url;
    else if (blob === 'right.mov') result.rightVideoUrl = url;
    else if (blob === 'collision.glb') result.collisionUrl = url;
    else if (blob === 'preview.png') result.previewUrl = url;
    else if (blob === 'preview_right.png') result.previewRightUrl = url;
  }
  validate('scene-upload', result);
  process.stdout.write(JSON.stringify(result));
} catch (e) {
  console.error(e.message);
  process.exit(1);
}
