/**
 * 拉取待转换（待录制）场景队列，输出 JSON 供 Unity 批量录制使用。
 *
 * 用法：
 *   node Tools/scene-queue-json.mjs [--env=/path/to/.env.local] [--limit=50]
 *
 * 默认从 /Users/jammie/Code/ugcplatform/.env.local 读取 DATABASE_URL，
 * 并复用该目录下的 node_modules（dotenv / @neondatabase/serverless）。
 *
 * 输出（stdout）：JSON 对象 { "rows": [...] }，每个元素：
 *   { id, name, prompt, model, status, convertStatus, convertTaskId,
 *     operationId, providerSceneId, marbleUrl, thumbnailUrl,
 *     colliderMeshUrl, splatUrl, spzUrls, splatSemantics, panoUrl, hdrPanoUrl,
 *     worldJsonUrl, previewVideoUrl, error, convertError,
 *     createdAt, convertQueuedAt, roomType, ownerEmail, ownerUsername, ownerName }
 */
import { createRequire } from 'module';
import { readFileSync } from 'fs';
import { validate } from './contracts.mjs';

const UGC_ROOT = '/Users/jammie/Code/ugcplatform';

// 所有第三方模块都通过 ugcplatform 的 node_modules 解析。
const require = createRequire(`${UGC_ROOT}/noop.js`);
const { neon } = require('@neondatabase/serverless');

const args = process.argv.slice(2);
const envArg = args.find((a) => a.startsWith('--env='));
const envPath = envArg ? envArg.slice(6) : `${UGC_ROOT}/.env.local`;
const limitArg = args.find((a) => a.startsWith('--limit='));
const limit = Math.min(Math.max(Number(limitArg?.split('=')[1] ?? 50) || 50, 1), 200);

// 手动解析 .env.local，避免 dotenvx 往 stdout 打日志污染 JSON 输出。
let databaseUrl = process.env.DATABASE_URL;
if (!databaseUrl) {
  try {
    const envText = readFileSync(envPath, 'utf8');
    for (const line of envText.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed || trimmed.startsWith('#')) continue;
      const eq = trimmed.indexOf('=');
      if (eq < 0) continue;
      const key = trimmed.slice(0, eq).trim();
      let val = trimmed.slice(eq + 1).trim();
      if ((val.startsWith('"') && val.endsWith('"')) || (val.startsWith("'") && val.endsWith("'"))) {
        val = val.slice(1, -1);
      }
      if (key === 'DATABASE_URL') { databaseUrl = val; break; }
    }
  } catch (e) {
    console.error('Failed to read ' + envPath + ': ' + e.message);
    process.exit(1);
  }
}

if (!databaseUrl) {
  console.error('DATABASE_URL not found in ' + envPath);
  process.exit(1);
}

const sql = neon(databaseUrl);

const rows = await sql`
  SELECT s.id, s.name, s.prompt, s.model, s.status,
         s.convert_status AS "convertStatus", s.convert_task_id AS "convertTaskId",
         s.operation_id AS "operationId", s.provider_world_id AS "providerSceneId",
         s.marble_url AS "marbleUrl", s.thumbnail_url AS "thumbnailUrl",
         s.collider_mesh_url AS "colliderMeshUrl",
         s.metadata->>'splatUrl' AS "splatUrl",
         s.metadata->'spzUrls' AS "spzUrls",
         s.metadata->'splatSemantics' AS "splatSemantics",
         s.pano_url AS "panoUrl",
         s.metadata->>'hdrPanoUrl' AS "hdrPanoUrl",
         s.world_json_url AS "worldJsonUrl",
         s.preview_video_url AS "previewVideoUrl",
         s.error, s.convert_error AS "convertError",
         s.created_at AS "createdAt", s.convert_queued_at AS "convertQueuedAt",
         s.scene_type AS "roomType",
         u.email AS "ownerEmail", u.username AS "ownerUsername", u.name AS "ownerName"
  FROM scenes s
  LEFT JOIN users u ON u.clerk_id = s.owner_clerk_id
  WHERE s.operation_id IS NOT NULL
    AND s.status = 'succeeded'
    AND s.convert_status = 'pending'
  ORDER BY COALESCE(s.convert_queued_at, s.created_at) ASC NULLS LAST, s.id ASC
  LIMIT ${limit}
`;

const output = { rows };
validate('scene-queue', output);
process.stdout.write(JSON.stringify(output, null, 2));
