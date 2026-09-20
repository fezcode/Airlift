import { readFileSync, writeFileSync, mkdirSync, copyFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { parse, stringify } from 'smol-toml';

// Exercise the shipping manifest, changing only identity and destinations so a
// fixture can never register, stop, overwrite or uninstall the user's Airlift.
const workspace = resolve(import.meta.dirname, '..');
const root = join(workspace, 'artifacts/forge-fixture');
const version = process.argv[2];
if (!['1.0.0', '1.1.0'].includes(version)) throw new Error('Unexpected fixture version');
const manifest = parse(readFileSync(join(workspace, 'forge.toml'), 'utf8'));
manifest.app = { ...manifest.app, name: 'Fixture', id: 'com.fezcode.airlift.integration', version, icon: 'airlift.ico' };
manifest.install.default_dir = join(root, version === '1.0.0' ? 'installed custom folder' : 'different default folder').replaceAll('\\', '/');
manifest.uninstall = { settings_dirs: ['${LOCALAPPDATA}/Fezcode/AirliftInstallerFixture'] };
manifest.registry = manifest.registry.map(entry => ({ ...entry, key: 'Software\\fezcode\\AirliftInstallerFixture' }));
manifest.shortcuts = manifest.shortcuts.map((entry, i) => ({
  ...entry, target: '${INSTALLDIR}/Fixture.exe', name: 'Airlift Installer Fixture',
  app_id: 'Fezcode.AirliftInstallerFixture', location: join(root, 'shortcuts', `${i}.lnk`).replaceAll('\\', '/'),
}));
for (const step of manifest.steps) {
  for (const launch of step.launches ?? []) {
    launch.target = '${INSTALLDIR}/Fixture.exe';
    launch.checked = false;
  }
}
manifest.dirs = manifest.dirs.map((entry, i) => ({ ...entry, src: `payload/${i === 0 ? 'desktop' : 'cli'}` }));
for (const dir of ['payload/desktop', 'payload/cli', 'dist', 'shortcuts']) mkdirSync(join(root, dir), { recursive: true });
for (const file of ['LICENSE.txt', 'native/Airlift.Desktop/Assets/airlift.ico']) {
  const name = file.endsWith('.ico') ? 'airlift.ico' : file;
  copyFileSync(join(workspace, file), join(root, name));
  copyFileSync(join(workspace, file), join(root, 'payload/desktop', name));
}
writeFileSync(join(root, 'payload/desktop/Fixture.exe'), 'Disposable non-executable fixture');
writeFileSync(join(root, 'payload/desktop/fixture.txt'), version);
writeFileSync(join(root, 'payload/cli/fixture-cli.txt'), version);
writeFileSync(join(root, 'forge.toml'), stringify(manifest));
