'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ducks, dom, markup, fakeClient, settle } = require('./helpers');

function entry(id, extra) {
    return Object.assign({ Id: id, UserId: 'u', UserName: 'bob', ItemId: 'i' + id, ItemName: 'Movie ' + id, Rating: 2, CreatedUtc: '2026-01-01T00:00:00Z' }, extra);
}

test('mountReviewsPage takes over the fallback page and loads the first page', async () => {
    const { doc } = dom(markup.fallbackPage);
    const client = fakeClient({ feed: () => Promise.resolve({ Items: [entry(1, { ImageItemId: 'p', UserHasImage: true })], Next: null }) });
    const view = ducks.mountReviewsPage(doc, client, { now: () => Date.parse('2026-01-01T00:00:30Z') });
    assert.ok(view);
    await settle();

    assert.equal(doc.title, 'Reviews');
    assert.ok(doc.querySelector('#fallbackPage h1').parentElement.hidden);
    assert.equal(doc.querySelectorAll('.ducksFeedEntry').length, 1);
    assert.match(doc.querySelector('.ducksFeedEntry').innerHTML, /\/img\/p/);
    assert.match(doc.querySelector('.ducksFeedEntry').innerHTML, /\/avatar\/u/);
    assert.match(doc.querySelector('.ducksFeedEntry').textContent, /just now/);
    assert.equal(doc.querySelector('.ducksMore').hidden, true);
    assert.equal(ducks.mountReviewsPage(doc, client), null, 'mounted only once');
});

test('mountReviewsPage does nothing without a visible fallback page', () => {
    const { doc } = dom(markup.fallbackPage.replace('class="page"', 'class="page hide"'));
    assert.equal(ducks.mountReviewsPage(doc, fakeClient()), null);
});

test('load more follows the cursor and stops at the end', async () => {
    const { doc } = dom('');
    const pages = [{ Items: [entry(3), entry(2)], Next: 'c1' }, { Items: [entry(1)] }];
    const client = fakeClient({ feed: () => Promise.resolve(pages.shift()) });
    const view = ducks.createReviewsPage(doc, client);
    doc.body.appendChild(view.el);

    await view.loadMore();
    assert.equal(doc.querySelector('.ducksMore').hidden, false);
    doc.querySelector('.ducksMore').click();
    await settle();
    await view.loadMore(); // done: no request

    assert.deepEqual(client.calls.map((c) => c[1]), [null, 'c1']);
    assert.equal(doc.querySelectorAll('.ducksFeedEntry').length, 3);
    assert.equal(doc.querySelector('.ducksMore').hidden, true);
});

test('concurrent loads are ignored while one is in flight', async () => {
    const { doc } = dom('');
    let resolve;
    const client = fakeClient({ feed: () => new Promise((r) => { resolve = r; }) });
    const view = ducks.createReviewsPage(doc, client);
    const first = view.loadMore();
    assert.equal(view.el.querySelector('.ducksMore').textContent, 'Loading…');
    assert.equal(view.el.querySelector('.ducksMore').disabled, true);
    await view.loadMore();
    resolve({ Items: [], Next: null });
    await first;
    assert.equal(client.calls.length, 1);
});

test('empty feed shows a hint; errors show a retry message', async () => {
    const empty = ducks.createReviewsPage(dom('').doc, fakeClient({ feed: () => Promise.resolve({}) }));
    await empty.loadMore();
    assert.match(empty.el.querySelector('.ducksEmpty').textContent, /No ratings yet/);

    const broken = ducks.createReviewsPage(dom('').doc, fakeClient({ feed: () => Promise.reject(new Error('x')) }));
    await broken.loadMore();
    assert.match(broken.el.querySelector('.ducksMessage').textContent, /Could not load/);
    assert.equal(broken.el.querySelector('.ducksMore').disabled, false);
});

test('infinite scroll loads more when the footer becomes visible', async () => {
    const observed = [];
    let callback;
    global.IntersectionObserver = function (cb, options) {
        callback = cb;
        this.options = options;
        this.observe = (el) => observed.push(el);
    };
    try {
        const client = fakeClient({ feed: () => Promise.resolve({ Items: [entry(1)], Next: 'n' }) });
        const view = ducks.createReviewsPage(dom('').doc, client);
        assert.equal(observed[0], view.el.querySelector('.ducksMore'));
        callback([{ isIntersecting: false }]);
        assert.equal(client.calls.length, 0);
        callback([{ isIntersecting: true }]);
        await settle();
        assert.equal(client.calls.length, 1);
    } finally {
        delete global.IntersectionObserver;
    }
});
