<?php

declare(strict_types=1);

namespace Condux;

/**
 * Per-event enrichment, passed at the capture call rather than set on the {@see Scope}: the request an
 * error happened during, and tags scoped to that one event.
 *
 * Classic PHP is share-nothing, so a static scope is already per request there. That stops being true
 * under a worker runtime (Swoole, RoadRunner, FrankenPHP worker mode), where the process is reused
 * across requests and static state carries over. Passing request detail per event is correct in both,
 * so it is the safe habit regardless of how the app is served.
 *
 * Deliberately no headers: they carry Cookie and Authorization, and while the relay scrubs sensitive
 * keys at ingest, not sending credentials at all is the stronger guarantee.
 */
final class CaptureContext
{
    /** @var array<string,string> */
    public readonly array $request;

    /** @var array<string,string> */
    public readonly array $tags;

    /**
     * @param array<string,string> $request url / method / query_string, in the shape the relay parses
     * @param array<string,string> $tags    merged over the ambient tags
     */
    public function __construct(array $request = [], array $tags = [])
    {
        // Blank parts are dropped, so a partially known request does not ship empty strings the relay
        // would then have to interpret.
        $this->request = array_filter($request, static fn ($value) => $value !== null && $value !== '');
        $this->tags = array_filter($tags, static fn ($value) => $value !== null && $value !== '');
    }

    /**
     * Build from a URL that may carry its query string inline, splitting the two the way the wire shape
     * keeps them apart.
     */
    public static function forRequest(?string $target, ?string $method): self
    {
        [$path, $queryString] = array_pad(explode('?', $target ?? '', 2), 2, null);

        return new self([
            'url' => $path ?? '',
            'method' => $method ?? '',
            'query_string' => $queryString ?? '',
        ]);
    }
}
