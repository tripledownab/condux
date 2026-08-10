# Condux SDK for the JVM

Report errors from a Java, Kotlin, or Scala app to a Condux relay. Emits the Sentry "store" wire shape,
so the relay normalizes it exactly like an official Sentry SDK — point it at a project DSN and it works.

Delivery is resilient (429 / 5xx / network failures retry with backoff, honoring `Retry-After`) and
**never throws** — a failed send returns a `SendResult`, it does not crash the caller.

## Install

Maven:

```xml
<dependency>
  <groupId>ai.condux</groupId>
  <artifactId>condux</artifactId>
  <version>0.1.0</version>
</dependency>
```

Gradle:

```kotlin
implementation("ai.condux:condux:0.1.0")
```

## Usage

```java
import ai.condux.ConduxClient;
import ai.condux.Level;

ConduxClient condux = ConduxClient.builder("https://<key>@ingest.condux.ai/<projectId>")
    .environment("production")
    .release("1.4.2")
    .build();

try {
    doWork();
} catch (Exception e) {
    condux.captureException(e); // captures the exception's stack trace
    throw e;
}

// or a bare message
condux.captureMessage("cache miss storm", Level.WARNING);
```

### Spring Boot / servlet apps

`ai.condux.servlet.ConduxExceptionFilter` is a servlet `Filter` that reports an uncaught request
exception as **unhandled** and re-throws, so the app's own error handling still runs. Register it as a
bean (Spring Boot MVC, or any Jakarta servlet app):

```java
@Bean
FilterRegistrationBean<ConduxExceptionFilter> conduxFilter(ConduxClient client) {
    var registration = new FilterRegistrationBean<>(new ConduxExceptionFilter(client));
    registration.setOrder(Ordered.HIGHEST_PRECEDENCE);
    return registration;
}
```

Its only dependency is the servlet API (`jakarta.servlet-api`, `provided` scope), which the container
supplies at runtime, so the SDK stays runtime dependency-free.

## Develop

The SDK is runtime dependency-free, so it builds and tests with just a JDK (no build tool required); the
servlet adapter compiles against the `provided` servlet API, fetched as a single jar:

```bash
curl -sfL -o /tmp/servlet-api.jar https://repo1.maven.org/maven2/jakarta/servlet/jakarta.servlet-api/6.0.0/jakarta.servlet-api-6.0.0.jar
javac -cp /tmp/servlet-api.jar -d out $(find src/main/java -name '*.java')
javac -cp out:/tmp/servlet-api.jar -d out-test $(find src/test/java -name '*.java')
java -cp out:out-test ai.condux.ConduxClientTest
java -cp out:out-test:/tmp/servlet-api.jar ai.condux.servlet.ConduxExceptionFilterTest
```

HTTP is `java.net.http.HttpClient` and the JSON is written by hand — no third-party runtime
dependencies. The transport, sleep, and clock are injectable via the builder
(`.transport(...)`, `.sleeper(...)`, `.clock(...)`), so the tests exercise the retry/backoff with no
real network or timers. `pom.xml` is the Maven Central publish descriptor.
