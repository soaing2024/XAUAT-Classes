// 生成 app.ico：圆角蓝底 + 白"课表"块（与托盘图标同一套设计）
import fs from "node:fs";
import { deflateSync } from "node:zlib";

const OUT = "app/XauatSchedule/Resources/app.ico";
const SIZES = [16, 24, 32, 48, 64, 128, 256];

const BLUE = [0x33, 0x4E, 0xAC];
const WHITE = [0xFF, 0xFF, 0xFF];

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    crc = c ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const t = Buffer.from(type, "ascii");
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(Buffer.concat([t, data])));
  return Buffer.concat([len, t, data, crc]);
}
function png(size, pixels) {
  const raw = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0;
    pixels.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw, { level: 9 })), chunk("IEND", Buffer.alloc(0)),
  ]);
}

/** 4x 超采样画一张图，再降采样成目标尺寸 */
function render(size) {
  const S = 4, W = size * S;
  const acc = new Float64Array(size * size * 4);
  const px = Buffer.alloc(size * size * 4);

  const inRounded = (x, y) => {
    const m = W * 0.055, r = W * 0.24;
    const x0 = m, y0 = m, x1 = W - m, y1 = W - m;
    const cx = Math.min(Math.max(x, x0 + r), x1 - r), cy = Math.min(Math.max(y, y0 + r), y1 - r);
    if (x < x0 || x > x1 || y < y0 || y > y1) return false;
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r || (x >= x0 + r && x <= x1 - r) || (y >= y0 + r && y <= y1 - r);
  };
  // 白色标记：顶部横条 + 左大格 + 右上、右下两小格
  const marks = [
    [0.22, 0.26, 0.78, 0.375],
    [0.22, 0.44, 0.46, 0.74],
    [0.54, 0.44, 0.78, 0.555],
    [0.54, 0.625, 0.78, 0.74],
  ];
  const inMark = (x, y) => marks.some(([a, b, c, d]) => x >= a * W && x <= c * W && y >= b * W && y <= d * W);

  for (let y = 0; y < W; y++) {
    for (let x = 0; x < W; x++) {
      const inside = inRounded(x + 0.5, y + 0.5);
      if (!inside) continue;
      const col = inMark(x + 0.5, y + 0.5) ? WHITE : BLUE;
      const i = (Math.floor(y / S) * size + Math.floor(x / S)) * 4;
      acc[i] += col[0]; acc[i + 1] += col[1]; acc[i + 2] += col[2]; acc[i + 3] += 255;
    }
  }
  const n = S * S;
  for (let i = 0; i < size * size; i++) {
    const a = acc[i * 4 + 3] / n;
    if (a <= 0.5) continue;
    // 未预乘：按覆盖率把颜色解算回来
    px[i * 4] = Math.round(acc[i * 4] / n / (a / 255));
    px[i * 4 + 1] = Math.round(acc[i * 4 + 1] / n / (a / 255));
    px[i * 4 + 2] = Math.round(acc[i * 4 + 2] / n / (a / 255));
    px[i * 4 + 3] = Math.round(a);
  }
  return png(size, px);
}

const images = SIZES.map(render);
const dir = Buffer.alloc(6);
dir.writeUInt16LE(0, 0); dir.writeUInt16LE(1, 2); dir.writeUInt16LE(SIZES.length, 4);
const entries = [];
let offset = 6 + 16 * SIZES.length;
SIZES.forEach((size, i) => {
  const e = Buffer.alloc(16);
  e[0] = size === 256 ? 0 : size;
  e[1] = size === 256 ? 0 : size;
  e[2] = 0; e[3] = 0;
  e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6);
  e.writeUInt32LE(images[i].length, 8);
  e.writeUInt32LE(offset, 12);
  offset += images[i].length;
  entries.push(e);
});

fs.mkdirSync("app/XauatSchedule/Resources", { recursive: true });
fs.writeFileSync(OUT, Buffer.concat([dir, ...entries, ...images]));
console.log(`已生成 ${OUT}：${SIZES.join("/")} 共 ${SIZES.length} 档，文件 ${(fs.statSync(OUT).size / 1024).toFixed(1)} KB`);
fs.writeFileSync("work-preview-icon.png", images[SIZES.length - 1]);
console.log("同时输出 256px 预览 work-preview-icon.png");
