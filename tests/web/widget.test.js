'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ducks, ID, dom, markup, item, mine, httpError, fakeClient, settle } = require('./helpers');

function setup(clientOverrides, data) {
    const { win, doc } = dom(markup.detailPage);
    const client = fakeClient(Object.assign({ get: () => Promise.resolve(data || item()) }, clientOverrides));
    const changes = [];
    const hooks = { onChange: (id) => changes.push(id), confirm: () => true };
    const widget = ducks.createWidget(doc, client, ID, hooks);
    doc.querySelector('.detailPagePrimaryContent').appendChild(widget.el);
    return { win, doc, client, changes, hooks, widget, el: widget.el };
}

function click(win, el, x) {
    el.dispatchEvent(new win.MouseEvent('click', { bubbles: true, clientX: x || 0 }));
}

function key(win, el, k) {
    const e = new win.KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true });
    el.dispatchEvent(e);
    return e;
}

test('renders nothing until loaded, then unrated state with aggregate', async () => {
    const t = setup({}, item({ Aggregate: { Average: 2.75, Count: 2 } }));
    t.widget.render();
    assert.equal(t.el.innerHTML, '');
    assert.equal(await t.widget.load(), true);
    assert.match(t.el.textContent, /Your rating/);
    assert.match(t.el.textContent, /2.8 average · 2 ratings/);
    assert.ok(t.el.querySelector('.ducks-unrated'));
    assert.equal(t.el.querySelector('.ducksPicker').getAttribute('aria-valuetext'), 'Not rated');
    assert.match(t.el.textContent, /Write a review/);
    assert.equal(t.el.querySelector('.ducksRemove'), null);
});

test('load resolves false when the item cannot be rated', async () => {
    const t = setup({ get: () => Promise.reject(httpError(404)) });
    assert.equal(await t.widget.load(), false);
});

test('clicking the picker saves the half-duck under the pointer and keeps review text', async () => {
    const t = setup({}, item({ Mine: mine(1, 'Title', 'Body') }));
    await t.widget.load();
    click(t.win, t.el.querySelector('.duck'), 60);
    await settle();

    assert.deepEqual(t.client.calls.find((c) => c[0] === 'save'), ['save', ID, { Rating: 2.5, Title: 'Title', Body: 'Body' }]);
    assert.deepEqual(t.changes, [ID]);
    assert.equal(t.el.querySelector('.ducksWidget-mine').textContent, '2.5');
});

test('rating an unrated item sends null review text; the 0 button saves zero ducks', async () => {
    const t = setup();
    await t.widget.load();
    click(t.win, t.el.querySelector('.ducksZero'));
    await settle();
    assert.deepEqual(t.client.calls.find((c) => c[0] === 'save'), ['save', ID, { Rating: 0, Title: null, Body: null }]);
    assert.ok(t.el.querySelector('.ducksZero-active'));
    assert.equal(t.el.querySelector('.ducksPicker').getAttribute('aria-valuetext'), '0 ducks');
});

test('hover previews the rating and leaving restores it', async () => {
    const t = setup({}, item({ Mine: mine(1) }));
    await t.widget.load();
    const move = (x, target) => (target || t.el.querySelector('.duck')).dispatchEvent(new t.win.MouseEvent('mousemove', { bubbles: true, clientX: x }));

    move(100);
    assert.equal(t.widget.state.preview, 4);
    assert.equal(t.el.querySelector('.ducksPicker').getAttribute('aria-valuenow'), '4');
    move(100); // same value: no re-render needed
    move(10, t.el.querySelector('.ducksWidget-label')); // outside the picker: ignored
    assert.equal(t.widget.state.preview, 4);

    t.el.dispatchEvent(new t.win.MouseEvent('mouseleave'));
    assert.equal(t.widget.state.preview, null);
    t.el.dispatchEvent(new t.win.MouseEvent('mouseleave'));
    assert.equal(t.el.querySelector('.ducksWidget-mine').textContent, '1');
});

test('mouseleave does not reset preview while a save is in flight', async () => {
    let resolve;
    const t = setup({ save: () => new Promise((r) => { resolve = r; }) });
    await t.widget.load();
    t.el.querySelector('.duck').dispatchEvent(new t.win.MouseEvent('mousemove', { bubbles: true, clientX: 50 }));
    click(t.win, t.el.querySelector('.duck'), 50);
    t.el.dispatchEvent(new t.win.MouseEvent('mouseleave'));
    assert.equal(t.widget.state.preview, 2);
    resolve(item({ Mine: mine(2) }));
    await settle();
    assert.equal(t.widget.state.preview, null);
});

