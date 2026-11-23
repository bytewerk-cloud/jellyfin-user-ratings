'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ducks, ID, ID2, dom, markup, fakeClient, tick, settle } = require('./helpers');

test('badges are batched into one request and rendered top-left in cardScalable', async () => {
    const { doc } = dom(markup.card(ID) + markup.card(ID2) + markup.card(ID));
    const client = fakeClient({ aggregates: () => Promise.resolve({ [ID]: { Average: 3.25, Count: 2 } }) });
    const badges = ducks.createBadgeManager(client, { delay: 0 });

    badges.scan(doc);
    await tick(5);
    await settle();

    assert.equal(client.calls.length, 1);
    assert.deepEqual(client.calls[0][1], [ID, ID2]);
    const rendered = doc.querySelectorAll('.cardScalable > .ducksBadge');
    assert.equal(rendered.length, 2);
    assert.match(rendered[0].textContent, /3.3/);
    assert.equal(doc.querySelectorAll('[data-id="' + ID2 + '"] .ducksBadge').length, 0);
});

test('cached results render without a new request; rescanning handled cards does nothing', async () => {
    const { doc } = dom(markup.card(ID));
    const client = fakeClient({ aggregates: () => Promise.resolve({ [ID]: { Average: 2, Count: 1 } }) });
    const badges = ducks.createBadgeManager(client, { delay: 0 });
    badges.scan(doc);
    await tick(5);
    await settle();

    badges.scan(doc);
    doc.body.insertAdjacentHTML('beforeend', markup.card(ID, 'legacy'));
    badges.scan(doc);

    assert.equal(client.calls.length, 1);
    assert.equal(doc.querySelectorAll('.ducksBadge').length, 2);
    assert.ok(doc.querySelector('.cardImageContainer > .ducksBadge'), 'legacy cards get the badge on the image container');
});

test('expired cache entries are fetched again', async () => {
    let now = 0;
    const { doc } = dom(markup.card(ID));
    const client = fakeClient();
    const badges = ducks.createBadgeManager(client, { delay: 0, now: () => now });
    badges.scan(doc);
    await tick(5);
    await settle();

    now = 10 * 60 * 1000;
    doc.querySelector('.card').removeAttribute('data-ducks-for');
    badges.scan(doc);
    await tick(5);
    await settle();

    assert.equal(client.calls.length, 2);
});

test('a card reused for a different item is redone, and stale responses are ignored', async () => {
    const { doc } = dom(markup.card(ID));
    let resolve;
    const client = fakeClient({ aggregates: () => new Promise((r) => { resolve = r; }) });
    const badges = ducks.createBadgeManager(client, { delay: 0 });
    badges.scan(doc);
    await tick(5);

    doc.querySelector('.card').setAttribute('data-id', ID2);
    badges.scan(doc);
    resolve({ [ID]: { Average: 4, Count: 1 } });
    await settle();

    assert.equal(doc.querySelectorAll('.ducksBadge').length, 0);
    assert.equal(doc.querySelector('.card').getAttribute('data-ducks-for'), ID2);
});

test('failed requests leave cards without badges and are retried on invalidate', async () => {
    const { doc } = dom(markup.card(ID));
    let fail = true;
    const client = fakeClient({
        aggregates: () => (fail ? Promise.reject(new Error('down')) : Promise.resolve({ [ID]: { Average: 1, Count: 1 } }))
    });
    const badges = ducks.createBadgeManager(client, { delay: 0 });
    badges.scan(doc);
    await tick(5);
    await settle();
    assert.equal(doc.querySelectorAll('.ducksBadge').length, 0);

    fail = false;
    badges.invalidate(ID, doc);
    await tick(5);
    await settle();
    assert.equal(doc.querySelectorAll('.ducksBadge').length, 1);
});

test('invalidate replaces an existing badge and removes it when ratings are gone', async () => {
    const { doc } = dom(markup.card(ID));
    let result = { [ID]: { Average: 1, Count: 1 } };
    const client = fakeClient({ aggregates: () => Promise.resolve(result) });
    const badges = ducks.createBadgeManager(client, { delay: 0 });
    badges.scan(doc);
    await tick(5);
    await settle();

    result = { [ID]: { Average: 3, Count: 2 } };
    badges.invalidate(ID, doc);
    await tick(5);
    await settle();
    assert.equal(doc.querySelectorAll('.ducksBadge').length, 1);
    assert.match(doc.querySelector('.ducksBadge').textContent, /3/);

    result = null;
    badges.invalidate(ID, doc);
    await tick(5);
    await settle();
    assert.equal(doc.querySelectorAll('.ducksBadge').length, 0);
});

test('render ignores cards without an image host and zero-count aggregates', () => {
    const { doc } = dom(markup.card(ID, 'bare') + markup.card(ID2));
    const badges = ducks.createBadgeManager(fakeClient());
    badges.render(doc.querySelector('[data-id="' + ID + '"]'), { Average: 2, Count: 1 });
    badges.render(doc.querySelector('[data-id="' + ID2 + '"]'), { Average: 0, Count: 0 });
    assert.equal(doc.querySelectorAll('.ducksBadge').length, 0);
});

test('large pages are split into chunks of 200 ids, using the default delay', async () => {
    const ids = Array.from({ length: 450 }, (_, i) => i.toString(16).padStart(32, '0'));
    const { doc } = dom(ids.map((id) => markup.card(id)).join(''));
    const client = fakeClient();
    const badges = ducks.createBadgeManager(client);
    badges.scan(doc);
    await tick(200);
    await settle();
    assert.deepEqual(client.calls.map((c) => c[1].length), [200, 200, 50]);
});

test('flush with nothing queued does nothing', async () => {
    const client = fakeClient();
    await ducks.createBadgeManager(client).flush();
    assert.equal(client.calls.length, 0);
});
