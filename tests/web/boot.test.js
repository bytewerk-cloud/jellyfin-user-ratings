'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ducks, DUCKS_PATH, ID, dom, markup, item, tick, settle } = require('./helpers');

function fakeApiClient(responses) {
    const requests = [];
    return {
        requests,
        getUrl: (path) => 'http://srv/' + path,
        ajax: (o) => {
            requests.push(o.type + ' ' + o.url);
            const match = Object.keys(responses).find((k) => o.url.indexOf(k) >= 0);
            return Promise.resolve(match ? responses[match](o) : {});
        }
    };
}

test('start wires nav, detail widget, badges and feed, and reacts to DOM changes', async () => {
    const { win, doc } = dom(markup.appBar + markup.detailPage + markup.card(ID), 'http://localhost/web/#/details?id=' + ID);
    const api = fakeApiClient({
        'Ducks/Items/': () => item({ Aggregate: { Average: 3, Count: 1 } }),
        'Ducks/Aggregates': () => ({ [ID]: { Average: 3, Count: 1 } }),
        'Ducks/Feed': () => ({ Items: [] })
    });
    let confirmed = null;
    win.confirm = (text) => { confirmed = text; return true; };
    const app = ducks.start(win, api, { delay: 0 });
    await tick(10);
    await settle();

    assert.ok(doc.querySelector('.ducksNavLink'));
    assert.ok(doc.querySelector('.ducksWidget'));
    assert.equal(doc.querySelectorAll('.ducksBadge').length, 1);

    // Saving invalidates the badge cache for that item (hooks.onChange).
    doc.querySelector('.ducksZero').click();
    await tick(10);
    await settle();
    assert.equal(api.requests.filter((r) => r.startsWith('POST http://srv/Ducks/Aggregates')).length, 2);

    // A removed widget is re-mounted on the next tick.
    doc.querySelector('.ducksWidget').remove();
    app.tick();
    await settle();
    assert.ok(doc.querySelector('.ducksWidget'));
    assert.equal(confirmed, null);

    // DOM mutations schedule a (debounced) tick: new cards get badges.
    doc.body.insertAdjacentHTML('beforeend', markup.card('dddddddddddddddddddddddddddddddd'));
    doc.body.insertAdjacentHTML('beforeend', '<i></i>');
    await tick(120);
    await settle();
    assert.equal(doc.querySelector('[data-id="dddddddddddddddddddddddddddddddd"]').getAttribute('data-ducks-for'), 'dddddddddddddddddddddddddddddddd');

    // Route change to the reviews page mounts the feed.
    doc.body.insertAdjacentHTML('beforeend', markup.fallbackPage);
    win.location.hash = '#/ducks-reviews';
    await tick(150);
    await settle();
    assert.ok(doc.querySelector('.ducksReviewsPage'));
    assert.ok(doc.querySelector('.ducksNavLink').classList.contains('ducksNav-active'));
});

test('remove via widget uses window.confirm', async () => {
    const { win, doc } = dom(markup.detailPage, 'http://localhost/web/#/details?id=' + ID);
    const api = fakeApiClient({ 'Ducks/Items/': () => item({ Mine: { Rating: 1 } }) });
    let asked = '';
    win.confirm = (text) => { asked = text; return false; };
    ducks.start(win, api, { delay: 0 });
    await settle();
    doc.querySelector('.ducksRemove').click();
    assert.equal(asked, 'Remove your rating and review?');
});

test('boot waits for ApiClient, then starts', async () => {
    const { win } = dom(markup.appBar);
    const realSetTimeout = win.setTimeout.bind(win);
    win.setTimeout = (fn) => realSetTimeout(fn, 1);
    ducks.boot(win);
    await tick(10);
    assert.equal(win.document.querySelector('.ducksNavLink'), null);
    win.ApiClient = fakeApiClient({});
    await tick(20);
    assert.ok(win.document.querySelector('.ducksNavLink'));
});

test('boot gives up after about a minute without ApiClient', async () => {
    const { win } = dom('');
    let waits = 0;
    win.setTimeout = (fn) => { waits++; fn(); };
    ducks.boot(win);
    assert.equal(waits, 240);
});

test('in a browser the script registers window.Ducks and boots itself', () => {
    const { win } = dom('');
    let booted = false;
    win.setTimeout = () => { booted = true; };
    global.window = win;
    try {
        delete require.cache[DUCKS_PATH];
        const fresh = require(DUCKS_PATH);
        assert.equal(win.Ducks, fresh);
        assert.ok(booted);
    } finally {
        delete global.window;
        delete require.cache[DUCKS_PATH];
    }
});
