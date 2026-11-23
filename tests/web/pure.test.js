'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ducks } = require('./helpers');

test('clampRating rounds to halves within 0..4', () => {
    assert.equal(ducks.clampRating(3.3), 3.5);
    assert.equal(ducks.clampRating(-2), 0);
    assert.equal(ducks.clampRating(9), 4);
    assert.equal(ducks.clampRating('2.5'), 2.5);
    assert.equal(ducks.clampRating(NaN), 0);
    assert.equal(ducks.clampRating(undefined), 0);
});

test('formatRating shows at most one decimal', () => {
    assert.equal(ducks.formatRating(3), '3');
    assert.equal(ducks.formatRating(3.25), '3.3');
    assert.equal(ducks.formatRating(2.75), '2.8');
    assert.equal(ducks.formatRating(0), '0');
});

test('duckStates maps a rating to full/half/empty ducks', () => {
    assert.deepEqual(ducks.duckStates(0), ['empty', 'empty', 'empty', 'empty']);
    assert.deepEqual(ducks.duckStates(2.5), ['full', 'full', 'half', 'empty']);
    assert.deepEqual(ducks.duckStates(4), ['full', 'full', 'full', 'full']);
    assert.deepEqual(ducks.duckStates(0.5), ['half', 'empty', 'empty', 'empty']);
});

test('ratingFromPointer uses half-duck steps and never goes below 0.5', () => {
    assert.equal(ducks.ratingFromPointer(0, 0, 100), 0.5);
    assert.equal(ducks.ratingFromPointer(10, 0, 100), 0.5);
    assert.equal(ducks.ratingFromPointer(13, 0, 100), 1);
    assert.equal(ducks.ratingFromPointer(60, 0, 100), 2.5);
    assert.equal(ducks.ratingFromPointer(100, 0, 100), 4);
    assert.equal(ducks.ratingFromPointer(500, 0, 100), 4);
    assert.equal(ducks.ratingFromPointer(150, 100, 100), 2);
    assert.equal(ducks.ratingFromPointer(10, 0, 0), 0.5);
});

test('escapeHtml escapes markup and handles null', () => {
    assert.equal(ducks.escapeHtml('<a href="x">\'&'), '&lt;a href=&quot;x&quot;&gt;&#39;&amp;');
    assert.equal(ducks.escapeHtml(null), '');
    assert.equal(ducks.escapeHtml(undefined), '');
    assert.equal(ducks.escapeHtml(5), '5');
});

test('itemIdFromHash only matches details routes', () => {
    const id = 'ABCDEFabcdef0123456789abcdef0123';
    assert.equal(ducks.itemIdFromHash('#/details?id=' + id + '&serverId=1'), id.toLowerCase());
    assert.equal(ducks.itemIdFromHash('#/details?serverId=1&id=' + id), id.toLowerCase());
    assert.equal(ducks.itemIdFromHash('#/movies?id=' + id), null);
    assert.equal(ducks.itemIdFromHash('#/details?id=short'), null);
    assert.equal(ducks.itemIdFromHash('#/details?id=' + id + 'zz'), null);
    assert.equal(ducks.itemIdFromHash(undefined), null);
});

test('isReviewsRoute ignores query string', () => {
    assert.equal(ducks.isReviewsRoute('#/ducks-reviews'), true);
    assert.equal(ducks.isReviewsRoute('#/ducks-reviews?x=1'), true);
    assert.equal(ducks.isReviewsRoute('#/home'), false);
    assert.equal(ducks.isReviewsRoute(null), false);
});

test('relativeTime buckets', () => {
    const now = Date.parse('2026-06-01T12:00:00Z');
    const ago = (s) => new Date(now - s * 1000).toISOString();
    assert.equal(ducks.relativeTime(ago(5), now), 'just now');
    assert.equal(ducks.relativeTime(ago(60), now), '1 minute ago');
    assert.equal(ducks.relativeTime(ago(150), now), '2 minutes ago');
    assert.equal(ducks.relativeTime(ago(3600), now), '1 hour ago');
    assert.equal(ducks.relativeTime(ago(5 * 3600), now), '5 hours ago');
    assert.equal(ducks.relativeTime(ago(86400), now), '1 day ago');
    assert.equal(ducks.relativeTime(ago(3 * 86400), now), '3 days ago');
    assert.equal(ducks.relativeTime(ago(40 * 86400), now), new Date(now - 40 * 86400000).toLocaleDateString());
    assert.equal(ducks.relativeTime(new Date(now + 5000).toISOString(), now), 'just now');
    assert.equal(ducks.relativeTime(new Date().toISOString()), 'just now');
});

