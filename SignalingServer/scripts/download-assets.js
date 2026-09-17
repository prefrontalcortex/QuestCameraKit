// Downloads model assets that are too large to commit (and don't need to be,
// since they're publicly hosted by Google) - run once after `npm install`.
// Idempotent: skips any file that already exists.
const fs = require('fs');
const path = require('path');
const https = require('https');

const ASSETS = [
  {
    url: 'https://storage.googleapis.com/mediapipe-models/pose_landmarker/pose_landmarker_lite/float16/latest/pose_landmarker_lite.task',
    dest: path.join(__dirname, '..', 'public', 'models', 'pose_landmarker_lite.task'),
  },
];

function download(url, dest) {
  return new Promise((resolve, reject) => {
    fs.mkdirSync(path.dirname(dest), { recursive: true });
    const file = fs.createWriteStream(dest);
    https.get(url, (res) => {
      if (res.statusCode !== 200) {
        reject(new Error(`${url} -> HTTP ${res.statusCode}`));
        return;
      }
      res.pipe(file);
      file.on('finish', () => file.close(resolve));
    }).on('error', (err) => {
      fs.unlink(dest, () => {});
      reject(err);
    });
  });
}

(async () => {
  for (const asset of ASSETS) {
    if (fs.existsSync(asset.dest)) {
      console.log(`[assets] already present: ${path.relative(process.cwd(), asset.dest)}`);
      continue;
    }
    console.log(`[assets] downloading ${asset.url}`);
    await download(asset.url, asset.dest);
    console.log(`[assets] saved to ${path.relative(process.cwd(), asset.dest)}`);
  }
})().catch((err) => {
  console.error('[assets] download failed:', err.message);
  process.exit(1);
});
