'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { ducks, httpError } = require('./helpers');

function fakeApiClient() {
    const requests = [];
    return {
        requests,
        getUrl: (path, params) => 'http://srv/' + path + (params ? '?' + new URLSearchParams(params).toString() : ''),
        ajax: (options) => { requests.push(options); return Promise.resolve({ ok: true }); }
    };
}

test('client builds JSON requests through ApiClient', async () => {
    const api = fakeApiClient();
    const client = ducks.createClient(api);
    await client.get('i1');
    await client.save('i1', { Rating: 2 });
    await client.remove('i1');
    await client.aggregates(['a', 'b']);
    await client.feed();
    await client.feed('123_4', 5);

    assert.deepEqual(api.requests.map((r) => r.type + ' ' + r.url), [
        'GET http://srv/Ducks/Items/i1',
        'PUT http://srv/Ducks/Items/i1',
        'DELETE http://srv/Ducks/Items/i1',
        'POST http://srv/Ducks/Aggregates',
        'GET http://srv/Ducks/Feed?limit=20',
        'GET http://srv/Ducks/Feed?limit=5&cursor=123_4'
    ]);
    assert.equal(api.requests[1].data, '{"Rating":2}');
    assert.equal(api.requests[1].contentType, 'application/json');
    assert.equal(api.requests[3].data, '{"ItemIds":["a","b"]}');
    assert.equal(api.requests[0].data, undefined);
    assert.ok(api.requests.every((r) => r.dataType === 'json'));
});

test('client image and avatar URLs', () => {
    const client = ducks.createClient(fakeApiClient());
    assert.equal(client.imageUrl({ ImageItemId: 'p', ImageTag: 't' }), 'http://srv/Items/p/Images/Primary?fillHeight=180&quality=90&tag=t');
    assert.equal(client.imageUrl({}), null);
    assert.equal(client.avatarUrl({ UserHasImage: true, UserId: 'u' }), 'http://srv/UserImage?userId=u&height=64');
    assert.equal(client.avatarUrl({ UserHasImage: false }), null);
});

test('errorMessage reads server error, falls back to status, or message', async () => {
    assert.equal(await ducks.errorMessage(httpError(400, { Error: 'Too long' })), 'Too long');
    assert.equal(await ducks.errorMessage(httpError(500, {})), 'Request failed (500)');
    assert.equal(await ducks.errorMessage(httpError(500, null)), 'Request failed (500)');
    assert.equal(await ducks.errorMessage(httpError(502)), 'Request failed (502)');
    assert.equal(await ducks.errorMessage(new Error('offline')), 'offline');
    assert.equal(await ducks.errorMessage(null), 'Request failed');
});
