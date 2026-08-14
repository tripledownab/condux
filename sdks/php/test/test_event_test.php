<?php

declare(strict_types=1);

// Tests for `condux test-event`. The exit code is the contract a CI script branches on, so assert the
// code, not just the output. Delivery runs the real bin against a local socket standing in for the relay:
// the command's whole purpose is proving the actual transport works, and a fake transport here would test
// nothing the rest of the suite does not.

namespace Condux;

/** @return array{0:int,1:string,2:string} exit code, stdout, stderr */
function runTestEvent(array $args): array
{
    $out = fopen('php://memory', 'w+');
    $err = fopen('php://memory', 'w+');
    $code = TestEvent::run($args, $out, $err);
    rewind($out);
    rewind($err);

    return [$code, stream_get_contents($out), stream_get_contents($err)];
}

// The environment is a fallback for --dsn, so a DSN in the developer's shell would mask the usage cases.
$originalDsn = getenv('CONDUX_DSN');
putenv('CONDUX_DSN');

// 1. Usage errors exit 2 and say what to do.
[$code, , $err] = runTestEvent(['test-event']);
check($code === 2, 'no DSN is a usage error');
check(str_contains($err, 'CONDUX_DSN'), 'the no-DSN message names the environment variable');

[$code] = runTestEvent(['frobnicate']);
check($code === 2, 'an unknown command is a usage error');

[$code] = runTestEvent([]);
check($code === 2, 'no command at all is a usage error');

[$code] = runTestEvent(['test-event', '--dsn', 'https://ingest.test/no-key']);
check($code === 2, 'a malformed DSN is a usage error, not a delivery failure');

// 2. The DSN falls back to the environment.
putenv('CONDUX_DSN=https://ingest.test/no-key');
[$code] = runTestEvent(['test-event']);
check($code === 2, 'the DSN falls back to CONDUX_DSN (a malformed one still fails as usage)');
putenv('CONDUX_DSN');

// 3. A relay that refuses the connection exits 1. Port 9 (discard) refuses immediately, so the retry
// loop gives up without waiting on a timeout.
[$code, , $err] = runTestEvent(['test-event', '--dsn', 'http://key@127.0.0.1:9/1']);
check($code === 1, 'a refused relay is a delivery failure');
check(str_contains($err, 'Delivery FAILED'), 'a delivery failure says so');

$originalDsn === false ? putenv('CONDUX_DSN') : putenv("CONDUX_DSN=$originalDsn");

// 4. Delivery through the real executable: spawn `php exe/condux test-event` at a local socket standing
// in for the relay, so the shim, the curl transport, the store URL and the exit code are all exercised.
$server = stream_socket_server('tcp://127.0.0.1:0', $errno, $errstr);
check($server !== false, "the stand-in relay listens ($errstr)");
$port = (int) substr(strrchr(stream_socket_get_name($server, false), ':'), 1);

$process = proc_open(
    [PHP_BINARY, __DIR__ . '/../exe/condux', 'test-event', '--dsn', "http://relaykey@127.0.0.1:$port/proj-uuid"],
    [1 => ['pipe', 'w'], 2 => ['pipe', 'w']],
    $pipes,
);

$request = '';
$body = '';
$connection = stream_socket_accept($server, 10);
if ($connection !== false) {
    while (($line = fgets($connection)) !== false && trim($line) !== '') {
        $request .= $line;
    }
    if (preg_match('/content-length:\s*(\d+)/i', $request, $length) === 1 && (int) $length[1] > 0) {
        $body = fread($connection, (int) $length[1]);
    }
    fwrite($connection, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    fclose($connection);
}
fclose($server);

$stdout = stream_get_contents($pipes[1]);
$stderr = stream_get_contents($pipes[2]);
fclose($pipes[1]);
fclose($pipes[2]);
$exitCode = proc_close($process);

check($exitCode === 0, "a delivered test event exits 0 (stderr: $stderr)");
check(str_contains($stdout, 'Delivered'), 'a delivered test event says so on stdout');
check(str_starts_with($request, 'POST /api/proj-uuid/store/ HTTP/'), 'it posts to the DSN project\'s store endpoint');
check(stripos($request, 'x-condux-auth: relaykey') !== false, 'it authenticates with the DSN key');
$event = json_decode($body, true);
check(is_array($event) && $event['level'] === 'info', 'the test event is info level');
check(($event['message'] ?? '') === 'Condux test event', 'the default message is sent');
check(($event['environment'] ?? '') === 'condux-test', 'the test event is tagged as a test');
