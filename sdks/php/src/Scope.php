<?php

declare(strict_types=1);

namespace Condux;

/**
 * Ambient event enrichment: who the user is, which tags and contexts apply, and the breadcrumb trail
 * leading up to an error. Set once (or as the app's state changes) and every subsequent event carries
 * it — the first triage questions ("which customer, which plan, what did they do last") answered
 * without threading anything through capture calls. The relay already scrubs all of these at ingest and
 * derives the pseudonymous users-affected key from the user fields.
 *
 * The state is static, so the scope a request sets is the scope whichever Client reports with — a
 * framework binds one client somewhere the calling code never sees. PHP normally tears the process down
 * between requests; a long-lived worker (queue, Octane) should call clear() between jobs so one job's
 * user and breadcrumbs do not ride the next one's errors.
 */
final class Scope
{
    /** Newest trail wins: a long-lived process drops the oldest crumbs rather than growing without bound. */
    public const MAX_BREADCRUMBS = 30;

    /** @var array<string,mixed>|null */
    private static ?array $user = null;
    /** @var array<string,string> */
    private static array $tags = [];
    /** @var array<string,array<string,mixed>> */
    private static array $contexts = [];
    /** @var list<array<string,mixed>> */
    private static array $breadcrumbs = [];

    private function __construct()
    {
    }

    /**
     * Attach the signed-in user (id / email / username) to subsequent events; null clears it (a sign-out).
     *
     * @param array<string,mixed>|null $user
     */
    public static function setUser(?array $user): void
    {
        self::$user = $user;
    }

    /** Attach a tag to subsequent events; a null value removes it. */
    public static function setTag(string $key, ?string $value): void
    {
        if ($value === null) {
            unset(self::$tags[$key]);

            return;
        }

        self::$tags[$key] = $value;
    }

    /**
     * Attach a named context object to subsequent events; null removes it.
     *
     * @param array<string,mixed>|null $context
     */
    public static function setContext(string $name, ?array $context): void
    {
        if ($context === null) {
            unset(self::$contexts[$name]);

            return;
        }

        self::$contexts[$name] = $context;
    }

    /**
     * Record a breadcrumb: a navigation, a job, a query — whatever helps replay the path to an error.
     * The trail (newest last, capped at MAX_BREADCRUMBS) rides every subsequent event. The timestamp is
     * epoch seconds, stamped for you when omitted.
     *
     * @param array<string,mixed>|null $data
     */
    public static function addBreadcrumb(
        string $message,
        ?string $category = null,
        ?string $level = null,
        ?string $type = null,
        ?array $data = null,
        ?float $timestamp = null,
    ): void {
        $crumb = ['message' => $message, 'timestamp' => $timestamp ?? microtime(true)];
        foreach (['category' => $category, 'level' => $level, 'type' => $type, 'data' => $data] as $key => $value) {
            if ($value !== null) {
                $crumb[$key] = $value;
            }
        }

        self::$breadcrumbs[] = $crumb;
        if (count(self::$breadcrumbs) > self::MAX_BREADCRUMBS) {
            self::$breadcrumbs = array_slice(self::$breadcrumbs, -self::MAX_BREADCRUMBS);
        }
    }

    /** Reset all ambient state (tests, a full sign-out, or between jobs in a long-lived worker). */
    public static function clear(): void
    {
        self::$user = null;
        self::$tags = [];
        self::$contexts = [];
        self::$breadcrumbs = [];
    }

    /**
     * The scope's contribution to an event, holding only the keys that are actually set so an unenriched
     * event keeps its exact wire shape. Breadcrumbs use the Sentry {"values": []} envelope.
     *
     * @return array<string,mixed>
     */
    public static function fields(): array
    {
        $fields = [];
        if (self::$user !== null) {
            $fields['user'] = self::$user;
        }
        if (self::$tags !== []) {
            $fields['tags'] = self::$tags;
        }
        if (self::$contexts !== []) {
            $fields['contexts'] = self::$contexts;
        }
        if (self::$breadcrumbs !== []) {
            $fields['breadcrumbs'] = ['values' => self::$breadcrumbs];
        }

        return $fields;
    }
}
