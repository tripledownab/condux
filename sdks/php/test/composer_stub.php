<?php

declare(strict_types=1);

namespace Composer;

/**
 * A stand-in for Composer's runtime API, so the inventory's real collection path is exercised.
 *
 * Without this the harness has no Composer autoloader, `class_exists` is false, and Modules::collect
 * returns immediately. Every line behind that guard, which is all of the actual collection, would
 * never run in a test: the suite would prove only that an SDK with no Composer reports nothing.
 *
 * getPrettyVersion throws for an unknown package, matching the real class, so the per-package catch is
 * exercised rather than assumed.
 *
 * Tests set $packages and MUST reset it to an empty array afterwards. This class cannot be undefined
 * once loaded, and the suites that run later construct clients of their own, which would otherwise
 * start carrying this stub's inventory on their events.
 */
final class InstalledVersions
{
    /** @var array<string,string|null> */
    public static array $packages = [];

    /** @return list<string> */
    public static function getInstalledPackages(): array
    {
        return array_keys(self::$packages);
    }

    public static function getPrettyVersion(string $packageName): ?string
    {
        if (!array_key_exists($packageName, self::$packages)) {
            throw new \OutOfBoundsException("Package {$packageName} is not installed");
        }

        return self::$packages[$packageName];
    }
}
