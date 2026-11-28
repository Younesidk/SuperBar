// Rasterizes the master app icon (src/SuperBar.App/Assets/SuperBar.svg) into SuperBar.png and SuperBar.ico.
//
//   node design/build-app-icon.mjs [--preview <dir>]
//
// The SVG uses <feGaussianBlur> and <clipPath> for its layer shadows, so it is drawn by a real browser engine
// (headless Chrome or Edge), not by an SVG library that would drop the filters. Each size is drawn from the vector
// at its exact pixel size; nothing is downscaled from a larger bitmap.
//
// Set CHROME_PATH to use a specific browser. --preview also writes every rendered size as a PNG into <dir>.

import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const assets = path.join(repo, 'src', 'SuperBar.App', 'Assets');
const svgPath = path.join(assets, 'SuperBar.svg');

const RENDER_SIZES = [16, 24, 32, 48, 64, 128, 256];
const ICO_SIZES = [16, 24, 32, 48, 64, 256];
const PNG_SIZE = 256;

function findBrowser() {
  const candidates = [
    process.env.CHROME_PATH,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  ];
  const found = candidates.find(p => p && fs.existsSync(p));
  if (!found) throw new Error('No Chrome or Edge found. Set CHROME_PATH.');
  return found;
}

/** Draws the SVG at every size in a headless browser and returns { size: { png, rgba } } as Buffers. */
function render(svg) {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'superbar-icon-'));
  try {
    const page = path.join(work, 'render.html');
    // A data: URL keeps the canvas origin-clean, so its pixels can be read back.
    const src = 'data:image/svg+xml;base64,' + Buffer.from(svg, 'utf8').toString('base64');
    fs.writeFileSync(page, `<!doctype html><meta charset="utf-8"><pre id="out"></pre><script type="module">
      const img = new Image();
      img.src = ${JSON.stringify(src)};
      await img.decode();
      const b64 = bytes => { let s = ''; for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000)); return btoa(s); };
      const out = {};
      for (const size of ${JSON.stringify(RENDER_SIZES)}) {
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = size;
        const ctx = canvas.getContext('2d');
        ctx.drawImage(img, 0, 0, size, size);
        out[size] = {
          png: canvas.toDataURL('image/png').split(',')[1],
          rgba: b64(ctx.getImageData(0, 0, size, size).data),
        };
      }
      document.getElementById('out').textContent = 'ICONDATA' + JSON.stringify(out) + 'ICONDATA';
    </script>`);

    const dom = execFileSync(findBrowser(), [
      '--headless=new', '--disable-gpu', '--no-first-run', '--force-device-scale-factor=1',
      '--user-data-dir=' + path.join(work, 'profile'),
      '--virtual-time-budget=10000', '--dump-dom', pathToFileURL(page).href,
    ], { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, stdio: ['ignore', 'pipe', 'ignore'] });

    const match = dom.match(/ICONDATA(.*?)ICONDATA/s);
    if (!match) throw new Error('The browser did not return any rendered icon data.');
    const json = match[1].replace(/&quot;/g, '"').replace(/&amp;/g, '&');
    return Object.fromEntries(Object.entries(JSON.parse(json)).map(
      ([size, v]) => [size, { png: Buffer.from(v.png, 'base64'), rgba: Buffer.from(v.rgba, 'base64') }]));
  } finally {
    fs.rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
}

/** A classic ICO frame: BITMAPINFOHEADER, bottom-up 32-bit BGRA, then the 1-bit AND mask. */
function bmpFrame(size, rgba) {
  const maskStride = Math.ceil(size / 32) * 4;
  const frame = Buffer.alloc(40 + size * size * 4 + maskStride * size);
  frame.writeUInt32LE(40, 0);
  frame.writeInt32LE(size, 4);
  frame.writeInt32LE(size * 2, 8); // XOR + AND bitmaps stacked
  frame.writeUInt16LE(1, 12);
  frame.writeUInt16LE(32, 14);
  frame.writeUInt32LE(size * size * 4, 20);
  for (let y = 0; y < size; y++) {
    const row = size - 1 - y; // bottom-up
    for (let x = 0; x < size; x++) {
      const s = (y * size + x) * 4;
      const d = 40 + (row * size + x) * 4;
      frame[d] = rgba[s + 2];
      frame[d + 1] = rgba[s + 1];
      frame[d + 2] = rgba[s];
      frame[d + 3] = rgba[s + 3];
      if (rgba[s + 3] === 0) {
        frame[40 + size * size * 4 + row * maskStride + (x >> 3)] |= 0x80 >> (x & 7);
      }
    }
  }
  return frame;
}

/** Packs the frames: BMP below 256 px, PNG at 256 px (the convention every Windows consumer reads). */
function buildIco(frames) {
  const payloads = ICO_SIZES.map(size => size >= 256 ? frames[size].png : bmpFrame(size, frames[size].rgba));
  const header = Buffer.alloc(6 + 16 * payloads.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(payloads.length, 4);
  let offset = header.length;
  payloads.forEach((payload, i) => {
    const size = ICO_SIZES[i];
    const e = 6 + 16 * i;
    header[e] = size >= 256 ? 0 : size;
    header[e + 1] = size >= 256 ? 0 : size;
    header.writeUInt16LE(1, e + 4);
    header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(payload.length, e + 8);
    header.writeUInt32LE(offset, e + 12);
    offset += payload.length;
  });
  return Buffer.concat([header, ...payloads]);
}

const frames = render(fs.readFileSync(svgPath, 'utf8'));
for (const size of RENDER_SIZES) {
  const { rgba } = frames[size];
  if (rgba.length !== size * size * 4) throw new Error(`Bad pixel data at ${size} px.`);
  let opaque = 0;
  for (let i = 3; i < rgba.length; i += 4) if (rgba[i] > 0) opaque++;
  if (opaque < size * size * 0.4) throw new Error(`The ${size} px render is blank or mostly empty.`);
}

fs.writeFileSync(path.join(assets, 'SuperBar.png'), frames[PNG_SIZE].png);
fs.writeFileSync(path.join(assets, 'SuperBar.ico'), buildIco(frames));
console.log(`SuperBar.png  ${PNG_SIZE} px`);
console.log(`SuperBar.ico  ${ICO_SIZES.join(', ')} px`);

const previewAt = process.argv.indexOf('--preview');
if (previewAt > 0 && process.argv[previewAt + 1]) {
  const dir = path.resolve(process.argv[previewAt + 1]);
  fs.mkdirSync(dir, { recursive: true });
  for (const size of RENDER_SIZES) fs.writeFileSync(path.join(dir, `SuperBar-${size}.png`), frames[size].png);
  console.log(`previews      ${dir}`);
}