test('mousemove with picker missing (form open) is ignored', async () => {
    const t = setup();
    await t.widget.load();
    t.el.querySelector('.ducksPicker').remove();
    t.el.dispatchEvent(new t.win.MouseEvent('mousemove', { bubbles: true, clientX: 50 }));
    click(t.win, t.el, 50);
    assert.equal(t.widget.state.preview, null);
});

test('keyboard: arrows preview, Home/End jump, Enter and Space save, other keys ignored', async () => {
    const t = setup({}, item({ Mine: mine(2) }));
    await t.widget.load();
    const picker = () => t.el.querySelector('.ducksPicker');
    picker().focus();

    assert.equal(key(t.win, picker(), 'ArrowRight').defaultPrevented, true);
    assert.equal(t.widget.state.preview, 2.5);
    assert.equal(t.win.document.activeElement, picker(), 'focus survives re-render');
    key(t.win, picker(), 'ArrowUp');
    assert.equal(t.widget.state.preview, 3);
    key(t.win, picker(), 'ArrowLeft');
    key(t.win, picker(), 'ArrowDown');
    assert.equal(t.widget.state.preview, 2);
    key(t.win, picker(), 'End');
    assert.equal(t.widget.state.preview, 4);
    key(t.win, picker(), 'Home');
    assert.equal(t.widget.state.preview, 0);
    assert.equal(key(t.win, picker(), 'a').defaultPrevented, false);

    key(t.win, picker(), 'Enter');
    await settle();
    assert.deepEqual(t.client.calls.filter((c) => c[0] === 'save').map((c) => c[2].Rating), [0]);

    key(t.win, picker(), ' ');
    await settle();
    assert.deepEqual(t.client.calls.filter((c) => c[0] === 'save').map((c) => c[2].Rating), [0, 0]);
});

test('keyboard on an unrated item starts from zero; keys outside the picker are ignored', async () => {
    const t = setup();
    await t.widget.load();
    key(t.win, t.el.querySelector('.ducksPicker'), 'ArrowRight');
    assert.equal(t.widget.state.preview, 0.5);
    key(t.win, t.el.querySelector('.ducksEdit'), 'ArrowRight');
    assert.equal(t.widget.state.preview, 0.5);
    key(t.win, t.el, 'ArrowRight'); // target without classList entry for picker
    assert.equal(t.widget.state.preview, 0.5);
});

test('write a review: form opens empty, saves text with current rating, closes', async () => {
    const t = setup({}, item({ Mine: mine(3) }));
    await t.widget.load();
    click(t.win, t.el.querySelector('.ducksEdit'));
    const title = t.el.querySelector('.ducksForm-title');
    const body = t.el.querySelector('.ducksForm-body');
    assert.equal(t.win.document.activeElement, title);
    assert.equal(title.value, '');
    assert.equal(title.getAttribute('maxlength'), '100');
    assert.equal(body.getAttribute('maxlength'), '2000');

    title.value = 'Great';
    title.dispatchEvent(new t.win.Event('input', { bubbles: true }));
    body.value = 'Loved it';
    body.dispatchEvent(new t.win.Event('input', { bubbles: true }));
    t.el.querySelector('.ducksForm').dispatchEvent(new t.win.Event('submit', { bubbles: true, cancelable: true }));
    await settle();

    assert.deepEqual(t.client.calls.find((c) => c[0] === 'save'), ['save', ID, { Rating: 3, Title: 'Great', Body: 'Loved it' }]);
    assert.equal(t.el.querySelector('.ducksForm'), null);
    assert.match(t.el.querySelector('.ducksMyReview').textContent, /GreatLoved it/);
    assert.match(t.el.querySelector('.ducksEdit').textContent, /Edit review/);
});

test('edit review pre-fills existing text; cancel closes without saving; other input ignored', async () => {
    const t = setup({}, item({ Mine: mine(3, 'Old', 'Text') }));
    await t.widget.load();
    assert.match(t.el.querySelector('.ducksMyReview').textContent, /OldText/);
    click(t.win, t.el.querySelector('.ducksEdit'));
    assert.equal(t.el.querySelector('.ducksForm-title').value, 'Old');
    assert.equal(t.el.querySelector('.ducksForm-body').value, 'Text');
    t.el.querySelector('.ducksForm-actions').dispatchEvent(new t.win.Event('input', { bubbles: true }));
    click(t.win, t.el.querySelector('.ducksCancel'));
    assert.equal(t.el.querySelector('.ducksForm'), null);
    assert.equal(t.client.calls.filter((c) => c[0] === 'save').length, 0);
});

