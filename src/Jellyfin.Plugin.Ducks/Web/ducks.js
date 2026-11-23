/*
 * Ducks — ratings and reviews for jellyfin-web.
 * Injected into index.html by the Ducks server plugin (via File Transformation).
 *
 * Works by observing the DOM: it never patches jellyfin-web files, so web updates don't break storage,
 * and if markup changes the worst case is a missing widget, never lost data.
 *
 * In the browser this file boots itself. Under Node (unit tests) it only exports its functions.
 */
(function (root, factory) {
    'use strict';
    var ducks = factory();
    if (typeof module === 'object' && module.exports) {
        module.exports = ducks;
    }

    if (root) {
        root.Ducks = ducks;
        ducks.boot(root);
    }
})(typeof window === 'undefined' ? null : window, function () {
    'use strict';

    var MAX_DUCKS = 4;
    var REVIEWS_ROUTE = '#/ducks-reviews';
    var CACHE_TTL_MS = 5 * 60 * 1000;
    var FEED_PAGE_SIZE = 20;

    // Rubber duck silhouette, 24x24, facing left.
    var DUCK_PATH = 'M9 2.6a4.6 4.6 0 0 0-4.5 3.7L1 7.7l3.6 1.5c.3.9.8 1.6 1.5 2.2C3.6 12.4 2 14.5 2 17c0 3 2.7 5 6.5 5h5.8'
        + 'c4.8 0 8.7-3.6 8.7-8.6 0-1-.2-2-.5-2.9l-2.7 1.4c-1.1.5-2.3.5-3.3 0-1-.5-1.6-1.4-1.8-2.4l-.3-1.3'
        + 'A4.6 4.6 0 0 0 9 2.6zm.6 3a1 1 0 1 1 0 2 1 1 0 0 1 0-2z';

    // ---------------------------------------------------------------- pure helpers

    function clampRating(value) {
        var n = Number(value);
        if (!isFinite(n)) {
            return 0;
        }

        return Math.min(MAX_DUCKS, Math.max(0, Math.round(n * 2) / 2));
    }

    /** Average shown with at most one decimal: 3 → "3", 3.25 → "3.3". */
    function formatRating(value) {
        var r = Math.round(Number(value) * 10) / 10;
        return r % 1 === 0 ? String(r) : r.toFixed(1);
    }

    /** One of 'full' | 'half' | 'empty' per duck, rounding the rating to the nearest half. */
    function duckStates(rating) {
        var r = clampRating(rating);
        var states = [];
        for (var i = 1; i <= MAX_DUCKS; i++) {
            states.push(r >= i ? 'full' : r >= i - 0.5 ? 'half' : 'empty');
        }

        return states;
    }

    /** Rating under the pointer: left half of a duck = .5, right half = whole. Never below 0.5. */
    function ratingFromPointer(clientX, left, width) {
        if (!(width > 0)) {
            return 0.5;
        }

        var ducks = ((clientX - left) / width) * MAX_DUCKS;
        return Math.min(MAX_DUCKS, Math.max(0.5, Math.ceil(ducks * 2) / 2));
    }

    function escapeHtml(value) {
        return String(value === null || value === undefined ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    /** Item id from a details route such as "#/details?id=abc…&serverId=…". */
    function itemIdFromHash(hash) {
        var path = String(hash || '').split('?')[0];
        if (path !== '#/details') {
            return null;
        }

        var match = /[?&]id=([0-9a-f]{32})(?:&|$)/i.exec(hash);
        return match ? match[1].toLowerCase() : null;
    }

    function isReviewsRoute(hash) {
        return String(hash || '').split('?')[0] === REVIEWS_ROUTE;
    }

    function pluralize(n, word) {
        return n + ' ' + word + (n === 1 ? '' : 's');
    }

    function relativeTime(iso, now) {
        var then = new Date(iso).getTime();
        var seconds = Math.max(0, Math.round(((now === undefined ? Date.now() : now) - then) / 1000));
        if (seconds < 60) {
            return 'just now';
        }

        var minutes = Math.floor(seconds / 60);
        if (minutes < 60) {
            return pluralize(minutes, 'minute') + ' ago';
        }

        var hours = Math.floor(minutes / 60);
        if (hours < 24) {
            return pluralize(hours, 'hour') + ' ago';
        }

        var days = Math.floor(hours / 24);
        if (days < 30) {
            return pluralize(days, 'day') + ' ago';
        }

        return new Date(then).toLocaleDateString();
    }

    function duckSvg(className) {
        return '<svg class="' + className + '" viewBox="0 0 24 24" aria-hidden="true" focusable="false">'
            + '<path fill-rule="evenodd" d="' + DUCK_PATH + '"/></svg>';
    }

    /** Four ducks, each a dim outline layer plus a fill layer clipped to full/half/none. */
    function ducksMarkup(rating, extraClass) {
        var r = clampRating(rating);
        var html = '<span class="ducks' + (extraClass ? ' ' + extraClass : '') + '" role="img" aria-label="'
            + formatRating(r) + ' of ' + MAX_DUCKS + ' ducks">';
        duckStates(r).forEach(function (state) {
            html += '<span class="duck duck-' + state + '">' + duckSvg('duck-bg') + duckSvg('duck-fill') + '</span>';
        });
        return html + '</span>';
    }

    function badgeMarkup(aggregate) {
        return '<div class="ducksBadge" title="' + escapeHtml(formatRating(aggregate.Average) + ' ducks from '
            + pluralize(aggregate.Count, 'rating')) + '">' + duckSvg('ducksBadge-icon')
            + '<span>' + formatRating(aggregate.Average) + '</span></div>';
    }

    function aggregateText(aggregate) {
        return aggregate
            ? formatRating(aggregate.Average) + ' average · ' + pluralize(aggregate.Count, 'rating')
            : 'No ratings yet';
    }

    function itemLabel(entry) {
        var name = escapeHtml(entry.ItemName);
        if (entry.SeriesName && entry.ItemType !== 'Series') {
            return escapeHtml(entry.SeriesName) + ' · ' + name;
        }

        var year = entry.ProductionYear ? ' (' + entry.ProductionYear + ')' : '';
        return year && name.slice(-year.length) !== year ? name + year : name;
    }

    /**
     * One feed entry. urls = { image(entry) → string|null, avatar(entry) → string|null }.
     */
    function feedEntryMarkup(entry, urls, now) {
        var image = urls.image(entry);
        var avatar = urls.avatar(entry);
        var link = '#/details?id=' + encodeURIComponent(entry.ItemId);
        var html = '<article class="ducksFeedEntry" data-id="' + escapeHtml(entry.Id) + '">';
        html += '<a class="ducksFeedPoster" href="' + link + '" aria-hidden="true" tabindex="-1">'
            + (image ? '<img loading="lazy" alt="" src="' + escapeHtml(image) + '">' : duckSvg('ducksFeedPoster-placeholder'))
            + '</a>';
        html += '<div class="ducksFeedBody">';
        html += '<div class="ducksFeedMeta">'
            + (avatar
                ? '<img class="ducksAvatar" alt="" src="' + escapeHtml(avatar) + '">'
                : '<span class="ducksAvatar ducksAvatar-initial">' + escapeHtml((entry.UserName || '?').charAt(0).toUpperCase()) + '</span>')
            + '<span class="ducksFeedUser">' + escapeHtml(entry.UserName) + '</span>'
            + '<span class="ducksFeedRated">rated</span>'
            + '<a class="ducksFeedItem" href="' + link + '">' + itemLabel(entry) + '</a>'
            + '</div>';
        html += '<div class="ducksFeedRating">' + ducksMarkup(entry.Rating) + '<span class="ducksFeedScore">'
            + formatRating(entry.Rating) + '</span>'
            + '<time datetime="' + escapeHtml(entry.CreatedUtc) + '" title="' + escapeHtml(new Date(entry.CreatedUtc).toLocaleString()) + '">'
            + relativeTime(entry.CreatedUtc, now) + '</time>'
            + (entry.IsEdited ? '<span class="ducksEdited" title="Edited ' + escapeHtml(new Date(entry.UpdatedUtc).toLocaleString()) + '">edited</span>' : '')
            + '</div>';
        if (entry.Title) {
            html += '<h3 class="ducksReviewTitle">' + escapeHtml(entry.Title) + '</h3>';
        }

        if (entry.Body) {
            html += '<p class="ducksReviewBody">' + escapeHtml(entry.Body) + '</p>';
        }

        return html + '</div></article>';
    }

    // ---------------------------------------------------------------- API client

    /** Wraps jellyfin-web's ApiClient so auth headers and base URL are handled by Jellyfin. */
    function createClient(apiClient) {
        function call(type, path, body) {
            var options = { type: type, url: apiClient.getUrl(path), dataType: 'json' };
            if (body !== undefined) {
                options.data = JSON.stringify(body);
                options.contentType = 'application/json';
            }

            return apiClient.ajax(options);
        }

        return {
            get: function (itemId) { return call('GET', 'Ducks/Items/' + itemId); },
            save: function (itemId, body) { return call('PUT', 'Ducks/Items/' + itemId, body); },
            remove: function (itemId) { return call('DELETE', 'Ducks/Items/' + itemId); },
            aggregates: function (ids) { return call('POST', 'Ducks/Aggregates', { ItemIds: ids }); },
            feed: function (cursor, limit) {
                return call('GET', 'Ducks/Feed?limit=' + (limit || FEED_PAGE_SIZE) + (cursor ? '&cursor=' + encodeURIComponent(cursor) : ''));
            },
            imageUrl: function (entry) {
                return entry.ImageItemId
                    ? apiClient.getUrl('Items/' + entry.ImageItemId + '/Images/Primary', { fillHeight: 180, quality: 90, tag: entry.ImageTag })
                    : null;
            },
            avatarUrl: function (entry) {
                return entry.UserHasImage ? apiClient.getUrl('UserImage', { userId: entry.UserId, height: 64 }) : null;
            }
        };
    }

    /** Turns a rejected ApiClient call (a fetch Response) into a readable message. */
    function errorMessage(error) {
        if (error && typeof error.json === 'function') {
            return error.json().then(function (body) {
                return (body && body.Error) || 'Request failed (' + error.status + ')';
            }, function () {
                return 'Request failed (' + error.status + ')';
            });
        }

        return Promise.resolve((error && error.message) || 'Request failed');
    }

    // ---------------------------------------------------------------- card badges

    /**
     * Adds the aggregate rating badge to every card with a data-id. Batches requests and caches results.
     */
    function createBadgeManager(client, options) {
        var opts = options || {};
        var now = opts.now || Date.now;
        var cache = new Map(); // id → { aggregate|null, at }
        var queue = new Set();
        var waiting = new Map(); // id → [cards]
        var timer = null;
        var delay = opts.delay === undefined ? 120 : opts.delay;

        function render(card, aggregate) {
            var host = card.querySelector('.cardScalable') || card.querySelector('.cardImageContainer');
            if (!host) {
                return;
            }

            var old = host.querySelector(':scope > .ducksBadge');
            if (old) {
                old.remove();
            }

            if (aggregate && aggregate.Count > 0) {
                host.insertAdjacentHTML('beforeend', badgeMarkup(aggregate));
            }
        }

        function flush() {
            timer = null;
            var ids = Array.from(queue);
            queue.clear();
            var chunks = [];
            for (var i = 0; i < ids.length; i += 200) {
                chunks.push(ids.slice(i, i + 200));
            }

            return Promise.all(chunks.map(function (chunk) {
                return client.aggregates(chunk).then(function (result) {
                    chunk.forEach(function (id) {
                        var aggregate = (result && result[id]) || null;
                        cache.set(id, { aggregate: aggregate, at: now() });
                        (waiting.get(id) || []).forEach(function (card) {
                            if (card.getAttribute('data-ducks-for') === id) {
                                render(card, aggregate);
                            }
                        });
                        waiting.delete(id);
                    });
                }, function () {
                    chunk.forEach(function (id) { waiting.delete(id); });
                });
            }));
        }

        function request(card, id) {
            if (!waiting.has(id)) {
                waiting.set(id, []);
            }

            waiting.get(id).push(card);
            queue.add(id);
            if (!timer) {
                timer = setTimeout(flush, delay);
            }
        }

        /** Badge every not-yet-handled card under root. Cards reused for another item are redone. */
        function scan(root) {
            var cards = root.querySelectorAll('.card[data-id]');
            for (var i = 0; i < cards.length; i++) {
                var card = cards[i];
                var id = card.getAttribute('data-id');
                if (card.getAttribute('data-ducks-for') === id) {
                    continue;
                }

                card.setAttribute('data-ducks-for', id);
                var hit = cache.get(id);
                if (hit && now() - hit.at < CACHE_TTL_MS) {
                    render(card, hit.aggregate);
                } else {
                    request(card, id);
                }
            }
        }

        /** After a rating changes: drop the cached value and redo the cards showing that item. */
        function invalidate(id, root) {
            cache.delete(id);
            var cards = root.querySelectorAll('.card[data-id="' + id + '"]');
            for (var i = 0; i < cards.length; i++) {
                cards[i].removeAttribute('data-ducks-for');
            }

            scan(root);
        }

        return { scan: scan, invalidate: invalidate, flush: flush, render: render, cache: cache };
    }

    // ---------------------------------------------------------------- detail page widget

    function widgetMarkup(state) {
        var data = state.data;
        var mine = data.Mine;
        var shown = state.preview !== null ? state.preview : mine ? mine.Rating : 0;
        var html = '<div class="ducksWidget-row">'
            + '<span class="ducksWidget-label">Your rating</span>'
            + '<button type="button" class="ducksZero' + (mine && mine.Rating === 0 ? ' ducksZero-active' : '') + '" title="0 ducks" aria-label="Rate 0 ducks">0</button>'
            + '<span class="ducksPicker" tabindex="0" role="slider" aria-label="Your rating" aria-valuemin="0" aria-valuemax="' + MAX_DUCKS
            + '" aria-valuenow="' + shown + '" aria-valuetext="' + (mine || state.preview !== null ? formatRating(shown) + ' ducks' : 'Not rated') + '">'
            + ducksMarkup(shown, mine || state.preview !== null ? '' : 'ducks-unrated') + '</span>'
            + '<span class="ducksWidget-mine">' + (mine ? formatRating(mine.Rating) : '–') + '</span>'
            + '</div>';
        html += '<div class="ducksWidget-row ducksWidget-aggregate">'
            + '<span class="ducksWidget-label">Everyone</span>'
            + (data.Aggregate ? ducksMarkup(data.Aggregate.Average, 'ducks-small') : '')
            + '<span class="ducksWidget-aggtext">' + escapeHtml(aggregateText(data.Aggregate)) + '</span>'
            + '</div>';

        if (state.editing) {
            html += '<form class="ducksForm">'
                + '<input type="text" class="ducksForm-title" maxlength="' + data.MaxTitleLength + '" placeholder="Review title (optional)" value="'
                + escapeHtml(state.draftTitle) + '">'
                + '<textarea class="ducksForm-body" rows="4" maxlength="' + data.MaxBodyLength + '" placeholder="What did you think? (optional)">'
                + escapeHtml(state.draftBody) + '</textarea>'
                + '<div class="ducksForm-actions">'
                + '<button type="submit" class="ducksButton ducksButton-primary">Save review</button>'
                + '<button type="button" class="ducksButton ducksCancel">Cancel</button>'
                + '</div></form>';
        } else {
            if (mine && (mine.Title || mine.Body)) {
                html += '<div class="ducksMyReview">'
                    + (mine.Title ? '<h3 class="ducksReviewTitle">' + escapeHtml(mine.Title) + '</h3>' : '')
                    + (mine.Body ? '<p class="ducksReviewBody">' + escapeHtml(mine.Body) + '</p>' : '')
                    + '</div>';
            }

            html += '<div class="ducksWidget-actions">'
                + '<button type="button" class="ducksButton ducksEdit">' + (mine && (mine.Title || mine.Body) ? 'Edit review' : 'Write a review') + '</button>'
                + (mine ? '<button type="button" class="ducksButton ducksRemove">Remove my rating</button>' : '')
                + '</div>';
        }

        if (state.message) {
            html += '<div class="ducksMessage" role="status">' + escapeHtml(state.message) + '</div>';
        }

        return html;
    }

    /**
     * Interactive rating/review block for one item. Mounted into the details page.
     * hooks.onChange(itemId) is called after every successful save/delete.
     */
    function createWidget(doc, client, itemId, hooks) {
        var el = doc.createElement('div');
        el.className = 'ducksWidget';
        el.setAttribute('data-item-id', itemId);
        var state = { data: null, preview: null, editing: false, draftTitle: '', draftBody: '', message: '', busy: false };

        function render() {
            if (!state.data) {
                el.innerHTML = '';
                return;
            }

            var focusPicker = doc.activeElement && doc.activeElement.classList && doc.activeElement.classList.contains('ducksPicker');
            el.innerHTML = widgetMarkup(state);
            if (focusPicker) {
                el.querySelector('.ducksPicker').focus();
            }
        }

        function apply(data) {
            state.data = data;
            state.preview = null;
            render();
        }

        function fail(error) {
            return errorMessage(error).then(function (text) {
                state.message = text;
                state.preview = null;
                render();
            });
        }

        function save(rating, title, body) {
            state.busy = true;
            state.message = '';
            return client.save(itemId, { Rating: rating, Title: title, Body: body }).then(function (data) {
                state.busy = false;
                state.editing = false;
                apply(data);
                hooks.onChange(itemId);
            }, function (error) {
                state.busy = false;
                return fail(error);
            });
        }

        function rate(rating) {
            var mine = state.data.Mine;
            return save(rating, mine ? mine.Title : null, mine ? mine.Body : null);
        }

        function picker() {
            return el.querySelector('.ducksPicker');
        }

        el.addEventListener('mousemove', function (e) {
            var p = picker();
            if (!p || !p.contains(e.target)) {
                return;
            }

            var rect = p.getBoundingClientRect();
            var value = ratingFromPointer(e.clientX, rect.left, rect.width);
            if (value !== state.preview) {
                state.preview = value;
                render();
            }
        });

        el.addEventListener('mouseleave', function () {
            if (state.preview !== null && !state.busy) {
                state.preview = null;
                render();
            }
        });

        el.addEventListener('click', function (e) {
            var target = e.target;
            var p = picker();
            if (p && p.contains(target)) {
                var rect = p.getBoundingClientRect();
                return rate(ratingFromPointer(e.clientX, rect.left, rect.width));
            }

            if (target.closest('.ducksZero')) {
                return rate(0);
            }

            if (target.closest('.ducksEdit')) {
                var mine = state.data.Mine;
                state.editing = true;
                state.draftTitle = mine && mine.Title ? mine.Title : '';
                state.draftBody = mine && mine.Body ? mine.Body : '';
                state.message = '';
                render();
                var title = el.querySelector('.ducksForm-title');
                title.focus();
                return undefined;
            }

            if (target.closest('.ducksCancel')) {
                state.editing = false;
                state.message = '';
                render();
                return undefined;
            }

            if (target.closest('.ducksRemove')) {
                if (!hooks.confirm('Remove your rating and review?')) {
                    return undefined;
                }

                return client.remove(itemId).then(function (data) {
                    apply(data);
                    hooks.onChange(itemId);
                }, fail);
            }

            return undefined;
        });

        el.addEventListener('keydown', function (e) {
            if (!e.target.classList || !e.target.classList.contains('ducksPicker')) {
                return;
            }

            var current = state.preview !== null ? state.preview : state.data.Mine ? state.data.Mine.Rating : 0;
            var next = null;
            if (e.key === 'ArrowRight' || e.key === 'ArrowUp') {
                next = clampRating(current + 0.5);
            } else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') {
                next = clampRating(current - 0.5);
            } else if (e.key === 'Home') {
                next = 0;
            } else if (e.key === 'End') {
                next = MAX_DUCKS;
            } else if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                rate(current);
                return;
            }

            if (next !== null) {
                e.preventDefault();
                state.preview = next;
                render();
            }
        });

        el.addEventListener('input', function (e) {
            if (e.target.classList.contains('ducksForm-title')) {
                state.draftTitle = e.target.value;
            } else if (e.target.classList.contains('ducksForm-body')) {
                state.draftBody = e.target.value;
            }
        });

        el.addEventListener('submit', function (e) {
            e.preventDefault();
            var mine = state.data.Mine;
            var rating = state.preview !== null ? state.preview : mine ? mine.Rating : null;
            if (rating === null) {
                state.message = 'Pick your ducks first, then save the review.';
                render();
                return undefined;
            }

            return save(rating, state.draftTitle, state.draftBody);
        });

        /** Loads data; resolves false if the item can't be rated (widget should be removed). */
        function load() {
            return client.get(itemId).then(function (data) {
                apply(data);
                return true;
            }, function () {
                return false;
            });
        }

        return { el: el, load: load, state: state, render: render };
    }

    // ---------------------------------------------------------------- navigation

    var NAV_ICON = '<svg class="ducksNavIcon" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path fill-rule="evenodd" d="' + DUCK_PATH + '"/></svg>';

    /** Adds the "Reviews" link to the app bar (desktop) and the navigation drawer (narrow screens). */
    function ensureNavLinks(doc, active) {
        var added = 0;

        var favourites = doc.querySelector('.MuiToolbar-root a[href="#/home?tab=1"]');
        var bar = favourites ? favourites.parentElement : null;
        if (bar && !bar.querySelector('.ducksNavLink')) {
            var link = doc.createElement('a');
            link.className = favourites.className.replace(/MuiButton-(text|color)Primary/g, 'MuiButton-$1Inherit') + ' ducksNavLink';
            link.href = REVIEWS_ROUTE;
            link.innerHTML = '<span class="MuiButton-icon MuiButton-startIcon MuiButton-iconSizeMedium">' + NAV_ICON + '</span>Reviews';
            bar.appendChild(link);
            added++;
        }

        var drawerFavourites = doc.querySelector('.MuiDrawer-paper a[href="#/home?tab=1"]');
        var drawerItem = drawerFavourites ? drawerFavourites.closest('li') : null;
        if (drawerItem && !drawerItem.parentElement.querySelector('.ducksDrawerLink')) {
            var li = drawerItem.cloneNode(true);
            li.classList.add('ducksDrawerLink');
            var a = li.querySelector('a');
            a.setAttribute('href', REVIEWS_ROUTE);
            a.classList.remove('Mui-selected');
            li.querySelector('.MuiListItemIcon-root').innerHTML = NAV_ICON;
            li.querySelector('.MuiListItemText-primary').textContent = 'Reviews';
            drawerItem.parentNode.insertBefore(li, drawerItem.nextSibling);
            added++;
        }

        // Legacy layout header (no React app bar). The legacy header can exist briefly before the
        // app bar renders, so drop its button once the app bar is there.
        var legacy = doc.querySelector('.skinHeader .headerRight');
        var legacyButton = legacy ? legacy.querySelector('.ducksHeaderButton') : null;
        if (bar && legacyButton) {
            legacyButton.remove();
        }

        if (!bar && legacy && !legacyButton) {
            legacy.insertAdjacentHTML('afterbegin', '<a href="' + REVIEWS_ROUTE + '" class="headerButton headerButtonRight ducksHeaderButton" title="Reviews">'
                + NAV_ICON + '<span class="ducksHeaderButton-text">Reviews</span></a>');
            added++;
        }

        doc.querySelectorAll('.ducksNavLink, .ducksDrawerLink a, .ducksHeaderButton').forEach(function (node) {
            node.classList.toggle('ducksNav-active', !!active);
            if (active) {
                node.setAttribute('aria-current', 'page');
            } else {
                node.removeAttribute('aria-current');
            }
        });

        return added;
    }

    // ---------------------------------------------------------------- reviews page

    /**
     * Feed of all reviews, rendered into the page jellyfin-web shows for unknown routes (#fallbackPage).
     */
    function createReviewsPage(doc, client, options) {
        var opts = options || {};
        var el = doc.createElement('div');
        el.className = 'ducksReviewsPage padded-left padded-right';
        el.innerHTML = '<h1 class="ducksReviewsPage-title">Reviews</h1>'
            + '<p class="ducksReviewsPage-sub">Every rating and review, newest first.</p>'
            + '<div class="ducksFeed" aria-live="polite"></div>'
            + '<div class="ducksFeedFooter"><button type="button" class="ducksButton ducksMore">Load more</button></div>';
        var list = el.querySelector('.ducksFeed');
        var more = el.querySelector('.ducksMore');
        var cursor = null;
        var loading = false;
        var done = false;

        function loadMore() {
            if (loading || done) {
                return Promise.resolve();
            }

            loading = true;
            more.disabled = true;
            more.textContent = 'Loading…';
            return client.feed(cursor, FEED_PAGE_SIZE).then(function (page) {
                var items = page.Items || [];
                var urls = { image: client.imageUrl, avatar: client.avatarUrl };
                list.insertAdjacentHTML('beforeend', items.map(function (entry) {
                    return feedEntryMarkup(entry, urls, opts.now ? opts.now() : undefined);
                }).join(''));
                cursor = page.Next || null;
                done = !cursor;
                if (done && !list.children.length) {
                    list.innerHTML = '<p class="ducksEmpty">No ratings yet. Open any movie or show and give it some ducks.</p>';
                }
            }, function () {
                list.insertAdjacentHTML('beforeend', '<p class="ducksMessage">Could not load reviews. Try again.</p>');
            }).then(function () {
                loading = false;
                more.disabled = false;
                more.textContent = 'Load more';
                more.hidden = done;
            });
        }

        more.addEventListener('click', loadMore);

        var observer = null;
        if (typeof IntersectionObserver === 'function') {
            observer = new IntersectionObserver(function (entries) {
                if (entries.some(function (e) { return e.isIntersecting; })) {
                    loadMore();
                }
            }, { rootMargin: '400px' });
            observer.observe(more);
        }

        return { el: el, loadMore: loadMore, observer: observer };
    }

    function mountReviewsPage(doc, client, options) {
        var page = doc.querySelector('#fallbackPage:not(.hide)');
        if (!page || page.querySelector('.ducksReviewsPage')) {
            return null;
        }

        Array.prototype.forEach.call(page.children, function (child) {
            child.hidden = true;
        });
        var view = createReviewsPage(doc, client, options);
        page.appendChild(view.el);
        page.classList.add('ducksReviewsHost');
        doc.title = 'Reviews';
        view.loadMore();
        return view;
    }

    // ---------------------------------------------------------------- detail page mounting

    function mountWidget(doc, client, itemId, hooks) {
        var page = doc.querySelector('.itemDetailPage:not(.hide)');
        // Top of the content area below the ribbon: the ribbon itself has a fixed height.
        var anchor = page ? page.querySelector('.detailPagePrimaryContent') : null;
        if (!anchor) {
            return null;
        }

        var existing = anchor.querySelector('.ducksWidget');
        if (existing && existing.getAttribute('data-item-id') === itemId) {
            return null;
        }

        if (existing) {
            existing.remove();
        }

        var widget = createWidget(doc, client, itemId, hooks);
        anchor.insertBefore(widget.el, anchor.firstChild);
        widget.load().then(function (ok) {
            if (!ok) {
                widget.el.remove();
            }
        });
        return widget;
    }

    // ---------------------------------------------------------------- boot

    /** Starts observing the page. Returns a tick function (useful for tests). */
    function start(win, apiClient, options) {
        var doc = win.document;
        var client = createClient(apiClient);
        var badges = createBadgeManager(client, options);
        var scheduled = false;

        var hooks = {
            onChange: function (itemId) { badges.invalidate(itemId, doc); },
            confirm: function (text) { return win.confirm(text); }
        };

        function tick() {
            scheduled = false;
            var hash = win.location.hash;
            var reviews = isReviewsRoute(hash);
            ensureNavLinks(doc, reviews);
            if (reviews) {
                mountReviewsPage(doc, client, options);
            }

            var itemId = itemIdFromHash(hash);
            if (itemId) {
                mountWidget(doc, client, itemId, hooks);
            }

            badges.scan(doc);
        }

        function schedule() {
            if (!scheduled) {
                scheduled = true;
                win.setTimeout(tick, 60);
            }
        }

        new win.MutationObserver(schedule).observe(doc.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'data-id'] });
        win.addEventListener('hashchange', schedule);
        tick();
        return { tick: tick, badges: badges, client: client };
    }

    /** Waits for jellyfin-web's global ApiClient, then starts. */
    function boot(win) {
        var tries = 0;
        (function wait() {
            if (win.ApiClient && win.document.body) {
                start(win, win.ApiClient);
            } else if (tries++ < 240) {
                win.setTimeout(wait, 250);
            }
        })();
    }

    return {
        MAX_DUCKS: MAX_DUCKS,
        REVIEWS_ROUTE: REVIEWS_ROUTE,
        clampRating: clampRating,
        formatRating: formatRating,
        duckStates: duckStates,
        ratingFromPointer: ratingFromPointer,
        escapeHtml: escapeHtml,
        itemIdFromHash: itemIdFromHash,
        isReviewsRoute: isReviewsRoute,
        relativeTime: relativeTime,
        ducksMarkup: ducksMarkup,
        badgeMarkup: badgeMarkup,
        aggregateText: aggregateText,
        feedEntryMarkup: feedEntryMarkup,
        widgetMarkup: widgetMarkup,
        createClient: createClient,
        errorMessage: errorMessage,
        createBadgeManager: createBadgeManager,
        createWidget: createWidget,
        ensureNavLinks: ensureNavLinks,
        createReviewsPage: createReviewsPage,
        mountReviewsPage: mountReviewsPage,
        mountWidget: mountWidget,
        start: start,
        boot: boot
    };
});
