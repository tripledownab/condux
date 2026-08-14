<?php

declare(strict_types=1);

namespace Condux;

/**
 * Condux SDK for PHP — report errors to a Condux relay.
 *
 * Emits the Sentry "store" wire shape (event_id, timestamp, level, exception.values[]) so the relay
 * normalizes it exactly like an official Sentry SDK: swap the DSN and it works. Delivery is resilient
 * (429 / 5xx / network failures retry with backoff, honoring Retry-After) and never throws — a failed
 * send returns a SendResult you can inspect. The transport, sleep, and clock are injectable so backoff
 * is exercised with no real network or timers. Inspired by common SDK transports, implemented fresh.
 */
final class Client
{
    private static bool $warnedCaptureFailed = false;

    private EventTransport $transport;
    /** @var callable():float */
    private $clock;

    /**
     * @param callable(string,array<string,string>,string):array{0:int,1:array<string,string>}|null $transport
     * @param callable(float):void|null $sleep
     * @param callable():float|null $clock
     */
    public function __construct(
        string $dsn,
        private readonly ?string $environment = null,
        private readonly ?string $release = null,
        int $maxRetries = 3,
        ?callable $transport = null,
        ?callable $sleep = null,
        ?callable $clock = null,
    ) {
        $parsed = Dsn::parse($dsn);
        $this->transport = new EventTransport($parsed->storeUrl(), $parsed->publicKey, $maxRetries, $transport, $sleep);
        $this->clock = $clock ?? static fn (): float => microtime(true);
    }

    /**
     * Report an exception as an error-level event, with its stack trace. Never throws on delivery failure.
     * Pass $handled = false when reporting an uncaught exception (a framework adapter does this) so the
     * relay marks it unhandled.
     */
    public function captureException(\Throwable $error, bool $handled = true): SendResult
    {
        return $this->dispatch([
            'level' => Level::ERROR,
            'exception' => ['values' => [EventPayload::exception($error, $handled)]],
        ]);
    }

    /** Report a bare message event at the given level (default info). */
    public function captureMessage(string $message, string $level = Level::INFO): SendResult
    {
        return $this->dispatch(['level' => $level, 'message' => $message]);
    }

    /**
     * Builds the event and hands it to the transport. Reporting never throws: an error monitor that
     * throws turns a handled error into an unhandled one in exactly the code path where someone is
     * already dealing with a failure. Serialization is the realistic failure here (an exception message
     * carrying invalid UTF-8 makes json_encode throw), so it warns once and returns the failure.
     *
     * @param array<string,mixed> $fields
     */
    private function dispatch(array $fields): SendResult
    {
        try {
            $event = [
                'event_id' => bin2hex(random_bytes(16)), // 32 lowercase hex, the Sentry event_id shape
                'timestamp' => ($this->clock)(),         // epoch seconds, the store convention
                'platform' => 'php',
            ] + Scope::fields() + $fields;
            if ($this->environment !== null) {
                $event['environment'] = $this->environment;
            }
            if ($this->release !== null) {
                $event['release'] = $this->release;
            }

            return $this->transport->send(json_encode($event, JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR));
        } catch (\Throwable $e) {
            $reason = $e->getMessage() !== '' ? $e->getMessage() : $e::class;
            if (!self::$warnedCaptureFailed) {
                self::$warnedCaptureFailed = true;
                error_log("Condux: could not report an event ($reason); events are being dropped.");
            }

            return new SendResult(false, 0, null, $reason);
        }
    }
}
