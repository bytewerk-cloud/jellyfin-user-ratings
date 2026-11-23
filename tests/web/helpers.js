'use strict';
// Shared test utilities: jsdom pages that mimic jellyfin-web 12.4 markup, and a fake Ducks API client.
const path = require('node:path');
const { JSDOM } = require('jsdom');

const DUCKS_PATH = path.resolve(__dirname, '../../src/Jellyfin.Plugin.Ducks/Web/ducks.js');
const ducks = require(DUCKS_PATH);

const ID = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const ID2 = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';

function dom(body, url) {
    const jsdom = new JSDOM('<!doctype html><html><body>' + (body || '') + '</body></html>', {
        url: url || 'http://localhost:8096/web/#/home',
        pretendToBeVisual: true
    });
    const win = jsdom.window;
    // jsdom has no layout: give every element a 100px-wide box at x=0.
    win.HTMLElement.prototype.getBoundingClientRect = () => ({ left: 0, top: 0, width: 100, height: 20, right: 100, bottom: 20 });
    return { win, doc: win.document };
}

/** Markup copied from jellyfin-web 12.4 (trimmed). */
const markup = {
    detailPage: '<div id="itemDetailPage" class="page itemDetailPage"><div class="detailRibbon"><div class="infoWrapper"></div></div>'
        + '<div class="detailPagePrimaryContent"><div class="detailSection"></div></div></div>',
    card: (id, variant) => variant === 'legacy'
        ? '<div class="card" data-id="' + id + '"><div class="cardBox"><a class="cardImageContainer"></a></div></div>'
        : variant === 'bare'
            ? '<div class="card" data-id="' + id + '"></div>'
            : '<div class="card" data-id="' + id + '"><div class="cardBox"><div class="cardScalable"><div class="cardImageContainer"></div></div></div></div>',
    appBar: '<div class="MuiToolbar-root"><div class="MuiStack-root">'
        + '<a class="MuiButton-root MuiButton-textInherit" href="#/home?tab=1">Favourites</a>'
        + '<a class="MuiButton-root MuiButton-textPrimary MuiButton-colorPrimary" href="#/movies">Movies</a></div></div>',
    drawer: '<div class="MuiDrawer-paper"><ul><li class="MuiListItem-root"><a class="Mui-selected" href="#/home">Home</a></li>'
        + '<li class="MuiListItem-root"><a class="Mui-selected" href="#/home?tab=1"><div class="MuiListItemIcon-root"><svg></svg></div>'
        + '<div><span class="MuiListItemText-primary">Favourites</span></div></a></li></ul></div>',
    legacyHeader: '<div class="skinHeader"><div class="headerRight"><button class="headerSearchButton"></button></div></div>',
    fallbackPage: '<div id="fallbackPage" class="page"><div class="padded-left"><h1>Page not found</h1></div></div>'
};

function item(overrides) {
    return Object.assign({ ItemId: ID, Mine: null, Aggregate: null, MaxTitleLength: 100, MaxBodyLength: 2000 }, overrides);
}

function mine(rating, title, body) {
    return { Rating: rating, Title: title || null, Body: body || null, CreatedUtc: '2026-01-01T00:00:00Z', UpdatedUtc: '2026-01-01T00:00:00Z', IsEdited: false };
}

/** A fake ApiClient error: jellyfin-web rejects with the fetch Response. */
function httpError(status, body) {
    return { status, json: () => (body === undefined ? Promise.reject(new Error('no body')) : Promise.resolve(body)) };
}

/** Fake Ducks client; each method records calls and returns the queued result (or a default). */
function fakeClient(overrides) {
    const calls = [];
    const record = (name, fallback) => function () {
        calls.push([name].concat(Array.from(arguments)));
        const impl = overrides && overrides[name];
        return impl ? impl.apply(null, arguments) : fallback.apply(null, arguments);
    };
    return {
        calls,
        get: record('get', () => Promise.resolve(item())),
        save: record('save', (id, body) => Promise.resolve(item({ ItemId: id, Mine: mine(body.Rating, body.Title, body.Body), Aggregate: { Average: body.Rating, Count: 1 } }))),
        remove: record('remove', (id) => Promise.resolve(item({ ItemId: id }))),
        aggregates: record('aggregates', () => Promise.resolve({})),
        feed: record('feed', () => Promise.resolve({ Items: [] })),
        imageUrl: (e) => (e.ImageItemId ? '/img/' + e.ImageItemId : null),
        avatarUrl: (e) => (e.UserHasImage ? '/avatar/' + e.UserId : null)
    };
}

const tick = (ms) => new Promise((r) => setTimeout(r, ms || 0));

/** Let pending promise chains settle. */
async function settle() {
    for (let i = 0; i < 5; i++) {
        await tick();
    }
}

module.exports = { ducks, DUCKS_PATH, ID, ID2, dom, markup, item, mine, httpError, fakeClient, tick, settle };