test('review title-only and body-only display', async () => {
    const a = setup({}, item({ Mine: mine(1, 'Only title') }));
    await a.widget.load();
    assert.equal(a.el.querySelector('.ducksMyReview .ducksReviewBody'), null);
    const b = setup({}, item({ Mine: mine(1, null, 'Only body') }));
    await b.widget.load();
    assert.equal(b.el.querySelector('.ducksMyReview .ducksReviewTitle'), null);
});

test('submitting a review without any rating asks for ducks first; picking via hover then saving works', async () => {
    const t = setup();
    await t.widget.load();
    click(t.win, t.el.querySelector('.ducksEdit'));
    t.el.querySelector('.ducksForm').dispatchEvent(new t.win.Event('submit', { bubbles: true, cancelable: true }));
    assert.match(t.el.querySelector('.ducksMessage').textContent, /Pick your ducks first/);
    assert.equal(t.client.calls.filter((c) => c[0] === 'save').length, 0);

    t.widget.state.preview = 1.5;
    t.el.querySelector('.ducksForm').dispatchEvent(new t.win.Event('submit', { bubbles: true, cancelable: true }));
    await settle();
    assert.equal(t.client.calls.find((c) => c[0] === 'save')[2].Rating, 1.5);
});

test('server validation errors are shown and the preview is dropped', async () => {
    const t = setup({ save: () => Promise.reject(httpError(400, { Error: 'Title is longer than 100 characters.' })) }, item({ Mine: mine(2) }));
    await t.widget.load();
    click(t.win, t.el.querySelector('.duck'), 100);
    await settle();
    assert.equal(t.el.querySelector('.ducksMessage').textContent, 'Title is longer than 100 characters.');
    assert.equal(t.widget.state.preview, null);
    assert.equal(t.changes.length, 0);
});

test('remove asks for confirmation, then deletes', async () => {
    const t = setup({}, item({ Mine: mine(2) }));
    await t.widget.load();
    t.hooks.confirm = () => false;
    click(t.win, t.el.querySelector('.ducksRemove'));
    await settle();
    assert.equal(t.client.calls.filter((c) => c[0] === 'remove').length, 0);

    t.hooks.confirm = () => true;
    click(t.win, t.el.querySelector('.ducksRemove'));
    await settle();
    assert.equal(t.client.calls.filter((c) => c[0] === 'remove').length, 1);
    assert.deepEqual(t.changes, [ID]);
    assert.equal(t.el.querySelector('.ducksRemove'), null);
});

test('remove failure shows the error', async () => {
    const t = setup({ remove: () => Promise.reject(new Error('offline')) }, item({ Mine: mine(2) }));
    await t.widget.load();
    click(t.win, t.el.querySelector('.ducksRemove'));
    await settle();
    assert.equal(t.el.querySelector('.ducksMessage').textContent, 'offline');
});

test('mountWidget places the widget at the top of the content area, once per item', async () => {
    const { doc } = dom(markup.detailPage);
    const client = fakeClient();
    const hooks = { onChange() {}, confirm: () => true };
    const first = ducks.mountWidget(doc, client, ID, hooks);
    assert.ok(first);
    assert.equal(doc.querySelector('.detailPagePrimaryContent').firstChild, first.el);
    assert.equal(ducks.mountWidget(doc, client, ID, hooks), null);

    const other = ducks.mountWidget(doc, client, 'cccccccccccccccccccccccccccccccc', hooks);
    assert.ok(other);
    assert.equal(doc.querySelectorAll('.ducksWidget').length, 1);
    await settle();
});

test('mountWidget removes the widget when the item is not ratable; no-op without a detail page', async () => {
    const { doc } = dom(markup.detailPage);
    const client = fakeClient({ get: () => Promise.reject(httpError(404)) });
    ducks.mountWidget(doc, client, ID, {});
    await settle();
    assert.equal(doc.querySelectorAll('.ducksWidget').length, 0);

    const hidden = dom(markup.detailPage.replace('class="page itemDetailPage"', 'class="page itemDetailPage hide"'));
    assert.equal(ducks.mountWidget(hidden.doc, client, ID, {}), null);
});
