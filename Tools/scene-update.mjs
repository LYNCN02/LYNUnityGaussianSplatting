/**
 * 直接 UPDATE scenes 表，回写转换结果状态。
 *
 * 用法（成功）：
 *   node Tools/scene-update.mjs --sceneId=33 --status=ready --worldJsonUrl=... --previewVideoUrl=...
 *
 * 用法（失败）：
 *   node Tools/scene-update.mjs --sceneId=33 --status=failed --error="下载失败"
 *
 * 输出（stdout）：JSON { "updated": true, "sceneId": 33, "convertStatus": "ready" }
 */
import { createRequire } from 'module';
import { readFileSync } from 'fs';
import { validate } from './contracts.mjs';

const UGC_ROOT = '/Users/jammie/Code/ugcplatform';
const require = createRequire(`${UGC_ROOT}/noop.js`);
const { neon } = require('@neondatabase/serverless');

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

function getDatabaseUrl(envPath) {
  if (process.env.DATABASE_URL) return process.env.DATABASE_URL;
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
      if (key === 'DATABASE_URL') return val;
    }
  } catch (e) { /* ignore */ }
  return null;
}

const args = parseArgs();
const envPath = `${UGC_ROOT}/.env.local`;
const databaseUrl = getDatabaseUrl(envPath);
const sceneId = parseInt(args.sceneId, 10);
const status = args.status;

if (!databaseUrl) { console.error('DATABASE_URL not found'); process.exit(1); }
if (!sceneId) { console.error('缺少 --sceneId'); process.exit(1); }
if (status !== 'ready' && status !== 'failed') { console.error('status 必须是 ready 或 failed'); process.exit(1); }

const sql = neon(databaseUrl);

try {
  if (status === 'ready') {
    const worldJsonUrl = args.worldJsonUrl;
    const previewVideoUrl = args.previewVideoUrl || null;
    if (!worldJsonUrl) { console.error('status=ready 时必须提供 --worldJsonUrl'); process.exit(1); }
    await sql`
      UPDATE scenes
      SET convert_status = 'ready',
          convert_task_id = ${'batch-' + sceneId + '-' + Date.now()},
          world_json_url = ${worldJsonUrl},
          preview_video_url = ${previewVideoUrl},
          convert_error = NULL,
          updated_at = NOW()
      WHERE id = ${sceneId}
    `;
  } else {
    const error = (args.error || '').slice(0, 1000);
    await sql`
      UPDATE scenes
      SET convert_status = 'failed',
          convert_error = ${error},
          updated_at = NOW()
      WHERE id = ${sceneId}
    `;
  }
  const output = { updated: true, sceneId, convertStatus: status };
  validate('scene-update', output);
  process.stdout.write(JSON.stringify(output));
} catch (e) {
  console.error(e.message);
  process.exit(1);
}
