import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const assets = new URL('../src/CameraLightTray.App/Assets/', import.meta.url);
for (const name of ['KeyLight', 'KeyLightAir', 'UnknownLight']) {
  const source = await readFile(new URL(`${name}.svg`, assets), 'utf8');
  if ((source.match(/id="panel"/g) || []).length !== 1) throw new Error(`Missing unique light panel in ${name}.`);
  for (const [state, color] of [['on', '#fff0cb'], ['off', '#596773'], ['unknown', '#818c95']]) {
    let svg = source.replace('#596773', color);
    if (state === 'unknown') svg = svg.replace('</svg>', '<circle cx="126" cy="128" r="16" fill="#6a7782" stroke="#d6e0e7" stroke-width="2"/><text x="126" y="136" text-anchor="middle" fill="#fff" font-size="23" font-family="Segoe UI, sans-serif" font-weight="600">?</text></svg>');
    await writeFile(fileURLToPath(new URL(`${name}.${state}.svg`, assets)), svg);
  }
}
