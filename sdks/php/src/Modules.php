<?php

declare(strict_types=1);

namespace Condux;

/**
 * The runtime dependency inventory that rides events (ADR-0041): which package versions are actually
 * installed alongside the running process, as opposed to which ones composer.json declares. The relay
 * indexes this so a security advisory can be answered with "and you are running 2.3.0 in production"
 * rather than only "your lockfile says so".
 *
 * The source is Composer's own runtime API, which reports what the installed.php it generated at
 * install time actually resolved to. That is the deployed tree, which is the whole point: a manifest
 * states ranges, and what answers "am I running the vulnerable version" is what resolution put on
 * disk. Nothing is parsed and nothing is guessed.
 *
 * Static rather than per-client, matching Scope, because a PHP client is instance based and a
 * framework binds it in a container the caller never sees, so per-client state could not be ambient.
 */
final class Modules
{
    /**
     * The most entries carried on one event. The cap applies after sorting, so which entries survive
     * is stable across events rather than varying with iteration order: the server sees one
     * consistent set instead of a shifting sample.
     */
    public const MAX_MODULES = 1000;

    /**
     * How long to wait before repeating the inventory on another event.
     *
     * This is what makes the feature affordable. The server deduplicates a release's inventory down to
     * one row per package per day, so attaching the whole map to every event would spend bytes for
     * nothing. Repeating on an interval rather than sending once keeps the robustness that every-event
     * buys: the event carrying the inventory can be dropped by a rate limit or a quota rejection
     * before anything parses it, so a single attempt per process would lose that day's inventory.
     */
    public const INTERVAL_SECONDS = 15 * 60;

    /** @var array<string,string>|null */
    private static ?array $modules = null;

    private static ?float $lastAttachedAt = null;

    /**
     * The installed packages as a name to version map, empty when Composer's runtime API is absent.
     *
     * Never throws. An application installed without Composer, or one running from a packaged binary,
     * has no inventory to report, and unknown is the honest answer there.
     *
     * @return array<string,string>
     */
    public static function collect(): array
    {
        if (!class_exists(\Composer\InstalledVersions::class)) {
            return [];
        }

        $found = [];
        try {
            foreach (\Composer\InstalledVersions::getInstalledPackages() as $package) {
                try {
                    $version = \Composer\InstalledVersions::getPrettyVersion($package);
                } catch (\Throwable) {
                    // One unresolvable package is not a reason to report nothing about the rest.
                    continue;
                }
                if ($package !== '' && $version !== null && $version !== '') {
                    $found[$package] = $version;
                }
            }
        } catch (\Throwable) {
            return [];
        }

        return $found;
    }

    /**
     * Declare the installed package versions for subsequent events; null clears them.
     *
     * @param array<string,string>|null $modules
     */
    public static function set(?array $modules): void
    {
        // A fresh declaration is news, so let the next event carry it rather than waiting out an
        // interval started by the previous inventory.
        self::$lastAttachedAt = null;

        if ($modules === null) {
            self::$modules = null;

            return;
        }

        $entries = [];
        foreach ($modules as $name => $version) {
            if ((string) $name !== '' && (string) $version !== '') {
                $entries[(string) $name] = (string) $version;
            }
        }
        ksort($entries);
        $entries = array_slice($entries, 0, self::MAX_MODULES, true);
        self::$modules = $entries === [] ? null : $entries;
    }

    /**
     * The inventory's contribution to an event: the full map on the first event and then at most once
     * per {@see INTERVAL_SECONDS}, and an empty array otherwise, so an event that carries nothing
     * keeps its exact previous wire shape.
     *
     * @return array<string,array<string,string>>
     */
    public static function fields(float $now): array
    {
        if (self::$modules === null) {
            return [];
        }
        if (self::$lastAttachedAt !== null && ($now - self::$lastAttachedAt) < self::INTERVAL_SECONDS) {
            return [];
        }

        self::$lastAttachedAt = $now;

        return ['modules' => self::$modules];
    }

    /** Reset the inventory and its interval. Tests only; a process has one installed tree. */
    public static function reset(): void
    {
        self::$modules = null;
        self::$lastAttachedAt = null;
    }
}
