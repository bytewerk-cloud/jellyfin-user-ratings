// Ducks browser smoke test: checks every UI hook against a live jellyfin-web.
// Run after a Jellyfin update to see whether the web UI still has the elements Ducks attaches to.
//
//   cd tests/e2e && npm install && JF_USER=admin JF_PASS=ducks npm run smoke
//
// Env: JF_URL (default http://localhost:8096), JF_USER, JF_PASS, SHOTS (folder for screenshots, optional).
// Needs at least one movie in the library. Leaves a rating on the first movie it finds.
import { chromium } from 'playwright';

const url = process.env.JF_URL || 'http://localhost:8096';
const shots = process.env.SHOTS;
const results = [];
const check = (name, ok, detail) => { results.push({ name, ok }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  (' + detail + ')' : ''}`); };
const shot = async (page, name) => shots && page.screenshot({ path: `${shots}/${name}.png` });

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
const errors = [];
page.on('pageerror', (e) => errors.push(e.message));

try {
    await page.goto(`${url}/web/#/login`);
    await page.waitForSelector('#txtManualName', { timeout: 30000 });
    await page.fill('#txtManualName', process.env.JF_USER || 'admin');
    await page.fill('#txtManualPassword', process.env.JF_PASS || '');
    await page.click('.manualLoginForm button[type=submit]');
    await page.waitForFunction(() => window.ApiClient && window.ApiClient.getCurrentUserId(), null, { timeout: 30000 });

    check('ducks.js injected', await page.evaluate(() => !!window.Ducks));

    const movie = await page.evaluate(async () => {
        const res = await window.ApiClient.getItems(window.ApiClient.getCurrentUserId(), { Recursive: true, IncludeItemTypes: 'Movie', Limit: 1 });
        return res.Items[0];
    });
    if (!movie) throw new Error('No movie in the library');

    await page.goto(`${url}/web/#/details?id=${movie.Id}`);
    const widget = await page.waitForSelector('.ducksWidget .ducksPicker', { timeout: 15000 }).catch(() => null);
    check('detail page widget', !!widget);
    await shot(page, 'details');

    if (widget) {
        const box = await widget.boundingBox();
        await page.mouse.click(box.x + box.width * 0.99, box.y + box.height / 2);
        await page.waitForFunction(() => document.querySelector('.ducksWidget-mine')?.textContent === '4', null, { timeout: 10000 }).catch(() => {});
        check('click saves 4 ducks', (await page.textContent('.ducksWidget-mine')) === '4');
        check('aggregate shown', /average/.test(await page.textContent('.ducksWidget-aggtext')));
    }

    await page.goto(`${url}/web/#/home`);
    const nav = await page.waitForSelector('.ducksNavLink >> visible=true', { timeout: 15000 })
        .catch(() => page.waitForSelector('.ducksHeaderButton >> visible=true', { timeout: 5000 }))
        .catch(() => null);
    check('Reviews link in header', !!nav);

    const badge = await page.waitForSelector('.card .ducksBadge', { timeout: 15000 }).catch(() => null);
    check('badge on a card', !!badge);
    if (badge) {
        const [b, card] = await page.evaluate(() => {
            const el = document.querySelector('.card .ducksBadge');
            const host = el.parentElement.getBoundingClientRect();
            const r = el.getBoundingClientRect();
            return [{ x: r.left - host.left, y: r.top - host.top }, { w: host.width }];
        });
        check('badge is top-left', b.x < card.w / 2 && b.y < 30, JSON.stringify(b));
    }
    await shot(page, 'home');

    if (nav) {
        await nav.click();
        const entry = await page.waitForSelector('.ducksFeedEntry', { timeout: 15000 }).catch(() => null);
        check('Reviews feed lists entries', !!entry);
        await shot(page, 'reviews');
    }

    check('no page errors', errors.length === 0, errors.join(' | '));
} catch (e) {
    check('smoke run', false, e.message);
} finally {
    await browser.close();
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exit(failed ? 1 : 0);
