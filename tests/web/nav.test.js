'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ducks, dom, markup } = require('./helpers');

test('adds an app bar link styled like the inactive links, once', () => {
    const { doc } = dom(markup.appBar);
    assert.equal(ducks.ensureNavLinks(doc, false), 1);
    assert.equal(ducks.ensureNavLinks(doc, false), 0);
    const link = doc.querySelector('.ducksNavLink');
    assert.equal(link.getAttribute('href'), '#/ducks-reviews');
    assert.match(link.textContent, /Reviews/);
    assert.equal(link.parentElement.lastElementChild, link);
    assert.ok(!link.classList.contains('ducksNav-active'));
});

test('copying an active link turns primary colour classes into inherit', () => {
    const { doc } = dom(markup.appBar.replace('MuiButton-textInherit" href="#/home?tab=1"', 'MuiButton-textPrimary MuiButton-colorPrimary" href="#/home?tab=1"'));
    ducks.ensureNavLinks(doc, false);
    const cls = doc.querySelector('.ducksNavLink').className;
    assert.match(cls, /MuiButton-textInherit/);
    assert.match(cls, /MuiButton-colorInherit/);
    assert.doesNotMatch(cls, /Primary/);
});

test('adds a drawer entry after Favourites', () => {
    const { doc } = dom(markup.drawer);
    assert.equal(ducks.ensureNavLinks(doc, true), 1);
    assert.equal(ducks.ensureNavLinks(doc, true), 0);
    const li = doc.querySelector('.ducksDrawerLink');
    assert.equal(li.previousElementSibling.querySelector('a').getAttribute('href'), '#/home?tab=1');
    assert.equal(li.querySelector('a').getAttribute('href'), '#/ducks-reviews');
    assert.equal(li.querySelector('.MuiListItemText-primary').textContent, 'Reviews');
    assert.ok(!li.querySelector('a').classList.contains('Mui-selected'));
    assert.equal(li.querySelector('a').getAttribute('aria-current'), 'page');
});

test('legacy layout gets a header button; it is skipped when the app bar exists', () => {
    const legacy = dom(markup.legacyHeader);
    assert.equal(ducks.ensureNavLinks(legacy.doc, false), 1);
    assert.equal(ducks.ensureNavLinks(legacy.doc, false), 0);
    assert.equal(legacy.doc.querySelector('.headerRight').firstElementChild.className.includes('ducksHeaderButton'), true);

    const both = dom(markup.appBar + markup.legacyHeader);
    ducks.ensureNavLinks(both.doc, false);
    assert.equal(both.doc.querySelector('.ducksHeaderButton'), null);
});

test('legacy button added before the app bar rendered is removed once it appears', () => {
    const { doc } = dom(markup.legacyHeader);
    ducks.ensureNavLinks(doc, false);
    assert.ok(doc.querySelector('.ducksHeaderButton'));
    doc.body.insertAdjacentHTML('beforeend', markup.appBar);
    ducks.ensureNavLinks(doc, false);
    assert.equal(doc.querySelector('.ducksHeaderButton'), null);
    assert.ok(doc.querySelector('.ducksNavLink'));
});

test('active state toggles on and off', () => {
    const { doc } = dom(markup.appBar);
    ducks.ensureNavLinks(doc, true);
    const link = doc.querySelector('.ducksNavLink');
    assert.ok(link.classList.contains('ducksNav-active'));
    assert.equal(link.getAttribute('aria-current'), 'page');
    ducks.ensureNavLinks(doc, false);
    assert.ok(!link.classList.contains('ducksNav-active'));
    assert.equal(link.getAttribute('aria-current'), null);
});

test('no header at all: nothing to do', () => {
    const { doc } = dom('');
    assert.equal(ducks.ensureNavLinks(doc, false), 0);
});
