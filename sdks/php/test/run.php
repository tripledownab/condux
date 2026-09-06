<?php

declare(strict_types=1);

// The entry point CI runs: loads the harness, then every suite, and reports the total. Suites are plain
// PHP scripts of check() calls (no PHPUnit, no composer install) — add one by dropping it in here.

namespace Condux;

require __DIR__ . '/harness.php';

foreach (['event', 'scope', 'capture_context', 'modules', 'test_event'] as $suite) {
    require __DIR__ . "/{$suite}_test.php";
}

echo "$checks checks, $failures failures\n";
exit($failures === 0 ? 0 : 1);
