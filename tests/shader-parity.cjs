/*
 * Compares the WebGL2 Anime4K implementation of the client script with the reference
 * implementation (FFmpeg + libplacebo) for every mode / quality.
 *
 * Requirements: node, playwright (chromium), ffmpeg with libplacebo and a Vulkan device
 * (lavapipe works: apt install mesa-vulkan-drivers).
 *
 *   node tests/shader-parity.cjs [input.png] [minPsnr]
 *   SCALE=3 QUALITIES=Fast node tests/shader-parity.cjs
 */
'use strict';

const { chromium } = require('playwright');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync, spawnSync } = require('child_process');

const root = path.resolve(__dirname, '..', 'Jellyfin.Plugin.Anime4K');
const shaderDir = path.join(root, 'Shaders');
const clientScript = fs.readFileSync(path.join(root, 'Web', 'anime4k.js'), 'utf8');
const work = fs.mkdtempSync(path.join(os.tmpdir(), 'a4k-'));
const minPsnr = Number(process.argv[3] || 35);

// same presets as Transcoding/ShaderLibrary.cs
function shaderFiles(mode, quality) {
    const [big, small] = quality === 'Fast' ? ['M', 'S'] : quality === 'Ultra' ? ['UL', 'L'] : ['VL', 'M'];
    const C = 'Anime4K_Clamp_Highlights.glsl';
    const D2 = 'Anime4K_AutoDownscalePre_x2.glsl';
    const D4 = 'Anime4K_AutoDownscalePre_x4.glsl';
    const R = (s) => `Anime4K_Restore_CNN_${s}.glsl`;
    const RS = (s) => `Anime4K_Restore_CNN_Soft_${s}.glsl`;
    const U = (s) => `Anime4K_Upscale_CNN_x2_${s}.glsl`;
    const UD = (s) => `Anime4K_Upscale_Denoise_CNN_x2_${s}.glsl`;
    switch (mode) {
        case 'B': return [C, RS(big), U(big), D2, D4, U(small)];
        case 'C': return [C, UD(big), D2, D4, U(small)];
        case 'AA': return [C, R(big), U(big), R(small), D2, D4, U(small)];
        case 'BB': return [C, RS(big), U(big), D2, D4, RS(small), U(small)];
        case 'CA': return [C, UD(big), D2, D4, R(small), U(small)];
        default: return [C, R(big), U(big), D2, D4, U(small)];
    }
}

function bundle(mode, quality) {
    return shaderFiles(mode, quality).map((f) => fs.readFileSync(path.join(shaderDir, f), 'utf8').replace(/\r\n?/g, '\n')).join('\n\n');
}

function psnr(a, b) {
    const r = spawnSync('ffmpeg', ['-hide_banner', '-i', a, '-i', b, '-lavfi', '[0:v]format=rgb24[x];[1:v]format=rgb24[y];[x][y]psnr', '-f', 'null', '-'], { encoding: 'utf8' });
    return r.stderr;
}

(async () => {
    let input = process.argv[2];
    if (!input) {
        input = path.join(work, 'in.png');
        // a frame with sharp edges, gradients and text - similar to line art
        execFileSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=1', '-frames:v', '1', '-y', input]);
    }
    const probe = execFileSync('ffprobe', ['-v', 'error', '-select_streams', 'v:0', '-show_entries', 'stream=width,height', '-of', 'csv=p=0', input], { encoding: 'utf8' }).trim().split(',');
    const inW = Number(probe[0]);
    const inH = Number(probe[1]);
    const scale = Number(process.env.SCALE || 2);
    const outW = Math.round(inW * scale / 2) * 2;
    const outH = Math.round(inH * scale / 2) * 2;

    const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
    const page = await browser.newPage({ viewport: { width: outW, height: outH }, deviceScaleFactor: 1 });
    page.on('console', (m) => { if (m.type() === 'error') console.log('browser:', m.text()); });
    await page.setContent(`<html><body style="margin:0;background:#000"><canvas id="out" style="width:${outW}px;height:${outH}px;display:block"></canvas></body></html>`);
    await page.addScriptTag({ content: clientScript });
    const imgData = fs.readFileSync(input).toString('base64');

    let failed = false;
    const results = [];
    for (const quality of (process.env.QUALITIES || 'Fast,HQ,Ultra').split(',')) {
        for (const mode of ['A', 'B', 'C', 'AA', 'BB', 'CA']) {
            const src = bundle(mode, quality);
            const shaderFile = path.join(work, `b_${mode}_${quality}.glsl`);
            fs.writeFileSync(shaderFile, src);
            const ref = path.join(work, `ref_${mode}_${quality}.png`);
            execFileSync('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-init_hw_device', 'vulkan=vk:0', '-filter_hw_device', 'vk',
                '-i', input, '-vf', `libplacebo=w=${outW}:h=${outH}:custom_shader_path='${shaderFile}':format=rgb24`, '-frames:v', '1', '-y', ref]);

            const t0 = Date.now();
            const dataUrl = await page.evaluate(async ({ src, imgData }) => {
                const img = new Image();
                img.src = 'data:image/png;base64,' + imgData;
                await img.decode();
                const source = document.createElement('canvas');
                source.width = img.naturalWidth;
                source.height = img.naturalHeight;
                source.getContext('2d').drawImage(img, 0, 0);
                // the renderer only needs a TexImageSource with video like properties
                source.videoWidth = source.width;
                source.videoHeight = source.height;
                source.readyState = 4;
                const old = document.getElementById('out');
                const canvas = document.createElement('canvas');
                canvas.id = 'out';
                canvas.style.cssText = old.style.cssText;
                old.replaceWith(canvas);
                const r = new window.__anime4kJellyfin.Renderer(source, canvas);
                r.setShaders(src);
                r.render();
                const res = { url: canvas.toDataURL('image/png'), stats: r.stats, passes: r.passes.length };
                return res;
            }, { src, imgData });
            const outPng = path.join(work, `webgl_${mode}_${quality}.png`);
            fs.writeFileSync(outPng, Buffer.from(dataUrl.url.split(',')[1], 'base64'));
            const p = psnr(outPng, ref);
            const m = /average:([\d.]+|inf)/.exec(p);
            const value = m ? (m[1] === 'inf' ? Infinity : Number(m[1])) : 0;
            const ok = value >= minPsnr;
            failed = failed || !ok;
            results.push({ mode, quality, passes: `${dataUrl.stats.executed}/${dataUrl.passes}`, result: dataUrl.stats.result, psnr: value.toFixed(2), ms: Date.now() - t0, ok });
            console.log(`${ok ? 'OK  ' : 'FAIL'} mode ${mode.padEnd(2)} ${quality.padEnd(5)} passes ${dataUrl.stats.executed}/${dataUrl.passes} result ${dataUrl.stats.result}  PSNR vs libplacebo: ${value.toFixed(2)} dB`);
        }
    }

    await browser.close();
    console.log('outputs in', work);
    process.exit(failed ? 1 : 0);
})().catch((e) => {
    console.error(e);
    process.exit(2);
});
