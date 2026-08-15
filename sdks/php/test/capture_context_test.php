<?php

declare(strict_types=1);

// Per-event enrichment: the request an error happened during, and tags scoped to that one event.
//
// Classic PHP is share-nothing, so the static scope is already per request there. Under a worker runtime
// (Swoole, RoadRunner, FrankenPHP worker mode) the process is reused and static state carries over, so
// passing request detail per event is the habit that is correct in both.

namespace Condux;

Scope::clear();

// 1. Absence, not an empty object: an event with no context keeps its exact previous wire shape.
$event = captureWith(static fn (Client $client) => $client->captureMessage('plain', Level::INFO));
check(!array_key_exists('request', $event), 'an event with no context carries no request key');

// 2. The request rides the wire in the shape the relay's parser reads.
$event = captureWith(static fn (Client $client) => $client->captureException(
    new \RuntimeException('boom'),
    false,
    new CaptureContext(['url' => '/checkout', 'method' => 'POST', 'query_string' => 'step=2'])
));
check(
    ($event['request'] ?? null) === ['url' => '/checkout', 'method' => 'POST', 'query_string' => 'step=2'],
    'the request rides the event in the store shape'
);

// 3. forRequest splits a target that carries its query string inline, the way the wire keeps them apart.
$context = CaptureContext::forRequest('/api/sync?since=2026', 'GET');
check(
    $context->request === ['url' => '/api/sync', 'method' => 'GET', 'query_string' => 'since=2026'],
    'forRequest splits the query string off the url'
);

// 4. Blank parts are dropped rather than shipped as empty strings the relay would have to interpret.
$context = CaptureContext::forRequest('/plain', null);
check($context->request === ['url' => '/plain'], 'blank parts are omitted');

// 5. Per-event tags merge OVER the ambient ones: a request tag must not drop the deployment-wide ones.
Scope::setTag('service', 'billing');
Scope::setTag('plan', 'business');
$event = captureWith(static fn (Client $client) => $client->captureException(
    new \RuntimeException('boom'),
    false,
    new CaptureContext([], ['route' => '/checkout/{id}', 'plan' => 'trial'])
));
check(
    ($event['tags'] ?? null) === ['service' => 'billing', 'plan' => 'trial', 'route' => '/checkout/{id}'],
    'per-event tags merge over the ambient ones'
);

// 6. And they do not leak into the next event, which is the concurrency hazard in miniature.
$event = captureWith(static fn (Client $client) => $client->captureMessage('after', Level::INFO));
check(($event['tags'] ?? null) === ['service' => 'billing', 'plan' => 'business'], 'per-event tags do not persist');

Scope::clear();
