<?php

declare(strict_types=1);

// A PSR-4 autoloader for the Condux\ namespace, so the SDK loads without Composer: the `condux` bin and
// the test harness both use it. A Composer install resolves these classes from composer.json's autoload
// section first, and never reaches this file.
spl_autoload_register(static function (string $class): void {
    $prefix = 'Condux\\';
    if (!str_starts_with($class, $prefix)) {
        return;
    }

    $path = __DIR__ . '/' . str_replace('\\', '/', substr($class, strlen($prefix))) . '.php';
    if (is_file($path)) {
        require $path;
    }
});