test('ducksMarkup renders four ducks with accessible label', () => {
    const html = ducks.ducksMarkup(2.5, 'ducks-small');
    assert.match(html, /class="ducks ducks-small"/);
    assert.match(html, /aria-label="2.5 of 4 ducks"/);
    assert.equal((html.match(/class="duck duck-/g) || []).length, 4);
    assert.match(html, /duck-full.*duck-full.*duck-half.*duck-empty/);
    assert.match(ducks.ducksMarkup(1), /class="ducks"/);
});

test('badgeMarkup shows formatted average and count', () => {
    const html = ducks.badgeMarkup({ Average: 3.25, Count: 1 });
    assert.match(html, /<span>3.3<\/span>/);
    assert.match(html, /title="3.3 ducks from 1 rating"/);
    assert.match(ducks.badgeMarkup({ Average: 2, Count: 4 }), /from 4 ratings/);
});

test('aggregateText', () => {
    assert.equal(ducks.aggregateText(null), 'No ratings yet');
    assert.equal(ducks.aggregateText({ Average: 2.75, Count: 2 }), '2.8 average · 2 ratings');
});

test('feedEntryMarkup escapes user content and links to the item', () => {
    const now = Date.parse('2026-06-01T12:00:00Z');
    const entry = {
        Id: 7, UserId: 'u1', UserName: '<bob>', UserHasImage: true, ItemId: 'i1', ItemName: 'Film <x>', ItemType: 'Movie',
        ProductionYear: 2020, ImageItemId: 'p1', Rating: 3.5, Title: 'T<i>', Body: 'line1\nline2 <script>',
        CreatedUtc: '2026-06-01T11:00:00Z', UpdatedUtc: '2026-06-01T11:30:00Z', IsEdited: true
    };
    const urls = { image: (e) => '/img/' + e.ImageItemId, avatar: (e) => '/av/' + e.UserId };
    const html = ducks.feedEntryMarkup(entry, urls, now);
    assert.match(html, /href="#\/details\?id=i1"/);
    assert.match(html, /<img loading="lazy" alt="" src="\/img\/p1">/);
    assert.match(html, /class="ducksAvatar" alt="" src="\/av\/u1"/);
    assert.match(html, /&lt;bob&gt;/);
    assert.match(html, /Film &lt;x&gt; \(2020\)/);
    assert.match(html, /T&lt;i&gt;/);
    assert.match(html, /line2 &lt;script&gt;/);
    assert.match(html, /1 hour ago/);
    assert.match(html, /class="ducksEdited"/);
    assert.doesNotMatch(html, /<script>/);
});

test('feedEntryMarkup without images, text or edits', () => {
    const entry = { Id: 1, UserName: '', ItemId: 'e1', ItemName: 'Pilot', ItemType: 'Episode', SeriesName: 'Show', Rating: 0, CreatedUtc: '2026-06-01T11:00:00Z', IsEdited: false };
    const html = ducks.feedEntryMarkup(entry, { image: () => null, avatar: () => null });
    assert.match(html, /ducksFeedPoster-placeholder/);
    assert.match(html, /ducksAvatar-initial">\?</);
    assert.match(html, /Show · Pilot/);
    assert.doesNotMatch(html, /ducksReviewTitle|ducksReviewBody|ducksEdited/);
});

test('feedEntryMarkup item labels: no duplicate year, series name on series itself, no year', () => {
    const base = { Id: 1, UserName: 'a', ItemId: 'x', Rating: 1, CreatedUtc: '2026-06-01T11:00:00Z' };
    const urls = { image: () => null, avatar: () => null };
    assert.match(ducks.feedEntryMarkup(Object.assign({}, base, { ItemName: 'Big Duck (2021)', ProductionYear: 2021 }), urls), /Big Duck \(2021\)<\/a>/);
    assert.match(ducks.feedEntryMarkup(Object.assign({}, base, { ItemName: 'Show', ItemType: 'Series', SeriesName: 'Show', ProductionYear: 2001 }), urls), />Show \(2001\)<\/a>/);
    assert.match(ducks.feedEntryMarkup(Object.assign({}, base, { ItemName: 'Plain' }), urls), />Plain<\/a>/);
});
