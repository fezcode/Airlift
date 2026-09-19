import { readFile, writeFile, mkdir, realpath } from 'node:fs/promises';
import path from 'node:path';

const pngSignature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
export function nativePng(bytes) {
  if (bytes.subarray(0, 8).equals(pngSignature)) return bytes;
  if (bytes.length < 6 || bytes.readUInt16LE(0) !== 0 || bytes.readUInt16LE(2) !== 1)
    throw new Error('Expected a PNG or Windows ICO icon.');
  const count = bytes.readUInt16LE(4);
  if (!count || 6 + count * 16 > bytes.length) throw new Error('Invalid ICO directory.');
  const frames = [];
  for (let index = 0; index < count; index++) {
    const entry = 6 + index * 16;
    const width = bytes[entry] || 256, height = bytes[entry + 1] || 256;
    const length = bytes.readUInt32LE(entry + 8), offset = bytes.readUInt32LE(entry + 12);
    if (offset < 6 + count * 16 || !length || offset + length > bytes.length) throw new Error('Invalid ICO frame bounds.');
    const data = bytes.subarray(offset, offset + length);
    if (data.subarray(0, 8).equals(pngSignature)) frames.push({ width, height, data });
  }
  frames.sort((a, b) => b.width * b.height - a.width * a.height);
  if (!frames.length) throw new Error('ICO has no PNG frame. Export a PNG-backed ICO or PNG from the source app.');
  return frames[0].data;
}

export async function prepareIcon(root, projectDirectory, id, iconPath) {
  if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(id) || !iconPath) throw new Error(`Missing/invalid icon identity: ${id}`);
  const directory = await realpath(projectDirectory), source = await realpath(path.resolve(directory, iconPath));
  const relative = path.relative(directory, source);
  if (relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative)) throw new Error(`Icon outside project: ${id}`);
  const extension = path.extname(source).toLowerCase();
  if (!['.ico', '.png'].includes(extension)) throw new Error(`Unsupported icon format: ${source}`);
  const bytes = await readFile(source);
  const png = nativePng(bytes);
  const icon = `/icons/${id}${extension}`;
  return { id, source, icon, bytes, png,
    webPath: path.join(root, 'public', icon), nativePath: path.join(root, 'native/Airlift.Desktop/Assets/Icons', `${id}.png`) };
}
export async function writeIcon(icon) {
  await mkdir(path.dirname(icon.webPath), { recursive: true });
  await mkdir(path.dirname(icon.nativePath), { recursive: true });
  await writeFile(icon.webPath, icon.bytes);
  await writeFile(icon.nativePath, icon.png);
}
