// Re-export the checked-in SVG masters. Requires Node.js and sharp.
// Optional: HWSCOPE_SHARP_MODULE can point to an existing sharp installation.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require(process.env.HWSCOPE_SHARP_MODULE || 'sharp');

const root = path.resolve(__dirname, '..');
const assets = path.join(root, 'assets', 'branding');
const png = path.join(assets, 'png');
const sizes = [16, 20, 24, 32, 48, 64, 72, 80, 96, 128, 256, 512, 1024];

async function raster(name, width, destination) {
  // Render at 4x first so thin curves remain smooth at Windows icon sizes.
  const master = await fs.readFile(path.join(assets, name + '.svg'));
  const highResolution = await sharp(master, { density: 600 })
    .resize(width * 4).png().toBuffer();
  await sharp(highResolution).resize(width).png().toFile(destination);
}

async function main() {
  await fs.mkdir(png, { recursive: true });
  for (const theme of ['', '-light']) {
    for (const size of sizes) {
      const source = `hwscope-icon${size <= 24 ? '-small' : ''}${theme}`;
      await raster(source, size, path.join(png, `hwscope-icon${theme}-${size}.png`));
    }
    await raster(`hwscope-lockup${theme}`, 2000, path.join(png, `hwscope-lockup${theme}-2000.png`));
  }
  for (const theme of ['', '-light', '-ink', '-white']) {
    await raster(`hwscope-mark${theme}`, 1024, path.join(png, `hwscope-mark${theme}-1024.png`));
  }
  await raster('hwscope-preview', 1600, path.join(assets, 'hwscope-preview.png'));

  const iconSizes = sizes.filter(size => size <= 256);
  const layers = await Promise.all(iconSizes.map(size => fs.readFile(path.join(png, `hwscope-icon-${size}.png`))));
  const header = Buffer.alloc(6 + 16 * layers.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(layers.length, 4);
  let offset = header.length;
  for (let index = 0; index < layers.length; index++) {
    const entry = 6 + index * 16;
    header[entry] = header[entry + 1] = iconSizes[index] === 256 ? 0 : iconSizes[index];
    header.writeUInt16LE(1, entry + 4);
    header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(layers[index].length, entry + 8);
    header.writeUInt32LE(offset, entry + 12);
    offset += layers[index].length;
  }
  const ico = Buffer.concat([header, ...layers]);
  await fs.writeFile(path.join(assets, 'hwscope.ico'), ico);
  await fs.writeFile(path.join(root, 'src', 'HwScope.App', 'Assets', 'HwScope.ico'), ico);
  console.log(`Exported PNG variants and ${iconSizes.length}-layer Windows ICO.`);
}

main().catch(error => { console.error(error); process.exitCode = 1; });
