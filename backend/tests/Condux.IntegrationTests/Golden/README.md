# Native symbolication fixtures

A real Flutter crash and the debug file that resolves it, so the native pass is tested against what
`sentry_flutter` and `flutter build --obfuscate --split-debug-info` actually produce.

- `flutter-crashapp.symbols` is the `app.android-arm64.symbols` file from an obfuscated release build of a
  four-function app whose `CartService.applyDiscount` throws at `lib/main.dart:13`. Its absolute paths were
  rewritten after the build to neutral ones of exactly the same length (the build machine's home directory
  and checkout directory), so no DWARF offset or length moved; llvm-symbolizer resolves the four frames
  below identically before and after. Its GNU build ID is `7a3ef9306827da183fa36f07eecef2fb`.
- `flutter-crashapp-envelope.txt` is the crash as `sentry_flutter` 9.30.1 sends it: frames with only an
  `instruction_addr`, `<asynchronous suspension>` markers between them, and the image once in
  `debug_meta`. The addresses, image base and size are the device's own; the event id, timestamp and
  `code_file` are neutral. The image base subtracted from each address gives the offsets `0x355b77`,
  `0x355c87`, `0x355d17` and `0x3095b3`, which resolve to `main`, `checkout`, `CartService.total` and
  `CartService.applyDiscount`. The Dart VM reports a return address minus one, so no adjustment is made.
- `cart.so` is not in this directory: the project file links the symbolizer's own NDK-built DWARF 2
  fixture (`backend/tests/Condux.Symbolizer.Tests/Fixtures`, whose README says how to rebuild it) into the
  test output as `Golden/cart.so`, because its trap at `0x4600` resolves to an inline chain,
  `apply_discount` inside `cart_total`, which the Flutter fixture has none of.
