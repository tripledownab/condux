<?php

declare(strict_types=1);

namespace Condux;

/**
 * Delivers a serialized event to the relay, retrying transient failures (429 / 5xx / network) with
 * capped exponential backoff, honoring Retry-After on a 429. Never throws — returns a SendResult.
 */
final class EventTransport
{
    private const BASE_BACKOFF_MS = 200.0;
    private const MAX_BACKOFF_MS = 30_000.0;

    /** @var callable(string,array<string,string>,string):array{0:int,1:array<string,string>} */
    private $transport;
    /** @var callable(float):void */
    private $sleep;

    /**
     * @param callable(string,array<string,string>,string):array{0:int,1:array<string,string>}|null $transport
     * @param callable(float):void|null $sleep
     */
    public function __construct(
        private readonly string $storeUrl,
        private readonly string $publicKey,
        private readonly int $maxRetries,
        ?callable $transport,
        ?callable $sleep,
    ) {
        $this->transport = $transport ?? $this->curlSend(...);
        $this->sleep = $sleep ?? static fn (float $seconds) => usleep((int) ($seconds * 1_000_000));
    }

    public function send(string $body): SendResult
    {
        $headers = ['Content-Type' => 'application/json', 'x-condux-auth' => $this->publicKey];
        $lastStatus = null;
        $lastError = null;

        for ($attempt = 0; $attempt <= $this->maxRetries; ++$attempt) {
            $status = null;
            $responseHeaders = [];
            try {
                [$status, $responseHeaders] = ($this->transport)($this->storeUrl, $headers, $body);
            } catch (\Throwable $e) {
                // A genuine network failure — retry; a failed send must never crash the caller.
                $lastError = $e->getMessage() !== '' ? $e->getMessage() : $e::class;
            }

            if ($status !== null) {
                $lastStatus = $status;
                $lastError = null;
                if ($status >= 200 && $status < 300) {
                    return new SendResult(true, $attempt + 1, $status);
                }
                if (!self::retriable($status)) {
                    return new SendResult(false, $attempt + 1, $status);
                }
            }

            if ($attempt === $this->maxRetries) {
                break;
            }
            ($this->sleep)(self::backoffSeconds($attempt, $status, $responseHeaders));
        }

        return new SendResult(false, $this->maxRetries + 1, $lastStatus, $lastError);
    }

    /** 429 (rate limited) and 5xx are worth retrying; other 4xx (bad DSN / payload) are not. */
    private static function retriable(int $status): bool
    {
        return $status === 429 || $status >= 500;
    }

    /**
     * Honor Retry-After (seconds) on a 429; otherwise capped exponential backoff. Returns seconds.
     *
     * @param array<string,string> $headers
     */
    private static function backoffSeconds(int $attempt, ?int $status, array $headers): float
    {
        $wantedMs = self::BASE_BACKOFF_MS * (2 ** $attempt);
        if ($status === 429) {
            $retryAfter = self::header($headers, 'retry-after');
            if ($retryAfter !== null && trim($retryAfter) !== '' && is_numeric(trim($retryAfter))) {
                $wantedMs = max(0.0, (float) trim($retryAfter)) * 1000.0;
            }
        }

        return min($wantedMs, self::MAX_BACKOFF_MS) / 1000.0;
    }

    /** @param array<string,string> $headers */
    private static function header(array $headers, string $name): ?string
    {
        foreach ($headers as $key => $value) {
            if (strtolower($key) === $name) {
                return $value;
            }
        }

        return null;
    }

    /**
     * @return array{0:int,1:array<string,string>}
     *
     * @throws \RuntimeException on a network failure (caught by the retry loop)
     */
    private function curlSend(string $url, array $headers, string $body): array
    {
        $curl = curl_init($url);
        $responseHeaders = [];
        curl_setopt_array($curl, [
            CURLOPT_POST => true,
            CURLOPT_POSTFIELDS => $body,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_TIMEOUT => 30,
            CURLOPT_HTTPHEADER => array_map(
                static fn (string $key, string $value): string => "$key: $value",
                array_keys($headers),
                array_values($headers),
            ),
            CURLOPT_HEADERFUNCTION => static function ($curl, string $line) use (&$responseHeaders): int {
                $parts = explode(':', $line, 2);
                if (count($parts) === 2) {
                    $responseHeaders[strtolower(trim($parts[0]))] = trim($parts[1]);
                }

                return strlen($line);
            },
        ]);

        $ok = curl_exec($curl);
        if ($ok === false) {
            $message = curl_error($curl);
            curl_close($curl);

            throw new \RuntimeException($message !== '' ? $message : 'curl transport error');
        }
        $status = (int) curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
        curl_close($curl);

        return [$status, $responseHeaders];
    }
}
