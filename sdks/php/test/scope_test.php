<?php

declare(strict_types=1);

// The enrichment scope: what it puts on the wire, and (just as important) what it leaves off. The scope
// is static, so every case here starts and ends by clearing it — leaking would enrich, and so break, the
// wire assertions in the rest of the suite.

namespace Condux;

// 1. An unenriched event carries none of the four scope keys: enrichment is additive, never a reshape.
Scope::clear();
$event = captureWith(static fn (Client $client) => $client->captureMessage('plain', Level::INFO));
foreach (['user', 'tags', 'contexts', 'breadcrumbs'] as $key) {
    check(!array_key_exists($key, $event), "an unenriched event does not carry \"$key\"");
}

// 2. A fully enriched event, field by field, in the shape the relay's parser reads.
Scope::setUser(['id' => '42', 'email' => 'dev@example.test']);
Scope::setTag('plan', 'team');
Scope::setContext('device', ['model' => 'laptop']);
Scope::addBreadcrumb('/checkout', category: 'navigation', level: Level::INFO, data: ['step' => 2]);

$event = captureWith(static fn (Client $client) => $client->captureMessage('after enrichment', Level::INFO));
check(($event['user'] ?? null) === ['id' => '42', 'email' => 'dev@example.test'], 'the user rides the event');
check(($event['tags'] ?? null) === ['plan' => 'team'], 'tags ride the event');
check(($event['contexts'] ?? null) === ['device' => ['model' => 'laptop']], 'contexts ride the event');
// Breadcrumbs ride the Sentry {"values": [...]} envelope, not a bare array.
$crumbs = $event['breadcrumbs']['values'] ?? [];
check(count($crumbs) === 1, 'one breadcrumb was recorded');
check(($crumbs[0]['message'] ?? null) === '/checkout', 'the breadcrumb message rides the event');
check(($crumbs[0]['category'] ?? null) === 'navigation', 'the breadcrumb category rides the event');
check(($crumbs[0]['level'] ?? null) === 'info', 'the breadcrumb level rides the event');
check(($crumbs[0]['data'] ?? null) === ['step' => 2], 'the breadcrumb data rides the event');
check(is_float($crumbs[0]['timestamp'] ?? null) && $crumbs[0]['timestamp'] > 0, 'the breadcrumb timestamp is stamped for us in epoch seconds');
check(!isset($crumbs[0]['type']), 'an omitted breadcrumb field is left off the wire');
check($event['level'] === 'info' && $event['message'] === 'after enrichment', 'the capture fields still ride the enriched event');

// 3. The scope rides an exception capture too, not just messages.
$event = captureWith(static fn (Client $client) => $client->captureException(new \RuntimeException('boom')));
check(($event['tags'] ?? []) === ['plan' => 'team'], 'the scope rides an exception capture');
check($event['exception']['values'][0]['value'] === 'boom', 'the exception still rides the enriched event');

// 4. Null clears: each entry is removable, and the key leaves the wire with it.
Scope::setUser(null);
Scope::setTag('plan', null);
Scope::setContext('device', null);
$event = captureWith(static fn (Client $client) => $client->captureMessage('after clearing', Level::INFO));
foreach (['user', 'tags', 'contexts'] as $key) {
    check(!array_key_exists($key, $event), "clearing removes \"$key\" from the wire");
}
check(isset($event['breadcrumbs']), 'clearing one entry leaves the rest of the scope alone');

// 5. The breadcrumb trail is capped, dropping the oldest.
Scope::clear();
for ($step = 0; $step < 35; ++$step) {
    Scope::addBreadcrumb("step $step", data: ['i' => $step]);
}
$event = captureWith(static fn (Client $client) => $client->captureMessage('after many steps', Level::INFO));
$crumbs = $event['breadcrumbs']['values'] ?? [];
check(count($crumbs) === Scope::MAX_BREADCRUMBS, 'the trail is capped at MAX_BREADCRUMBS');
// Newest last, oldest dropped: the trail spans step 5 to step 34.
check(($crumbs[0]['data']['i'] ?? null) === 5, 'the oldest breadcrumbs are the ones dropped');
check(($crumbs[count($crumbs) - 1]['data']['i'] ?? null) === 34, 'the newest breadcrumb is last');

// 6. clear() resets everything at once.
Scope::setUser(['id' => '42']);
Scope::clear();
$event = captureWith(static fn (Client $client) => $client->captureMessage('after clear', Level::INFO));
foreach (['user', 'tags', 'contexts', 'breadcrumbs'] as $key) {
    check(!array_key_exists($key, $event), "clear() drops \"$key\"");
}
