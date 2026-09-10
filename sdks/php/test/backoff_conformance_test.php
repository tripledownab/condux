<?php

declare(strict_types=1);

// Drives sdks/conformance/backoff.tsv, the retry schedule every Condux SDK owes. The cases live in that
// file rather than here so the seven transports assert against one artifact instead of seven readings of
// one sentence in a comment. See the file for why it is data and not prose.

namespace Condux;

$fixture = __DIR__ . '/../../conformance/backoff.tsv';
$cases = [];
foreach (file($fixture, FILE_IGNORE_NEW_LINES) as $line) {
    $text = trim($line);
    if ($text === '' || str_starts_with($text, '#')) {
        continue;
    }

    [$attempt, $status, $retryAfter, $expectedMs] = explode("\t", $text);
    $cases[] = [
        (int) $attempt,
        (int) $status,
        match ($retryAfter) { '<none>' => null, '<empty>' => '', default => $retryAfter },
        (int) $expectedMs,
    ];
}

// A fixture that failed to load reads exactly like one where every case passed, so the count is asserted
// rather than assumed. A floor, so adding a case does not mean editing seven SDKs.
check(count($cases) >= 15, 'backoff.tsv loaded (' . count($cases) . ' cases)');

foreach ($cases as [$attempt, $status, $retryAfter, $expectedMs]) {
    $headers = $retryAfter === null ? [] : ['Retry-After' => $retryAfter];
    // One more retry than the attempt under test, so the sleep that follows it is recorded. The scripted
    // response repeats, so every attempt fails and the schedule runs to its end.
    [$client, $recorder] = build([['status' => $status, 'headers' => $headers]], maxRetries: $attempt + 1);

    $client->captureMessage('hi');

    $shown = $retryAfter === null ? 'no Retry-After' : "Retry-After '$retryAfter'";
    $context = "attempt $attempt, status $status, $shown";
    check(count($recorder->delays) === $attempt + 1, "$context: recorded one sleep per failed attempt");
    check((int) round($recorder->delays[$attempt] * 1000) === $expectedMs, "$context: waits {$expectedMs}ms");
}
