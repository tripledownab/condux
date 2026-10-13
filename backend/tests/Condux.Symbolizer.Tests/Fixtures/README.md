# Symbolizer test fixture

`cart.so` is built from `cart.c` with the Android NDK's clang, so the tests resolve real DWARF from a real
toolchain rather than bytes written by hand. It is DWARF version 2, the version a Flutter
`--split-debug-info` build emits, and `apply_discount` is inlined into `cart_total`, so the trap at
`0x4600` resolves to two frames.

Rebuild it, from this directory, with NDK r28:

```sh
NDK_BIN=$ANDROID_HOME/ndk/28.2.13676358/toolchains/llvm/prebuilt/darwin-x86_64/bin
$NDK_BIN/aarch64-linux-android30-clang -O1 -g -gdwarf-2 -fdebug-prefix-map="$PWD"=/src \
  -shared -Wl,--build-id=sha1 -o cart.so cart.c
```

If the trap's address changes, update `SymbolizerApiTest` and the `symbolizer-image` CI job. Both hash
the file themselves, so a rebuild needs nothing else.
