/*
 * End-to-end test against a running Jellyfin server with the plugin installed.
 *
 *   JF_URL=http://localhost:8096 JF_USER=admin JF_PASS=admin JF_ITEM=<episode id> node tests/e2e-web.cjs [outdir]
 *
 * Checks: script injection, OSD button, remote control navigation of the menu (arrow keys, OK, Back),
 * client side WebGL rendering and switching to server side upscaling (playback restarts on the Anime4K stream).
 */
'use strict';

const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

const BASE = process.env.JF_URL || 'http://localhost:8096';
const USER = process.env.JF_USER || 'admin';
const PASS = process.env.JF_PASS || 'admin';
const ITEM = process.env.JF_ITEM;
const OUT = process.argv[2] || path.join(__dirname, 'out');
fs.mkdirSync(OUT, { recursive: true });

function step(msg) {
    console.log('- ' + msg);
}

async function shot(page, name) {
    await page.screenshot({ path: path.join(OUT, name + '.png') });
}

let currentPage = null;

(async () => {
    const browser = await chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--disable-gpu-watchdog'] });
    const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
    currentPage = page;
    page.on('console', (m) => {
        const t = m.text();
        if (t.includes('[Anime4K]') || m.type() === 'error') {
            console.log('   browser ' + m.type() + ': ' + t.slice(0, 300));
        }
    });

    await page.goto(BASE + '/web/');
    await page.waitForFunction(() => !!window.__anime4kJellyfin, null, { timeout: 30000 });
    step('client script injected');

    await page.waitForSelector('#txtManualName', { timeout: 30000 }).catch(async () => {
        // user selection screen -> manual login
        await page.click('.btnManual');
    });
    await page.fill('#txtManualName', USER);
    await page.fill('#txtManualPassword', PASS);
    await page.click('.manualLoginForm button[type=submit]');
    await page.waitForURL(/\/web\/#\/home/, { timeout: 30000 });
    step('logged in');

    // start playback
    step('url after login: ' + page.url());
    await page.evaluate((id) => {
        window.location.hash = '#/details?id=' + id;
    }, ITEM);
    await page.waitForSelector('.btnPlay, .detailButton', { timeout: 30000 });
    await page.waitForTimeout(1500);
    await page.click('.mainDetailButtons .btnPlay, .btnPlay');
    await page.waitForSelector('.videoPlayerContainer video', { timeout: 60000 });
    await page.waitForFunction(() => {
        const v = document.querySelector('.videoPlayerContainer video');
        return v && v.readyState >= 2 && v.currentTime > 0.5;
    }, null, { timeout: 90000 });
    step('video playing: ' + await page.evaluate(() => {
        const v = document.querySelector('.videoPlayerContainer video');
        return v.videoWidth + 'x' + v.videoHeight;
    }));

    // OSD button
    await page.mouse.move(640, 400);
    await page.mouse.move(650, 420);
    await page.waitForSelector('.videoOsdBottom .btnAnime4K', { timeout: 20000 });
    step('OSD button present');
    await shot(page, '01-osd');

    // software rendering in CI is slow: use the fast preset
    await page.evaluate(() => {
        window.__anime4kJellyfin.settings.quality = 'Fast';
    });

    // open the menu with the red remote key (keyCode 403)
    await page.evaluate(() => {
        // real remote key events target the focused element
        (document.activeElement || document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'ColorF0Red', keyCode: 403, bubbles: true, cancelable: true }));
    });
    await page.waitForSelector('.a4k-panel', { timeout: 5000 });
    step('menu opened with red key');

    // D-pad: first row is "Anime4K" -> Right switches to "Dieses Gerät (GPU)"
    await page.keyboard.press('ArrowRight');
    await page.waitForTimeout(300);
    const location = await page.evaluate(() => window.__anime4kJellyfin.settings.location);
    if (location !== 'client') {
        throw new Error('expected client location, got ' + location);
    }
    step('ArrowRight switched to client rendering');
    await page.keyboard.press('ArrowDown'); // Modus
    await page.keyboard.press('ArrowRight'); // A -> B
    const mode = await page.evaluate(() => window.__anime4kJellyfin.settings.mode);
    if (mode !== 'B') {
        throw new Error('expected mode B, got ' + mode);
    }
    step('mode changed with D-pad: ' + mode);
    await page.keyboard.press('ArrowDown'); // Qualität
    await page.keyboard.press('ArrowDown'); // Vergleich
    await page.keyboard.press('Enter'); // toggle compare
    await page.keyboard.press('ArrowDown'); // Statistik
    await page.keyboard.press('Enter');
    await shot(page, '02-menu');

    // Back closes the menu (webOS back key = 461)
    await page.evaluate(() => {
        (document.activeElement || document.body).dispatchEvent(new KeyboardEvent('keydown', { key: 'GoBack', keyCode: 461, bubbles: true, cancelable: true }));
    });
    await page.waitForSelector('.a4k-panel', { state: 'detached', timeout: 5000 });
    await page.waitForTimeout(500);
    if (!(await page.$('.videoPlayerContainer video'))) {
        throw new Error('back key left the player');
    }
    step('menu closed with back key, player still open');

    await page.waitForFunction(() => {
        const c = window.__anime4kJellyfin.controller;
        return c && c.renderer && c.renderer.stats.executed > 0;
    }, null, { timeout: 60000 });
    await page.waitForTimeout(3000);
    const stats = await page.evaluate(() => {
        const s = window.__anime4kJellyfin.controller.renderer.stats;
        return { executed: s.executed, input: s.input, result: s.result, output: s.output, fps: s.fps };
    });
    step('client rendering active: ' + JSON.stringify(stats));
    await shot(page, '03-client-compare');

    // switch to server mode via the menu
    await page.evaluate(() => window.__anime4kJellyfin.openPanel());
    await page.waitForSelector('.a4k-panel');
    await page.keyboard.press('ArrowRight'); // client -> server
    await page.waitForTimeout(300);
    await shot(page, '04-menu-server');
    // navigate to the "Übernehmen" button and press OK
    for (let i = 0; i < 10; i++) {
        const label = await page.evaluate(() => {
            const el = document.querySelector('.a4k-row.a4k-focus');
            return el ? el.textContent : '';
        });
        if (label.indexOf('Übernehmen') >= 0) {
            break;
        }
        await page.keyboard.press('ArrowDown');
    }
    const requests = [];
    page.on('request', (r) => {
        if (r.url().includes('/Anime4K/Hls/')) {
            requests.push(r.url());
        }
    });
    await page.keyboard.press('Enter');
    step('server mode applied, waiting for restarted playback');
    await page.waitForFunction(() => {
        const v = document.querySelector('.videoPlayerContainer video');
        return v && v.videoHeight >= 1000 && v.currentTime > 0;
    }, null, { timeout: 240000 });
    const v = await page.evaluate(() => {
        const el = document.querySelector('.videoPlayerContainer video');
        return el.videoWidth + 'x' + el.videoHeight + ' @ ' + el.currentTime.toFixed(1) + 's';
    });
    step('playing server upscaled stream: ' + v + ', ' + requests.length + ' Anime4K requests');
    await page.waitForTimeout(2000);
    await shot(page, '05-server');

    // turn server mode off again for this device
    await page.evaluate(() => window.ApiClient.ajax({
        type: 'POST', url: window.ApiClient.getUrl('Anime4K/Devices/Current'),
        data: JSON.stringify({ ServerUpscaling: false, Mode: 'A', Quality: 'HQ', TargetHeight: 2160 }), contentType: 'application/json'
    }));
    await browser.close();
    console.log('OK');
})().catch(async (e) => {
    console.error('FAILED:', e);
    if (currentPage) {
        await shot(currentPage, 'failure').catch(() => {});
    }
    process.exit(1);
});
