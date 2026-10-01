/**
 * C# ⇄ Node 契约校验入口（唯一）。
 *
 * 所有 Tools/*.mjs 脚本在 stdout 输出 JSON 前，都应调用本模块的 validate，
 * 保证输出结构与 C# 端 JsonUtility 模型（LYNOOKWorldStudioService 下的
 * SceneQueueResult / SceneDownloadResult / SceneUploadResult / SceneUpdateResult）
 * 以及 Tools/schemas/*.schema.json 三者一致。
 *
 * AJV 复用 ugcplatform 的 node_modules（与其它脚本相同的解析口径）。
 *
 * 用法（库）：
 *   import { validate } from '../contracts.mjs';
 *   validate('scene-download', result);   // 不合法时 throw Error
 *
 * 用法（自检 CLI）：
 *   node Tools/contracts.mjs --selftest
 */
import { createRequire } from 'module';
import { readFileSync } from 'fs';
import { dirname, join } from 'path';
import { fileURLToPath } from 'url';

const UGC_ROOT = '/Users/jammie/Code/ugcplatform';
const require = createRequire(`${UGC_ROOT}/noop.js`);
const Ajv = require('ajv');

const here = dirname(fileURLToPath(import.meta.url));
const schemaDir = join(here, 'schemas');

// 契约名 → schema 文件。名称与 C# 端 LYNOOKWorldStudioService 嵌套结果类对应。
const CONTRACT_FILES = {
  'scene-queue': 'scene-queue.schema.json',
  'scene-download': 'scene-download.schema.json',
  'scene-upload': 'scene-upload.schema.json',
  'scene-update': 'scene-update.schema.json',
};

// AJV 6（draft-07）；编译结果缓存，进程内只解析一次 schema。
const ajv = new Ajv({ allErrors: true, nullable: true });
const compilers = new Map();

function compiler(name) {
  if (compilers.has(name)) return compilers.get(name);
  const file = CONTRACT_FILES[name];
  if (!file) throw new Error(`未知契约：${name}（可用：${Object.keys(CONTRACT_FILES).join(', ')}）`);
  const schema = JSON.parse(readFileSync(join(schemaDir, file), 'utf8'));
  const fn = ajv.compile(schema);
  compilers.set(name, fn);
  return fn;
}

/**
 * 按命名契约校验 data。合法返回 true；不合法时抛出带完整错误清单的 Error，
 * 错误信息同时适合写入 stderr 与 C# 端 FailCurrent。
 */
export function validate(name, data) {
  const fn = compiler(name);
  if (fn(data)) return true;
  const details = (fn.errors || [])
    .map((e) => `${e.dataPath || '<root>'} ${e.message}`.trim())
    .join('; ');
  throw new Error(`契约 ${name} 校验失败：${details}`);
}

/** 仅校验不抛错，返回 { valid, errors }，供非致命场景使用。 */
export function check(name, data) {
  try {
    validate(name, data);
    return { valid: true, errors: null };
  } catch (e) {
    return { valid: false, errors: e.message };
  }
}

// ── 自检：用最小正/反例验证四个 schema 都能正常工作 ──
function selftest() {
  const cases = [
    ['scene-queue', { rows: [{ id: 1, roomType: 'kitchen' }] }, true],
    ['scene-queue', { rows: [{ id: 1, roomType: 'spaceship' }] }, false],
    ['scene-queue', { rows: [{ roomType: 'bedroom' }] }, false],
    ['scene-download', { splat: '/a.spz', collider: '/b.glb', splatBytes: 1, colliderBytes: 2 }, true],
    ['scene-download', { splat: '/a.spz' }, false],
    ['scene-upload', {
      worldJsonUrl: 'https://x/a.json', previewVideoUrl: 'https://x/a.mp4',
      rightVideoUrl: 'https://x/b.mp4', collisionUrl: 'https://x/c.glb', previewUrl: 'https://x/d.png',
    }, true],
    ['scene-upload', { worldJsonUrl: 'https://x/a.json' }, false],
    ['scene-update', { updated: true, sceneId: 3, convertStatus: 'ready' }, true],
    ['scene-update', { updated: false, sceneId: 3, convertStatus: 'ready' }, false],
  ];
  let failed = 0;
  for (const [name, data, expectValid] of cases) {
    const { valid } = check(name, data);
    const ok = valid === expectValid;
    if (!ok) failed++;
    console.log(`${ok ? 'PASS' : 'FAIL'} ${name} expect=${expectValid} actual=${valid}`);
  }
  if (failed > 0) { console.error(`契约自检失败 ${failed} 例`); process.exit(1); }
  console.log('LYNOOK_CONTRACT_SELFTEST_PASSED: 4 schemas, ' + cases.length + ' cases.');
}

if (process.argv.includes('--selftest')) selftest();
